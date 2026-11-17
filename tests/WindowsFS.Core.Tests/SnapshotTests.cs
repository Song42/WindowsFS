using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsFS.Core;
using WindowsFS.Core.Persistence;

internal static class SnapshotTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Snapshot round trip preserves real scan data and timestamp", RoundTrip),
        ("Snapshot preserves case-distinct paths and rejects exact duplicates", CaseDistinctPaths),
        ("Snapshot restores deep folder trees without recursive JSON", DeepTree),
        ("Missing snapshot does not create a cache", Missing),
        ("Malformed and unsupported snapshots are rejected", InvalidDocuments),
        ("Cancelled and partial saves preserve the last completed scan", CancelledSave),
        ("Failed atomic replacement preserves the previous snapshot", FailedReplacement),
        ("Concurrent snapshot readers never observe a partial file", ConcurrentReaders),
        ("Deleting a snapshot preserves neighboring and analyzed files", DeleteSnapshot),
        ("Deleting a missing snapshot does not create a cache", DeleteMissing),
        ("Deleting a corrupt snapshot does not require loading it", DeleteCorrupt),
        ("Cancelled deletion preserves the completed snapshot", CancelledDelete),
        ("Failed deletion preserves the completed snapshot", FailedDelete),
        ("Deleting after an in-flight save cannot restore the snapshot", DeleteAfterSave)
    ];

    private static async Task RoundTrip()
    {
        using var fixture = new Fixture();
        fixture.File("input/first.bin", 100);
        fixture.File("input/nested/second.TXT", 200);
        fixture.File("other/empty", 0);
        fixture.Directory("input/empty-folder");
        var result = await new DiskScanner().ScanAsync([fixture.PathOf("input"), fixture.PathOf("other")]);
        var saved = new SavedScan(result, null, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)));
        var store = new FileScanResultStore(fixture.PathOf("cache/latest.scan.json.gz"));
        await store.SaveAsync(saved);
        Check(await store.ExistsAsync(), "The completed snapshot was not detected.");
        var loaded = await new FileScanResultStore(fixture.PathOf("cache/latest.scan.json.gz")).LoadAsync();
        Check(loaded is not null, "The completed snapshot was not restored by a new store instance.");
        Equal(saved.ScannedAtUtc, loaded!.ScannedAtUtc);
        Equal(TimeSpan.Zero, loaded.ScannedAtUtc.Offset);
        Equal<string?>(null, loaded.SelectedPath);
        Equal(result.TotalBytes, loaded.Result.TotalBytes);
        Equal(result.FileCount, loaded.Result.FileCount);
        Equal(result.FolderCount, loaded.Result.FolderCount);
        Equal(result.SkippedCount, loaded.Result.SkippedCount);
        Equal(result.Elapsed, loaded.Result.Elapsed);
        Check(!loaded.Result.IsCancelled, "The saved complete scan became a partial result.");
        Check(result.LargestFiles.SequenceEqual(loaded.Result.LargestFiles), "Largest-file metadata changed.");
        Check(result.FileTypes.SequenceEqual(loaded.Result.FileTypes), "Extension aggregates changed.");
        Check(Flatten(result.Roots).SequenceEqual(Flatten(loaded.Result.Roots)), "Folder hierarchy or totals changed.");
    }

    private static async Task DeepTree()
    {
        using var fixture = new Fixture();
        const int depth = 1_024;
        var paths = new string[depth];
        paths[0] = fixture.PathOf("synthetic-tree");
        for (var index = 1; index < depth; index++) paths[index] = Path.Combine(paths[index - 1], "d");
        var node = new FolderNode(paths[^1], 1, 1);
        for (var index = depth - 2; index >= 0; index--) node = new FolderNode(paths[index], 1, 1, [node]);
        var file = new FileRecord(Path.Combine(paths[^1], "leaf.bin"), "leaf.bin", 1, ".bin");
        var result = new ScanResult([node], [file], [new FileTypeSummary(".bin", 1, 1)],
            1, 1, depth, 0, TimeSpan.FromMilliseconds(12), false);
        var store = new FileScanResultStore(fixture.PathOf("cache/deep.scan.json.gz"));
        await store.SaveAsync(new SavedScan(result, paths[0], DateTimeOffset.UtcNow));
        var loaded = (await store.LoadAsync())!;
        Equal(paths[0], loaded.SelectedPath);
        Equal(depth, Flatten(loaded.Result.Roots).Count());
        Check(Flatten(result.Roots).SequenceEqual(Flatten(loaded.Result.Roots)), "Deep hierarchy changed on restore.");
    }

    private static async Task CaseDistinctPaths()
    {
        using var fixture = new Fixture();
        // Windows supports case-sensitive NTFS directories (for example, those used by WSL).
        // Construct that scan result directly so this regression also runs without OS setup.
        var upper = new FolderNode(Path.Combine(fixture.Root, "Cache"), 10, 1);
        var lower = new FolderNode(Path.Combine(fixture.Root, "cache"), 20, 1);
        var root = new FolderNode(fixture.Root, 100, 4, [upper, lower]);
        FileRecord[] files =
        [
            new(Path.Combine(upper.Path, "item.bin"), "item.bin", 10, ".bin"),
            new(Path.Combine(lower.Path, "item.bin"), "item.bin", 20, ".bin"),
            new(Path.Combine(root.Path, "FILE.bin"), "FILE.bin", 30, ".bin"),
            new(Path.Combine(root.Path, "file.bin"), "file.bin", 40, ".bin")
        ];
        var result = new ScanResult([root], files, [new FileTypeSummary(".bin", 100, 4)],
            100, 4, 3, 0, TimeSpan.FromMilliseconds(12), false);
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        await store.SaveAsync(new SavedScan(result, lower.Path, DateTimeOffset.UtcNow));
        var loaded = (await store.LoadAsync())!;
        Equal(lower.Path, loaded.SelectedPath);
        Check(Flatten(result.Roots).SequenceEqual(Flatten(loaded.Result.Roots)), "Case-distinct folders changed on restore.");
        Check(result.LargestFiles.SequenceEqual(loaded.Result.LargestFiles), "Case-distinct files changed on restore.");

        JsonObject original;
        await using (var file = File.OpenRead(path))
        await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            original = (JsonObject)(await JsonNode.ParseAsync(gzip))!;
        Action<JsonObject>[] duplicatePaths =
        [
            document => document["Folders"]![2]!["Path"] = upper.Path,
            document => document["LargestFiles"]![1]!["Path"] = files[0].Path
        ];
        foreach (var corrupt in duplicatePaths)
        {
            var document = (JsonObject)original.DeepClone();
            corrupt(document);
            await using (var file = File.Create(path))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(gzip, document);
            await ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }
    }

    private static async Task Missing()
    {
        using var fixture = new Fixture();
        var directory = fixture.PathOf("not-created");
        var store = new FileScanResultStore(Path.Combine(directory, "latest.scan.json.gz"));
        Check(!await store.ExistsAsync(), "A missing cache was reported as present.");
        Check(await store.LoadAsync() is null, "Missing cache was not treated as empty.");
        Check(!Directory.Exists(directory), "Reading missing cache created directories.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => store.ExistsAsync(cancellation.Token));
        await ThrowsAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
    }

    private static async Task InvalidDocuments()
    {
        using var fixture = new Fixture();
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        await store.SaveAsync(SyntheticScan(fixture.Root, 100));
        JsonObject original;
        await using (var file = File.OpenRead(path))
        await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            original = (JsonObject)(await JsonNode.ParseAsync(gzip))!;

        Action<JsonObject>[] corruptions =
        [
            document => document["Version"] = 99,
            document => document.Remove("ScannedAtUtc"),
            document => document["Folders"] = null,
            document => document["Folders"]![0]!["ParentIndex"] = 0,
            document => document["Folders"]![0]!["Path"] = "",
            document => document["TotalBytes"] = -1,
            document => document["FileCount"] = 999,
            document => document["LargestFiles"]![0]!["Size"] = 999,
            document => document["FileTypes"]![0]!["Extension"] = null
        ];
        foreach (var corrupt in corruptions)
        {
            var document = (JsonObject)original.DeepClone();
            corrupt(document);
            await using (var file = File.Create(path))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(gzip, document);
            await ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }
        await File.WriteAllTextAsync(path, "not a compressed snapshot");
        await ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await store.SaveAsync(SyntheticScan(fixture.Root, 100));
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)]);
        await ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    private static async Task CancelledSave()
    {
        using var fixture = new Fixture();
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        var original = SyntheticScan(fixture.Root, 100);
        await store.SaveAsync(original);
        var before = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(SyntheticScan(fixture.Root, 200), cancellation.Token));
        await ThrowsAsync<ArgumentException>(() => store.SaveAsync(original with
            { Result = original.Result with { IsCancelled = true } }));
        await ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original with
            { Result = original.Result with { TotalBytes = -1 } }));
        var after = await File.ReadAllBytesAsync(path);
        Check(before.SequenceEqual(after), "An interrupted/invalid save replaced the previous scan.");
        Equal(100L, (await store.LoadAsync())!.Result.TotalBytes);
        Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    private static async Task FailedReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        await store.SaveAsync(SyntheticScan(fixture.Root, 100));
        // A reader that denies delete-sharing makes the final Windows rename fail after writing the temp file.
        await using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                await store.SaveAsync(SyntheticScan(fixture.Root, 200));
                throw new InvalidOperationException("Replacing a locked destination unexpectedly succeeded.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Equal(100L, (await store.LoadAsync())!.Result.TotalBytes);
        Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    private static async Task ConcurrentReaders()
    {
        using var fixture = new Fixture();
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        await store.SaveAsync(SyntheticScan(fixture.Root, 100));
        var writers = Enumerable.Range(1, 16)
            .Select(index => new FileScanResultStore(path).SaveAsync(SyntheticScan(fixture.Root, index * 100))).ToArray();
        try
        {
            for (var index = 0; index < 24; index++)
            {
                var loaded = await new FileScanResultStore(path).LoadAsync();
                Check(loaded is not null && loaded.Result.TotalBytes is >= 100 and <= 1600,
                    "A concurrent reader observed missing or partial data.");
                Equal(loaded!.Result.TotalBytes, loaded.Result.Roots.Single().TotalBytes);
                Equal(loaded.Result.TotalBytes, loaded.Result.LargestFiles.Single().Size);
            }
        }
        finally { await Task.WhenAll(writers); }
        Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    private static async Task DeleteSnapshot()
    {
        using var fixture = new Fixture();
        fixture.File("input/sample.bin", 100);
        fixture.File("input/nested/keep.bin", 200);
        fixture.File("cache/sentinel.bin", 300);
        var sourcePath = fixture.PathOf("input/sample.bin");
        var nestedPath = fixture.PathOf("input/nested/keep.bin");
        var sentinelPath = fixture.PathOf("cache/sentinel.bin");
        var before = await File.ReadAllBytesAsync(sourcePath);
        var sentinelBefore = await File.ReadAllBytesAsync(sentinelPath);
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        await store.SaveAsync(SyntheticScan(fixture.PathOf("input"), 100));

        await store.DeleteAsync();

        Check(!File.Exists(path), "The saved snapshot still exists after deletion.");
        Check(!await store.ExistsAsync(), "A deleted snapshot was reported as present.");
        Check(await new FileScanResultStore(path).LoadAsync() is null, "A new store restored a deleted snapshot.");
        var after = await File.ReadAllBytesAsync(sourcePath);
        var sentinelAfter = await File.ReadAllBytesAsync(sentinelPath);
        Check(before.SequenceEqual(after), "An analyzed source file changed.");
        Equal(200L, new FileInfo(nestedPath).Length);
        Check(sentinelBefore.SequenceEqual(sentinelAfter), "A neighboring cache file changed.");
        Check(Directory.Exists(fixture.PathOf("cache")), "Deletion removed the cache directory.");
        await store.DeleteAsync();
    }

    private static async Task DeleteMissing()
    {
        using var fixture = new Fixture();
        var directory = fixture.PathOf("not-created/nested");
        var path = Path.Combine(directory, "latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        await store.DeleteAsync();
        await store.DeleteAsync();
        Check(!Directory.Exists(fixture.PathOf("not-created")), "Deleting a missing cache created directories.");
        Check(await store.LoadAsync() is null, "A missing cache was created by deletion.");
    }

    private static async Task DeleteCorrupt()
    {
        using var fixture = new Fixture();
        var path = fixture.PathOf("corrupt.scan.json.gz");
        await File.WriteAllTextAsync(path, "not a compressed snapshot");
        var store = new FileScanResultStore(path);
        Check(await store.ExistsAsync(), "A corrupt snapshot must still be detected without parsing it.");
        await ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await store.DeleteAsync();
        Check(!await store.ExistsAsync(), "A deleted corrupt snapshot was reported as present.");
        Check(await new FileScanResultStore(path).LoadAsync() is null, "A corrupt snapshot could not be removed.");
    }

    private static async Task CancelledDelete()
    {
        using var fixture = new Fixture();
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        await store.SaveAsync(SyntheticScan(fixture.Root, 100));
        var before = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await ThrowsAsync<OperationCanceledException>(() => store.DeleteAsync(cancellation.Token));

        var after = await File.ReadAllBytesAsync(path);
        Check(before.SequenceEqual(after), "Cancelled deletion changed the snapshot.");
        Equal(100L, (await store.LoadAsync())!.Result.TotalBytes);
        await store.DeleteAsync();
        Check(await store.LoadAsync() is null, "Cancelled deletion left the mutation gate locked.");
    }

    private static async Task FailedDelete()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        await store.SaveAsync(SyntheticScan(fixture.Root, 100));
        var before = await File.ReadAllBytesAsync(path);
        await using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                await store.DeleteAsync();
                throw new InvalidOperationException("Deleting a locked snapshot unexpectedly succeeded.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        var after = await File.ReadAllBytesAsync(path);
        Check(before.SequenceEqual(after), "Failed deletion changed the snapshot.");
        Equal(100L, (await store.LoadAsync())!.Result.TotalBytes);
        await store.DeleteAsync();
        Check(await store.LoadAsync() is null, "Failed deletion left the mutation gate locked.");
    }

    private static async Task DeleteAfterSave()
    {
        using var fixture = new Fixture();
        var path = fixture.PathOf("cache/latest.scan.json.gz");
        var store = new FileScanResultStore(path);
        for (var index = 0; index < 16; index++)
        {
            var save = store.SaveAsync(SyntheticScan(fixture.Root, 100));
            var delete = store.DeleteAsync();
            await delete;
            Check(!File.Exists(path), "Deletion completed while the preceding save was still visible.");
            await save;
            Check(await new FileScanResultStore(path).LoadAsync() is null, "An in-flight save restored a deleted snapshot.");
        }
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    private static SavedScan SyntheticScan(string root, long bytes)
    {
        var result = new ScanResult([new FolderNode(root, bytes, 1)],
            [new FileRecord(Path.Combine(root, "sample.bin"), "sample.bin", bytes, ".bin")],
            [new FileTypeSummary(".bin", bytes, 1)], bytes, 1, 1, 0, TimeSpan.FromMilliseconds(15), false);
        return new SavedScan(result, root, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    }

    private static IEnumerable<(int Depth, string Path, long Bytes, long Files)> Flatten(IReadOnlyList<FolderNode> roots)
    {
        var pending = new Stack<(FolderNode Node, int Depth)>();
        for (var index = roots.Count - 1; index >= 0; index--) pending.Push((roots[index], 0));
        while (pending.TryPop(out var item))
        {
            yield return (item.Depth, item.Node.Path, item.Node.TotalBytes, item.Node.FileCount);
            for (var index = item.Node.Children.Count - 1; index >= 0; index--)
                pending.Push((item.Node.Children[index], item.Depth + 1));
        }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
}
