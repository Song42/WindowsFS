using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using WindowsFS.Core;

namespace WindowsFS.App.Services;

public sealed record DiskSpace(long FreeBytes, long AvailableBytes, DateTimeOffset ReadAt);
public interface IDiskSpaceReader
{
    Task<DiskSpace> ReadAsync(string folderPath, CancellationToken token);
}

/// <summary>Reads volume free-space metadata for the supplied directory, including mounted volumes. No enumeration or file writes.</summary>
public sealed class DiskSpaceReader : IDiskSpaceReader
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task<DiskSpace> ReadAsync(string folderPath, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(folderPath)) throw new ArgumentException("An absolute folder path is required.", nameof(folderPath));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                // Query the folder itself, not its drive-letter root: a mounted folder can belong to another volume.
                var path = ScanPath.ForFileSystemAccess(ScanPath.NormalizeRoot(folderPath));
                if (!GetDiskFreeSpaceExW(path.TrimEnd('\\') + "\\", out var available, out _, out var free))
                    throw new IOException("Disk free space is unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
                token.ThrowIfCancellationRequested();
                return new DiskSpace(checked((long)free), checked((long)available), DateTimeOffset.Now);
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}
