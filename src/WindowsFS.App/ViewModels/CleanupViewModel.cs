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

public sealed class CleanupViewModel : INotifyPropertyChanged
{
    private readonly ICleanupContextReader? _contextReader;
    private readonly Action<string> _navigate;
    private ScanResult? _result;
    private CancellationTokenSource? _cancellation;
    private Task _load = Task.CompletedTask;
    private IReadOnlyList<CleanupGroupRow> _all = [];
    private CleanupGroupRow? _selectedGroup;
    private CleanupBucketRow? _selectedBucket;
    private string _search = "";
    public CleanupViewModel(ICleanupContextReader? contextReader, Action<string> navigate)
    {
        _contextReader = contextReader; _navigate = navigate;
        BrowseFolderCommand = new RelayCommand(value => { if (value is CleanupLocationRow row && Locations.Contains(row)) _navigate(row.Path); });
        OpenHelpCommand = new RelayCommand(_ => OpenHelp(), _ => SelectedBucket is not null);
    }
    public IReadOnlyList<CleanupGroupRow> Groups { get; private set; } = [];
    public IReadOnlyList<CleanupBucketRow> Buckets { get; private set; } = [];
    public IReadOnlyList<CleanupLocationRow> Locations { get; private set; } = [];
    public string Status { get; private set; } = "";
    public string ScopeNote { get; private set; } = "";
    public bool IsLoading { get; private set; }
    public bool HasSelection => SelectedBucket is not null;
    public bool NoGroups => !IsLoading && Groups.Count == 0;
    public string Search { get => _search; set { if (_search == value) return; _search = value; Changed(); Filter(); } }
    public CleanupGroupRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            _selectedGroup = value; Changed();
            Buckets = value?.Group.Buckets.Select(bucket => new CleanupBucketRow(bucket)).ToArray() ?? [];
            Changed(nameof(Buckets)); SelectedBucket = Buckets.FirstOrDefault();
        }
    }
    public CleanupBucketRow? SelectedBucket
    {
        get => _selectedBucket;
        set
        {
            _selectedBucket = value; Changed(); Changed(nameof(HasSelection));
            Locations = value?.Bucket.Locations.Select(location => new CleanupLocationRow(location)).ToArray() ?? [];
            Changed(nameof(Locations)); CommandManager.InvalidateRequerySuggested();
        }
    }
    public ICommand BrowseFolderCommand { get; }
    public ICommand OpenHelpCommand { get; }
    public void Reset()
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
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
    public void Cancel() => _cancellation?.Cancel();
    private async Task LoadAsync(ScanResult result, CancellationToken token)
    {
        IsLoading = true; Status = "앱·용도별 집계 중";
        Changed(nameof(IsLoading)); Changed(nameof(Status)); Changed(nameof(NoGroups));
        try
        {
            CleanupContext context;
            try { context = _contextReader is null ? CleanupContext.Empty : await _contextReader.ReadAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token); }
            catch (TimeoutException) { context = CleanupContext.Empty with { InventoryStatus = "설치 등록 정보 시간 초과 · 미분류 집계" }; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { context = CleanupContext.Empty with { InventoryStatus = "설치 등록 정보 접근 불가 · 미분류 집계" }; }
            var groups = await Task.Run(() => CleanupAnalyzer.Analyze(result, context, token), token);
            if (token.IsCancellationRequested || !ReferenceEquals(result, _result)) return;
            _all = groups.Select(group => new CleanupGroupRow(group)).ToArray();
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
        Groups = string.IsNullOrWhiteSpace(_search) ? _all : _all.Where(group => group.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
            group.Group.Buckets.Any(bucket => bucket.Policy.Title.Contains(_search, StringComparison.OrdinalIgnoreCase) || bucket.Policy.Assessment.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToArray();
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
