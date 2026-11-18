namespace WindowsFS.Core.Persistence;

/// <summary>A completed scan and the original selection needed to restore the workspace.</summary>
public sealed record SavedScan(ScanResult Result, string? SelectedPath, DateTimeOffset ScannedAtUtc)
{
    public FolderAnswer[] Answers { get; init; } = [];
}

public sealed record FolderAnswer(string Path, string Question, string Text);

public interface IScanResultStore
{
    /// <summary>Checks whether the snapshot path exists without reading or validating its contents.</summary>
    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns null when no snapshot exists; invalid snapshots throw InvalidDataException.</summary>
    Task<SavedScan?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the last completed scan. Partial scans are not persisted.</summary>
    Task SaveAsync(SavedScan snapshot, CancellationToken cancellationToken = default);

    /// <summary>Deletes the saved snapshot only. An absent snapshot is already deleted.</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);
}
