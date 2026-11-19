using System.IO;
using System.Threading.Channels;
using WindowsFS.App.Services;
using WindowsFS.Core;
using WindowsFS.Core.Cleanup;

namespace WindowsFS.App.History;

internal sealed record Observation(long Time, string Path, string Action, string Program);
internal sealed class HistoryRecorder
{
    private string[] roots = [];
    private Task? worker;
    private CancellationTokenSource? cancel;
    private DetailClient? detail;
    private readonly Channel<Observation> observations = Channel.CreateBounded<Observation>(8192);
    private int lost;
    private volatile string status = "추적 꺼짐";
    private volatile string detailStatus = "";
    internal string Status => status + (detailStatus.Length == 0 ? "" : "  ·  " + detailStatus);
    internal bool Active => worker is { IsCompleted: false } && cancel is { IsCancellationRequested: false };
    internal string[] Roots => roots;
    internal bool Detailed => detail != null;
    internal static bool Under(string path, string root, StringComparison comparison = StringComparison.Ordinal) =>
        path.Equals(root, comparison) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + "\\", comparison);
    internal static bool Includes(string path, IReadOnlyList<string> scope) =>
        !Under(path, HistoryStore.Folder, StringComparison.OrdinalIgnoreCase) && scope.Any(root => Under(path, root));
    internal void Start(IEnumerable<string> paths, bool fresh = false)
    {
        if (worker is { IsCompleted: false }) return;
        roots = paths.Where(p => Path.IsPathFullyQualified(p) && !p.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(ScanPath.NormalizeRoot).Distinct(StringComparer.Ordinal).Order().ToArray();
        roots = roots.Where(p => !roots.Any(other => other != p && Under(p, other))).ToArray();
        if (roots.Length == 0) { status = "분석된 로컬 경로 없음"; return; }
        cancel?.Dispose();
        cancel = new();
        status = "추적 시작 중";
        while (observations.Reader.TryRead(out _)) { }
        lost = 0;
        worker = Task.Run(() => RunAsync(fresh, cancel.Token));
    }
    internal async Task PauseAsync()
    {
        cancel?.Cancel();
        await StopDetailAsync().ConfigureAwait(false);
        if (worker != null) await worker.ConfigureAwait(false);
    }
    internal async Task SetDetailAsync(bool enable)
    {
        await StopDetailAsync();
        if (!enable || !Active) return;
        var client = new DetailClient();
        detail = client;
        detailStatus = "프로그램 추적 연결 중";
        await client.RunAsync(roots, o =>
        {
            if (o.Action == "상태")
            {
                detailStatus = o.Program;
                if (o.Program.Contains("누락", StringComparison.Ordinal)) Interlocked.Increment(ref lost);
            }
            else if (Includes(o.Path, roots) && !observations.Writer.TryWrite(o)) Interlocked.Increment(ref lost);
        });
        if (ReferenceEquals(detail, client)) { detail = null; if (Active) detailStatus = client.EndStatus; }
    }
    private async Task StopDetailAsync()
    {
        var old = detail;
        detail = null;
        if (old != null) await old.StopAsync().ConfigureAwait(false);
        detailStatus = "";
    }

    private async Task RunAsync(bool fresh, CancellationToken token)
    {
        List<VolumeScope> volumes = [];
        try
        {
            HistoryStore.CheckLocation(true);
            using var store = new HistoryStore(HistoryStore.Database, true);
            var classify = CleanupAnalyzer.CreatePathClassifier(await new InstalledAppsReader().ReadAsync(token));
            foreach (var group in roots.Where(IsLocal).GroupBy(p => VolumeJournal.VolumeFor(p) ?? Path.GetPathRoot(p)!, StringComparer.OrdinalIgnoreCase))
            {
                var v = new VolumeScope(group.Key, group.ToArray());
                volumes.Add(v);
                v.Begin(store, fresh);
            }
            if (volumes.Count == 0) { status = "접근 가능한 로컬 볼륨 없음"; return; }
            var reader = new DiskSpaceReader();
            long nextSample = 0, nextMaintain = 0, lastTick = HistoryStore.Now;
            while (!token.IsCancellationRequested)
            {
                var now = HistoryStore.Now;
                if (now - lastTick > 90000 || now < lastTick) { store.BreakGraph(); store.Gap(now, "", "절전 또는 수집 지연"); }
                lastTick = now;
                store.Atomic(() =>
                {
                    DrainActors(store);
                    foreach (var volume in volumes) volume.Poll(store, classify, token);
                });
                if (now >= nextSample)
                {
                    foreach (var volume in volumes)
                    {
                        if (!volume.Available) continue;
                        try { var sample = await reader.ReadAsync(volume.Roots[0], token); store.Sample(sample.ReadAt.ToUnixTimeMilliseconds(), volume.Label, sample.FreeBytes); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                        catch (IOException) { store.BreakGraph(); store.Gap(now, volume.Label, "볼륨 여유 공간 미확인"); }
                    }
                    nextSample = now + 30000;
                }
                if (now >= nextMaintain) { store.Maintain(now); nextMaintain = now + 60000; }
                status = string.Join("  ·  ", volumes.Select(v => v.Label + " " + v.Mode));
                try { await Task.Delay(750, token); } catch (OperationCanceledException) { break; }
            }
            store.Atomic(() =>
            {
                DrainActors(store);
                foreach (var v in volumes) v.Poll(store, classify, CancellationToken.None);
                store.Gap(HistoryStore.Now, "", "추적 중지");
            });
            status = "추적 일시 정지";
        }
        catch (OperationCanceledException) { status = "추적 일시 정지"; }
        catch (Exception ex) { status = "추적 중단 · " + ex.GetType().Name; }
        finally { foreach (var v in volumes) v.Dispose(); await StopDetailAsync().ConfigureAwait(false); }
    }
    private static bool IsLocal(string p)
    {
        try { return new DriveInfo(Path.GetPathRoot(p)!).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram; }
        catch (IOException) { return false; }
    }
    private void DrainActors(HistoryStore store)
    {
        for (int i = 0; i < 8192 && observations.Reader.TryRead(out var o); i++) store.Actor(o.Time, o.Path, o.Action, o.Program);
        if (Interlocked.Exchange(ref lost, 0) > 0) store.Gap(HistoryStore.Now, "", "프로그램 작업 일부 누락");
    }
}

internal sealed class VolumeScope(string volume, string[] roots, Func<string, VolumeJournal?>? openJournal = null) : IDisposable
{
    internal string[] Roots => roots;
    internal string Label { get; } = VolumeJournal.MountFor(roots[0]);
    internal bool Available => !disconnected;
    internal string Mode => disconnected ? "연결 대기" : journal != null ? "변경 저널" : watchers.Count > 0 ? "실시간 변경 알림" : "볼륨 표본만";
    private VolumeJournal? journal;
    private long cursor;
    private long replayEnd;
    private long checkpointTime;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Channel<(long Time, string Path, string? Old, WatcherChangeTypes Type)> changes = Channel.CreateBounded<(long, string, string?, WatcherChangeTypes)>(4096);
    private int missed;
    private long nextVolumeCheck;
    private bool disconnected;
    private string Scope => string.Join("|", roots);
    internal void Begin(HistoryStore store, bool fresh)
    {
        journal = (openJournal ?? VolumeJournal.Open)(volume);
        var prior = store.Query("SELECT journal,usn,scope FROM cursor WHERE volume=$p0", r => (Id: r.GetInt64(0), Next: r.GetInt64(1), Scope: r.GetString(2)), volume).SingleOrDefault();
        bool resume = !fresh && journal != null && prior.Scope == Scope && prior.Id == journal.JournalId && prior.Next >= journal.First && prior.Next <= journal.End;
        replayEnd = resume ? journal!.End : 0;
        store.Atomic(() =>
        {
            if (!resume) ResetState(store);
            cursor = resume ? prior.Next : journal?.End ?? 0;
            SaveCursor(store);
            store.Gap(HistoryStore.Now, roots[0], resume ? "추적 재개 · 유효한 변경 저널에서 보충" : "추적 시작 · 새 기준점");
        });
        if (journal == null) StartWatchers();
    }
    private void ResetState(HistoryStore store) => store.Exec("DELETE FROM state WHERE substr(id,1,length($p0))=$p0", volume + ":");
    private void SaveCursor(HistoryStore store)
    {
        if (journal == null) store.Exec("DELETE FROM cursor WHERE volume=$p0", volume);
        else store.Exec("INSERT OR REPLACE INTO cursor VALUES($p0,$p1,$p2,$p3)", volume, journal.JournalId, cursor, Scope);
    }
    private void StartWatchers()
    {
        foreach (string path in roots)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new(path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 32768,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.DirectoryName
                };
                watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
                watcher.Error += (_, _) => Interlocked.Increment(ref missed);
                watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { watcher?.Dispose(); }
        }
    }
    private void Changed(object sender, FileSystemEventArgs e)
    {
        if (!HistoryRecorder.Includes(e.FullPath, roots)) return;
        if (!changes.Writer.TryWrite((HistoryStore.Now, e.FullPath, (e as RenamedEventArgs)?.OldFullPath, e.ChangeType)))
            Interlocked.Increment(ref missed);
    }
    internal void Poll(HistoryStore store, Func<string, FolderAssociation> classify, CancellationToken token)
    {
        if (volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && HistoryStore.Now >= nextVolumeCheck)
        {
            nextVolumeCheck = HistoryStore.Now + 30000;
            var same = string.Equals(VolumeJournal.VolumeFor(roots[0]), volume, StringComparison.OrdinalIgnoreCase);
            if (!same && !disconnected)
            {
                disconnected = true;
                Dispose(); watchers.Clear(); journal = null;
                while (changes.Reader.TryRead(out _)) { }
                store.BreakGraph(); store.Gap(HistoryStore.Now, Label, "볼륨 연결 중단");
            }
            if (same && disconnected)
            {
                disconnected = false;
                // Poll already owns the transaction. Reopen at the last committed cursor on the next session.
                journal = VolumeJournal.Open(volume);
                if (journal == null || cursor < journal.First || cursor > journal.End ||
                    !store.Query("SELECT journal FROM cursor WHERE volume=$p0", r => r.GetInt64(0), volume).Contains(journal.JournalId))
                { ResetState(store); cursor = journal?.End ?? 0; }
                replayEnd = journal?.End ?? 0;
                SaveCursor(store); if (journal == null) StartWatchers();
                store.Gap(HistoryStore.Now, Label, "볼륨 다시 연결");
            }
        }
        if (disconnected) return;
        if (journal != null)
        {
            try
            {
                var parents = new Dictionary<long, string?>();
                // Bounded catch-up. Each page and its checkpoint commit together.
                for (int i = 0; i < 2 && !token.IsCancellationRequested; i++)
                {
                    var page = journal.Read(cursor);
                    bool changed = false;
                    foreach (var c in page.Changes)
                    {
                        if (!parents.TryGetValue(c.Parent, out var parent)) parents[c.Parent] = parent = journal.ParentPath(c.Parent);
                        var key = VolumeJournal.Key(volume, c.File);
                        bool delete = (c.Reason & VolumeJournal.Delete) != 0;
                        var path = parent != null && c.Name.Length > 0 ? Path.Join(parent, c.Name) : delete ? null : journal.ParentPath(c.File);
                        if (path != null && !HistoryRecorder.Includes(path, roots))
                        {
                            if ((c.Reason & VolumeJournal.RenameNew) != 0 && store.Previous(key) != null) { store.Forget(key); changed = true; }
                            continue;
                        }
                        var previous = store.Previous(key);
                        if (path == null && delete) path = previous?.Path;
                        if (path == null || !HistoryRecorder.Includes(path, roots))
                        {
                            if (previous != null) store.Forget(key);
                            continue;
                        }
                        // A current metadata query cannot reconstruct sizes during an offline interval.
                        var current = delete || c.Usn < replayEnd ? null : VolumeJournal.Inspect(path, volume, c.File);
                        Apply(store, classify, c.Time, path, previous, current, c.Reason, true);
                        changed = true;
                    }
                    if (page.Next == cursor) break;
                    cursor = page.Next;
                    if (changed || HistoryStore.Now - checkpointTime >= 30000)
                    {
                        SaveCursor(store);
                        checkpointTime = HistoryStore.Now;
                    }
                }
                return;
            }
            catch (IOException)
            {
                journal.Dispose(); journal = null;
                ResetState(store); SaveCursor(store); StartWatchers();
                store.Gap(HistoryStore.Now, roots[0], "변경 저널 중단 · 실시간 알림으로 전환");
            }
        }
        if (Interlocked.Exchange(ref missed, 0) > 0)
        {
            while (changes.Reader.TryRead(out _)) { }
            ResetState(store); store.Gap(HistoryStore.Now, roots[0], "변경 알림 일부 누락 · 기준점 초기화");
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 1024 && changes.Reader.TryRead(out var item); i++)
        {
            // Coalesce consecutive notifications for a path, never across a create/rename/delete boundary.
            if (item.Type == WatcherChangeTypes.Changed && !seen.Add(item.Path)) continue;
            if (item.Type != WatcherChangeTypes.Changed) seen.Remove(item.Path);
            var previous = store.Previous(item.Old ?? item.Path, true);
            bool removed = item.Type == WatcherChangeTypes.Deleted;
            var current = removed ? null : VolumeJournal.Inspect(item.Path, volume);
            if (current != null && previous?.Id != current.Id) previous = store.Previous(current.Id);
            if (current == null && !removed && previous == null) continue;
            uint reason = item.Type switch
            {
                WatcherChangeTypes.Created => VolumeJournal.Create,
                WatcherChangeTypes.Renamed => VolumeJournal.RenameNew,
                WatcherChangeTypes.Deleted => VolumeJournal.Delete,
                _ => 1u
            };
            // A removal from a subdirectory can be a move outside the watched tree.
            bool fullEvidence = removed ? previous is { Links: 1 } && VolumeJournal.ConfirmRemoved(roots[0], previous)
                : roots.Any(p => string.Equals(p, Path.GetPathRoot(p), StringComparison.OrdinalIgnoreCase));
            Apply(store, classify, item.Time, item.Path, previous, current, reason, fullEvidence);
        }
    }
    internal static void Apply(HistoryStore db, Func<string, FolderAssociation> classify, long t, string path, Footprint? old,
        Footprint? current, uint reason, bool fullEvidence)
    {
        bool removed = (reason & VolumeJournal.Delete) != 0, moved = (reason & (VolumeJournal.RenameOld | VolumeJournal.RenameNew)) != 0;
        bool link = (reason & VolumeJournal.HardLink) != 0;
        bool created = (reason & VolumeJournal.Create) != 0 && !moved && !link && fullEvidence && current?.Links == 1 && old == null;
        var before = created ? 0L : old?.Bytes;
        long? delta = null, freed = null;
        string action;
        if (removed)
        {
            if (old is { Links: 1 } && !link && fullEvidence && (reason & 7) == 0) freed = old.Allocated;
            action = fullEvidence ? "삭제" : "삭제·범위 밖 이동";
            if (old != null) db.Forget(old.Id);
        }
        else
        {
            if (current != null && !link && !moved && (old != null || created))
                delta = current.Allocated - (old?.Allocated ?? 0);
            action = moved ? "이동·이름 변경" : link ? "하드 링크 변경" : created ? "생성" : "변경";
            if (current != null) db.Remember(current, t);
            else if (old != null && !moved) db.Forget(old.Id);
        }
        db.Record(t, path, action, removed ? "삭제 요청" : moved || link ? "" : "쓰기 요청", classify(Path.GetDirectoryName(path) ?? path),
            before, current?.Bytes, delta, freed, created ? 0L : old?.Allocated, current?.Allocated);
    }
    public void Dispose() { journal?.Dispose(); foreach (var w in watchers) w.Dispose(); }
}
