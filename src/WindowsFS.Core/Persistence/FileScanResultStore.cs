using System.IO.Compression;
using System.Text.Json;

namespace WindowsFS.Core.Persistence;

/// <summary>
/// Stores one versioned, compressed snapshot at an explicitly supplied local path.
/// Folder records are flat so deeply nested directory trees never require recursive JSON.
/// </summary>
public sealed class FileScanResultStore : IScanResultStore
{
    private const int CurrentVersion = 1;
    private const long MaximumJsonBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly string _filePath;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public FileScanResultStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = File.GetAttributes(_filePath);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }, cancellationToken);

    // JSON validation, tree conversion, compression, and durable flushing stay off the UI thread.
    public Task<SavedScan?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCoreAsync(cancellationToken), cancellationToken);

    public Task SaveAsync(SavedScan snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Result);
        if (snapshot.Result.IsCancelled)
            throw new ArgumentException("Only completed scans can replace the saved snapshot.", nameof(snapshot));
        return MutateAsync(() => SaveCoreAsync(snapshot, cancellationToken), cancellationToken);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Delete(_filePath); }
            catch (DirectoryNotFoundException) { }
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task MutateAsync(Func<Task> mutation, CancellationToken cancellationToken)
    {
        // Acquire before scheduling work so an in-flight save finishes before a later delete.
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(mutation, cancellationToken).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    private async Task<SavedScan?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var compressed = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
            using var limited = new SizeLimitedStream(compressed, MaximumJsonBytes);
            var document = await JsonSerializer.DeserializeAsync<SnapshotDocument>(limited, JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (document is null) throw InvalidSnapshot();
            Validate(document, cancellationToken);
            var nodes = new FolderNode[document.Folders.Length];
            var roots = new List<FolderNode>();
            for (var index = 0; index < nodes.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = document.Folders[index];
                var node = new FolderNode(folder.Path, folder.TotalBytes, folder.FileCount);
                nodes[index] = node;
                if (folder.ParentIndex < 0) roots.Add(node);
                else nodes[folder.ParentIndex].AddChild(node);
            }
            return new SavedScan(new ScanResult(roots, document.LargestFiles, document.FileTypes,
                document.TotalBytes, document.FileCount, document.FolderCount, document.SkippedCount,
                TimeSpan.FromTicks(document.ElapsedTicks), false), document.SelectedPath, document.ScannedAtUtc)
                { Answers = document.Answers };
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is JsonException or NotSupportedException or OverflowException)
        {
            throw InvalidSnapshot(error);
        }
    }

    private async Task SaveCoreAsync(SavedScan snapshot, CancellationToken cancellationToken)
    {
        var document = Flatten(snapshot, cancellationToken);
        Validate(document, cancellationToken);
        var directory = Path.GetDirectoryName(_filePath)!;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        // Each writer has its own sibling file. Readers see either the old or the new complete file.
        var temporaryPath = Path.Combine(directory, Path.GetFileName(_filePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var compressed = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                {
                    using var limited = new SizeLimitedStream(compressed, MaximumJsonBytes);
                    await JsonSerializer.SerializeAsync(limited, document, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
            }
            // Cancellation before this commit point preserves the previous valid snapshot.
            await ReplaceAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task ReplaceAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, _filePath, overwrite: true);
                return;
            }
            catch (Exception error) when (attempt < 5 && error is IOException or UnauthorizedAccessException)
            {
                // Windows can briefly deny a rename while another process replaces or inspects
                // the destination. Retry the atomic operation; never truncate the saved file.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static SnapshotDocument Flatten(SavedScan snapshot, CancellationToken cancellationToken)
    {
        var result = snapshot.Result;
        if (result.Roots is null || result.LargestFiles is null || result.FileTypes is null) throw InvalidSnapshot();
        var folders = new List<FlatFolder>();
        var visited = new HashSet<FolderNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(FolderNode Node, int ParentIndex)>();
        for (var index = result.Roots.Count - 1; index >= 0; index--) pending.Push((result.Roots[index], -1));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Node is null || !visited.Add(item.Node)) throw InvalidSnapshot();
            var index = folders.Count;
            folders.Add(new FlatFolder(item.ParentIndex, item.Node.Path, item.Node.TotalBytes, item.Node.FileCount));
            for (var child = item.Node.Children.Count - 1; child >= 0; child--)
                pending.Push((item.Node.Children[child], index));
        }
        return new SnapshotDocument
        {
            Version = CurrentVersion,
            Answers = snapshot.Answers,
            ScannedAtUtc = snapshot.ScannedAtUtc.ToUniversalTime(),
            SelectedPath = snapshot.SelectedPath,
            Folders = folders.ToArray(),
            LargestFiles = result.LargestFiles.ToArray(),
            FileTypes = result.FileTypes.ToArray(),
            TotalBytes = result.TotalBytes,
            FileCount = result.FileCount,
            FolderCount = result.FolderCount,
            SkippedCount = result.SkippedCount,
            ElapsedTicks = result.Elapsed.Ticks
        };
    }

    private static void Validate(SnapshotDocument document, CancellationToken cancellationToken)
    {
        if (document.Version != CurrentVersion || document.ScannedAtUtc == default ||
            document.SelectedPath is not null && string.IsNullOrWhiteSpace(document.SelectedPath) ||
            document.Folders is null || document.LargestFiles is null || document.FileTypes is null ||
            document.TotalBytes < 0 || document.FileCount < 0 || document.FolderCount < 0 ||
            document.SkippedCount < 0 || document.ElapsedTicks < 0 ||
            document.FolderCount != document.Folders.Length || document.LargestFiles.LongLength > document.FileCount)
            throw InvalidSnapshot();

        // NTFS directories can opt into case-sensitive names. Preserve the exact paths returned
        // by enumeration: differently cased siblings are distinct entries even on Windows.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var childBytes = new long[document.Folders.Length];
        var childFiles = new long[document.Folders.Length];
        long rootBytes = 0, rootFiles = 0;
        try
        {
            checked
            {
                for (var index = 0; index < document.Folders.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var folder = document.Folders[index];
                    if (folder is null || string.IsNullOrWhiteSpace(folder.Path) || !paths.Add(folder.Path) ||
                        folder.TotalBytes < 0 || folder.FileCount < 0 || folder.ParentIndex < -1 || folder.ParentIndex >= index)
                        throw InvalidSnapshot();
                    if (folder.ParentIndex < 0) { rootBytes += folder.TotalBytes; rootFiles += folder.FileCount; }
                    else
                    {
                        childBytes[folder.ParentIndex] += folder.TotalBytes;
                        childFiles[folder.ParentIndex] += folder.FileCount;
                    }
                }
                if (rootBytes != document.TotalBytes || rootFiles != document.FileCount) throw InvalidSnapshot();
                if (document.Answers is null || document.Answers.Any(a => a is null || string.IsNullOrWhiteSpace(a.Path)
                    || (a.IsFile ? !IsFileInIndexedFolder(a.Path, paths) : !paths.Contains(a.Path))
                    || string.IsNullOrWhiteSpace(a.Question) || string.IsNullOrWhiteSpace(a.Text))) throw InvalidSnapshot();
                for (var index = 0; index < document.Folders.Length; index++)
                    if (childBytes[index] > document.Folders[index].TotalBytes || childFiles[index] > document.Folders[index].FileCount)
                        throw InvalidSnapshot();

                var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long typeBytes = 0, typeFiles = 0;
                foreach (var type in document.FileTypes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (type is null || type.Extension is null || !extensions.Add(type.Extension) || type.TotalBytes < 0 || type.FileCount < 0)
                        throw InvalidSnapshot();
                    typeBytes += type.TotalBytes; typeFiles += type.FileCount;
                }
                if (typeBytes != document.TotalBytes || typeFiles != document.FileCount) throw InvalidSnapshot();
                paths.Clear();
                long retainedBytes = 0;
                foreach (var file in document.LargestFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file is null || string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Name) ||
                        file.Extension is null || file.Size < 0 || !paths.Add(file.Path)) throw InvalidSnapshot();
                    retainedBytes += file.Size;
                }
                if (retainedBytes > document.TotalBytes) throw InvalidSnapshot();
            }
        }
        catch (OverflowException error) { throw InvalidSnapshot(error); }
    }

    private static bool IsFileInIndexedFolder(string path, HashSet<string> folders) =>
        Path.GetFileName(path) is not ("" or "." or "..") && Path.GetDirectoryName(path) is { } parent && folders.Contains(parent);

    private static InvalidDataException InvalidSnapshot(Exception? inner = null) =>
        new("The saved scan is invalid or uses an unsupported format.", inner);

    private sealed record FlatFolder(int ParentIndex, string Path, long TotalBytes, long FileCount);

    private sealed class SnapshotDocument
    {
        public SnapshotDocument() { }
        public required int Version { get; init; }
        public FolderAnswer[] Answers { get; init; } = [];
        public required DateTimeOffset ScannedAtUtc { get; init; }
        public required string? SelectedPath { get; init; }
        public required FlatFolder[] Folders { get; init; }
        public required FileRecord[] LargestFiles { get; init; }
        public required FileTypeSummary[] FileTypes { get; init; }
        public required long TotalBytes { get; init; }
        public required long FileCount { get; init; }
        public required long FolderCount { get; init; }
        public required long SkippedCount { get; init; }
        public required long ElapsedTicks { get; init; }
    }

    /// <summary>Bounds expanded JSON on both save and load without buffering it in memory.</summary>
    private sealed class SizeLimitedStream(Stream inner, long limit) : Stream
    {
        private long _transferred;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Count(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Count(read);
            return read;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { Count(buffer.Length); inner.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Count(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        private void Count(int count)
        {
            _transferred += count;
            if (_transferred > limit) throw new InvalidDataException("The saved scan exceeds the supported snapshot size.");
        }
        // The surrounding scope owns the gzip stream and finalizes it before the durable file flush.
    }
}
