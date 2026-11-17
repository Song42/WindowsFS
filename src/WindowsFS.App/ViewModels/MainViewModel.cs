using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using WindowsFS.App.Controls;
using WindowsFS.Core;
using WindowsFS.Core.Persistence;
using WindowsFS.Core.SystemItems;

namespace WindowsFS.App.ViewModels;

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

public sealed class DriveItem(string path, string label, string capacity, double usedPercent) : INotifyPropertyChanged
{
    private bool _selected;
    public string Path { get; } = path;
    public string Label { get; } = label;
    public string Capacity { get; } = capacity;
    public double UsedPercent { get; } = usedPercent;
    public bool IsSelected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record ResultRow(string Name, string Path, long Bytes, long Count, bool IsDirectory, string Kind, double Share)
{
    public string Size => DisplayFormat.Bytes(Bytes);
    public string CountText => Count < 0 ? Kind : $"{Count:N0}개 파일";
    public string ShareText => $"{Share:0.0}%";
    public string Glyph => IsDirectory ? "\uE8B7" : Kind == "파일 형식" ? "\uE8A5" : "\uE7C3";
}

public static class DisplayFormat
{
    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double n = value;
        var unit = 0;
        while (n >= 1024 && unit < units.Length - 1) { n /= 1024; unit++; }
        return $"{n.ToString(unit == 0 ? "N0" : "0.##", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDiskScanner _scanner;
    private readonly IScanResultStore? _store;
    private readonly SystemItemCatalog _systemCatalog;
    private Task? _pendingSave;
    private Task? _pendingDeletion;
    private CancellationTokenSource? _scanCancellation;
    private ScanResult? _result;
    private Dictionary<string, FolderNode> _folders = new(StringComparer.Ordinal);
    private Dictionary<string, string> _parents = new(StringComparer.Ordinal);
    private string? _selectedPath;
    private string? _folderPath;
    private string _resultScope = "";
    private string _view = "overview";
    private string _search = "";
    private bool _busy;
    private bool _drivesLoaded;
    private bool _isRestoring;
    private bool _isSaving;
    private bool _isDeleting;
    private bool _hasSavedAnalysis;
    private bool _isRestored;
    private bool _snapshotSaved;
    private DateTimeOffset? _scannedAtUtc;
    private string _status = "분석 대기";
    private string _currentPath = "";
    private long _bytes, _files, _folderCount, _skipped;
    private TimeSpan _elapsed;
    private IReadOnlyList<ResultRow> _rows = [];
    private IReadOnlyList<MapItem> _mapItems = [];
    private SpaceMapLayout _mapLayout = SpaceMapLayout.Blocks;
    private ResultRow? _selectedRow;
    private SystemItemInfo? _systemInfo;
    private bool _systemInfoDismissed;

    public MainViewModel() : this(new DiskScanner(), new FileScanResultStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsFS", "Cache", "latest.scan.json.gz"))) { }

    // Injected scanners use an explicit store so tests and alternative hosts never write a user's cache implicitly.
    public MainViewModel(IDiskScanner scanner, IScanResultStore? store = null, SystemItemCatalog? systemCatalog = null)
    {
        _scanner = scanner;
        _store = store;
        _systemCatalog = systemCatalog ?? new SystemItemCatalog();
        ScanCommand = new RelayCommand(async _ => await ScanAsync(), _ => IsIdle && _drivesLoaded && (_selectedPath is not null || Drives.Count > 0));
        CancelCommand = new RelayCommand(_ =>
        {
            if (_scanCancellation is null) return;
            _scanCancellation.Cancel();
            Status = "중지 중 · 부분 결과 집계";
        }, _ => IsBusy && !IsSaving && _scanCancellation is not null);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => IsIdle);
        DeleteSavedAnalysisCommand = new RelayCommand(async _ => await DeleteSavedAnalysisAsync(), _ => _store is not null && IsIdle && HasSavedAnalysis);
        SelectDriveCommand = new RelayCommand(p => SelectPath(p as string), _ => IsIdle);
        NavigateCommand = new RelayCommand(p => { _view = p as string ?? "overview"; _search = ""; Changed(nameof(Search)); RefreshView(); });
        SetMapLayoutCommand = new RelayCommand(p =>
        {
            if (p is "blocks") MapLayout = SpaceMapLayout.Blocks;
            else if (p is "donut") MapLayout = SpaceMapLayout.Donut;
        });
        OpenRowCommand = new RelayCommand(p => OpenRow(p as ResultRow ?? SelectedRow), _ => !IsBusy);
        RevealCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedRow is { Path.Length: > 0 } && _view != "types");
        CopyPathCommand = new RelayCommand(_ => CopySelectedPath(), _ => SelectedRow is { Path.Length: > 0 } && _view != "types");
        UpCommand = new RelayCommand(_ => { _folderPath = _folderPath is not null && _parents.TryGetValue(_folderPath, out var parent) ? parent : null; RefreshView(); }, _ => _folderPath is not null);
        HomeCommand = new RelayCommand(_ => { _folderPath = null; RefreshView(); });
        DismissSystemInfoCommand = new RelayCommand(_ => { _systemInfoDismissed = true; NotifySystemInfo(); });
        ShowSystemInfoCommand = new RelayCommand(_ => { _systemInfoDismissed = false; NotifySystemInfo(); }, _ => CanShowSystemInfo);
        OpenSystemHelpCommand = new RelayCommand(_ => OpenSystemHelp(), _ => CanShowSystemInfo);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DriveItem> Drives { get; } = [];
    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand DeleteSavedAnalysisCommand { get; }
    public ICommand SelectDriveCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand SetMapLayoutCommand { get; }
    public ICommand OpenRowCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand DismissSystemInfoCommand { get; }
    public ICommand ShowSystemInfoCommand { get; }
    public ICommand OpenSystemHelpCommand { get; }
    public SystemItemInfo? SystemInfo => _systemInfo;
    public bool CanShowSystemInfo => _systemInfo is not null;
    public bool HasSystemInfo => CanShowSystemInfo && !_systemInfoDismissed;
    public bool IsMapVisible => ShowMap && !HasSystemInfo;
    public bool IsBusy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(IsIdle)); Changed(nameof(ScanLabel)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsIdle => !IsBusy && !IsRestoring && !IsSaving && !IsDeleting;
    public bool IsRestoring { get => _isRestoring; private set { _isRestoring = value; Changed(); Changed(nameof(IsIdle)); Changed(nameof(ScanState)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsSaving { get => _isSaving; private set { _isSaving = value; Changed(); Changed(nameof(IsIdle)); Changed(nameof(ScanState)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsDeleting { get => _isDeleting; private set { _isDeleting = value; Changed(); Changed(nameof(IsIdle)); Changed(nameof(ScanState)); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasSavedAnalysis { get => _hasSavedAnalysis; private set { _hasSavedAnalysis = value; Changed(); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasResult => _result is not null;
    public bool ShowEmpty => _result is null;
    public bool ShowMap => _view is "overview" or "folders";
    public bool ShowFolderNavigation => _view == "folders";
    public bool IsOverview => _view == "overview";
    public bool IsDetailView => !IsOverview;
    public bool IsFiles => _view == "files";
    public bool IsFolders => _view == "folders";
    public bool IsTypes => _view == "types";
    public bool IsAllDrivesSelected => _selectedPath is null;
    public string ResultScope => $"현재 결과 · {_resultScope}";
    public string ScanLabel => _result is null ? "분석 시작" : "새로 분석";
    public string SnapshotDescription => _scannedAtUtc is null ? "" :
        $"{_scannedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 분석 · " +
        (_snapshotSaved ? "저장됨" :
         _result?.IsCancelled == true ? "부분 결과 · 미저장" : "미저장");
    public string TargetLabel => _selectedPath ?? "모든 로컬 드라이브";
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string CurrentPath { get => _currentPath; private set { _currentPath = value; Changed(); } }
    public string TotalSize => DisplayFormat.Bytes(_bytes);
    public string FileCount => _files.ToString("N0");
    public string FolderCount => $"{_folderCount:N0}개 폴더";
    public string LargestFileCount => $"{_result?.LargestFiles.Count ?? 0:N0}개 파일";
    public string FileTypeCount => $"{_result?.FileTypes.Count ?? 0:N0}개 형식";
    public string Elapsed => _elapsed.TotalHours >= 1 ? _elapsed.ToString(@"h\:mm\:ss") : _elapsed.ToString(@"mm\:ss");
    public string Speed => _elapsed.TotalSeconds > 0 ? $"{_files / _elapsed.TotalSeconds:N0} 파일/초" : "분석 대기";
    public string Skipped => _skipped.ToString("N0");
    public string ScanState => IsDeleting ? "삭제 중" : IsRestoring ? "불러오는 중" : IsSaving ? "저장 중" : IsBusy ? "분석 중" : _result is null ? "준비됨" : _result.IsCancelled ? "부분 결과" : _isRestored ? "저장된 분석" : "분석 완료";
    public string PageTitle => _view switch { "files" => "가장 큰 파일", "folders" => "폴더 탐색", "types" => "파일 형식", _ => "대시보드" };
    public string PageDescription => _view switch { "files" => "최대 1,000개 · 용량순", "folders" => "폴더별 용량 · 두 번 클릭: 하위 폴더 열기", "types" => "확장자별 용량 합계", _ => "파일·폴더별 용량 분포" };
    public string TableTitle => _view switch { "folders" => "하위 폴더", "types" => "형식별 사용량", _ => "가장 큰 파일" };
    public string TableHint => _view == "folders" ? "선택: 시스템 항목 설명 · 두 번 클릭: 폴더 열기" : _view == "types" ? "확장자 없는 파일 포함" : "상위 1,000개 · 선택: 시스템 항목 설명";
    public string Breadcrumb => _folderPath ?? "분석한 모든 드라이브 / 폴더";
    public string MapTitle => _view == "folders" ? "폴더 공간 구성" : "공간 구성";
    public SpaceMapLayout MapLayout
    {
        get => _mapLayout;
        private set
        {
            if (_mapLayout == value) return;
            _mapLayout = value;
            Changed(); Changed(nameof(IsBlockMap)); Changed(nameof(IsDonutMap)); Changed(nameof(MapHint));
        }
    }
    public bool IsBlockMap => MapLayout == SpaceMapLayout.Blocks;
    public bool IsDonutMap => MapLayout == SpaceMapLayout.Donut;
    public string MapHint => IsDonutMap ? "각도 = 용량 비율 · 클릭: 폴더 열기" : "영역 크기 = 용량 · 클릭: 폴더 열기";
    public string ResultCount => $"{Rows.Count:N0}개 항목";
    public string Search { get => _search; set { if (_search == value) return; _search = value; Changed(); RefreshRows(); } }
    public IReadOnlyList<ResultRow> Rows { get => _rows; private set { _rows = value; Changed(); Changed(nameof(ResultCount)); Changed(nameof(NoRows)); } }
    public bool NoRows => _result is not null && Rows.Count == 0;
    public IReadOnlyList<MapItem> MapItems { get => _mapItems; private set { _mapItems = value; Changed(); } }
    public ResultRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            _selectedRow = value;
            Changed();
            UpdateSystemInfo();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public async Task InitializeAsync(string? initialPath = null)
    {
        var driveDiscovery = Task.Run(() =>
        {
            var drives = new List<DriveItem>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                    var total = drive.TotalSize;
                    drives.Add(new DriveItem(drive.Name, string.IsNullOrEmpty(drive.VolumeLabel) ? $"로컬 디스크 ({drive.Name.TrimEnd('\\')})" : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})", $"{DisplayFormat.Bytes(drive.AvailableFreeSpace)} 사용 가능 / {DisplayFormat.Bytes(total)}", total > 0 ? (double)(total - drive.TotalFreeSpace) / total * 100 : 0));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return drives;
        });
        // Load the saved metadata while drive capacity labels are being read. Never trigger a scan on a normal launch.
        if (string.IsNullOrWhiteSpace(initialPath)) await RestoreLastScanAsync();
        var items = await driveDiscovery;
        foreach (var drive in items) Drives.Add(drive);
        SelectPath(_selectedPath);
        _drivesLoaded = true;
        CommandManager.InvalidateRequerySuggested();
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            SelectPath(initialPath);
            await ScanAsync();
        }
    }

    public async Task RestoreLastScanAsync()
    {
        if (_store is null || !IsIdle) return;
        IsRestoring = true;
        Status = "저장된 분석 로딩 중";
        try
        {
            var saved = await _store.LoadAsync();
            if (saved is null)
            {
                HasSavedAnalysis = false;
                Status = "분석 대기";
                return;
            }
            if (saved.Result.IsCancelled) throw new InvalidDataException("Incomplete saved scan.");
            await ApplyResultAsync(saved.Result);
            SelectPath(saved.SelectedPath);
            _resultScope = TargetLabel;
            _scannedAtUtc = saved.ScannedAtUtc;
            _snapshotSaved = _isRestored = true;
            HasSavedAnalysis = true;
            Status = "저장된 분석";
            CurrentPath = "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable or corrupt cache must remain removable without loading its contents.
            HasSavedAnalysis = true;
            Status = "저장된 분석 로딩 실패";
        }
        finally
        {
            IsRestoring = false;
            NotifyResult();
            RefreshView();
        }
    }

    public async Task ScanAsync()
    {
        if (!IsIdle) return;
        var roots = _selectedPath is null ? Drives.Select(d => d.Path).ToArray() : [_selectedPath];
        if (roots.Length == 0) { Status = "분석 가능한 드라이브 없음"; return; }
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        IsBusy = true;
        _result = null;
        _isRestored = _snapshotSaved = false;
        _scannedAtUtc = null;
        Changed(nameof(SnapshotDescription));
        _resultScope = TargetLabel;
        Changed(nameof(ResultScope));
        _folders.Clear(); _parents.Clear(); _folderPath = null;
        Rows = []; MapItems = [];
        _bytes = _files = _folderCount = _skipped = 0; _elapsed = TimeSpan.Zero;
        Status = "분석 중";
        CurrentPath = TargetLabel;
        NotifyStatistics(); Changed(nameof(HasResult)); Changed(nameof(ShowEmpty)); Changed(nameof(NoRows));
        try
        {
            // Explicit --scan startup skips restoration; retain access to any previous cache
            // even when this scan is cancelled or its replacement cannot be saved.
            if (_store is not null && !HasSavedAnalysis)
            {
                try { HasSavedAnalysis = await _store.ExistsAsync(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    HasSavedAnalysis = true;
                }
            }
            var progress = new Progress<ScanProgress>(p =>
            {
                if (!ReferenceEquals(_scanCancellation, cancellation) || !IsBusy) return;
                _bytes = p.TotalBytes; _files = p.FileCount; _folderCount = p.FolderCount; _skipped = p.SkippedCount; _elapsed = p.Elapsed;
                CurrentPath = p.CurrentPath;
                NotifyStatistics();
            });
            var result = await _scanner.ScanAsync(roots, progress, cancellation.Token);
            _scanCancellation = null;
            CommandManager.InvalidateRequerySuggested();
            _scannedAtUtc = DateTimeOffset.UtcNow;
            await ApplyResultAsync(result);
            if (_selectedPath is not null && result.Roots.Count == 1)
            {
                // Persist the scanner's canonical target, including when --scan used a relative path.
                SelectPath(result.Roots[0].Path);
                _resultScope = TargetLabel;
            }
            Status = result.IsCancelled ? "분석 중지 · 부분 결과" : "분석 완료";
            CurrentPath = "";
            if (!result.IsCancelled && _store is not null)
            {
                IsSaving = true;
                Status = "분석 완료 · 저장 중";
                _pendingSave = SaveSnapshotAsync(new SavedScan(result, _selectedPath, _scannedAtUtc.Value));
                await _pendingSave;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            Status = "분석 실패 · 경로 또는 접근 권한 오류";
            CurrentPath = "";
        }
        finally
        {
            _scanCancellation = null;
            IsSaving = false;
            IsBusy = false;
            NotifyResult();
            RefreshView();
        }
    }

    public void Cancel() => _scanCancellation?.Cancel();

    public Task WaitForPendingSaveAsync() => _pendingSave ?? Task.CompletedTask;

    public Task WaitForPendingPersistenceAsync() => Task.WhenAll(
        _pendingSave ?? Task.CompletedTask, _pendingDeletion ?? Task.CompletedTask);

    public Task DeleteSavedAnalysisAsync()
    {
        if (_store is null || !IsIdle || !HasSavedAnalysis) return Task.CompletedTask;
        IsDeleting = true;
        Status = "저장된 분석 삭제 중";
        _pendingDeletion = DeleteSnapshotAsync();
        return _pendingDeletion;
    }

    private async Task DeleteSnapshotAsync()
    {
        try
        {
            await _store!.DeleteAsync();
            HasSavedAnalysis = false;
            _result = null;
            _folders.Clear(); _parents.Clear(); _folderPath = null;
            _resultScope = "";
            _search = "";
            _scannedAtUtc = null;
            _snapshotSaved = _isRestored = false;
            _bytes = _files = _folderCount = _skipped = 0;
            _elapsed = TimeSpan.Zero;
            CurrentPath = "";
            _pendingSave = null;
            Changed(nameof(Search));
            NotifyResult();
            RefreshView();
            Status = "저장된 분석 삭제됨";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Status = "저장된 분석 삭제 실패";
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private async Task SaveSnapshotAsync(SavedScan snapshot)
    {
        try
        {
            await _store!.SaveAsync(snapshot);
            _snapshotSaved = true;
            HasSavedAnalysis = true;
            Status = "분석 완료 · 저장됨";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Status = "분석 완료 · 저장 실패";
        }
    }

    private async Task ApplyResultAsync(ScanResult result)
    {
        // Indexing a saved tree does not touch any scanned paths or read their current metadata.
        var index = await Task.Run(() => IndexFolders(result.Roots));
        _result = result;
        _folders = index.Folders; _parents = index.Parents;
        _folderPath = null;
        _bytes = result.TotalBytes; _files = result.FileCount; _folderCount = result.FolderCount;
        _skipped = result.SkippedCount; _elapsed = result.Elapsed;
    }

    private void NotifyResult()
    {
        NotifyStatistics();
        foreach (var name in new[] { nameof(HasResult), nameof(ShowEmpty), nameof(NoRows), nameof(ScanLabel), nameof(ResultScope), nameof(SnapshotDescription) }) Changed(name);
    }

    public void NavigateMap(MapItem item)
    {
        if (!item.IsDirectory || !_folders.ContainsKey(item.Path)) return;
        _folderPath = item.Path;
        _view = "folders";
        _search = ""; Changed(nameof(Search));
        RefreshView();
    }

    private static (Dictionary<string, FolderNode> Folders, Dictionary<string, string> Parents) IndexFolders(IReadOnlyList<FolderNode> roots)
    {
        // Navigation uses paths from the result tree; preserve distinct entries in case-sensitive directories.
        var folders = new Dictionary<string, FolderNode>(StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new Stack<FolderNode>(roots);
        while (stack.TryPop(out var node))
        {
            folders[node.Path] = node;
            foreach (var child in node.Children) { parents[child.Path] = node.Path; stack.Push(child); }
        }
        return (folders, parents);
    }

    private void ChooseFolder()
    {
        var dialog = new FolderPickerWindow(_selectedPath, Drives.Select(drive => drive.Path))
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true || dialog.SelectedPath is null) return;
        SelectPath(dialog.SelectedPath);
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectPath(string? path)
    {
        _selectedPath = path;
        foreach (var drive in Drives) drive.IsSelected = string.Equals(drive.Path, path, StringComparison.OrdinalIgnoreCase);
        Changed(nameof(TargetLabel)); Changed(nameof(IsAllDrivesSelected));
    }

    private void RefreshView()
    {
        foreach (var name in new[] { nameof(PageTitle), nameof(PageDescription), nameof(TableTitle), nameof(TableHint), nameof(ShowMap), nameof(ShowFolderNavigation), nameof(Breadcrumb), nameof(MapTitle), nameof(IsOverview), nameof(IsDetailView), nameof(IsFiles), nameof(IsFolders), nameof(IsTypes) }) Changed(name);
        RefreshRows(); RefreshMap();
        Changed(nameof(IsMapVisible));
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateSystemInfo()
    {
        // Descriptions are an exact-path catalog lookup; selecting an item never opens its contents.
        _systemInfo = _view == "types" ? null : _selectedRow is { } row
            ? _systemCatalog.Describe(row.Path, row.IsDirectory)
            : _view == "folders" && _folderPath is not null
                ? _systemCatalog.Describe(_folderPath, true) : null;
        _systemInfoDismissed = false;
        NotifySystemInfo();
    }

    private void NotifySystemInfo()
    {
        Changed(nameof(SystemInfo)); Changed(nameof(CanShowSystemInfo));
        Changed(nameof(HasSystemInfo)); Changed(nameof(IsMapVisible));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenSystemHelp()
    {
        if (_systemInfo is null || !Uri.TryCreate(_systemInfo.SourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase))) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { Status = "문서 열기 실패"; }
    }

    private IEnumerable<FolderNode> ScopeFolders()
    {
        if (_result is null) return [];
        return _folderPath is not null && _folders.TryGetValue(_folderPath, out var node) ? node.Children : _result.Roots;
    }

    private double Share(long bytes) => _bytes > 0 ? (double)bytes / _bytes * 100 : 0;

    private void RefreshRows()
    {
        SelectedRow = null;
        if (_result is null) { Rows = []; return; }
        IEnumerable<ResultRow> rows = _view switch
        {
            "folders" => ScopeFolders().Select(n => new ResultRow(n.Name, n.Path, n.TotalBytes, n.FileCount, true, "폴더", Share(n.TotalBytes))),
            "types" => _result.FileTypes.Select(t => new ResultRow(string.IsNullOrEmpty(t.Extension) ? "확장자 없음" : t.Extension, "", t.TotalBytes, t.FileCount, false, "파일 형식", Share(t.TotalBytes))),
            _ => _result.LargestFiles.Select(f => new ResultRow(f.Name, f.Path, f.Size, -1, false, string.IsNullOrEmpty(f.Extension) ? "파일" : f.Extension, Share(f.Size)))
        };
        if (!string.IsNullOrWhiteSpace(_search)) rows = rows.Where(r => r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(_search, StringComparison.OrdinalIgnoreCase));
        Rows = rows.ToArray();
    }

    private void RefreshMap()
    {
        if (_result is null) { MapItems = []; return; }
        var scope = _view == "folders" ? ScopeFolders() : _result.Roots.Count == 1 ? _result.Roots[0].Children : _result.Roots;
        var entries = scope.Where(n => n.TotalBytes > 0).OrderByDescending(n => n.TotalBytes).ToList();
        long scopeTotal = _view == "folders" && _folderPath is not null && _folders.TryGetValue(_folderPath, out var selected) ? selected.TotalBytes : _result.TotalBytes;
        var maps = entries.Take(34).Select(n => new MapItem(n.Name, DisplayFormat.Bytes(n.TotalBytes), n.TotalBytes, n.Path, true)).ToList();
        var otherFolders = entries.Skip(34).Sum(n => n.TotalBytes);
        if (otherFolders > 0) maps.Add(new MapItem("그 외 폴더", DisplayFormat.Bytes(otherFolders), otherFolders, "", false));
        var direct = Math.Max(0, scopeTotal - entries.Sum(n => n.TotalBytes));
        if (direct > 0) maps.Add(new MapItem("직접 포함된 파일", DisplayFormat.Bytes(direct), direct, "", false));
        MapItems = maps;
    }

    private void OpenRow(ResultRow? row)
    {
        if (row is { IsDirectory: true }) NavigateMap(new MapItem(row.Name, row.Size, row.Bytes, row.Path, true));
        else if (row is { Path.Length: > 0 }) Reveal(row.Path);
    }

    private void RevealSelected() { if (SelectedRow is { Path.Length: > 0 } row) Reveal(row.Path); }
    private void Reveal(string path)
    {
        try
        {
            // Explorer selects the item; files are never executed by this app.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Win32Exception) { Status = "탐색기 열기 실패"; }
    }

    private void CopySelectedPath()
    {
        if (SelectedRow is not { Path.Length: > 0 } row) return;
        try { Clipboard.SetText(row.Path); Status = "경로 복사됨"; }
        catch (System.Runtime.InteropServices.COMException) { Status = "경로 복사 실패 · 클립보드 접근 오류"; }
    }

    private void NotifyStatistics()
    {
        foreach (var name in new[] { nameof(TotalSize), nameof(FileCount), nameof(FolderCount), nameof(LargestFileCount), nameof(FileTypeCount), nameof(Elapsed), nameof(Speed), nameof(Skipped), nameof(ScanState) }) Changed(name);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
