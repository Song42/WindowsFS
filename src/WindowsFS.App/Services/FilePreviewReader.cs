using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;

namespace WindowsFS.App.Services;

public enum PreviewKind { Unsupported, Text, Image, Video, Pdf, Document }

public sealed record FilePreview(string Text = "", BitmapSource? Image = null, string? VideoPath = null,
    int PageIndex = 0, int PageCount = 0)
{
    public bool HasImage => Image is not null;
    public bool HasVideo => VideoPath is not null;
    public bool HasText => Text.Length > 0;
}

public interface IFilePreviewReader
{
    Task<FilePreview> ReadAsync(ResultRow row, int pageIndex, CancellationToken token);
}

/// <summary>Explicit, bounded previews. No shell handlers, Office automation, extraction, or disk cache.</summary>
public sealed class FilePreviewReader : IFilePreviewReader
{
    public const long MaximumFileBytes = 64 * 1024 * 1024;
    public const long MaximumVideoBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan MaximumVideoDuration = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1);

    public static PreviewKind KindFor(ResultRow row)
    {
        if (row.IsDirectory) return PreviewKind.Unsupported;
        return Path.GetExtension(row.Path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" => PreviewKind.Image,
            ".mp4" or ".m4v" or ".mov" or ".avi" or ".wmv" or ".mkv" or ".webm" => PreviewKind.Video,
            ".pdf" => PreviewKind.Pdf,
            ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm" or ".hwpx" or ".odt" or ".ods" or ".odp" => PreviewKind.Document,
            _ when ItemDetailsReader.SupportsTextPreview(row) => PreviewKind.Text,
            _ => PreviewKind.Unsupported
        };
    }

    public static string Description(ResultRow row) => KindFor(row) switch
    {
        PreviewKind.Image => "이미지 · 최대 64 MB · GIF/TIFF 첫 프레임",
        PreviewKind.Video => "영상 · 처음 30초 · 최대 2 GB · Windows 지원 코덱",
        PreviewKind.Pdf => "PDF · 페이지 미리보기 · 최대 64 MB",
        PreviewKind.Document => "문서 본문·표 텍스트 · 최대 64 MB · 서식·매크로 제외",
        PreviewKind.Text => "텍스트 · 최대 64 KB · UTF-8/BOM",
        _ => "미리보기 미지원 형식"
    };

    public async Task<FilePreview> ReadAsync(ResultRow row, int pageIndex, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var kind = KindFor(row);
                    if (kind == PreviewKind.Unsupported) return new FilePreview(Description(row));
                    ValidateLocalFile(row.Path, kind == PreviewKind.Video ? MaximumVideoBytes : kind == PreviewKind.Text ? long.MaxValue : MaximumFileBytes);
                    if (kind == PreviewKind.Video)
                    {
                        ValidateVideoFile(row.Path);
                        return new FilePreview(Description(row), VideoPath: row.Path);
                    }
                    if (kind == PreviewKind.Text) return new FilePreview(new ItemDetailsReader().ReadPreview(row));
                    using var source = OpenRead(row.Path);
                    // Read one bounded in-memory copy: decoders cannot access paths, external resources, or growing source files.
                    using var memory = new MemoryStream();
                    var buffer = new byte[64 * 1024];
                    int count;
                    while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        if (memory.Length + count > MaximumFileBytes) return new FilePreview("미리보기 크기 제한 · 최대 64 MB");
                        memory.Write(buffer, 0, count);
                    }
                    memory.Position = 0;
                    token.ThrowIfCancellationRequested();
                    return kind switch
                    {
                        PreviewKind.Image => new FilePreview(Image: DecodeImage(memory)),
                        PreviewKind.Pdf => PdfPreview.Read(memory, pageIndex, token),
                        _ => new FilePreview(DocumentPreview.Read(memory, Path.GetExtension(row.Path), token))
                    };
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
                    System.Security.SecurityException or COMException or System.Xml.XmlException or FileFormatException or InvalidOperationException or
                    DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    return new FilePreview(error is PreviewLimitException ? error.Message : "미리보기 불가 · 접근 권한, 암호 또는 파일 형식 확인");
                }
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal static FileStream OpenRead(string path) => new(ScanPath.ForFileSystemAccess(path), FileMode.Open,
        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    public static void ValidateVideoFile(string path)
    {
        ValidateLocalFile(path, MaximumVideoBytes);
        using var stream = OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
            throw new InvalidDataException("Incomplete video header.");
        var valid = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" or ".mov" => header[4..8].SequenceEqual("ftyp"u8) || header[4..8].SequenceEqual("moov"u8) ||
                header[4..8].SequenceEqual("mdat"u8) || header[4..8].SequenceEqual("wide"u8),
            ".avi" => header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("AVI "u8),
            ".wmv" => header.SequenceEqual(new byte[] { 0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0, 0xaa, 0, 0x62, 0xce, 0x6c }),
            ".mkv" or ".webm" => header[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Only video containers are accepted; playlists and web documents are excluded.");
    }

    public static void ValidateLocalFile(string path, long maximumBytes)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("Only local files can be previewed.");
        // Check ancestors too: a local-looking directory link may lead to a remote or offline target.
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(ScanPath.ForFileSystemAccess(current));
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
                throw new PreviewLimitException("링크·오프라인 파일 미리보기 제외");
        }
        var info = new FileInfo(ScanPath.ForFileSystemAccess(path));
        if (info.Length > maximumBytes) throw new PreviewLimitException($"미리보기 크기 제한 · 최대 {DisplayFormat.Bytes(maximumBytes)}");
    }

    private static BitmapSource DecodeImage(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000) throw new PreviewLimitException("이미지 해상도 제한 · 최대 1억 픽셀");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = Math.Min(frame.PixelWidth, 1600);
        else bitmap.DecodePixelHeight = Math.Min(frame.PixelHeight, 1600);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class PreviewLimitException(string message) : IOException(message);
}
