using WindowsFS.Core.SystemItems;

internal static class SystemItemTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Known Windows locations explain their role with Microsoft sources", KnownLocations),
        ("System information never classifies arbitrary user files or descendants", UnknownLocations),
        ("System location matching preserves literal path and item kind", PathMatching),
        ("System locations respect the configured Windows directory and drive", CustomLocations)
    ];

    private static Task KnownLocations()
    {
        var catalog = new SystemItemCatalog(@"C:\Windows", @"C:\");
        (string Path, bool IsDirectory)[] known =
        [
            (@"C:\Windows", true), (@"C:\Windows\WinSxS", true), (@"C:\Windows\System32", true),
            (@"C:\Windows\System32\DriverStore", true), (@"C:\Windows\SoftwareDistribution", true),
            (@"C:\Windows\Installer", true), (@"C:\Windows\Panther", true), (@"C:\Windows.old", true),
            (@"C:\pagefile.sys", false), (@"C:\hiberfil.sys", false), (@"C:\$Recycle.Bin", true)
        ];
        foreach (var (path, isDirectory) in known)
        {
            var info = catalog.Describe(path, isDirectory);
            Check(info is not null, "A supported location was not recognized.");
            Check(new[] { info!.Title, info.Purpose, info.ManagedBy, info.Necessity, info.CleanupAdvice, info.MatchBasis }
                .All(value => !string.IsNullOrWhiteSpace(value)), "A system explanation is incomplete.");
            var source = new Uri(info.SourceUrl);
            Check(source.Scheme == Uri.UriSchemeHttps && source.Host is "learn.microsoft.com" or "support.microsoft.com",
                "A system explanation lacks an official Microsoft source.");
            Check(info.MatchBasis.Contains("실제 생성자", StringComparison.Ordinal), "The inference limitation is missing.");
            Check(catalog.Describe(path, !isDirectory) is null, "An item of the wrong kind was recognized.");
        }
        return Task.CompletedTask;
    }

    private static Task UnknownLocations()
    {
        var catalog = new SystemItemCatalog(@"C:\Windows", @"C:\");
        string[] unknown =
        [
            @"C:\Users\Example\Documents\System32", @"C:\Users\Example\Documents\pagefile.sys",
            @"C:\Users\Example\Windows\WinSxS", @"C:\Windows\personal.txt", @"C:\Windows\System32\notes.txt",
            @"C:\Windows\WinSxS\custom", @"C:\Windows\System32\DriverStore\personal.txt",
            @"C:\Windows.old\Users\Example\Documents", @"C:\$Recycle.Bin\private-file",
            @"C:\Windows.backup\System32", @"C:\Windows\System32-copy", @"D:\Windows\System32",
            @"C:\Program Files", @"C:\Users", @"C:\unrecognized.sys"
        ];
        foreach (var path in unknown)
        {
            Check(catalog.Describe(path, true) is null, "An arbitrary folder inherited a system classification.");
            Check(catalog.Describe(path, false) is null, "An arbitrary file inherited a system classification.");
        }
        return Task.CompletedTask;
    }

    private static Task PathMatching()
    {
        var catalog = new SystemItemCatalog(@"C:\Windows", @"C:\");
        var expected = catalog.Describe(@"C:\Windows\System32", true);
        foreach (var path in new[] { @"c:\WINDOWS\system32\", "C:/Windows/System32", @"\\?\C:\Windows\System32" })
            Check(Equals(expected, catalog.Describe(path, true)), "An ordinary Windows path spelling did not match.");
        string?[] ambiguous =
        [
            null, "", @"Windows\System32", @"C:Windows\System32", @"\Windows\System32",
            @"C:\Windows\System32.", @"C:\Windows\System32 ", @"\\?\C:\Windows\System32.",
            @"C:\Windows\..\Windows\System32", @"C:\Windows\.\System32", @"C:\Windows\\System32",
            @"C:\Windows\System32:stream", @"\\server\share\Windows\System32", @"\\.\C:\Windows\System32",
            "\\\\?\\C:/Windows/System32", "C:\\Windows\\System32\0"
        ];
        foreach (var path in ambiguous)
            Check(catalog.Describe(path, true) is null, "An ambiguous path was classified as a system location.");
        Check(catalog.Describe(@"C:\pagefile.sys\", false) is null, "A directory spelling was recognized as a system file.");
        return Task.CompletedTask;
    }

    private static Task CustomLocations()
    {
        var catalog = new SystemItemCatalog(@"X:\OperatingSystem\", @"X:\");
        Check(catalog.Describe(@"X:\OperatingSystem\System32", true) is not null, "The configured Windows directory was ignored.");
        Check(catalog.Describe(@"X:\hiberfil.sys", false) is not null, "The configured system drive was ignored.");
        Check(catalog.Describe(@"X:\Windows.old", true) is not null, "The old-installation location did not follow the system drive.");
        Check(catalog.Describe(@"C:\Windows\System32", true) is null, "The catalog hardcoded a C drive Windows folder.");
        Check(catalog.Describe(@"X:\Windows\System32", true) is null, "An unconfigured Windows folder was recognized.");
        Check(catalog.Describe(@"X:\OperatingSystem\pagefile.sys", false) is null, "A system-drive file was matched inside the Windows directory.");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
