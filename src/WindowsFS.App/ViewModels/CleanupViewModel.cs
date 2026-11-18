using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WindowsFS.App.Services;
using WindowsFS.Core;
using WindowsFS.Core.Cleanup;

namespace WindowsFS.App.ViewModels;

public sealed record CleanupGroupRow(CleanupGroup Group)
{
    public string Name => Group.Name;
    public string Size => DisplayFormat.Bytes(Group.Bytes);
    public string Summary => $"{Group.FileCount:N0}개 파일 · {Group.Buckets.Count:N0}개 용도 묶음";
}
public sealed record CleanupBucketRow(CleanupBucket Bucket)
{
    public string Title => Bucket.Policy.Title;
    public string Assessment => Bucket.Policy.Assessment;
    public string Size => DisplayFormat.Bytes(Bucket.Bytes);
    public string Count => $"{Bucket.FileCount:N0}개";
    public string Purpose => Bucket.Policy.Purpose;
    public string Impact => Bucket.Policy.Impact;
    public string Method => Bucket.Policy.Method;
    public string Evidence => Bucket.Evidence;
    public string Source => Bucket.Policy.Source;
}
public sealed record CleanupLocationRow(CleanupLocation Location)
{
    public string Path => Location.Path;
    public string Summary => $"{DisplayFormat.Bytes(Location.Bytes)} · 직접 포함 파일 {Location.FileCount:N0}개";
}
public sealed record CleanupFileRow(FileRecord File)
{
    public string Name => File.Name;
    public string Path => File.Path;
    public long Bytes => File.Size;
    public string Size => DisplayFormat.Bytes(Bytes);
}

public sealed class CleanupViewModel : INotifyPropertyChanged
{
    private readonly ICleanupContextReader? _contextReader;
    private readonly Action<string> _navigate;
    private readonly ICleanupFilesReader? _filesReader;
    private CancellationTokenSource? _filesCancellation;
    private Task _filesLoad = Task.CompletedTask;
    private readonly Dictionary<CleanupBucket, CleanupFilesResult> _fileCache = [];
    private readonly Queue<CleanupBucket> _cacheOrder = [];
    private ScanResult? _result;
    private CancellationTokenSource? _cancellation;
    private Task _load = Task.CompletedTask;
    private IReadOnlyList<CleanupGroupRow> _all = [];
    private CleanupGroupRow? _selectedGroup;
    private CleanupBucketRow? _selectedBucket;
    private string _search = "";
    private bool _candidatesOnly, _analysisReady;
    private double _sidebarWidth = 360;
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set { if (!double.IsFinite(value)) return; _sidebarWidth = Math.Clamp(value, 280, 720); Changed(); }
    }
    public bool CandidatesOnly
    {
        get => _candidatesOnly;
        set { if (_candidatesOnly == value) return; _candidatesOnly = value; Changed(); Filter(); }
    }
    public CleanupViewModel(ICleanupContextReader? contextReader, Action<string> navigate, ICleanupFilesReader? filesReader = null)
    {
        _contextReader = contextReader; _navigate = navigate; _filesReader = filesReader;
        BrowseFolderCommand = new RelayCommand(value => { if (value is CleanupLocationRow row && Locations.Contains(row)) _navigate(row.Path); });
        OpenHelpCommand = new RelayCommand(_ => OpenHelp(), _ => SelectedBucket is not null);
        RefreshFilesCommand = new RelayCommand(_ => StartFiles(force: true), _ => SelectedBucket is not null && !FilesLoading && _filesReader is not null);
    }
    public IReadOnlyList<CleanupGroupRow> Groups { get; private set; } = [];
    public IReadOnlyList<CleanupBucketRow> Buckets { get; private set; } = [];
    public IReadOnlyList<CleanupLocationRow> Locations { get; private set; } = [];
    public string Status { get; private set; } = "";
    public string ScopeNote { get; private set; } = "";
    public bool IsLoading { get; private set; }
    public bool HasSelection => SelectedBucket is not null;
    public bool NoGroups => !IsLoading && Groups.Count == 0;
    public IReadOnlyList<CleanupFileRow> Files { get; private set; } = [];
    private CleanupFileRow? _selectedFile;
    public CleanupFileRow? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (value is not null && !Files.Any(file => ReferenceEquals(file, value))) return;
            _selectedFile = value; Changed();
            FileSelectionChanged?.Invoke(value?.File);
        }
    }
    public event Action<FileRecord?>? FileSelectionChanged;
    public bool FilesLoading { get; private set; }
    public string FilesStatus { get; private set; } = "";
    public bool NoFiles => HasSelection && !FilesLoading && Files.Count == 0;
    public string Search { get => _search; set { if (_search == value) return; _search = value; Changed(); Filter(); } }
    public CleanupGroupRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            var previous = SelectedBucket?.Bucket;
            _selectedGroup = value; Changed();
            Buckets = value?.Group.Buckets.Select(bucket => new CleanupBucketRow(bucket)).ToArray() ?? [];
            Changed(nameof(Buckets)); SelectedBucket = Buckets.FirstOrDefault(bucket => ReferenceEquals(bucket.Bucket, previous)) ?? Buckets.FirstOrDefault();
        }
    }
    public CleanupBucketRow? SelectedBucket
    {
        get => _selectedBucket;
        set
        {
            var same = ReferenceEquals(_selectedBucket?.Bucket, value?.Bucket);
            _selectedBucket = value; Changed(); Changed(nameof(HasSelection));
            Locations = value?.Bucket.Locations.Select(location => new CleanupLocationRow(location)).ToArray() ?? [];
            Changed(nameof(Locations)); CommandManager.InvalidateRequerySuggested();
            if (!same) StartFiles();
        }
    }
    public ICommand BrowseFolderCommand { get; }
    public ICommand OpenHelpCommand { get; }
    public ICommand RefreshFilesCommand { get; }
    public void Reset()
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        _analysisReady = false; _fileCache.Clear(); _cacheOrder.Clear();
        _result = null; _all = []; Groups = []; SelectedGroup = null; IsLoading = false; Status = ""; ScopeNote = "";
        _search = ""; Changed(nameof(Search));
        foreach (var name in new[] { nameof(Groups), nameof(Status), nameof(ScopeNote), nameof(IsLoading), nameof(NoGroups) }) Changed(name);
    }
    public void Load(ScanResult result)
    {
        if (ReferenceEquals(result, _result)) return;
        Reset(); _result = result;
        _cancellation = new CancellationTokenSource();
        _load = LoadAsync(result, _cancellation.Token);
    }
    public Task WaitAsync() => _load;
    public void Cancel() { _cancellation?.Cancel(); _filesCancellation?.Cancel(); }
    public Task WaitForFilesAsync() => _filesLoad;

    private void StartFiles(bool force = false)
    {
        _filesCancellation?.Cancel(); _filesCancellation?.Dispose();
        _filesCancellation = null; _filesLoad = Task.CompletedTask; Files = []; FilesStatus = ""; FilesLoading = false;
        SelectedFile = null;
        if (SelectedBucket is { } selection && _filesReader is not null)
        {
            if (!force && _fileCache.TryGetValue(selection.Bucket, out var cached)) SetFiles(selection.Bucket, cached);
            else
            {
                var request = _filesCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                FilesLoading = true; FilesStatus = "현재 파일 메타데이터 읽는 중";
                _filesLoad = LoadFilesAsync(selection.Bucket, request);
            }
        }
        NotifyFiles();
    }
    private async Task LoadFilesAsync(CleanupBucket bucket, CancellationTokenSource request)
    {
        try
        {
            var result = await _filesReader!.ReadAsync(bucket, request.Token).WaitAsync(request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(_filesCancellation, request)) return;
            if (!_fileCache.ContainsKey(bucket)) _cacheOrder.Enqueue(bucket);
            _fileCache[bucket] = result;
            while (_cacheOrder.Count > 3) _fileCache.Remove(_cacheOrder.Dequeue());
            SetFiles(bucket, result);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_filesCancellation, request)) FilesStatus = "파일 목록 읽기 중단";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (ReferenceEquals(_filesCancellation, request)) FilesStatus = "파일 목록 접근 불가";
        }
        finally
        {
            if (ReferenceEquals(_filesCancellation, request)) { FilesLoading = false; NotifyFiles(); }
        }
    }
    private void SetFiles(CleanupBucket bucket, CleanupFilesResult result)
    {
        Files = result.Files.Select(file => new CleanupFileRow(file)).ToArray();
        var bytes = result.Files.Sum(file => file.Size);
        FilesStatus = $"{result.ReadAt:HH:mm:ss} 조회 · {Files.Count:N0}개 · {DisplayFormat.Bytes(bytes)}" +
            (result.SkippedLocations > 0 ? $" · {result.SkippedLocations:N0}개 위치 접근 불가" : "") +
            (result.Limited ? " · 일부 목록: 최대 100,000개·15초" : "") +
            (!result.Limited && result.SkippedLocations == 0 && (Files.Count != bucket.FileCount || bytes != bucket.Bytes) ? " · 저장된 집계와 차이 있음" : "");
    }
    private void NotifyFiles()
    {
        Changed(nameof(Files)); Changed(nameof(FilesStatus)); Changed(nameof(FilesLoading)); Changed(nameof(NoFiles));
        CommandManager.InvalidateRequerySuggested();
    }
    internal async Task<CleanupContext> ReadContextAsync(CancellationToken token)
    {
        if (_contextReader is null) return CleanupContext.Empty with { InventoryStatus = "설치 등록 정보 미조회" };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await _contextReader.ReadAsync(budget.Token).WaitAsync(budget.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return CleanupContext.Empty with { InventoryStatus = "설치 등록 정보 시간 초과 · 미분류 집계" }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return CleanupContext.Empty with { InventoryStatus = "설치 등록 정보 접근 불가 · 미분류 집계" }; }
    }

    private async Task LoadAsync(ScanResult result, CancellationToken token)
    {
        IsLoading = true; Status = "앱·용도별 집계 중";
        Changed(nameof(IsLoading)); Changed(nameof(Status)); Changed(nameof(NoGroups));
        try
        {
            var context = await ReadContextAsync(token);
            var groups = await Task.Run(() => CleanupAnalyzer.Analyze(result, context, token), token);
            if (token.IsCancellationRequested || !ReferenceEquals(result, _result)) return;
            _all = groups.Select(group => new CleanupGroupRow(group)).ToArray();
            _analysisReady = true;
            Status = $"{groups.Count:N0}개 앱·관련 영역 · {groups.Sum(group => group.FileCount):N0}개 파일 · {DisplayFormat.Bytes(groups.Sum(group => group.Bytes))}";
            ScopeNote = "분석 당시 폴더 집계 기준 · 논리적 크기 · 실제 확보 용량과 다를 수 있음" +
                (result.IsCancelled || result.SkippedCount > 0 ? " · 부분 분석 또는 접근 제외 항목 있음" : "") +
                (context.InventoryStatus.Length > 0 ? " · " + context.InventoryStatus : "");
            Filter(); Changed(nameof(Status)); Changed(nameof(ScopeNote));
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!token.IsCancellationRequested && ReferenceEquals(result, _result)) { IsLoading = false; Changed(nameof(IsLoading)); Changed(nameof(NoGroups)); }
        }
    }
    private void Filter()
    {
        var previous = SelectedGroup?.Group.Id;
        var eligible = CandidatesOnly ? _all.Select(group => new CleanupGroupRow(new CleanupGroup(group.Group.Id, group.Name,
            group.Group.Buckets.Where(bucket => bucket.Policy.Assessment == "정리 후보").ToArray()))).Where(group => group.Group.Buckets.Count > 0).OrderByDescending(group => group.Group.Bytes).ToArray() : _all;
        Groups = string.IsNullOrWhiteSpace(_search) ? eligible : eligible.Where(group => group.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
            group.Group.Buckets.Any(bucket => bucket.Policy.Title.Contains(_search, StringComparison.OrdinalIgnoreCase) || bucket.Policy.Assessment.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (_analysisReady)
        {
            Status = (CandidatesOnly ? "정리 후보 · " : "") + $"{Groups.Count:N0}개 앱·관련 영역 · {Groups.Sum(group => group.Group.FileCount):N0}개 파일 · {DisplayFormat.Bytes(Groups.Sum(group => group.Group.Bytes))}";
            Changed(nameof(Status));
        }
        Changed(nameof(Groups)); Changed(nameof(NoGroups));
        SelectedGroup = Groups.FirstOrDefault(group => group.Group.Id == previous) ?? Groups.FirstOrDefault();
    }
    private void OpenHelp()
    {
        var url = SelectedBucket?.Source;
        if (url is not (CleanupAnalyzer.StorageHelp or CleanupAnalyzer.InstallerHelp or CleanupAnalyzer.ComponentsHelp or CleanupAnalyzer.ChromeHelp or CleanupAnalyzer.CodeHelp or CleanupAnalyzer.RegistryHelp)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { Status = "문서 열기 실패"; Changed(nameof(Status)); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
