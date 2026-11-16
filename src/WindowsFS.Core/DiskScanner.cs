using System.Diagnostics;
using System.IO.Enumeration;
using System.Security;
using System.Threading.Channels;

namespace WindowsFS.Core;

/// <summary>
/// Enumerates directory metadata with bounded worker concurrency. No file content is opened.
/// Results contain logical lengths, not allocated disk space. Hard links are counted per path.
/// </summary>
public sealed class DiskScanner : IDiskScanner
{
    private readonly ScanOptions _options;

    public DiskScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
        if (_options.MaxDegreeOfParallelism is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Worker count must be between 1 and 64.");
        if (_options.LargestFileLimit is < 0 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Largest file limit must be between 0 and 100,000.");
        if (_options.ProgressInterval < TimeSpan.FromMilliseconds(50))
            throw new ArgumentOutOfRangeException(nameof(options), "Progress interval must be at least 50 ms.");
    }

    public Task<ScanResult> ScanAsync(IEnumerable<string> roots, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = NormalizeRoots(roots);
        return new ScanState(_options, normalized, progress, cancellationToken).RunAsync();
    }

    private static List<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var candidates = roots.Select(root =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            return ScanPath.NormalizeRoot(root);
        }).OrderBy(static root => root.Length);
        var normalized = new List<string>();
        foreach (var candidate in candidates)
        {
            if (normalized.Any(root => candidate.Equals(root, comparison) ||
                candidate.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                    comparison))) continue;
            normalized.Add(candidate);
        }
        return normalized;
    }

    private sealed class ScanState
    {
        private readonly ScanOptions _options;
        private readonly IProgress<ScanProgress>? _progress;
        private readonly CancellationToken _cancellationToken;
        private readonly Channel<FolderNode> _queue = Channel.CreateUnbounded<FolderNode>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = false, AllowSynchronousContinuations = false });
        private readonly List<FolderNode> _roots;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _pendingDirectories;
        private long _totalBytes;
        private long _fileCount;
        private long _folderCount;
        private long _skippedCount;
        private long _lastReport;
        private string _currentPath = string.Empty;

        public ScanState(ScanOptions options, List<string> roots, IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            _options = options;
            _progress = progress;
            _cancellationToken = cancellationToken;
            _roots = roots.Select(static root => new FolderNode(root)).ToList();
            _pendingDirectories = _folderCount = _roots.Count;
            foreach (var root in _roots) _queue.Writer.TryWrite(root);
            if (_roots.Count == 0) _queue.Writer.TryComplete();
        }

        public async Task<ScanResult> RunAsync()
        {
            Report(force: true);
            var workers = Enumerable.Range(0, _options.MaxDegreeOfParallelism)
                .Select(_ => new Worker(this)).ToArray();
            // Task.Run keeps all synchronous OS enumeration away from the calling UI thread.
            await Task.WhenAll(workers.Select(worker => Task.Run(worker.RunAsync))).ConfigureAwait(false);

            // Iterative post-order traversal also supports very deep directory trees.
            var stack = new Stack<(FolderNode Node, bool Visited)>();
            foreach (var root in _roots) stack.Push((root, false));
            while (stack.TryPop(out var item))
            {
                if (!item.Visited)
                {
                    stack.Push((item.Node, true));
                    foreach (var child in item.Node.Children) stack.Push((child, false));
                    continue;
                }
                foreach (var child in item.Node.Children)
                {
                    item.Node.TotalBytes += child.TotalBytes;
                    item.Node.FileCount += child.FileCount;
                }
                item.Node.SortChildren();
            }
            _roots.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
            var files = new PriorityQueue<FileRecord, long>();
            var types = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var worker in workers)
            {
                foreach (var pair in worker.LargestFiles.UnorderedItems)
                    KeepLargest(files, pair.Element, _options.LargestFileLimit);
                foreach (var (extension, aggregate) in worker.FileTypes)
                {
                    types.TryGetValue(extension, out var existing);
                    types[extension] = (existing.Bytes + aggregate.Bytes, existing.Count + aggregate.Count);
                }
            }
            _clock.Stop();
            Report(force: true);
            return new ScanResult(_roots,
                files.UnorderedItems.Select(static item => item.Element).OrderByDescending(static file => file.Size)
                    .ThenBy(static file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                types.Select(static pair => new FileTypeSummary(pair.Key, pair.Value.Bytes, pair.Value.Count))
                    .OrderByDescending(static type => type.TotalBytes).ThenBy(static type => type.Extension).ToArray(),
                _totalBytes, _fileCount, _folderCount, _skippedCount, _clock.Elapsed, _cancellationToken.IsCancellationRequested);
        }

        private void Report(bool force = false)
        {
            if (_progress is null) return;
            var now = _clock.ElapsedMilliseconds;
            var previous = Volatile.Read(ref _lastReport);
            if (!force && (now - previous < _options.ProgressInterval.TotalMilliseconds ||
                Interlocked.CompareExchange(ref _lastReport, now, previous) != previous)) return;
            if (force) Interlocked.Exchange(ref _lastReport, now);
            _progress.Report(new ScanProgress(Interlocked.Read(ref _totalBytes), Interlocked.Read(ref _fileCount),
                Interlocked.Read(ref _folderCount), Interlocked.Read(ref _skippedCount), _clock.Elapsed,
                Volatile.Read(ref _currentPath)));
        }

        private static void KeepLargest(PriorityQueue<FileRecord, long> heap, FileRecord file, int limit)
        {
            if (limit == 0) return;
            if (heap.Count < limit) heap.Enqueue(file, file.Size);
            else if (heap.TryPeek(out _, out var minimum) && file.Size > minimum)
                heap.DequeueEnqueue(file, file.Size);
        }

        private sealed class Worker(ScanState state)
        {
            private long _pendingBytes;
            private long _pendingFiles;
            private long _pendingSkipped;
            private int _entriesSinceFlush;
            private FolderNode _current = null!;
            public PriorityQueue<FileRecord, long> LargestFiles { get; } = new();
            public Dictionary<string, (long Bytes, long Count)> FileTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

            public async Task RunAsync()
            {
                try
                {
                    while (await state._queue.Reader.WaitToReadAsync(state._cancellationToken).ConfigureAwait(false))
                    {
                        while (state._queue.Reader.TryRead(out var folder))
                        {
                            state._cancellationToken.ThrowIfCancellationRequested();
                            _current = folder;
                            Volatile.Write(ref state._currentPath, folder.Path);
                            try { ReadDirectory(folder); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
                            {
                                _pendingSkipped++;
                            }
                            finally
                            {
                                Flush();
                                if (Interlocked.Decrement(ref state._pendingDirectories) == 0)
                                    state._queue.Writer.TryComplete();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (state._cancellationToken.IsCancellationRequested) { }
                catch (Exception error)
                {
                    // Unblock readers if a non-I/O failure (for example a progress consumer) aborts a worker.
                    state._queue.Writer.TryComplete(error);
                    throw;
                }
                finally { Flush(); }
            }

            private void ReadDirectory(FolderNode folder)
            {
                // Recheck queued directories: a directory may have changed since its parent was read.
                // Win32 normalization can turn a literal NTFS name such as ". " or "..."
                // into its parent. Always open the exact enumerated path, or traversal can loop.
                var enumerationPath = ScanPath.ForFileSystemAccess(folder.Path);
                var attributes = File.GetAttributes(enumerationPath);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                {
                    _pendingSkipped++;
                    return;
                }
                var entries = new FileSystemEnumerable<bool>(enumerationPath, Visit,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = 0,
                        ReturnSpecialDirectories = false,
                        BufferSize = 64 * 1024
                    });
                foreach (var _ in entries) state._cancellationToken.ThrowIfCancellationRequested();
            }

            private bool Visit(ref FileSystemEntry entry)
            {
                state._cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                {
                    _pendingSkipped++;
                }
                else if (entry.IsDirectory)
                {
                    var child = new FolderNode(Path.Join(_current.Path, entry.FileName));
                    _current.AddChild(child);
                    Interlocked.Increment(ref state._pendingDirectories);
                    Interlocked.Increment(ref state._folderCount);
                    state._queue.Writer.TryWrite(child);
                }
                else
                {
                    var bytes = Math.Max(0, entry.Length);
                    _current.TotalBytes += bytes;
                    _current.FileCount++;
                    _pendingBytes += bytes;
                    _pendingFiles++;
                    // Span lookup reuses an existing extension key: common file types do not
                    // allocate a fresh string for every one of potentially millions of files.
                    var extensionSpan = Path.GetExtension(entry.FileName);
                    var types = FileTypes.GetAlternateLookup<ReadOnlySpan<char>>();
                    if (!types.TryGetValue(extensionSpan, out var extension, out var aggregate))
                        extension = extensionSpan.ToString().ToLowerInvariant();
                    FileTypes[extension] = (aggregate.Bytes + bytes, aggregate.Count + 1);
                    if (LargestFiles.Count < state._options.LargestFileLimit ||
                        (LargestFiles.TryPeek(out _, out var minimum) && bytes > minimum))
                    {
                        var path = Path.Join(_current.Path, entry.FileName);
                        KeepLargest(LargestFiles, new FileRecord(path, Path.GetFileName(path), bytes, extension),
                            state._options.LargestFileLimit);
                    }
                }
                if (++_entriesSinceFlush >= 256) Flush();
                return true;
            }

            private void Flush()
            {
                Interlocked.Add(ref state._totalBytes, _pendingBytes);
                Interlocked.Add(ref state._fileCount, _pendingFiles);
                Interlocked.Add(ref state._skippedCount, _pendingSkipped);
                _pendingBytes = _pendingFiles = _pendingSkipped = 0;
                _entriesSinceFlush = 0;
                state.Report();
            }
        }
    }
}
