namespace WindowsFS.Core.SystemItems;

/// <summary>
/// A small, offline allowlist of documented Windows locations. Matching never reads file contents,
/// follows links, or attributes an arbitrary child of a known folder to Windows.
/// </summary>
public sealed class SystemItemCatalog
{
    private const string MatchBasis = "Windows 표준 위치·항목 종류 기준. 실제 생성자와 내부 파일 출처는 미확인.";
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
            "Windows 시스템 파일, 구성요소, 드라이버와 관리 데이터의 기본 폴더.",
            "주로 Windows 설치 프로그램과 Windows의 각 관리 기능",
            "운영체제에 필수. 삭제 시 Windows·앱 작동 오류 가능.",
            "설정 → 시스템 → 저장소 → 정리 권장 사항.",
            "https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getwindowsdirectoryw", MatchBasis));
        AddWindows("WinSxS", new(
            "WinSxS · Windows 구성요소 저장소",
            "Windows 구성요소와 이전 버전을 보관하는 업데이트·복구용 저장소.",
            "Windows 구성요소 서비스 및 Windows Update",
            "필수 구성요소 저장소. 직접 삭제 시 부팅·업데이트 실패 가능.",
            "Windows 자동 구성요소 정리 또는 공식 문서의 StartComponentCleanup 절차.",
            "https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11", MatchBasis));
        AddWindows("System32", new(
            "System32 · Windows 시스템 파일",
            "Windows 시스템 실행 파일과 라이브러리의 주요 폴더. 64비트 Windows의 기본 64비트 시스템 경로.",
            "Windows 설치 및 구성요소 관리 기능 · 일부 하위 항목은 별도 소프트웨어가 관리",
            "Windows·앱 실행에 필요. 임의 삭제 시 시스템 기능 손상 가능.",
            "Windows 저장소 정리 기능의 제공 항목으로 정리.",
            "https://learn.microsoft.com/en-us/windows/win32/winprog64/file-system-redirector", MatchBasis));
        AddWindows(@"System32\DriverStore", new(
            "DriverStore · 장치 드라이버 저장소",
            "장치 설치용 드라이버 패키지 저장소. Windows 기본 드라이버와 제조사 드라이버 포함 가능.",
            "Windows 장치 설치 기능 · 패키지 공급자는 Microsoft 또는 장치 제조사",
            "장치 설치·동작에 필요. 패키지 일부 삭제 시 장치 설치·드라이버 관리 실패 가능.",
            "장치 관리자 또는 제조사의 드라이버 제거 절차.",
            "https://learn.microsoft.com/en-us/windows-hardware/drivers/install/driver-store", MatchBasis));
        AddWindows("SoftwareDistribution", new(
            "SoftwareDistribution · Windows Update 데이터",
            "Windows Update 다운로드 파일과 업데이트 관리 데이터베이스 저장 위치.",
            "Windows Update 서비스",
            "업데이트 작업 데이터. 업데이트 진행 중 삭제 시 설치 오류 가능.",
            "업데이트 오류 진단: Windows Update 문제 해결사. 캐시 초기화: 공식 복구 절차.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/additional-resources-for-windows-update", MatchBasis));
        AddWindows("Installer", new(
            "Installer · 앱 설치 캐시",
            "Windows Installer로 설치한 앱의 유지 관리용 설치 파일 캐시.",
            "Windows Installer · 설치한 각 앱의 설치 프로그램",
            "앱 복구·업데이트·제거에 필요. 캐시 삭제 시 해당 작업 실패 가능.",
            "설정 → 앱 → 설치된 앱에서 앱 제거.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/missing-windows-installer-cache", MatchBasis));
        AddWindows("Panther", new(
            "Panther · Windows 설치 기록",
            "Windows 설치·업그레이드 과정, 오류와 데이터 이전에 관한 진단 기록.",
            "Windows 설치 및 업그레이드 프로그램",
            "설치 문제 분석용. 삭제 시 이전 오류·데이터 이전 문제의 진단 자료 소실.",
            "Windows 저장소 정리의 설치 관련 항목으로 정리. 설치 문제 조사 중에는 진단 기록 보관 필요.",
            "https://learn.microsoft.com/en-us/windows/deployment/upgrade/log-files", MatchBasis));
        AddDrive("Windows.old", true, new(
            "Windows.old · 이전 Windows 설치",
            "Windows 업그레이드 이전 설치의 보관 위치. 이전 사용자 파일 포함 가능.",
            "Windows 설치 및 업그레이드 프로그램",
            "이전 버전 복원·이전 파일 확인에 필요. 삭제 시 이전 버전 복원 불가.",
            "설정 → 시스템 → 저장소 → 임시 파일 → 이전 Windows 설치. 삭제 전 필요한 파일의 백업 필요.",
            "https://support.microsoft.com/en-us/windows/deployment/install-upgrade/delete-your-previous-version-of-windows", MatchBasis));
        AddDrive("pagefile.sys", false, new(
            "pagefile.sys · 가상 메모리 파일",
            "메모리 일부를 디스크에 보관해 시스템 메모리 한도를 확장하는 파일. 오류 발생 시 메모리 덤프에도 사용 가능.",
            "Windows 메모리 관리자",
            "필요 여부·크기는 메모리 사용량과 덤프 설정에 따라 결정. 제거·과도한 축소 시 메모리 부족·오류 기록 누락 가능.",
            "Windows 가상 메모리 설정에서 크기·사용 여부 조정. 파일 직접 삭제 대신 설정으로 관리.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file", MatchBasis));
        AddDrive("hiberfil.sys", false, new(
            "hiberfil.sys · 최대 절전 모드 파일",
            "최대 절전 모드 등의 전원 기능을 위한 메모리 상태 저장 파일.",
            "Windows 커널 전원 관리자",
            "최대 절전 모드에 필요. 기능 해제 시 최대 절전 모드와 관련 전원 기능 사용 불가.",
            "공식 전원 관리 절차로 최대 절전 모드 사용 여부 변경. 하이브리드 절전 모드에도 영향. 파일 직접 삭제 대신 전원 기능으로 관리.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/disable-and-re-enable-hibernation", MatchBasis));
        AddDrive("$Recycle.Bin", true, new(
            "$Recycle.Bin · 휴지통 저장소",
            "이 드라이브의 휴지통 항목을 보관하는 Windows 관리 폴더. 사용자가 삭제한 개인 파일 포함 가능.",
            "Windows 파일 탐색기의 휴지통 기능",
            "삭제 파일 복원용. 휴지통을 비우면 해당 파일의 휴지통 복원 불가.",
            "파일 탐색기 → 휴지통 → 비우기. 저장소 센스에서 휴지통 보관 기간 설정 가능.",
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
