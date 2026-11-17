using System.IO;
using System.IO.Enumeration;
using WindowsFS.Core;

namespace WindowsFS.App.Services;

public sealed record FolderEntry(string Name, string Path);
public sealed record FolderLocation(string Path, string? ParentPath, IReadOnlyList<FolderEntry> Entries);

public interface IReadOnlyFolderBrowser
{
    Task<FolderLocation> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Reads one directory's metadata. No shell controls or filesystem mutations are used.</summary>
public sealed class ReadOnlyFolderBrowser : IReadOnlyFolderBrowser
{
    public Task<FolderLocation> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(path, cancellationToken), cancellationToken);

    private static FolderLocation Read(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute folder path is required.", nameof(path));
        var normalized = ScanPath.NormalizeRoot(path);
        var accessPath = ScanPath.ForFileSystemAccess(normalized);
        var attributes = File.GetAttributes(accessPath);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            throw new IOException("The selected path is not an available regular directory.");

        var entries = new List<FolderEntry>();
        var enumeration = new FileSystemEnumerable<FolderEntry?>(accessPath,
            (ref FileSystemEntry entry) => entry.IsDirectory &&
                (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) == 0
                    ? new FolderEntry(entry.FileName.ToString(), Path.Join(normalized, entry.FileName)) : null,
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                BufferSize = 64 * 1024
            });
        foreach (var entry in enumeration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is not null) entries.Add(entry);
        }
        entries.Sort((left, right) =>
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Name, right.Name);
        });
        cancellationToken.ThrowIfCancellationRequested();
        return new FolderLocation(normalized, Path.GetDirectoryName(normalized), entries);
    }
}
