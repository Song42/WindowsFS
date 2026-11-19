using System.IO;
using Microsoft.Data.Sqlite;
using WindowsFS.App.ViewModels;
using WindowsFS.Core.Cleanup;

namespace WindowsFS.App.History;

internal record Footprint(string Id, string Path, long Bytes, long Allocated, int Links);
internal record VolumePoint(long Time, string Volume, long Free, string Segment);
internal record HistoryRow(long Time, string Path, string Action, string Program, string Purpose, string Evidence,
    long? Before, long? After, long? Change, long? Reclaimed, long? BeforeAllocated = null, long? AfterAllocated = null)
{
    public string When => DateTimeOffset.FromUnixTimeMilliseconds(Time).LocalDateTime.ToString("MM-dd HH:mm:ss");
    public string Delta => Change is long n ? (n < 0 ? "−" : n > 0 ? "+" : "") + Size(n) : "—";
    public string Freed => Reclaimed is long n ? Size(n) : "—";
    public static string Size(long n) => DisplayFormat.Bytes(Math.Abs(n));
    public string LogicalRange => $"{Known(Before)} → {Known(After)}";
    public string AllocatedRange => $"{Known(BeforeAllocated)} → {Known(AfterAllocated)}";
    private static string Known(long? bytes) => bytes is long n ? Size(n) : "미확인";
}

internal sealed class HistoryStore : IDisposable
{
    internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsFS", "History");
    internal static string Database => Path.Combine(Folder, "history.db");
    internal static void CheckLocation(bool create)
    {
        if (!Path.IsPathFullyQualified(Folder) || Folder.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(Folder)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new IOException("로컬 기록 경로를 사용할 수 없음");
        for (var dir = new DirectoryInfo(Folder); dir != null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("기록 폴더가 다른 경로를 가리킴");
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            string path = Database + suffix;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                VolumeJournal.Inspect(path, "history") is not { Links: 1 })
                throw new IOException("기록 파일 경로를 확인할 수 없음");
        }
        if (create) Directory.CreateDirectory(Folder);
    }
    private readonly SqliteConnection db;
    internal HistoryStore(string database, bool initialize)
    {
        db = new(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
            Mode = initialize ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            DefaultTimeout = 2
        }.ToString());
        try
        {
            db.Open();
            Exec("PRAGMA temp_store=MEMORY");
            if (!initialize) return;
            Exec("""
                PRAGMA auto_vacuum=INCREMENTAL;
                PRAGMA max_page_count=30720;
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA wal_autocheckpoint=128;
                PRAGMA journal_size_limit=2097152;
                CREATE TABLE IF NOT EXISTS sample(t INTEGER, volume TEXT, free INTEGER,segment TEXT);
                CREATE INDEX IF NOT EXISTS sample_time ON sample(t);
                CREATE TABLE IF NOT EXISTS event(t INTEGER,path TEXT,action TEXT,program TEXT,purpose TEXT,evidence TEXT,b INTEGER,a INTEGER,delta INTEGER,freed INTEGER,b_alloc INTEGER,a_alloc INTEGER);
                CREATE INDEX IF NOT EXISTS event_time ON event(t);
                CREATE TABLE IF NOT EXISTS actor(t INTEGER,path TEXT,op TEXT,name TEXT);
                CREATE INDEX IF NOT EXISTS actor_match ON actor(path,op,t);
                CREATE INDEX IF NOT EXISTS actor_time ON actor(t);
                CREATE TABLE IF NOT EXISTS state(id TEXT PRIMARY KEY,path TEXT,b INTEGER,alloc INTEGER,links INTEGER,t INTEGER);
                CREATE INDEX IF NOT EXISTS state_path ON state(path);
                CREATE TABLE IF NOT EXISTS cursor(volume TEXT PRIMARY KEY,journal INTEGER,usn INTEGER,scope TEXT);
                CREATE TABLE IF NOT EXISTS hour(t INTEGER,volume TEXT,grown INTEGER,freed INTEGER,unknown INTEGER,PRIMARY KEY(t,volume));
                """);
        }
        catch { db.Dispose(); throw; }
    }

    private SqliteCommand Command(string sql, params object?[] values)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        for (int i = 0; i < values.Length; i++) c.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        return c;
    }
    internal void Exec(string sql, params object?[] values) { using var c = Command(sql, values); c.ExecuteNonQuery(); }
    internal List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params object?[] values)
    {
        using var c = Command(sql, values);
        using var r = c.ExecuteReader();
        List<T> result = [];
        while (r.Read()) result.Add(read(r));
        return result;
    }
    internal void Atomic(Action action)
    {
        Exec("BEGIN IMMEDIATE");
        try { action(); Exec("COMMIT"); }
        catch { Exec("ROLLBACK"); throw; }
    }
    internal Footprint? Previous(string key, bool byPath = false) =>
        Query("SELECT id,path,b,alloc,links FROM state WHERE " + (byPath ? "path" : "id") + "=$p0 LIMIT 1",
            r => new Footprint(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4)), key).FirstOrDefault();
    internal void Remember(Footprint f, long t) => Exec("INSERT OR REPLACE INTO state VALUES($p0,$p1,$p2,$p3,$p4,$p5)", f.Id, f.Path, f.Bytes, f.Allocated, f.Links, t);
    internal void Forget(string id) => Exec("DELETE FROM state WHERE id=$p0", id);
    private string segment = Guid.NewGuid().ToString("N");
    internal void BreakGraph() => segment = Guid.NewGuid().ToString("N");
    internal void Sample(long t, string volume, long free) => Exec("INSERT INTO sample VALUES($p0,$p1,$p2,$p3)", t, volume, free, segment);
    internal void Gap(long t, string scope, string reason) => Record(t, scope, "수집 구간", "", new("", "", reason), null, null, null, null);
    internal void Record(long t, string path, string action, string op, FolderAssociation match, long? before, long? after, long? delta, long? freed, long? beforeAllocated = null, long? afterAllocated = null)
    {
        Exec("INSERT INTO event VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11)", t, path, action, op, match.Purpose,
            match.AppOrArea.Length == 0 ? match.Evidence : match.AppOrArea + " · " + match.Evidence, before, after, delta, freed, beforeAllocated, afterAllocated);
        int unknown = action.StartsWith("삭제", StringComparison.Ordinal) && freed == null ? 1 : 0;
        if (delta is > 0 || freed is > 0 || unknown != 0)
            Exec("""
                INSERT INTO hour VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(t,volume) DO UPDATE SET
                grown=grown+excluded.grown,freed=freed+excluded.freed,unknown=unknown+excluded.unknown
                """, t - t % 3600000, Path.GetPathRoot(path), Math.Max(0, delta ?? 0), freed ?? 0, unknown);
    }
    internal void Actor(long t, string path, string op, string name) => Exec("INSERT INTO actor VALUES($p0,$p1,$p2,$p3)", t, path, op, name);
    internal void Maintain(long now)
    {
        long day = 86400000;
        Exec("DELETE FROM event WHERE t<$p0; DELETE FROM actor WHERE t<$p0; DELETE FROM state WHERE t<$p0", now - 7 * day);
        Exec("DELETE FROM sample WHERE t<$p0; DELETE FROM hour WHERE t<$p0", now - 30 * day);
        // Leave headroom for the WAL and the next batch. Purge the oldest detail before summaries.
        long used = Query("SELECT (page_count-freelist_count)*page_size FROM pragma_page_count,pragma_freelist_count,pragma_page_size", r => r.GetInt64(0))[0];
        if (used > 112L * 1024 * 1024)
            foreach (var table in new[] { "event", "actor", "state" })
                Exec($"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} ORDER BY t LIMIT (SELECT count(*)/4+1 FROM {table}))");
        Exec("PRAGMA incremental_vacuum(256)");
    }
    internal List<VolumePoint> Samples(long since) => Query("""
        SELECT max(t),volume,free,segment FROM sample WHERE t >= $p0 GROUP BY volume,segment,t/$p1 ORDER BY 1
        """, r => new VolumePoint(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3)), since, Math.Max(30000, (Now - since) / 500));
    internal List<HistoryRow> Rows(long from, long to, bool largest) => Query("""
        SELECT t,path,action,coalesce((SELECT group_concat(name,' · ') FROM (
        SELECT DISTINCT name FROM actor WHERE actor.path=event.path AND actor.op=event.program
        AND actor.t BETWEEN event.t-2000 AND event.t+2000 ORDER BY actor.t DESC LIMIT 5)), '미확인'),
        purpose,evidence,b,a,delta,freed,b_alloc,a_alloc FROM event WHERE t BETWEEN $p0 AND $p1 ORDER BY
        """ + (largest ? " max(abs(coalesce(delta,0)),coalesce(freed,0)) DESC, " : " ") + "t DESC LIMIT 2000",
        r => new HistoryRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            Number(r, 6), Number(r, 7), Number(r, 8), Number(r, 9), Number(r, 10), Number(r, 11)), from, to);
    internal List<HistoryRow> Operations(long from, long to) => Query("""
        SELECT t,path,op,name FROM actor WHERE t BETWEEN $p0 AND $p1 ORDER BY t DESC LIMIT 2000
        """, r => new HistoryRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), "", "Windows 실시간 파일 작업 · 요청 시각", null, null, null, null), from, to);
    internal (long Growth, long Freed, long Unknown) Totals(long from, long to) => Query(
        """
        SELECT coalesce(sum(g),0),coalesce(sum(f),0),coalesce(sum(u),0) FROM (
          SELECT grown g,freed f,unknown u FROM hour WHERE t>=$p0 AND t+3599999<=$p1
          UNION ALL SELECT max(delta,0),freed,(action LIKE '삭제%' AND freed IS NULL) FROM event WHERE t BETWEEN $p0 AND $p1
            AND (t-t%3600000<$p0 OR t-t%3600000+3599999>$p1))
        """,
        r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), from, to)[0];
    private static long? Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    internal static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public void Dispose() => db.Dispose();
}
