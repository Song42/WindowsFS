namespace WindowsFS.Core;

/// <summary>Replaceable metadata-only scanner contract for the application shell.</summary>
public interface IDiskScanner
{
    Task<ScanResult> ScanAsync(IEnumerable<string> roots, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record ScanOptions
{
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    public int LargestFileLimit { get; init; } = 1_000;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}

public sealed class FolderNode
{
    private readonly List<FolderNode> _children;

    public FolderNode(string path, long totalBytes = 0, long fileCount = 0,
        IReadOnlyList<FolderNode>? children = null)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(Name)) Name = path;
        TotalBytes = totalBytes;
        FileCount = fileCount;
        _children = children?.ToList() ?? [];
    }

    public string Path { get; }
    public string Name { get; }
    public long TotalBytes { get; internal set; }
    public long FileCount { get; internal set; }
    public IReadOnlyList<FolderNode> Children => _children;

    internal void AddChild(FolderNode child) => _children.Add(child);
    internal void SortChildren() => _children.Sort(static (a, b) =>
    {
        var sizeOrder = b.TotalBytes.CompareTo(a.TotalBytes);
        return sizeOrder != 0 ? sizeOrder : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    });
}

public sealed record FileRecord(string Path, string Name, long Size, string Extension);
public sealed record FileTypeSummary(string Extension, long TotalBytes, long FileCount);
public sealed record ScanProgress(long TotalBytes, long FileCount, long FolderCount,
    long SkippedCount, TimeSpan Elapsed, string CurrentPath);
public sealed record ScanResult(IReadOnlyList<FolderNode> Roots, IReadOnlyList<FileRecord> LargestFiles,
    IReadOnlyList<FileTypeSummary> FileTypes, long TotalBytes, long FileCount, long FolderCount,
    long SkippedCount, TimeSpan Elapsed, bool IsCancelled);
