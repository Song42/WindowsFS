namespace WindowsFS.Core.Persistence;

/// <summary>A completed scan and the original selection needed to restore the workspace.</summary>
public sealed record SavedScan(ScanResult Result, string? SelectedPath, DateTimeOffset ScannedAtUtc);

public interface IScanResultStore
{
    /// <summary>Returns null when no snapshot exists; invalid snapshots throw InvalidDataException.</summary>
    Task<SavedScan?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the last completed scan. Partial scans are not persisted.</summary>
    Task SaveAsync(SavedScan snapshot, CancellationToken cancellationToken = default);
}
