using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using WindowsFS.Core;

if (args.Contains("--benchmark", StringComparer.Ordinal)) return await Benchmark();

var tests = new (string Name, Func<Task> Run)[]
{
    ("Nested folder totals and case-insensitive file types", Totals),
    ("Overlapping and duplicate roots are counted once", OverlappingRoots),
    ("Largest-file bound and ordering across workers", LargestFiles),
    ("Zero-sized files and disabled largest-file retention", EmptyAndZeroFiles),
    ("Missing roots are skipped without stopping healthy roots", MissingRoot),
    ("Access-denied directories preserve healthy sibling results", AccessDenied),
    ("Pre-cancellation returns a coherent partial result", PreCancelled),
    ("Cancellation during enumeration preserves aggregate consistency", CancelDuringScan),
    ("Directory junctions are not followed", ReparsePoints),
    ("Concurrent calls do not share mutable scan state", ConcurrentScans),
    ("Invalid options fail before starting work", InvalidOptions)
}.Concat(SnapshotTests.All).ToArray();
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {test.Name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static async Task Totals()
{
    using var fixture = new Fixture();
    fixture.File("first.bin", 100);
    fixture.File("nested/small.TXT", 20);
    fixture.File("nested/deeper/large.txt", 180);
    fixture.Directory("empty");
    var result = await new DiskScanner().ScanAsync([fixture.Root]);
    Equal(300L, result.TotalBytes);
    Equal(3L, result.FileCount);
    Equal(4L, result.FolderCount);
    Equal(0L, result.SkippedCount);
    Equal(300L, result.Roots.Single().TotalBytes);
    Equal(3L, result.Roots.Single().FileCount);
    Equal(200L, result.Roots.Single().Children.First().TotalBytes);
    var text = result.FileTypes.Single(type => type.Extension == ".txt");
    Equal(200L, text.TotalBytes);
    Equal(2L, text.FileCount);
    Check(!result.IsCancelled, "Healthy scan unexpectedly cancelled.");
    CheckConsistency(result);
}

static async Task OverlappingRoots()
{
    using var fixture = new Fixture();
    fixture.File("one/a.bin", 100);
    fixture.File("one/nested/b.bin", 200);
    fixture.File("one-other/c.bin", 400);
    var result = await new DiskScanner().ScanAsync([
        fixture.PathOf("one/nested"), fixture.PathOf("one"), fixture.PathOf("one"),
        fixture.PathOf("one-other")]);
    Equal(700L, result.TotalBytes);
    Equal(3L, result.FileCount);
    Equal(2, result.Roots.Count);
    CheckConsistency(result);
}

static async Task LargestFiles()
{
    using var fixture = new Fixture();
    for (var index = 1; index <= 100; index++) fixture.File($"group-{index % 7}/file-{index}.bin", index);
    var result = await new DiskScanner(new ScanOptions { LargestFileLimit = 7, MaxDegreeOfParallelism = 4 })
        .ScanAsync([fixture.Root]);
    Equal(7, result.LargestFiles.Count);
    Check(result.LargestFiles.Select(file => file.Size).SequenceEqual(new long[] { 100, 99, 98, 97, 96, 95, 94 }),
        "Global top K is incorrect.");
    Equal(5050L, result.TotalBytes);
    CheckConsistency(result);
}

static async Task EmptyAndZeroFiles()
{
    using var fixture = new Fixture();
    fixture.File("empty", 0);
    fixture.Directory("folder");
    var result = await new DiskScanner(new ScanOptions { LargestFileLimit = 0 }).ScanAsync([fixture.Root]);
    Equal(0L, result.TotalBytes);
    Equal(1L, result.FileCount);
    Equal(0, result.LargestFiles.Count);
    Equal(string.Empty, result.FileTypes.Single().Extension);
    var noRoots = await new DiskScanner().ScanAsync([]);
    Equal(0L, noRoots.FolderCount);
    Equal(0, noRoots.Roots.Count);
}

static async Task MissingRoot()
{
    using var fixture = new Fixture();
    fixture.File("good/a.bin", 32);
    var result = await new DiskScanner().ScanAsync([fixture.PathOf("missing"), fixture.PathOf("good")]);
    Equal(32L, result.TotalBytes);
    Equal(1L, result.SkippedCount);
    CheckConsistency(result);
}

static async Task AccessDenied()
{
    if (!OperatingSystem.IsWindows()) return;
    using var fixture = new Fixture();
    fixture.File("allowed.bin", 64);
    fixture.File("blocked/private.bin", 128);
    var blocked = new DirectoryInfo(fixture.PathOf("blocked"));
    var original = blocked.GetAccessControl(AccessControlSections.Access);
    var restricted = blocked.GetAccessControl(AccessControlSections.Access);
    restricted.AddAccessRule(new FileSystemAccessRule(
        new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ListDirectory,
        InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
    try
    {
        blocked.SetAccessControl(restricted);
        // Confirm the fixture really is unreadable before using it to validate skip behavior.
        Throws<UnauthorizedAccessException>(() => System.IO.Directory.EnumerateFileSystemEntries(blocked.FullName).ToArray());
        var result = await new DiskScanner().ScanAsync([fixture.Root]);
        Equal(64L, result.TotalBytes);
        Equal(1L, result.FileCount);
        Equal(1L, result.SkippedCount);
        CheckConsistency(result);
    }
    finally
    {
        // SetAccessControl persists only modified sections. Rehydrate the saved descriptor
        // into a fresh object so restoring the original DACL is an actual write.
        var restored = new DirectorySecurity();
        restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        blocked.SetAccessControl(restored);
    }
}

static async Task PreCancelled()
{
    using var fixture = new Fixture();
    fixture.File("one.bin", 10);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var result = await new DiskScanner().ScanAsync([fixture.Root], cancellationToken: cancellation.Token);
    Check(result.IsCancelled, "Cancellation flag missing.");
    Equal(0L, result.TotalBytes);
    Equal(0L, result.FileCount);
    CheckConsistency(result);
}

static async Task CancelDuringScan()
{
    using var fixture = new Fixture();
    for (var index = 0; index < 2_000; index++) fixture.File($"file-{index}.bin", 1);
    using var cancellation = new CancellationTokenSource();
    var initialReport = true;
    var progress = new InlineProgress<ScanProgress>(report =>
    {
        // Expire the progress throttle before enumeration starts so the next worker flush
        // deterministically cancels an active scan, even on exceptionally fast storage.
        if (initialReport) { initialReport = false; Thread.Sleep(60); }
        if (report.FileCount > 0) cancellation.Cancel();
    });
    var result = await new DiskScanner(new ScanOptions { MaxDegreeOfParallelism = 2,
        ProgressInterval = TimeSpan.FromMilliseconds(50) }).ScanAsync([fixture.Root], progress, cancellation.Token);
    Check(result.IsCancelled, "Cancellation flag missing.");
    Check(result.FileCount > 0 && result.FileCount < 2_000, "Cancellation did not interrupt active enumeration.");
    CheckConsistency(result);
}

static async Task ReparsePoints()
{
    using var fixture = new Fixture();
    fixture.File("data/a.bin", 50);
    var link = fixture.PathOf("data/loop");
    if (OperatingSystem.IsWindows())
    {
        // Junction creation does not require Developer Mode or administrator privileges.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(fixture.PathOf("data"));
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "Could not create test junction.");
    }
    else System.IO.Directory.CreateSymbolicLink(link, fixture.PathOf("data"));
    try
    {
        var result = await new DiskScanner().ScanAsync([fixture.PathOf("data")]);
        Equal(50L, result.TotalBytes);
        Equal(1L, result.FileCount);
        Equal(1L, result.SkippedCount);
        Equal(1L, result.FolderCount);
        var explicitLink = await new DiskScanner().ScanAsync([link]);
        Equal(0L, explicitLink.FileCount);
        Equal(1L, explicitLink.SkippedCount);
    }
    finally { System.IO.Directory.Delete(link); }
}

static async Task ConcurrentScans()
{
    using var fixture = new Fixture();
    fixture.File("first/a.bin", 100);
    fixture.File("second/b.bin", 200);
    var scanner = new DiskScanner();
    var results = await Task.WhenAll(scanner.ScanAsync([fixture.PathOf("first")]),
        scanner.ScanAsync([fixture.PathOf("second")]));
    Equal(100L, results[0].TotalBytes);
    Equal(200L, results[1].TotalBytes);
}

static Task InvalidOptions()
{
    Throws<ArgumentOutOfRangeException>(() => new DiskScanner(new ScanOptions { MaxDegreeOfParallelism = 0 }));
    Throws<ArgumentOutOfRangeException>(() => new DiskScanner(new ScanOptions { LargestFileLimit = -1 }));
    return Task.CompletedTask;
}

static async Task<int> Benchmark()
{
    const int fileCount = 32_768;
    const int directories = 256;
    using var fixture = new Fixture();
    Console.WriteLine($"Creating {fileCount:N0} synthetic files in {directories} directories; fixture is automatically removed.");
    long expectedBytes = 0;
    string[] extensions = ["bin", "txt", "log"];
    for (var index = 0; index < fileCount; index++)
    {
        var size = index * 7919L % 4096;
        fixture.File($"group-{index % directories}/file-{index}.{extensions[index % extensions.Length]}", size);
        expectedBytes += size;
    }
    Console.WriteLine("Metadata-only benchmark; fixture creation warms the OS cache. No cold-cache or whole-disk claim.");
    var options = new ScanOptions();
    Console.WriteLine($"Workers: {options.MaxDegreeOfParallelism}; retained largest-file limit: {options.LargestFileLimit}.");
    foreach (var label in new[] { "First pass (includes JIT)", "Immediate repeat" })
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var result = await new DiskScanner(options).ScanAsync([fixture.Root]);
        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Equal((long)fileCount, result.FileCount);
        Equal(expectedBytes, result.TotalBytes);
        Equal(0L, result.SkippedCount);
        CheckConsistency(result);
        Console.WriteLine($"{label}: {clock.Elapsed.TotalMilliseconds:F1} ms, {fileCount / clock.Elapsed.TotalSeconds:N0} files/s, " +
            $"{allocated / 1048576.0:F2} MiB managed allocations during the scan.");
        GC.KeepAlive(result);
    }
    Console.WriteLine("Counts and byte totals validated for both passes. Timings exclude fixture creation and cleanup.");
    return 0;
}

static void CheckConsistency(ScanResult result)
{
    Equal(result.TotalBytes, result.Roots.Sum(root => root.TotalBytes));
    Equal(result.FileCount, result.Roots.Sum(root => root.FileCount));
    Equal(result.TotalBytes, result.FileTypes.Sum(type => type.TotalBytes));
    Equal(result.FileCount, result.FileTypes.Sum(type => type.FileCount));
    Check(result.LargestFiles.Sum(file => file.Size) <= result.TotalBytes, "Top files exceed total.");
}

static void Equal<T>(T expected, T actual) where T : IEquatable<T>
{
    if (!expected.Equals(actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WindowsFS-tests-" + Guid.NewGuid().ToString("N"));

    public Fixture() => System.IO.Directory.CreateDirectory(Root);
    public string PathOf(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public void Directory(string relative) => System.IO.Directory.CreateDirectory(PathOf(relative));
    public void File(string relative, long size)
    {
        var path = PathOf(relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = System.IO.File.Create(path);
        stream.SetLength(size);
    }
    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Root);
        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(resolved).StartsWith("WindowsFS-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Fixture cleanup path escaped the temporary directory.");
        System.IO.Directory.Delete(resolved, recursive: true);
    }
}
