namespace WindowsFS.Core.Cleanup;

public sealed record InstalledApp(string Name, string Location, string Publisher);
public sealed record CleanupContext(string Windows, string Profiles, string ProgramData, IReadOnlyList<string> ProgramFiles,
    IReadOnlyList<InstalledApp> InstalledApps, string InventoryStatus = "")
{
    public static CleanupContext Empty { get; } = new("", "", "", [], []);
}
public sealed record CleanupLocation(string Path, long Bytes, long FileCount);
public sealed record CleanupPolicy(string Id, string Title, string Assessment, string Purpose, string Impact, string Method, string Source);
public sealed record CleanupBucket(CleanupPolicy Policy, string Evidence, IReadOnlyList<CleanupLocation> Locations)
{
    public long Bytes { get; } = Locations.Sum(item => item.Bytes);
    public long FileCount { get; } = Locations.Sum(item => item.FileCount);
}
public sealed record CleanupGroup(string Id, string Name, IReadOnlyList<CleanupBucket> Buckets)
{
    public long Bytes { get; } = Buckets.Sum(item => item.Bytes);
    public long FileCount { get; } = Buckets.Sum(item => item.FileCount);
}

/// <summary>Pure classification of saved folder totals. Never enumerates, opens or modifies source files.</summary>
public static class CleanupAnalyzer
{
    public const string StorageHelp = "https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/free-up-drive-space-in-windows";
    public const string InstallerHelp = "https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/missing-windows-installer-cache";
    public const string ComponentsHelp = "https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11";
    public const string ChromeHelp = "https://support.google.com/chrome/answer/2392709";
    public const string CodeHelp = "https://code.visualstudio.com/docs/setup/uninstall";
    public const string RegistryHelp = "https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key";
    private static readonly CleanupPolicy Unknown = new("unknown", "용도 미확인", "판단 불가", "파일의 용도와 소유 앱을 확정할 수 없는 데이터",
        "파일명이나 오래된 수정 날짜만으로 불필요 여부를 판단할 수 없음", "관련 앱과 실제 내용을 확인한 후 판단", StorageHelp);
    private static readonly CleanupPolicy Install = new("install", "설치 파일·구성요소", "유지 권장", "앱 실행·업데이트에 사용하는 설치 영역",
        "일부 파일 제거 시 실행·업데이트·복구 실패 가능", "사용하지 않는 앱은 Windows 또는 해당 앱의 정상 제거 절차로 관리", StorageHelp);
    private static readonly CleanupPolicy Profile = new("profile", "설정·프로필·앱 데이터", "유지 권장", "앱 설정과 사용자 상태를 포함할 수 있는 데이터",
        "로그인 정보, 설정, 확장 기능 또는 저장 데이터 손실 가능", "앱의 데이터 관리 기능과 백업 상태 확인", CodeHelp);
    private static readonly CleanupPolicy Personal = new("personal", "사용자 파일", "조건 확인 필요", "사용자가 저장하거나 생성한 콘텐츠가 포함될 수 있는 영역",
        "원본 문서·다운로드·사진·작업 파일 손실 가능", "내용과 백업 유무를 기준으로 사용자 판단", StorageHelp);
    private static readonly CleanupPolicy System = new("system", "Windows 구성요소", "유지 권장", "Windows 실행 및 유지관리에 사용하는 시스템 영역",
        "수동 제거 시 운영체제·앱 업데이트 또는 복구에 영향", "Windows 저장소 관리 기능의 정리 항목을 기준으로 판단", StorageHelp);
    private static readonly CleanupPolicy Installer = new("installer", "설치·복구 캐시", "유지 권장", "설치된 제품과 Windows의 업데이트·복구 구성요소",
        "캐시라는 이름이어도 제거 시 업데이트·복구·제거 작업 실패 가능", "직접 파일 삭제 대상에서 제외", InstallerHelp);
    private static readonly CleanupPolicy Temp = new("temp", "임시·업데이트 데이터", "조건 확인 필요", "앱 실행 또는 업데이트 중 사용하는 임시 영역",
        "사용 중인 작업이나 업데이트에 필요한 파일이 포함될 수 있음", "Windows 저장소의 임시 파일 정리 항목에서 확인", StorageHelp);
    private static readonly CleanupPolicy Cache = new("cache", "재생성 가능한 캐시", "정리 후보", "알려진 앱의 캐시 경로와 일치하는 데이터",
        "정리 후 재다운로드·재생성으로 초기 실행이 느려질 수 있음. 사용 중 여부는 미확인",
        "앱 내부의 캐시 관리 기능에서 해당 데이터 범위 확인", ChromeHelp);
    private static readonly CleanupPolicy Logs = new("logs", "진단 로그", "조건 확인 필요", "앱의 실행·오류 진단 기록",
        "정리하면 과거 오류 분석에 필요한 기록이 사라질 수 있음", "진단 필요성과 해당 앱의 기록 관리 방식 확인", CodeHelp);
    private sealed record Match(string Id, string Name, CleanupPolicy Policy, string Evidence);

    public static IReadOnlyList<CleanupGroup> Analyze(ScanResult result, CleanupContext context, CancellationToken token = default)
    {
        var installs = context.InstalledApps.Where(app => ValidInstall(app.Location, context))
            .GroupBy(app => Normalize(app.Location), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<(string Id, string Policy, string Evidence), (Match Match, List<CleanupLocation> Locations)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<FolderNode>(result.Roots);
        while (stack.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(folder.Path)) continue;
            long childBytes = 0, childCount = 0;
            foreach (var child in folder.Children) { childBytes += child.TotalBytes; childCount += child.FileCount; stack.Push(child); }
            // Direct-file totals partition the tree: a parent and its descendants are never counted twice.
            var bytes = Math.Max(0, folder.TotalBytes - childBytes);
            var count = Math.Max(0, folder.FileCount - childCount);
            if (bytes == 0 && count == 0) continue;
            var match = Classify(Normalize(folder.Path), context, installs);
            var key = (match.Id, match.Policy.Id, match.Evidence);
            if (!groups.TryGetValue(key, out var bucket)) groups[key] = bucket = (match, []);
            bucket.Locations.Add(new(folder.Path, bytes, count));
        }
        return groups.Values.GroupBy(value => value.Match.Id).Select(group => new CleanupGroup(group.Key, group.First().Match.Name,
            group.Select(value => new CleanupBucket(value.Match.Policy, value.Match.Evidence,
                value.Locations.OrderByDescending(item => item.Bytes).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray()))
                .OrderByDescending(bucket => bucket.Bytes).ToArray()))
            .OrderByDescending(group => group.Bytes).ThenBy(group => group.Name, StringComparer.Ordinal).ToArray();
    }

    private static Match Classify(string path, CleanupContext context, Dictionary<string, InstalledApp[]> installs)
    {
        if (Relative(path, context.Windows) is { } system)
        {
            var policy = Under(system, "Installer") ? Installer : Under(system, "WinSxS") ? System with
                { Id = "components", Title = "Windows 구성요소 저장소", Source = ComponentsHelp } :
                Under(system, "Temp") || Under(system, @"SoftwareDistribution\Download") ? Temp : System;
            return new("windows", "Windows", policy, "Windows 표준 위치 일치 · 파일 생성자·진위 검증 아님");
        }
        if (Relative(path, context.Profiles) is { } profile)
        {
            var parts = profile.Split('\\', 2);
            var tail = parts.Length == 2 ? parts[1] : "";
            foreach (var area in new[] { @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow" })
            {
                if (Relative(tail, area) is not { } data) continue;
                var known = KnownData(data, area.EndsWith("Roaming", StringComparison.Ordinal));
                if (known is not null) return known;
                if (area == @"AppData\Local" && Under(data, "Temp")) return new("user-temp", "사용자 임시 데이터", Temp, "사용자 프로필의 표준 임시 경로 일치");
                if (area == @"AppData\Local" && Relative(data, "Packages") is { Length: > 0 } package)
                {
                    var family = package.Split('\\')[0];
                    return new("package:" + family, "패키지 앱 · " + family, Profile with { Source = StorageHelp }, "패키지 데이터 경로 추정 · 현재 설치 여부 미확인");
                }
                var registered = Registered(path, installs);
                if (registered is not null) return registered;
                var name = data.Split('\\')[0];
                return new("data:" + name.ToUpperInvariant(), name.Length > 0 ? name + " · 앱 데이터" : "앱 데이터", Unknown,
                    "AppData 하위 경로 추정 · 설치 정보와 연결되지 않음. 폴더 이름은 소유 앱의 증명이 아님");
            }
            return Registered(path, installs) ?? new("personal", "사용자 파일", Personal, "사용자 프로필 영역 · 내용과 필요성 미확인");
        }
        var install = Registered(path, installs);
        if (install is not null) return install;
        foreach (var root in context.ProgramFiles)
            if (Relative(path, root) is { } app)
            {
                var name = app.Split('\\')[0];
                return new("install-path:" + name.ToUpperInvariant(), name.Length > 0 ? name : "프로그램 설치 영역", Install,
                    "프로그램 설치 경로 추정 · 설치 등록 정보와 연결되지 않음");
            }
        if (Relative(path, context.ProgramData) is { } shared)
        {
            var name = shared.Split('\\')[0];
            return new("shared:" + name.ToUpperInvariant(), "공유 앱 데이터" + (name.Length > 0 ? " · " + name : ""), Unknown,
                "ProgramData 경로 일치 · 여러 앱·사용자가 공유할 수 있음");
        }
        return new("unknown", "미분류", Unknown, "알려진 앱·용도 규칙과 일치하지 않음");
    }

    private static Match? KnownData(string data, bool roaming)
    {
        foreach (var browser in new[] { (Root: @"Google\Chrome\User Data", Name: "Google Chrome"), (Root: @"Microsoft\Edge\User Data", Name: "Microsoft Edge") })
        {
            if (roaming || Relative(data, browser.Root) is not { } tail) continue;
            var parts = tail.Split('\\');
            var profile = parts[0].Equals("Default", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
            var cache = profile && parts.Length > 1 && new[] { "Cache", "Code Cache", "GPUCache" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
            var source = browser.Name == "Google Chrome" ? ChromeHelp : StorageHelp;
            return new("app:" + browser.Name, browser.Name, (cache ? Cache : Profile) with { Source = source },
                "앱별 표준 데이터 경로 일치 · 경로 규칙 추정. 쿠키·설정·프로필 전체는 캐시로 분류하지 않음");
        }
        if (roaming && Relative(data, "Code") is { } code)
        {
            var first = code.Split('\\')[0];
            var policy = first.Equals("logs", StringComparison.OrdinalIgnoreCase) ? Logs : Profile;
            return new("app:Microsoft Visual Studio Code", "Microsoft Visual Studio Code", policy,
                "VS Code 표준 사용자 데이터 경로 일치 · 사용자 설정과 확장 데이터 포함 가능");
        }
        if (roaming && Relative(data, "discord") is { } discord)
        {
            var cache = new[] { "Cache", "Code Cache", "GPUCache" }.Contains(discord.Split('\\')[0], StringComparer.OrdinalIgnoreCase);
            return new("app:Discord", "Discord", cache ? Cache with { Assessment = "조건 확인 필요", Source = StorageHelp } : Profile with { Source = StorageHelp },
                "Discord 표준 데이터 경로 일치 · 경로 규칙 추정");
        }
        return null;
    }

    private static Match? Registered(string path, Dictionary<string, InstalledApp[]> installs)
    {
        for (var current = path; current.Length > 3;)
        {
            if (installs.TryGetValue(current, out var entries))
            {
                var names = entries.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
                if (names.Length > 1) return new("shared-install:" + current, "공유 설치 영역 · " + string.Join(" / ", names), Install,
                    "동일 위치에 여러 제품의 설치 등록 정보 존재 · 단일 앱 소유로 단정하지 않음");
                var name = names[0];
                if (name.StartsWith("Microsoft Visual Studio Code", StringComparison.OrdinalIgnoreCase)) name = "Microsoft Visual Studio Code";
                return new("app:" + name, name, Install, "현재 설치 등록 정보의 InstallLocation과 일치 · " +
                    (string.IsNullOrWhiteSpace(entries[0].Publisher) ? "게시자 미기재" : entries[0].Publisher) + " · 실제 생성자·진위 검증 아님");
            }
            var slash = current.LastIndexOf('\\');
            if (slash < 0) break;
            current = current[..slash];
        }
        return null;
    }

    private static bool ValidInstall(string path, CleanupContext context)
    {
        var normalized = Normalize(path);
        return normalized.Length > 3 && normalized.Length < 32768 && normalized[1] == ':' && normalized[2] == '\\' &&
            !new[] { context.Windows, context.Profiles, context.ProgramData }.Concat(context.ProgramFiles)
                .Any(root => normalized.Equals(Normalize(root), StringComparison.OrdinalIgnoreCase));
    }
    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
    private static bool Under(string path, string root) => Relative(path, root) is not null;
    private static string? Relative(string path, string root)
    {
        root = Normalize(root);
        if (root.Length == 0) return null;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        return path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : null;
    }
}
