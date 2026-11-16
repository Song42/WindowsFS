using WindowsFS.Core;

internal static class ScanPathTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Literal NTFS dot and space names complete without revisiting parents", LiteralNames),
        ("Literal scan roots preserve names and deduplicate extended aliases", LiteralRoots),
        ("Relative roots resolve navigation without stripping literal names", RelativeRoots),
        ("Long paths preserve unusual intermediate directory names", LongPaths)
    ];

    private static async Task LiteralNames()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LiteralFixture();
        fixture.File("root.bin", 1);
        string[] names = [". ", ".. ", "...", "normal.", "normal "];
        long bytes = 1;
        foreach (var name in names)
        {
            fixture.File(Path.Join(name, "direct.bin"), 10);
            fixture.File(Path.Join(name, ". ", "nested.bin"), 20);
            bytes += 30;
        }

        foreach (var workers in new[] { 1, 4, 16 })
        {
            var result = await Complete([fixture.Root], workers);
            Equal(11L, result.FileCount);
            Equal(11L, result.FolderCount);
            Equal(bytes, result.TotalBytes);
            Equal(0L, result.SkippedCount);
            Equal(bytes, result.Roots.Single().TotalBytes);
            Check(result.Roots.Single().Children.Select(folder => folder.Name).Order()
                .SequenceEqual(names.Order()), "A literal directory name changed during enumeration.");
            Equal(11, result.LargestFiles.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Check(result.LargestFiles.All(file => !file.Path.StartsWith(@"\\?\", StringComparison.Ordinal)),
                "Native access prefixes should not leak into ordinary display paths.");
        }
    }

    private static async Task LiteralRoots()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LiteralFixture();
        foreach (var name in new[] { ". ", ".. ", "...", "selected. " })
        {
            fixture.File(Path.Join(name, "leaf.bin"), 7);
            var selected = Path.Join(fixture.Root, name);
            var result = await Complete([selected, LiteralFixture.Native(selected)]);
            Equal(1, result.Roots.Count);
            Equal(selected, result.Roots.Single().Path);
            Equal(1L, result.FolderCount);
            Equal(1L, result.FileCount);
            Equal(7L, result.TotalBytes);
            Equal(0L, result.SkippedCount);
        }
    }

    private static async Task RelativeRoots()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LiteralFixture();
        fixture.File(Path.Join("selected. ", "leaf.bin"), 9);
        var selected = Path.Join(fixture.Root, "selected. ");
        // GetRelativePath normalizes its target itself, so relativize the ordinary parent first.
        var relative = Path.Join(Path.GetRelativePath(Environment.CurrentDirectory, fixture.Root), "selected. ");
        var paths = new List<string>
        {
            relative,
            Path.Join(fixture.Root, "not-created", "..", ".", "selected. "),
            selected.Replace('\\', '/')
        };
        var currentDrive = Path.GetPathRoot(Environment.CurrentDirectory);
        if (currentDrive is { Length: 3 } && string.Equals(currentDrive, Path.GetPathRoot(selected), StringComparison.OrdinalIgnoreCase))
            paths.Add(currentDrive[..2] + relative);
        foreach (var path in paths)
        {
            var result = await Complete([path]);
            Equal(selected, result.Roots.Single().Path);
            Equal(1L, result.FileCount);
            Equal(9L, result.TotalBytes);
            Equal(0L, result.SkippedCount);
        }
    }

    private static async Task LongPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LiteralFixture();
        var relative = ". ";
        for (var index = 0; index < 24; index++) relative = Path.Join(relative, $"segment-{index:D2}-long-name");
        relative = Path.Join(relative, "...", "leaf.bin");
        fixture.File(relative, 13);
        var result = await Complete([fixture.Root]);
        Equal(1L, result.FileCount);
        Equal(27L, result.FolderCount);
        Equal(13L, result.TotalBytes);
        Equal(0L, result.SkippedCount);
        Equal(Path.Join(fixture.Root, relative), result.LargestFiles.Single().Path);
    }

    private static async Task<ScanResult> Complete(IEnumerable<string> roots, int workers = 4)
    {
        using var cancellation = new CancellationTokenSource();
        var scan = new DiskScanner(new ScanOptions { MaxDegreeOfParallelism = workers })
            .ScanAsync(roots, cancellationToken: cancellation.Token);
        try
        {
            var result = await scan.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!result.IsCancelled, "A healthy scan required cancellation to finish.");
            return result;
        }
        catch (TimeoutException)
        {
            // A test guard, not a production scan timeout. Stop a regressed loop before fixture cleanup.
            cancellation.Cancel();
            await scan.WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("Literal directory traversal did not finish naturally.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual) where T : notnull =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");

    private sealed class LiteralFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WindowsFS-literal-path-" + Guid.NewGuid().ToString("N"));
        public LiteralFixture() => Directory.CreateDirectory(Root);
        public static string Native(string path) => @"\\?\" + path;
        public void File(string relative, long bytes)
        {
            var path = Native(Path.Join(Root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = System.IO.File.Create(path);
            file.SetLength(bytes);
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("WindowsFS-literal-path-", StringComparison.Ordinal))
                throw new InvalidOperationException("Literal fixture cleanup escaped the temporary directory.");
            Directory.Delete(Native(resolved), recursive: true);
        }
    }
}
