using System.IO;
using System.Text;
using WindowsFS.App.History;
using WindowsFS.Core.Cleanup;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static Task LocalHistory()
    {
        using var fixture = new PickerFixture();
        var database = Path.Combine(fixture.Root, "fixture.db");
        var path = Path.Combine(fixture.Root, "data.bin");
        long now = HistoryStore.Now;
        var classify = CleanupAnalyzer.CreatePathClassifier(CleanupContext.Empty);
        var bytes = new byte[65536];
        File.WriteAllBytes(path, bytes);
        var file = VolumeJournal.Inspect(path, "synthetic-volume") ?? throw new Exception("Missing fixture allocation metadata");
        Equal((long)bytes.Length, file.Bytes);
        Check(file.Allocated > 0 && file.Links == 1, "Expected a single allocated file.");
        Check(VolumeJournal.Inspect(path, "synthetic-volume", 123) == null, "A replacement file inherited an unrelated identity.");
        using (var db = new HistoryStore(database, true))
        {
            db.Atomic(() =>
            {
                VolumeScope.Apply(db, classify, now, path, null, file, VolumeJournal.Create, true);
                VolumeScope.Apply(db, classify, now + 1, path, file, file, 1, true);
                db.Actor(now, path, "쓰기 요청", "Writer A");
                db.Actor(now + 2, path, "쓰기 요청", "Writer B");
            });
            var rows = db.Rows(now, now + 10, false);
            Check(rows.Count == 2 && rows.All(r => r.Program.Contains("Writer A") && r.Program.Contains("Writer B")), "Concurrent observations lost a program.");
            Equal(0L, rows[0].Change!.Value);
            Equal(file.Allocated, rows[1].Change!.Value);
            var renamed = Path.Combine(fixture.Root, "moved.bin");
            File.Move(path, renamed);
            var moved = VolumeJournal.Inspect(renamed, "synthetic-volume")!;
            Equal(file.Id, moved.Id);
            VolumeScope.Apply(db, classify, now + 3, renamed, file, moved, VolumeJournal.RenameNew, true);
            Check(db.Rows(now + 3, now + 3, false).Single().Reclaimed == null, "A rename freed space.");
            File.Delete(renamed);
            VolumeScope.Apply(db, classify, now + 4, renamed, moved, null, VolumeJournal.Delete, true);
            VolumeScope.Apply(db, classify, now + 5, path, null, null, VolumeJournal.Delete, true);
            VolumeScope.Apply(db, classify, now + 6, path, moved with { Links = 2 }, null, VolumeJournal.Delete, true);
            VolumeScope.Apply(db, classify, now + 7, path, moved, null, VolumeJournal.Delete, false);
            Equal(file.Allocated, db.Rows(now + 4, now + 4, false).Single().Reclaimed!.Value);
            Check(db.Rows(now + 5, now + 7, false).All(r => r.Reclaimed == null), "Missing evidence was counted as reclaimed.");
            db.Sample(now, "Synthetic", 400000);
            db.Record(now - 10 * 86400000L, path, "생성", "쓰기 요청", classify(path), 0, 4096, 4096, null);
            db.Maintain(now);
            Equal((4096L + file.Allocated, file.Allocated, 3L), db.Totals(now - 30 * 86400000L, now + 10));
            Check(!HistoryRecorder.Includes(Path.Combine(HistoryStore.Folder, "history.db-wal"), [Path.GetPathRoot(HistoryStore.Folder)!]),
                "History observed its own writes.");
            Check(!HistoryRecorder.Includes(fixture.Root + "-other", [fixture.Root]), "Scope matched a sibling prefix.");
        }
        using (var db = new HistoryStore(database, false))
        {
            Equal(1, db.Samples(now - 1).Count);
            Equal((file.Allocated, file.Allocated, 3L), db.Totals(now, now + 10));
            Check(db.Previous(file.Id) == null, "Deleted state survived reopening.");
            Check(db.Operations(now, now + 10).Count == 2, "Process observations did not persist.");
        }
        byte[] journal = new byte[80];
        BitConverter.GetBytes(64L).CopyTo(journal, 0);
        BitConverter.GetBytes(72).CopyTo(journal, 8);
        BitConverter.GetBytes((ushort)2).CopyTo(journal, 12);
        BitConverter.GetBytes(DateTimeOffset.Now.ToFileTime()).CopyTo(journal, 40);
        BitConverter.GetBytes((ushort)2).CopyTo(journal, 64);
        BitConverter.GetBytes((ushort)60).CopyTo(journal, 66);
        Encoding.Unicode.GetBytes("x").CopyTo(journal, 68);
        Equal("x", VolumeJournal.Decode(journal).Changes.Single().Name);
        return Task.CompletedTask;
    }

    private static async Task LiveHistoryFixture()
    {
        using var fixture = new PickerFixture();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "watched")).FullName;
        var path = Path.Combine(root, "example.bin");
        using var store = new HistoryStore(Path.Combine(fixture.Root, "fixture.db"), true);
        // Exercise live notifications inside the fixture without subscribing to the host volume journal.
        using var volume = new VolumeScope("fixture", [root], _ => null);
        volume.Begin(store, true);
        var classify = CleanupAnalyzer.CreatePathClassifier(CleanupContext.Empty);
        async Task Until(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(40);
                store.Atomic(() => volume.Poll(store, classify, CancellationToken.None));
                if (condition()) return;
            }
            throw new Exception("The expected file observation did not arrive.");
        }
        File.WriteAllBytes(path, new byte[8192]);
        await Until(() => store.Previous(path, true)?.Bytes == 8192);
        using (var append = File.Open(path, FileMode.Append)) append.Write(new byte[32768]);
        await Until(() => store.Previous(path, true)?.Bytes == 40960);
        var state = store.Previous(path, true)!;
        Check(!VolumeJournal.ConfirmRemoved(root, state), "An existing file was marked removed.");
        var renamed = Path.Combine(root, "renamed.bin");
        File.Move(path, renamed);
        await Until(() => store.Previous(renamed, true)?.Id == state.Id);
        File.Delete(renamed);
        await Until(() => store.Previous(state.Id) == null);
        var rows = store.Rows(0, HistoryStore.Now + 100, false);
        Check(rows.Any(row => row.Change > 0), "Allocation growth was missing.");
        Check(rows.Any(row => row.Path == renamed && row.Reclaimed == state.Allocated), "Last-link deletion was missing.");
        Check(rows.All(row => row.Program == "미확인"), "Process attribution appeared without ETW.");
    }

    private static async Task LiveProcessHistory()
    {
        using var fixture = new PickerFixture();
        var path = Path.Combine(fixture.Root, "process.bin");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collect = Task.Run(() => DetailCollector.CollectAsync(Guid.NewGuid(), [fixture.Root], stopped.Task, o =>
        {
            if (o.Action == "상태") started.TrySetResult();
            if (o.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && o.Program.Contains(Environment.ProcessId.ToString()))
            {
                if (o.Action == "쓰기 요청") write.TrySetResult();
                if (o.Action == "삭제 요청") delete.TrySetResult();
            }
            return Task.CompletedTask;
        }, () => Task.CompletedTask));
        try
        {
            await Task.WhenAny(started.Task, collect).WaitAsync(TimeSpan.FromSeconds(10));
            if (collect.IsCompleted) await collect;
            for (int i = 0; i < 16 && !write.Task.IsCompleted; i++)
            {
                File.WriteAllBytes(path, new byte[65536]);
                await Task.WhenAny(write.Task, Task.Delay(500));
            }
            await write.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(path);
            await delete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { stopped.TrySetResult(); await collect.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
