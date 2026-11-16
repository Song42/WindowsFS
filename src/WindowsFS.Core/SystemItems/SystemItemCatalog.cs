namespace WindowsFS.Core.SystemItems;

/// <summary>
/// A small, offline allowlist of documented Windows locations. Matching never reads file contents,
/// follows links, or attributes an arbitrary child of a known folder to Windows.
/// </summary>
public sealed class SystemItemCatalog
{
    private const string MatchBasis = "Windows 표준 위치와 항목 종류를 기준으로 한 설명입니다. 실제 생성자나 내부 파일의 출처를 확인한 결과는 아닙니다.";
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public SystemItemCatalog() : this(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty)
    {
    }

    public SystemItemCatalog(string windowsDirectory, string systemDrive)
    {
        var windows = Normalize(windowsDirectory, true)
            ?? throw new ArgumentException("A fully qualified Windows directory is required.", nameof(windowsDirectory));
        var drive = Normalize(systemDrive, true);
        if (drive is null || drive.Length != 3)
            throw new ArgumentException("A drive root such as C:\\ is required.", nameof(systemDrive));

        Add(windows, true, new(
            "Windows · 운영체제 폴더",
            "Windows 운영체제의 시스템 파일, 구성요소, 드라이버와 관리 데이터를 담는 기본 폴더입니다.",
            "주로 Windows 설치 프로그램과 Windows의 각 관리 기능",
            "운영체제에 필요합니다. 폴더를 삭제하면 Windows나 앱이 정상 작동하지 않을 수 있습니다.",
            "폴더 전체를 삭제하지 마세요. 설정 → 시스템 → 저장소의 정리 권장 사항에서 정리 가능한 항목을 확인하세요.",
            "https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getwindowsdirectoryw", MatchBasis));
        AddWindows("WinSxS", new(
            "WinSxS · Windows 구성요소 저장소",
            "Windows 구성요소와 업데이트된 구성요소의 이전 버전을 보관합니다. 업데이트와 복구에 사용됩니다.",
            "Windows 구성요소 서비스 및 Windows Update",
            "필수 구성요소 저장소입니다. 직접 삭제하면 부팅이나 업데이트가 실패할 수 있습니다.",
            "Windows의 자동 구성요소 정리를 사용하세요. 추가 정리는 공식 문서의 StartComponentCleanup 절차로 진행하고 내부 파일은 직접 삭제하지 마세요.",
            "https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11", MatchBasis));
        AddWindows("System32", new(
            "System32 · Windows 시스템 파일",
            "Windows의 시스템 실행 파일과 라이브러리를 담는 주요 폴더입니다. 64비트 Windows에서는 기본 64비트 시스템 경로입니다.",
            "Windows 설치 및 구성요소 관리 기능 · 일부 하위 항목은 별도 소프트웨어가 관리",
            "Windows와 앱 실행에 필요합니다. 파일을 임의로 지우면 시스템 기능이 손상될 수 있습니다.",
            "용량 확보를 위해 폴더나 파일을 직접 삭제하지 마세요. Windows 저장소 정리 기능에서 제공하는 항목을 이용하세요.",
            "https://learn.microsoft.com/en-us/windows/win32/winprog64/file-system-redirector", MatchBasis));
        AddWindows(@"System32\DriverStore", new(
            "DriverStore · 장치 드라이버 저장소",
            "장치 설치에 사용하는 드라이버 패키지를 보관합니다. Windows 기본 드라이버와 제조사 드라이버가 함께 들어갈 수 있습니다.",
            "Windows 장치 설치 기능 · 패키지 공급자는 Microsoft 또는 장치 제조사",
            "장치 설치와 정상 동작을 위해 유지해야 합니다. 패키지의 일부만 지우면 장치 설치나 드라이버 관리가 실패할 수 있습니다.",
            "내부 파일을 직접 삭제하지 마세요. 드라이버 제거가 필요하면 장치 관리자나 제조사가 안내하는 제거 절차를 사용하세요.",
            "https://learn.microsoft.com/en-us/windows-hardware/drivers/install/driver-store", MatchBasis));
        AddWindows("SoftwareDistribution", new(
            "SoftwareDistribution · Windows Update 데이터",
            "Windows Update의 다운로드 파일과 업데이트 관리 데이터베이스 등이 저장되는 위치입니다.",
            "Windows Update 서비스",
            "업데이트가 사용하는 작업 데이터입니다. 진행 중인 업데이트에서 임의로 지우면 설치에 문제가 생길 수 있습니다.",
            "업데이트 오류가 있으면 Windows Update 문제 해결사를 먼저 사용하세요. 캐시 초기화는 공식 복구 절차에 따라 진행하세요.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/additional-resources-for-windows-update", MatchBasis));
        AddWindows("Installer", new(
            "Installer · 앱 설치 캐시",
            "Windows Installer로 설치한 앱의 유지 관리에 필요한 설치 파일을 보관합니다.",
            "Windows Installer · 설치한 각 앱의 설치 프로그램",
            "앱 복구, 업데이트와 제거에 필요합니다. 캐시를 삭제하면 나중에 해당 작업이 실패할 수 있습니다.",
            "설치 캐시를 직접 삭제하지 마세요. 사용하지 않는 앱은 설정 → 앱 → 설치된 앱에서 제거하세요.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/missing-windows-installer-cache", MatchBasis));
        AddWindows("Panther", new(
            "Panther · Windows 설치 기록",
            "Windows 설치·업그레이드 과정과 오류, 데이터 이전에 관한 진단 기록이 저장되는 위치입니다.",
            "Windows 설치 및 업그레이드 프로그램",
            "설치 문제 분석에 쓰입니다. 기록을 지우면 이전 오류나 데이터 이전 문제를 조사하기 어려워집니다.",
            "설치 문제를 조사 중이면 보관하세요. 정리는 Windows 저장소 정리에서 제시하는 설치 관련 항목으로 진행하세요.",
            "https://learn.microsoft.com/en-us/windows/deployment/upgrade/log-files", MatchBasis));
        AddDrive("Windows.old", true, new(
            "Windows.old · 이전 Windows 설치",
            "Windows 업그레이드 뒤 이전 설치를 보관하는 위치입니다. 이전 사용자 파일이 포함될 수 있습니다.",
            "Windows 설치 및 업그레이드 프로그램",
            "이전 버전으로 돌아가거나 이전 파일을 찾을 때 필요합니다. 삭제하면 이전 버전으로 돌아갈 수 없습니다.",
            "필요한 파일과 백업을 확인한 뒤 설정 → 시스템 → 저장소 → 임시 파일의 이전 Windows 설치 항목을 사용하세요.",
            "https://support.microsoft.com/en-us/windows/deployment/install-upgrade/delete-your-previous-version-of-windows", MatchBasis));
        AddDrive("pagefile.sys", false, new(
            "pagefile.sys · 가상 메모리 파일",
            "메모리 일부를 디스크에 보관하고 시스템이 사용할 수 있는 메모리 한도를 늘립니다. 오류 발생 시 메모리 덤프에도 사용될 수 있습니다.",
            "Windows 메모리 관리자",
            "필요 여부와 크기는 메모리 사용량과 덤프 설정에 따라 다릅니다. 없애거나 과도하게 줄이면 메모리 부족이나 오류 기록 누락이 생길 수 있습니다.",
            "파일을 직접 삭제하지 마세요. 변경이 필요하면 Windows의 가상 메모리 설정에서 용도와 사용량을 확인하고 조정하세요.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file", MatchBasis));
        AddDrive("hiberfil.sys", false, new(
            "hiberfil.sys · 최대 절전 모드 파일",
            "최대 절전 모드 등의 전원 기능을 위해 메모리 상태를 디스크에 저장하는 파일입니다.",
            "Windows 커널 전원 관리자",
            "최대 절전 모드에 필요합니다. 기능을 끄면 최대 절전 모드와 이를 사용하는 전원 기능을 이용할 수 없습니다.",
            "직접 삭제하지 마세요. 최대 절전 모드를 사용하지 않을 경우 공식 전원 관리 절차로 기능을 변경하세요. 하이브리드 절전 모드에도 영향을 줍니다.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/disable-and-re-enable-hibernation", MatchBasis));
        AddDrive("$Recycle.Bin", true, new(
            "$Recycle.Bin · 휴지통 저장소",
            "이 드라이브에서 휴지통으로 보낸 항목을 보관하는 Windows 관리 폴더입니다. 내부에는 사용자가 삭제한 개인 파일이 포함될 수 있습니다.",
            "Windows 파일 탐색기의 휴지통 기능",
            "삭제한 파일을 복원할 수 있도록 보관합니다. 휴지통을 비우면 해당 파일을 휴지통에서 복원할 수 없습니다.",
            "파일 탐색기의 휴지통에서 내용을 확인한 뒤 비우기 기능을 사용하세요. 저장소 센스의 휴지통 보관 기간 설정도 이용할 수 있습니다.",
            "https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/manage-drive-space-with-storage-sense", MatchBasis));

        void AddWindows(string relativePath, SystemItemInfo info) => Add(Combine(windows, relativePath), true, info);
        void AddDrive(string name, bool isDirectory, SystemItemInfo info) => Add(Combine(drive, name), isDirectory, info);
    }

    public SystemItemInfo? Describe(string? path, bool isDirectory)
    {
        var normalized = Normalize(path, isDirectory);
        return normalized is not null && _entries.TryGetValue(normalized, out var entry) && entry.IsDirectory == isDirectory
            ? entry.Info
            : null;
    }

    private void Add(string path, bool isDirectory, SystemItemInfo info) => _entries.Add(path, new(isDirectory, info));
    private static string Combine(string root, string child) => root.TrimEnd('\\') + "\\" + child;

    // Deliberately independent of the host OS and current directory. Do not resolve dot segments,
    // trim meaningful filename characters, or turn a literal extended path into a Win32 alias.
    private static string? Normalize(string? path, bool isDirectory)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (path.Contains('/')) return null;
            path = path[4..];
        }
        else path = path.Replace('/', '\\');

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
        if (!isDirectory && path.EndsWith('\\')) return null;
        if (isDirectory && path.Length > 3) path = path.TrimEnd('\\');
        if (path.Length == 3) return path;

        foreach (var segment in path[3..].Split('\\'))
        {
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ')) return null;
            foreach (var character in segment)
                if (character < ' ' || character is '<' or '>' or ':' or '"' or '|' or '?' or '*') return null;
        }
        return path;
    }

    private sealed record Entry(bool IsDirectory, SystemItemInfo Info);
}
