namespace WindowsFS.Core;

/// <summary>Preserves literal filesystem names while separating Windows access and display paths.</summary>
internal static class ScanPath
{
    public static string NormalizeRoot(string path)
    {
        if (!OperatingSystem.IsWindows())
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        path = ToDisplayPath(path.Replace('/', '\\'));
        if (!Path.IsPathFullyQualified(path))
        {
            if (path.StartsWith('\\'))
                path = Path.Join(Path.GetPathRoot(Environment.CurrentDirectory)!, path.TrimStart('\\'));
            else if (path.Length >= 2 && path[1] == ':')
            {
                // Resolve drive-relative input using Windows' current directory for that drive.
                // Only the safe base is passed to GetFullPath; literal input components stay intact.
                var driveDirectory = Path.GetFullPath(path[..2] + ".");
                path = Path.Join(driveDirectory, path.AsSpan(2));
            }
            else path = Path.Join(Environment.CurrentDirectory, path);
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) throw new ArgumentException("The scan path has no filesystem root.", nameof(path));
        var segments = new List<string>();
        foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        return Path.TrimEndingDirectorySeparator(Path.Join(root, string.Join('\\', segments)));
    }

    public static string ForFileSystemAccess(string path)
    {
        if (!OperatingSystem.IsWindows() || IsDevicePath(path)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    private static string ToDisplayPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.Length >= 7 && (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal)) && path[5] == ':' && path[6] == '\\')
            return path[4..];
        return path;
    }

    private static bool IsDevicePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);
}
