using System.IO;
using System.IO.Enumeration;
using WindowsFS.Core;

namespace WindowsFS.App.Services;

public interface IReadOnlyFileBrowser
{
    Task<IReadOnlyList<FileRecord>> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Reads only immediate file metadata, without opening contents or traversing subdirectories.</summary>
public sealed class ReadOnlyFileBrowser : IReadOnlyFileBrowser
{
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<IReadOnlyList<FileRecord>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Read(path, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<FileRecord> Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute folder path is required.", nameof(path));
        var normalized = ScanPath.NormalizeRoot(path);
        var accessPath = ScanPath.ForFileSystemAccess(normalized);
        // Saved paths may now point through a junction or offline ancestor. Never traverse those.
        for (string? current = normalized; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(ScanPath.ForFileSystemAccess(current));
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
                throw new IOException("The selected path is not an available regular directory.");
        }

        var entries = new FileSystemEnumerable<FileRecord?>(accessPath,
            (ref FileSystemEntry entry) => entry.IsDirectory ? null :
                new FileRecord(Path.Join(normalized, entry.FileName), entry.FileName.ToString(),
                    entry.Length, Path.GetExtension(entry.FileName).ToString()),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                BufferSize = 64 * 1024
            });
        var files = new List<FileRecord>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry is not null) files.Add(entry);
        }
        token.ThrowIfCancellationRequested();
        return files;
    }
}
