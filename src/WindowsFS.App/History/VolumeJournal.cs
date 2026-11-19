using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindowsFS.Core;

namespace WindowsFS.App.History;

internal record JournalChange(long File, long Parent, string Name, uint Reason, long Time, long Usn);
internal record ChangePage(long Next, List<JournalChange> Changes);
internal sealed class VolumeJournal : IDisposable
{
    internal const uint Create = 0x100, Delete = 0x200, RenameOld = 0x1000, RenameNew = 0x2000, HardLink = 0x10000;
    private const uint Data = 7 | Create | Delete | RenameOld | RenameNew | HardLink | 0x8000 | 0x20000;
    private const uint Attributes = 0x80, OpenReparse = 0x00200000, Backup = 0x02000000;
    private readonly SafeFileHandle volume;
    private readonly uint readCode;
    internal long JournalId { get; }
    internal long First { get; }
    internal long End { get; }
    private VolumeJournal(SafeFileHandle volume, uint code, byte[] info)
    {
        this.volume = volume; readCode = code;
        JournalId = BitConverter.ToInt64(info, 0);
        First = Math.Max(BitConverter.ToInt64(info, 8), BitConverter.ToInt64(info, 24));
        End = BitConverter.ToInt64(info, 16);
    }
    internal static string? VolumeFor(string path)
    {
        StringBuilder mount = new(32768), name = new(128);
        return GetVolumePathNameW(path, mount, mount.Capacity) && GetVolumeNameForVolumeMountPointW(mount.ToString(), name, name.Capacity)
            ? name.ToString() : null;
    }
    internal static string MountFor(string path)
    {
        var mount = new StringBuilder(32768);
        return GetVolumePathNameW(path, mount, mount.Capacity) ? mount.ToString() : Path.GetPathRoot(path)!;
    }
    internal static VolumeJournal? Open(string volumeName)
    {
        foreach (var (directory, privileged) in new[] { (false, false), (true, false), (false, true) })
        {
            var h = CreateFileW(directory ? volumeName : volumeName.TrimEnd('\\'), privileged ? 0x80000000u : directory ? Attributes : 0,
                7, 0, 3, Backup, 0);
            var info = new byte[128];
            if (!h.IsInvalid && DeviceIoControl(h, 0x900f4, null, 0, info, info.Length, out var n, 0) && n >= 56)
            {
                var journal = new VolumeJournal(h, privileged ? 0x900bbu : 0x903abu, info);
                try { journal.Read(journal.End); return journal; }
                catch (IOException) { }
            }
            h.Dispose();
        }
        return null;
    }
    internal ChangePage Read(long start)
    {
        byte[] request = new byte[48], buffer = new byte[65536];
        BitConverter.TryWriteBytes(request.AsSpan(0), start);
        BitConverter.TryWriteBytes(request.AsSpan(8), Data);
        BitConverter.TryWriteBytes(request.AsSpan(12), 1); // closed handles, accumulated reasons
        BitConverter.TryWriteBytes(request.AsSpan(32), JournalId);
        BitConverter.TryWriteBytes(request.AsSpan(40), (ushort)2);
        BitConverter.TryWriteBytes(request.AsSpan(42), (ushort)2);
        if (!DeviceIoControl(volume, readCode, request, request.Length, buffer, buffer.Length, out var n, 0) || n < 8)
            throw new IOException("변경 저널 접근 중단");
        return Decode(buffer.AsSpan(0, n));
    }
    internal static ChangePage Decode(ReadOnlySpan<byte> bytes)
    {
        long next = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        List<JournalChange> changes = [];
        for (var at = 8; at < bytes.Length;)
        {
            var r = bytes[at..];
            if (r.Length < 60) throw new IOException("잘린 변경 기록");
            int length = BitConverter.ToInt32(r);
            if (length < 60 || length > r.Length || BitConverter.ToUInt16(r[4..]) != 2) throw new IOException("지원되지 않는 변경 기록");
            ushort size = BitConverter.ToUInt16(r[56..]), offset = BitConverter.ToUInt16(r[58..]);
            if (offset < 60 || offset + size > length || size % 2 != 0) throw new IOException("잘린 파일 이름");
            if ((BitConverter.ToUInt32(r[52..]) & 0x10) == 0)
                changes.Add(new(BitConverter.ToInt64(r[8..]), BitConverter.ToInt64(r[16..]), Encoding.Unicode.GetString(r.Slice(offset, size)),
                    BitConverter.ToUInt32(r[40..]), DateTimeOffset.FromFileTime(BitConverter.ToInt64(r[32..])).ToUnixTimeMilliseconds(), BitConverter.ToInt64(r[24..])));
            at += length;
        }
        return new(next, changes);
    }
    internal string? ParentPath(long id)
    {
        var desc = new Identifier { Size = 24, Id = id };
        using var h = OpenFileById(volume, ref desc, Attributes, 7, 0, Backup | OpenReparse);
        if (h.IsInvalid) return null;
        var name = new StringBuilder(512);
        uint n = GetFinalPathNameByHandleW(h, name, name.Capacity, 0);
        if (n >= name.Capacity && n <= 32768)
        {
            name.EnsureCapacity((int)n + 1);
            n = GetFinalPathNameByHandleW(h, name, name.Capacity, 0);
        }
        return n > 0 && n < name.Capacity ? ScanPath.NormalizeRoot(name.ToString()) : null;
    }

    internal static Footprint? Inspect(string path, string volumeKey, long? expectedId = null)
    {
        using var h = CreateFileW(ScanPath.ForFileSystemAccess(path), Attributes, 7, 0, 3, OpenReparse | 0x00100000, 0); // OPEN_NO_RECALL
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info) ||
            (info.Attributes & (0x10 | 0x400 | 0x1000 | 0x40000 | 0x400000)) != 0 ||
            !GetFileInformationByHandleEx(h, 1, out var standard, Marshal.SizeOf<Standard>()) || standard.Pending != 0) return null;
        long id = ((long)info.IndexHigh << 32) | info.IndexLow;
        if (expectedId.HasValue && expectedId != id) return null;
        return new(Key(volumeKey, id), path, standard.Bytes, standard.Allocated, checked((int)standard.Links));
    }
    internal static string Key(string volume, long id) => volume + ":" + id.ToString("X16");
    internal static bool ConfirmRemoved(string root, Footprint previous)
    {
        var suffix = previous.Id.AsSpan(previous.Id.LastIndexOf(':') + 1);
        if (!long.TryParse(suffix, System.Globalization.NumberStyles.HexNumber, null, out long id)) return false;
        using var hint = CreateFileW(ScanPath.ForFileSystemAccess(root), Attributes, 7, 0, 3, Backup | OpenReparse, 0);
        if (hint.IsInvalid) return false;
        var descriptor = new Identifier { Size = 24, Id = id };
        using var file = OpenFileById(hint, ref descriptor, Attributes, 7, 0, OpenReparse | Backup);
        var error = Marshal.GetLastWin32Error();
        if (!file.IsInvalid) return false; // The object still exists, possibly at another path.
        if (error == 2) return true;
        // NTFS also reports INVALID_PARAMETER for a retired file reference. Verify the
        // same descriptor operation works on this volume before interpreting that result.
        if (error != 87 || !GetFileInformationByHandle(hint, out var rootInfo)) return false;
        descriptor.Id = ((long)rootInfo.IndexHigh << 32) | rootInfo.IndexLow;
        using var control = OpenFileById(hint, ref descriptor, Attributes, 7, 0, OpenReparse | Backup);
        return !control.IsInvalid;
    }
    public void Dispose() => volume.Dispose();

    [StructLayout(LayoutKind.Sequential, Size = 24)] private struct Identifier { public int Size, Type; public long Id; }
    [StructLayout(LayoutKind.Sequential)] private struct Standard { public long Allocated, Bytes; public uint Links; public byte Pending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint sec, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref Identifier id, uint access, uint share, nint sec, uint flags);
    [DllImport("kernel32.dll")] private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? input, int size, [Out] byte[] output, int capacity, out int returned, nint overlap);
    [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle h, out FileInfo info);
    [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int type, out Standard info, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder path, int size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool GetVolumePathNameW(string path, StringBuilder mount, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder volume, int size);
}
