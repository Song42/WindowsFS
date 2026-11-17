using System.IO;
using System.Text;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;

namespace WindowsFS.App.Services;

public sealed record DetailField(string Label, string Value);

/// <summary>Metadata and bounded plain-text reads only. No shell preview handlers.</summary>
public sealed class ItemDetailsReader
{
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".csv", ".json", ".xml", ".log", ".ini", ".yaml", ".yml", ".cs", ".js", ".ts", ".py", ".css", ".html", ".sql" };

    public static bool SupportsPreview(ResultRow row) => !row.IsDirectory && TextTypes.Contains(Path.GetExtension(row.Path));

    public async Task<T> RunAsync<T>(Func<T> read, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => { token.ThrowIfCancellationRequested(); return read(); }, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<DetailField> ReadMetadata(ResultRow row)
    {
        try
        {
            var path = ScanPath.ForFileSystemAccess(row.Path);
            FileSystemInfo info = row.IsDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            info.Refresh();
            if (!info.Exists) return [new("현재 상태", "없음 또는 접근 불가 · 저장된 분석 정보 표시")];
            var fields = new List<DetailField>();
            if (info is FileInfo file) fields.Add(new("현재 크기 · 논리적", $"{DisplayFormat.Bytes(file.Length)} ({file.Length:N0} B)"));
            fields.Add(new("만든 날짜", info.CreationTime.ToString("yyyy-MM-dd HH:mm:ss")));
            fields.Add(new("수정한 날짜", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
            fields.Add(new("액세스한 날짜", info.LastAccessTime.ToString("yyyy-MM-dd HH:mm:ss")));
            fields.Add(new("속성", info.Attributes.ToString()));
            fields.Add(new("날짜 기준", "현재 파일 메타데이터 · 로컬 시간"));
            return fields;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return [new("현재 상태", "메타데이터 접근 불가 · 저장된 분석 정보 표시")]; }
    }

    public string ReadPreview(ResultRow row)
    {
        if (!SupportsPreview(row)) return "텍스트 미리보기 미지원 형식";
        try
        {
            var path = ScanPath.ForFileSystemAccess(row.Path);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
                return "링크·오프라인 파일 미리보기 제외";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[65536];
            var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            using var reader = new StreamReader(new MemoryStream(bytes, 0, count), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (text.Contains('\0')) return "바이너리 내용 · 텍스트 미리보기 제외";
            return text.Length == 0 ? "빈 파일" : text + (stream.Length > count ? "\n\n[앞부분 64 KB]" : "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return "미리보기 접근 불가"; }
    }
}
