using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App.Services;

public sealed record MediaInfoGroup(string Title, IReadOnlyList<DetailField> Fields);
public sealed record MediaInfoResult(IReadOnlyList<MediaInfoGroup> Groups, string Status = "");

public interface IMediaInfoReader
{
    Task<MediaInfoResult> ReadAsync(ResultRow row, CancellationToken token);
}

/// <summary>MediaInfo's buffer API receives bytes, never a path. No extraction, shell, playback or cache.</summary>
public sealed class MediaInfoReader : IMediaInfoReader
{
    private static readonly SemaphoreSlim Gate = new(1);
    private const long ReadBudget = 64L * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp2", ".aac", ".m4a", ".m4b", ".flac", ".wav", ".wave", ".ogg", ".oga", ".opus",
        ".wma", ".aif", ".aiff", ".ape", ".wv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".asf",
        ".mkv", ".mka", ".webm", ".ogv", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".3gp", ".3g2", ".flv"
    };
    public static bool Supports(ResultRow row) => !row.IsDirectory && Extensions.Contains(Path.GetExtension(row.Path));

    public async Task<MediaInfoResult> ReadAsync(ResultRow row, CancellationToken token)
    {
        if (!Supports(row)) return new([], "미디어 정보 미지원 형식");
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                try { return Read(row, token); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                    NotSupportedException or System.Security.SecurityException or DllNotFoundException or
                    EntryPointNotFoundException or BadImageFormatException)
                { return new MediaInfoResult([], "미디어 정보 읽기 불가 · 파일 형식 또는 접근 권한"); }
            }, token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static MediaInfoResult Read(ResultRow row, CancellationToken token)
    {
        FilePreviewReader.ValidateLocalFile(row.Path, long.MaxValue);
        using var source = FilePreviewReader.OpenRead(row.Path);
        var clock = Stopwatch.StartNew();
        var parser = ValidateContainer(source, token);
        var handle = Native.MediaInfo_New();
        if (handle == IntPtr.Zero) throw new IOException("MediaInfo initialization failed.");
        try
        {
            // Force the validated binary parser: renamed playlists/XML cannot invoke reference-file readers.
            Option(handle, "File_ForceParser", parser);
            Option(handle, "File_IsSeekable", "1");
            Option(handle, "File_ParseSpeed", "0.5");
            Option(handle, "File_TestContinuousFileNames", "0");
            Option(handle, "File_TestDirectory", "0");
            Option(handle, "File_CheckSideCarFiles", "0");
            Option(handle, "File_TryToFix", "0");
            Option(handle, "File_TimeToLive", "8");
            source.Position = 0;
            var length = source.Length;
            Native.MediaInfo_Open_Buffer_Init(handle, (ulong)length, 0);
            var buffer = new byte[64 * 1024];
            long read = 0;
            var seeks = 0;
            var limited = false;
            while (source.Position < length)
            {
                token.ThrowIfCancellationRequested();
                if (read >= ReadBudget || seeks >= 128 || clock.Elapsed > TimeSpan.FromSeconds(8)) { limited = true; break; }
                var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length - source.Position));
                if (count == 0) break;
                read += count;
                var state = Native.MediaInfo_Open_Buffer_Continue(handle, buffer, (nuint)count);
                if ((state & 8) != 0) break;
                var next = Native.MediaInfo_Open_Buffer_Continue_GoTo_Get(handle);
                if (next == ulong.MaxValue) continue;
                if (next > (ulong)length) throw new InvalidDataException("Invalid media offset.");
                source.Position = (long)next;
                Native.MediaInfo_Open_Buffer_Init(handle, (ulong)length, next);
                seeks++;
            }
            token.ThrowIfCancellationRequested();
            Native.MediaInfo_Open_Buffer_Finalize(handle);
            var groups = new List<MediaInfoGroup>();
            string[] names = ["컨테이너", "영상", "오디오", "자막", "기타", "이미지", "메뉴"];
            var characters = 0;
            var totalTracks = 0;
            for (var kind = 0; kind < names.Length; kind++)
            {
                var tracks = Native.MediaInfo_Count_Get(handle, kind, nuint.MaxValue);
                for (nuint track = 0; track < tracks; track++)
                {
                    if (++totalTracks > 64 || characters >= 256 * 1024) { limited = true; break; }
                    token.ThrowIfCancellationRequested();
                    var fields = new List<DetailField>();
                    var count = Native.MediaInfo_Count_Get(handle, kind, track);
                    for (nuint field = 0; field < count && field < 4096; field++)
                    {
                        if (!Get(handle, kind, track, field, 3).StartsWith('Y')) continue;
                        var value = Get(handle, kind, track, field, 1);
                        if (value.Length == 0) continue;
                        if (value.Length > 4096) { value = value[..4096] + "…"; limited = true; }
                        characters += value.Length;
                        if (characters > 256 * 1024) { limited = true; break; }
                        var label = Get(handle, kind, track, field, 4);
                        if (label.Length == 0) label = Get(handle, kind, track, field, 0);
                        fields.Add(new(label, value));
                    }
                    if (fields.Count > 0) groups.Add(new(kind == 0 ? names[kind] : $"{names[kind]} {track + 1}", fields));
                }
            }
            return new(groups, groups.Count == 0 ? "미디어 정보 없음" : limited ? "일부 정보 · 읽기 범위 제한" : "");
        }
        finally { Native.MediaInfo_Delete(handle); }
    }

    internal static string ValidateContainer(Stream source, CancellationToken token)
    {
        Span<byte> h = stackalloc byte[16];
        source.Position = 0;
        if (source.ReadAtLeast(h, h.Length, false) < h.Length) throw new InvalidDataException("Incomplete media header.");
        if (h[..4].SequenceEqual("RIFF"u8) && (h[8..12].SequenceEqual("WAVE"u8) || h[8..12].SequenceEqual("AVI "u8))) return "Riff";
        if (h[..4].SequenceEqual("FORM"u8) && (h[8..12].SequenceEqual("AIFF"u8) || h[8..12].SequenceEqual("AIFC"u8))) return "Aiff";
        if (h[..4].SequenceEqual("fLaC"u8)) return "Flac";
        if (h[..4].SequenceEqual("OggS"u8)) return "Ogg";
        if (h[..4].SequenceEqual("MAC "u8)) return "Ape";
        if (h[..4].SequenceEqual("wvpk"u8)) return "Wvpk";
        if (h[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) return "Mkv";
        if (h.SequenceEqual(new byte[] { 0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0, 0xaa, 0, 0x62, 0xce, 0x6c })) return "Wm";
        if (h[..3].SequenceEqual("FLV"u8)) return "Flv";
        if (h[..3].SequenceEqual("ID3"u8) || (h[0] == 0xff && (h[1] & 0xe6) == 0xe2)) return "Mpega";
        if (h[0] == 0xff && (h[1] & 0xf6) == 0xf0) return "Aac";
        if (h[..4].SequenceEqual(new byte[] { 0, 0, 1, 0xba })) return "MpegPs";
        if (h[0] == 0x47 || h[4] == 0x47) return "MpegTs";
        if (h[4..8].SequenceEqual("ftyp"u8) || h[4..8].SequenceEqual("moov"u8) || h[4..8].SequenceEqual("mdat"u8) || h[4..8].SequenceEqual("wide"u8))
        {
            var boxes = 0;
            ValidateMp4Boxes(source, 0, source.Length, 0, ref boxes, token);
            return "Mpeg4";
        }
        throw new InvalidDataException("Only recognized binary media containers are accepted.");
    }

    // QuickTime reference movies and external data references must never reach MediaInfo's file readers.
    private static void ValidateMp4Boxes(Stream source, long begin, long end, int depth, ref int boxes, CancellationToken token)
    {
        if (depth > 12) throw new InvalidDataException("Excessive container depth.");
        Span<byte> header = stackalloc byte[16];
        for (var offset = begin; offset < end;)
        {
            token.ThrowIfCancellationRequested();
            if (++boxes > 20000 || end - offset < 8) throw new InvalidDataException("Invalid container boxes.");
            source.Position = offset;
            source.ReadExactly(header[..8]);
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header[4..8]);
            var headerSize = 8;
            if (size == 1)
            {
                source.ReadExactly(header[8..]);
                var large = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                if (large > long.MaxValue) throw new InvalidDataException("Invalid box length.");
                size = (long)large; headerSize = 16;
            }
            if (size == 0) size = end - offset;
            if (size < headerSize || size > end - offset) throw new InvalidDataException("Invalid box length.");
            var content = offset + headerSize;
            var next = offset + size;
            if (type is "rmra" or "rmda" or "rdrf" or "cmov" or "code") throw new InvalidDataException("Reference movies and compressed reference tables are excluded.");
            if (type == "dref")
            {
                if (next - content < 8) throw new InvalidDataException("Invalid data reference.");
                source.ReadExactly(header[..8]);
                var entries = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
                if (entries > 1024) throw new InvalidDataException("Too many data references.");
                for (var i = 0; i < entries; i++)
                {
                    if (next - source.Position < 12) throw new InvalidDataException("Invalid data reference.");
                    source.ReadExactly(header[..12]);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (length != 12 || !header[4..8].SequenceEqual("url "u8) || BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 1)
                        throw new InvalidDataException("External data references are excluded.");
                }
            }
            if (type is "moov" or "trak" or "mdia" or "minf" or "dinf")
                ValidateMp4Boxes(source, content, next, depth + 1, ref boxes, token);
            offset = next;
        }
    }

    private static void Option(IntPtr handle, string key, string value) => Native.MediaInfo_Option(handle, key, value);
    private static string Get(IntPtr handle, int kind, nuint track, nuint field, int info) =>
        Marshal.PtrToStringUni(Native.MediaInfo_GetI(handle, kind, track, field, info)) ?? "";

    private static class Native
    {
        private const string Library = "libmediainfo";
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr MediaInfo_New();
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)] internal static extern void MediaInfo_Delete(IntPtr handle);
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)] internal static extern nuint MediaInfo_Open_Buffer_Init(IntPtr handle, ulong size, ulong offset);
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)] internal static extern nuint MediaInfo_Open_Buffer_Continue(IntPtr handle, byte[] buffer, nuint size);
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)] internal static extern ulong MediaInfo_Open_Buffer_Continue_GoTo_Get(IntPtr handle);
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)] internal static extern nuint MediaInfo_Open_Buffer_Finalize(IntPtr handle);
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)] internal static extern IntPtr MediaInfo_Option(IntPtr handle, string option, string value);
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)] internal static extern nuint MediaInfo_Count_Get(IntPtr handle, int kind, nuint track);
        [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)] internal static extern IntPtr MediaInfo_GetI(IntPtr handle, int kind, nuint track, nuint field, int info);
    }
}
