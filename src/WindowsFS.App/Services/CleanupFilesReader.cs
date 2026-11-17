using System.IO;
using WindowsFS.Core;
using WindowsFS.Core.Cleanup;

namespace WindowsFS.App.Services;

public sealed record CleanupFilesResult(IReadOnlyList<FileRecord> Files, int SkippedLocations, bool Limited, DateTimeOffset ReadAt);
public interface ICleanupFilesReader
{
    Task<CleanupFilesResult> ReadAsync(CleanupBucket bucket, CancellationToken token);
}

/// <summary>Reads immediate file metadata from saved bucket locations only; never discovers new subtrees or opens contents.</summary>
public sealed class CleanupFilesReader(IReadOnlyFileBrowser browser) : ICleanupFilesReader
{
    public const int MaximumFiles = 100_000;
    public async Task<CleanupFilesResult> ReadAsync(CleanupBucket bucket, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        var files = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        var skipped = 0;
        var limited = false;
        try
        {
            foreach (var location in bucket.Locations.DistinctBy(item => item.Path, StringComparer.Ordinal))
            {
                budget.Token.ThrowIfCancellationRequested();
                try
                {
                    var rows = await browser.ReadAsync(location.Path, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        // A browser implementation cannot widen the selected bucket's immediate-folder scope.
                        if (!string.Equals(Path.GetDirectoryName(row.Path), Path.TrimEndingDirectorySeparator(location.Path), StringComparison.Ordinal)) continue;
                        if (files.Count >= MaximumFiles) { limited = true; break; }
                        files.TryAdd(row.Path, row);
                    }
                    if (limited) break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                { skipped++; }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { limited = true; }
        token.ThrowIfCancellationRequested();
        var sorted = await Task.Run(() => files.Values.OrderByDescending(file => file.Size).ThenBy(file => file.Path, StringComparer.Ordinal).ToArray(), token).ConfigureAwait(false);
        return new(sorted, skipped, limited, DateTimeOffset.Now);
    }
}
