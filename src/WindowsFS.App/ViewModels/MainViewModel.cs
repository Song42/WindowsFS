using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.Core;
using WindowsFS.Core.Cleanup;
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
public enum FolderNavigationKind { None, Current, Parent }

public sealed record ResultRow(string Name, string Path, long Bytes, long Count, bool IsDirectory, string Kind, double Share)
{
    public FolderNavigationKind Navigation { get; init; }
    public bool IsLiveMetadata { get; init; }
    public string Size => DisplayFormat.Bytes(Bytes);
    public string CountText => Count < 0 ? Kind : $"{Count:N0}개 파일";
    public string ShareText => $"{Share:0.0}%";
    public string Glyph => IsDirectory ? "\uE8B7" : Kind == "파일 형식" ? "\uE8A5" : "\uE7C3";
}

public sealed record BreadcrumbItem(string Name, string? Path, bool IsCurrent);
internal sealed record QuestionTarget(ScanResult Scan, string Path, FolderNode? Folder)
{
    public bool IsFile => Folder is null;
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
    public CleanupViewModel Cleanup { get; }
    private Task? _pendingSave;
    private string? _snapshotTarget;
    private readonly Dictionary<(string Path, bool IsFile, string Question), FolderAnswer> _answers = [];
    internal Func<string, CancellationToken, Task<string>> AskItemAsync { get; init; } = FolderQuestions.AskAsync;
    public IReadOnlyList<FolderQuestionSection> QuestionSections { get; private set; } = [];
    internal QuestionTarget? SectionTarget { get; private set; }
    private readonly Dictionary<(QuestionTarget, string), Task<string>> _pendingQuestions = [];
    private CancellationTokenSource? _questionCancellation;

    private void UpdateQuestionSections()
    {
        QuestionTarget? target = null;
        if (_result is { } scan && DetailRow is { Path.Length: > 0 } row)
        {
            if (row.IsDirectory && _folders.TryGetValue(row.Path, out var folder))
                target = new(scan, row.Path, folder);
            else if (!row.IsDirectory && Path.GetDirectoryName(row.Path) is { } parent && _folders.ContainsKey(parent))
                target = new(scan, row.Path, null);
        }
        if (target == SectionTarget) return;
        SectionTarget = target;
        QuestionSections = target is null ? [] : [
            new(FolderQuestions.DeleteQuestion, () => AnswerAsync(target, FolderQuestions.DeleteQuestion)),
            new(FolderQuestions.PurposeQuestion, () => AnswerAsync(target, FolderQuestions.PurposeQuestion))];
        RefreshQuestionStatus();
        Changed(nameof(QuestionSections));
    }

    private void RefreshQuestionStatus()
    {
        if (SectionTarget is not { } target) return;
        foreach (var section in QuestionSections)
        {
            var status = FindAnswer(target.Path, target.IsFile, section.Question) is null && !_pendingQuestions.ContainsKey((target, section.Question))
                ? "요청하기" : "";
            section.SetRequestStatus(status);
        }
    }

    private async Task<string> AnswerAsync(QuestionTarget target, string question)
    {
        if (target != SectionTarget || !ReferenceEquals(target.Scan, _result) || IsRestoring || IsBusy || IsDeleting)
            throw new FolderQuestionException("선택한 분석 항목이 변경됨");
        if (FindAnswer(target.Path, target.IsFile, question) is { } saved) return saved.Text;
        var key = (target, question);
        if (_pendingQuestions.TryGetValue(key, out var pending)) return await pending;
        _questionCancellation ??= new CancellationTokenSource();
        var request = RequestAnswerAsync(target, question, _questionCancellation.Token);
        _pendingQuestions[key] = request;
        RefreshQuestionStatus();
        try { return await request; }
        finally
        {
            if (ReferenceEquals(_pendingQuestions.GetValueOrDefault(key), request))
            {
                _pendingQuestions.Remove(key);
                RefreshQuestionStatus();
            }
        }
    }

    private async Task<string> RequestAnswerAsync(QuestionTarget target, string question, CancellationToken token)
    {
        var input = await BuildQuestionInputAsync(target, question, token);
        token.ThrowIfCancellationRequested();
        var answer = await AskItemAsync(input, token);
        token.ThrowIfCancellationRequested();
        _ = RememberAnswerAsync(target, question, answer);
        return answer;
    }

    private void CancelQuestions()
    {
        _questionCancellation?.Cancel();
        _questionCancellation?.Dispose();
        _questionCancellation = null;
        _pendingQuestions.Clear();
        RefreshQuestionStatus();
    }
    internal FolderAnswer? FindAnswer(string path, bool isFile, string question) => _answers.GetValueOrDefault((path, isFile, question));
    internal async Task<string> BuildQuestionInputAsync(QuestionTarget target, string question, CancellationToken token)
    {
        if (target != SectionTarget || !ReferenceEquals(target.Scan, _result))
            throw new FolderQuestionException("선택한 분석 항목이 변경됨");
        if (target.Folder is not { } folder) return FolderQuestions.BuildFileInput(target.Path, question);
        var scannedAt = _scannedAtUtc;
        IReadOnlyList<FileRecord>? files = null;
        var status = "미조회";
        if (_fileBrowser is not null)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                files = await _fileBrowser.ReadAsync(folder.Path, budget.Token).WaitAsync(budget.Token);
                status = "조회 완료";
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { status = "시간 초과"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            { status = "조회 불가"; }
        }
        var readAt = DateTimeOffset.UtcNow;
        var context = await Cleanup.ReadContextAsync(token);
        var input = await Task.Run(() => FolderQuestions.BuildInput(folder, files, question,
            CleanupAnalyzer.DescribeFolder(folder, context, token), scannedAt, target.Scan.IsCancelled, target.Scan.SkippedCount,
            readAt, status), token);
        if (!ReferenceEquals(target.Scan, _result))
            throw new FolderQuestionException("선택한 분석 폴더가 변경됨");
        return input;
    }

    internal async Task<bool> RememberAnswerAsync(QuestionTarget target, string question, string text)
    {
        if (IsBusy || IsDeleting || IsRestoring || !ReferenceEquals(target.Scan, _result)) return false;
        _answers[(target.Path, target.IsFile, question)] = new(target.Path, question, text) { IsFile = target.IsFile };
        RefreshQuestionStatus();
        if (_store is null || _result is not { IsCancelled: false } || _scannedAtUtc is null) return false;
        var snapshot = new SavedScan(_result, _snapshotTarget, _scannedAtUtc.Value) { Answers = _answers.Values.ToArray() };
        IsSaving = true;
        var save = SaveAfterPreviousAsync(_pendingSave, snapshot);
        _pendingSave = save;
        try
        {
            return await save;
        }
        finally
        {
            if (ReferenceEquals(save, _pendingSave)) { _pendingSave = null; IsSaving = false; }
        }
    }

    private async Task<bool> SaveAfterPreviousAsync(Task? previous, SavedScan snapshot)
    {
        if (previous is not null) await previous;
        return await SaveSnapshotAsync(snapshot);
    }
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
    private IReadOnlyList<BreadcrumbItem> _breadcrumbItems = [];
    private SpaceMapLayout _mapLayout = SpaceMapLayout.Blocks;
    private ResultRow? _selectedRow;
    private FileRecord? _cleanupFile;
    private ResultRow? _cleanupDetailRow;
    private ResultRow? _currentFolderRow;
    private ResultRow? _parentFolderRow;
    private readonly IReadOnlyFileBrowser? _fileBrowser;
    private readonly IDiskSpaceReader? _diskSpaceReader;
    private CancellationTokenSource? _diskSpaceCancellation;
    private Task _diskSpaceTask = Task.CompletedTask;
    public bool ShowMapFreeSpace => HasResult && IsFolders && _diskSpaceReader is not null;
    public string MapFreeSpaceText { get; private set; } = "";
    public string MapFreeSpaceNote { get; private set; } = "";
    private bool _mapIncludesFreeSpace;
    public string MapBasisText => _mapIncludesFreeSpace
        ? "비율 기준: 분석된 용량 + 조회 시점 여유 공간"
        : "폴더별 합산 크기 · 0 B 항목은 지도에서 제외";
    private string? DiskRootMapPath
    {
        get
        {
            if (!IsFolders || _result is null) return null;
            var path = _folderPath ?? (_result.Roots.Count == 1 ? _result.Roots[0].Path : null);
            if (path is null) return null;
            var normalized = ScanPath.NormalizeRoot(path);
            return normalized.Length == 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\' ? path : null;
        }
    }
    private readonly Dictionary<string, IReadOnlyList<FileRecord>> _folderFiles = new(StringComparer.Ordinal);
    private CancellationTokenSource? _fileListCancellation;
    private Task _fileListTask = Task.CompletedTask;
    private string _fileListStatus = "";
    private ResultRow? DetailRow => IsCleanup ? _cleanupDetailRow : _view == "types" ? null : _selectedRow?.Navigation switch
    {
        FolderNavigationKind.Current => _currentFolderRow,
        FolderNavigationKind.Parent => _parentFolderRow,
        _ => _selectedRow ?? _currentFolderRow
    };
    private SystemItemInfo? _systemInfo;
    private bool _systemInfoDismissed;
    private readonly ItemDetailsReader? _detailsReader;
    private CancellationTokenSource? _detailsCancellation;
    private IReadOnlyList<DetailField> _details = [];
    public IReadOnlyList<DetailField> Details => _details;
    private readonly IFilePreviewReader _previewReader;
    private readonly IMediaInfoReader _mediaInfoReader;
    private Task _mediaInfoTask = Task.CompletedTask;
    private bool _mediaInfoExpanded, _mediaInfoLoaded;
    private bool _previewExpanded, _systemInfoExpanded;
    private bool _generalExpanded = true;
    public bool GeneralExpanded
    {
        get => _generalExpanded;
        set { if (_generalExpanded == value) return; _generalExpanded = value; Changed(); }
    }
    public bool PreviewExpanded
    {
        get => _previewExpanded;
        set { if (_previewExpanded == value) return; _previewExpanded = value; Changed(); }
    }
    public bool SystemInfoExpanded
    {
        get => _systemInfoExpanded;
        set { if (_systemInfoExpanded == value) return; _systemInfoExpanded = value; Changed(); }
    }
    public IReadOnlyList<MediaInfoGroup> MediaInfoGroups { get; private set; } = [];
    public string MediaInfoStatus { get; private set; } = "";
    public bool HasMediaInfo => DetailRow is { } row && MediaInfoReader.Supports(row);
    public bool MediaInfoExpanded
    {
        get => _mediaInfoExpanded;
        set
        {
            if (_mediaInfoExpanded == value) return;
            _mediaInfoExpanded = value; Changed();
            if (value && HasMediaInfo && !_mediaInfoLoaded) _mediaInfoTask = LoadMediaInfoAsync();
        }
    }
    private Task _previewTask = Task.CompletedTask;
    private long _previewRequest;
    public FilePreview Preview { get; private set; } = new();
    public string PreviewText => Preview.Text;
    public bool HasPreviewPages => Preview.PageCount > 1;
    public string PreviewPageLabel => Preview.PageCount > 0 ? $"{Preview.PageIndex + 1} / {Preview.PageCount}" : "";
    public bool HasDetails => DetailRow is { Path.Length: > 0 };
    public bool HasDetailPanel => (HasDetails || CanShowSystemInfo) && !_systemInfoDismissed;
    public bool CanShowDetails => HasDetails || CanShowSystemInfo;
    private bool _previewBusy;
    public bool CanPreview => HasDetails && ItemDetailsReader.SupportsPreview(DetailRow!);
    public ICommand PreviousPreviewPageCommand { get; }
    public ICommand NextPreviewPageCommand { get; }

    public MainViewModel() : this(new DiskScanner(), new FileScanResultStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsFS", "Cache", "latest.scan.json.gz")), detailsReader: new ItemDetailsReader(), fileBrowser: new ReadOnlyFileBrowser(), cleanupReader: new InstalledAppsReader(), diskSpaceReader: new DiskSpaceReader()) { }

    // Injected scanners use an explicit store so tests and alternative hosts never write a user's cache implicitly.
    public MainViewModel(IDiskScanner scanner, IScanResultStore? store = null, SystemItemCatalog? systemCatalog = null, ItemDetailsReader? detailsReader = null,
        IReadOnlyFileBrowser? fileBrowser = null, IFilePreviewReader? previewReader = null, IMediaInfoReader? mediaInfoReader = null, ICleanupContextReader? cleanupReader = null, IDiskSpaceReader? diskSpaceReader = null)
    {
        _scanner = scanner;
        Cleanup = new CleanupViewModel(cleanupReader, path => NavigateMap(new MapItem("", "", 0, path, true)),
            fileBrowser is null ? null : new CleanupFilesReader(fileBrowser));
        Cleanup.FileSelectionChanged += SelectCleanupFile;
        _detailsReader = detailsReader;
        _fileBrowser = fileBrowser;
        _diskSpaceReader = diskSpaceReader;
        _previewReader = previewReader ?? new FilePreviewReader();
        _mediaInfoReader = mediaInfoReader ?? new MediaInfoReader();
        PreviousPreviewPageCommand = new RelayCommand(_ => _previewTask = LoadPreviewAsync(Preview.PageIndex - 1),
            _ => !_previewBusy && Preview.PageIndex > 0);
        NextPreviewPageCommand = new RelayCommand(_ => _previewTask = LoadPreviewAsync(Preview.PageIndex + 1),
            _ => !_previewBusy && Preview.PageIndex + 1 < Preview.PageCount);
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
        NavigateBreadcrumbCommand = new RelayCommand(NavigateBreadcrumb, CanNavigateBreadcrumb);
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
        ShowSystemInfoCommand = new RelayCommand(_ => { _systemInfoDismissed = false; NotifySystemInfo(); }, _ => CanShowSystemInfo || HasDetails);
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
    public ICommand NavigateBreadcrumbCommand { get; }
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
    // Available width, rather than system-item selection alone, determines the rendered map visibility.
    public bool IsMapVisible => ShowMap;
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
    public bool IsCleanup => _view == "cleanup";
    public bool ShowStandardResults => HasResult && !IsCleanup;
    public bool ShowCleanupResults => HasResult && IsCleanup;
    public bool IsAllDrivesSelected => _selectedPath is null;
    public string ResultScope => $"현재 결과 · {_resultScope}";
    public string ScanLabel => _result is null ? "분석 시작" : "새로 분석";
    public string SnapshotDescription => _scannedAtUtc is null ? "" :
        $"{_scannedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 분석 · " +
        (_snapshotSaved ? "저장됨" :
         _result?.IsCancelled == true ? "부분 결과 · 미저장" : "미저장");
    public string TargetLabel => _selectedPath ?? "모든 로컬 드라이브";
    public string Status { get => _status; private set { _status = value; Changed(); Changed(nameof(ShowStatus)); } }
    public bool ShowStatus => !string.IsNullOrEmpty(Status) && Status != "저장된 분석";
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
    public string PageTitle => _view switch { "files" => "가장 큰 파일", "folders" => "폴더 탐색", "types" => "파일 형식", "cleanup" => "정리 분석", _ => "대시보드" };
    public string PageDescription => _view switch { "files" => "", "folders" => "폴더별 용량 · 두 번 클릭: 하위 폴더 열기", "types" => "확장자별 용량 합계", "cleanup" => "앱·용도별 대상 크기 · 정리 영향·판단 근거", _ => "파일·폴더별 용량 분포" };
    public bool ShowPageDescription => PageDescription.Length > 0;
    public bool ShowScanControls => !IsFiles && !IsCleanup;
    public string TableTitle => _view switch { "folders" => "폴더 및 파일", "types" => "형식별 사용량", _ => "가장 큰 파일" };
    public bool ShowTableTitle => _view != "files";
    public string TableHint => _view == "folders" ? "선택: 시스템 항목 설명 · 두 번 클릭: 폴더 열기" : _view == "types" ? "확장자 없는 파일 포함" : "";
    public bool ShowTableHint => TableHint.Length > 0;
    public string Breadcrumb => _folderPath ?? "분석한 모든 드라이브 / 폴더";
    public IReadOnlyList<BreadcrumbItem> BreadcrumbItems => _breadcrumbItems;
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
    public string ResultCount => $"{Rows.Count:N0}개 항목" + (IsFolders && _fileListStatus.Length > 0 ? $" · {_fileListStatus}" : "");
    public string Search { get => _search; set { if (_search == value) return; _search = value; Changed(); RefreshRows(); } }
    public IReadOnlyList<ResultRow> Rows { get => _rows; private set { _rows = value; RefreshDisplayRows(); Changed(); Changed(nameof(ResultCount)); Changed(nameof(NoRows)); } }
    public IReadOnlyList<ResultRow> DisplayRows { get; private set; } = [];
    public bool NoRows => _result is not null && DisplayRows.Count == 0;
    public IReadOnlyList<MapItem> MapItems { get => _mapItems; private set { _mapItems = value; Changed(); } }
    public ResultRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            _selectedRow = value;
            Changed();
            UpdateSystemInfo();
            _ = LoadDetailsAsync();
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
        CancelQuestions();
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
            _snapshotTarget = saved.SelectedPath;
            foreach (var answer in saved.Answers) _answers[(answer.Path, answer.IsFile, answer.Question)] = answer;
            RefreshQuestionStatus();
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
        CancelQuestions();
        IsBusy = true;
        _result = null;
        _fileListCancellation?.Cancel();
        _folderFiles.Clear();
        _fileListStatus = "";
        _isRestored = _snapshotSaved = false;
        _scannedAtUtc = null;
        Changed(nameof(SnapshotDescription));
        _resultScope = TargetLabel;
        Changed(nameof(ResultScope));
        _folders.Clear(); _parents.Clear(); _folderPath = null;
        _currentFolderRow = null;
        SelectedRow = null;
        RefreshBreadcrumbs(); Changed(nameof(Breadcrumb));
        Rows = []; MapItems = []; RefreshMapFreeSpace();
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
                _snapshotTarget = _selectedPath;
                _pendingSave = SaveSnapshotAsync(new SavedScan(result, _snapshotTarget, _scannedAtUtc.Value));
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

    public void Cancel() { CancelQuestions(); _scanCancellation?.Cancel(); _fileListCancellation?.Cancel(); _detailsCancellation?.Cancel(); _diskSpaceCancellation?.Cancel(); Cleanup.Cancel(); }

    public Task WaitForPendingSaveAsync() => _pendingSave ?? Task.CompletedTask;

    public Task WaitForPendingPersistenceAsync() => Task.WhenAll(
        _pendingSave ?? Task.CompletedTask, _pendingDeletion ?? Task.CompletedTask);

    public Task DeleteSavedAnalysisAsync()
    {
        if (_store is null || !IsIdle || !HasSavedAnalysis) return Task.CompletedTask;
        IsDeleting = true;
        CancelQuestions();
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
            _answers.Clear();
            Cleanup.Reset();
            _folderFiles.Clear();
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

    private async Task<bool> SaveSnapshotAsync(SavedScan snapshot)
    {
        try
        {
            await _store!.SaveAsync(snapshot);
            _snapshotSaved = true;
            HasSavedAnalysis = true;
            Status = "분석 완료 · 저장됨";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Status = "분석 완료 · 저장 실패";
            return false;
        }
    }

    private async Task ApplyResultAsync(ScanResult result)
    {
        // Indexing a saved tree does not touch any scanned paths or read their current metadata.
        var index = await Task.Run(() => IndexFolders(result.Roots));
        _result = result;
        _answers.Clear();
        Cleanup.Reset();
        _folderFiles.Clear();
        _folders = index.Folders; _parents = index.Parents;
        _folderPath = null;
        _bytes = result.TotalBytes; _files = result.FileCount; _folderCount = result.FolderCount;
        _skipped = result.SkippedCount; _elapsed = result.Elapsed;
    }

    private void NotifyResult()
    {
        NotifyStatistics();
        foreach (var name in new[] { nameof(HasResult), nameof(ShowEmpty), nameof(NoRows), nameof(ScanLabel), nameof(ResultScope), nameof(SnapshotDescription), nameof(ShowStandardResults), nameof(ShowCleanupResults) }) Changed(name);
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
        RefreshBreadcrumbs();
        foreach (var name in new[] { nameof(PageTitle), nameof(PageDescription), nameof(ShowPageDescription), nameof(ShowScanControls), nameof(TableTitle), nameof(ShowTableTitle), nameof(TableHint), nameof(ShowTableHint), nameof(ShowMap), nameof(ShowFolderNavigation), nameof(Breadcrumb), nameof(MapTitle), nameof(IsOverview), nameof(IsDetailView), nameof(IsFiles), nameof(IsFolders), nameof(IsTypes) }) Changed(name);
        RefreshRows(); RefreshMap();
        Changed(nameof(IsCleanup)); Changed(nameof(ShowStandardResults)); Changed(nameof(ShowCleanupResults));
        if (IsCleanup && _result is not null) Cleanup.Load(_result);
        _fileListTask = LoadFolderFilesAsync();
        Changed(nameof(IsMapVisible));
        CommandManager.InvalidateRequerySuggested();
    }

    private bool TryGetBreadcrumbPath(object? parameter, out string? path)
    {
        path = parameter switch { BreadcrumbItem item => item.Path, string value => value, _ => null };
        return (parameter is null or BreadcrumbItem or string) && (path is null || _folders.ContainsKey(path));
    }

    private bool CanNavigateBreadcrumb(object? parameter) => HasResult && IsIdle &&
        TryGetBreadcrumbPath(parameter, out var path) &&
        (_view != "folders" || !string.Equals(path, _folderPath, StringComparison.Ordinal));

    private void NavigateBreadcrumb(object? parameter)
    {
        if (!CanNavigateBreadcrumb(parameter) || !TryGetBreadcrumbPath(parameter, out var path)) return;
        _folderPath = path;
        _view = "folders";
        _search = "";
        Changed(nameof(Search));
        RefreshView();
    }

    private void RefreshBreadcrumbs()
    {
        var items = new List<BreadcrumbItem>();
        if (_result is not null)
        {
            items.Add(new BreadcrumbItem("전체", null, _folderPath is null));
            var ancestors = new Stack<string>();
            var path = _folderPath;
            while (path is not null && _folders.ContainsKey(path))
            {
                ancestors.Push(path);
                path = _parents.TryGetValue(path, out var parent) ? parent : null;
            }
            while (ancestors.TryPop(out path))
            {
                var folder = _folders[path];
                // Start at the analyzed root; ancestors outside the saved tree are not navigable.
                var name = _parents.ContainsKey(path) ? folder.Name : folder.Path;
                items.Add(new BreadcrumbItem(name, path, string.Equals(path, _folderPath, StringComparison.Ordinal)));
            }
        }
        _breadcrumbItems = items;
        Changed(nameof(BreadcrumbItems));
    }

    private void SelectCleanupFile(FileRecord? file)
    {
        if (ReferenceEquals(_cleanupFile, file))
        {
            if (IsCleanup) { _systemInfoDismissed = false; NotifySystemInfo(); }
            return;
        }
        _cleanupFile = file;
        _cleanupDetailRow = file is null ? null : new ResultRow(file.Name, file.Path, file.Size, -1, false,
            string.IsNullOrEmpty(file.Extension) ? "파일" : file.Extension, Share(file.Size)) { IsLiveMetadata = true };
        if (!IsCleanup) return;
        UpdateSystemInfo();
        _ = LoadDetailsAsync();
    }

    private void UpdateSystemInfo()
    {
        // Descriptions are an exact-path catalog lookup; selecting an item never opens its contents.
        _systemInfo = _view == "types" ? null : DetailRow is { } row
            ? _systemCatalog.Describe(row.Path, row.IsDirectory)
            : _view == "folders" && _folderPath is not null
                ? _systemCatalog.Describe(_folderPath, true) : null;
        _systemInfoDismissed = false;
        NotifySystemInfo();
    }

    private void NotifySystemInfo()
    {
        UpdateQuestionSections();
        Changed(nameof(SystemInfo)); Changed(nameof(CanShowSystemInfo));
        Changed(nameof(HasSystemInfo)); Changed(nameof(IsMapVisible));
        Changed(nameof(HasDetails)); Changed(nameof(HasDetailPanel)); Changed(nameof(CanPreview));
        Changed(nameof(CanShowDetails));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task LoadDetailsAsync()
    {
        _detailsCancellation?.Cancel();
        _detailsCancellation?.Dispose();
        var cancellation = _detailsCancellation = new CancellationTokenSource();
        var row = HasDetails ? DetailRow : null;
        _mediaInfoLoaded = false;
        _mediaInfoTask = Task.CompletedTask;
        MediaInfoGroups = []; MediaInfoStatus = "";
        Changed(nameof(HasMediaInfo));
        Changed(nameof(MediaInfoGroups)); Changed(nameof(MediaInfoStatus));
        _previewRequest++;
        _previewBusy = false;
        SetPreview(new FilePreview(row is null ? "" : FilePreviewReader.Description(row)));
        var fields = new List<DetailField>();
        if (row is not null)
        {
            fields.Add(new("이름", row.Name));
            fields.Add(new("전체 경로", row.Path));
            fields.Add(new("상위 폴더", Path.GetDirectoryName(row.Path) ?? row.Path));
            fields.Add(new("종류", row.IsDirectory ? "폴더" : row.Kind));
            fields.Add(new(row.IsLiveMetadata ? IsCleanup ? "목록 조회 시점 크기 · 논리적" : "폴더를 연 시점 크기 · 논리적" : "분석 당시 크기 · 논리적", $"{row.Size} ({row.Bytes:N0} B)"));
            fields.Add(new("전체 분석 대비", row.ShareText));
            if (row.IsDirectory) fields.Add(new("포함 파일 · 분석 당시", row.CountText));
        }
        _details = fields.ToArray(); Changed(nameof(Details));
        _previewTask = CanPreview ? LoadPreviewAsync(0) : Task.CompletedTask;
        _mediaInfoTask = MediaInfoExpanded && HasMediaInfo ? LoadMediaInfoAsync() : Task.CompletedTask;
        if (row is null || _detailsReader is null) return;
        try
        {
            var live = await _detailsReader.RunAsync(() => _detailsReader.ReadMetadata(row), cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(row, DetailRow)) return;
            _details = fields.Concat(live).ToArray(); Changed(nameof(Details));
        }
        catch (OperationCanceledException) { }
    }

    private void SetPreview(FilePreview preview)
    {
        Preview = preview;
        Changed(nameof(Preview)); Changed(nameof(PreviewText)); Changed(nameof(HasPreviewPages)); Changed(nameof(PreviewPageLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    public Task WaitForPreviewAsync() => _previewTask;

    public Task WaitForMediaInfoAsync() => _mediaInfoTask;

    private async Task LoadMediaInfoAsync()
    {
        if (!HasMediaInfo || _detailsCancellation is null) return;
        var row = DetailRow!;
        var selection = _detailsCancellation;
        _mediaInfoLoaded = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(selection.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(12));
        MediaInfoStatus = "미디어 정보 읽는 중"; Changed(nameof(MediaInfoStatus));
        try
        {
            var result = await _mediaInfoReader.ReadAsync(row, cancellation.Token).WaitAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(selection, _detailsCancellation)) return;
            MediaInfoGroups = result.Groups; MediaInfoStatus = result.Status;
            Changed(nameof(MediaInfoGroups)); Changed(nameof(MediaInfoStatus));
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(selection, _detailsCancellation)) return;
            MediaInfoStatus = "미디어 정보 읽기 중단"; Changed(nameof(MediaInfoStatus));
            _mediaInfoLoaded = false;
        }
    }

    private async Task LoadPreviewAsync(int pageIndex)
    {
        if (!CanPreview || _previewBusy || _detailsCancellation is null) return;
        var row = DetailRow!;
        var request = ++_previewRequest;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_detailsCancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var token = cancellation.Token;
        _previewBusy = true; CommandManager.InvalidateRequerySuggested();
        SetPreview(Preview with { Text = "미리보기 읽는 중" });
        try
        {
            var preview = await _previewReader.ReadAsync(row, pageIndex, token).WaitAsync(token);
            if (token.IsCancellationRequested || request != _previewRequest || !ReferenceEquals(row, DetailRow)) return;
            SetPreview(preview);
        }
        catch (OperationCanceledException)
        {
            if (request == _previewRequest && ReferenceEquals(row, DetailRow)) SetPreview(new FilePreview("미리보기 읽기 중단"));
        }
        finally
        {
            if (request == _previewRequest && ReferenceEquals(row, DetailRow)) { _previewBusy = false; CommandManager.InvalidateRequerySuggested(); }
        }
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

    public Task WaitForFolderFilesAsync() => _fileListTask;

    private async Task LoadFolderFilesAsync()
    {
        _fileListCancellation?.Cancel();
        _fileListCancellation?.Dispose();
        _fileListCancellation = null;
        _fileListStatus = "";
        Changed(nameof(ResultCount));
        if (!IsFolders || _result is null || _folderPath is null || _fileBrowser is null ||
            !_folders.ContainsKey(_folderPath) || _folderFiles.ContainsKey(_folderPath)) return;
        var path = _folderPath;
        var result = _result;
        var cancellation = _fileListCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = cancellation.Token;
        _fileListStatus = "파일 읽는 중";
        Changed(nameof(ResultCount));
        try
        {
            var files = await _fileBrowser.ReadAsync(path, token).WaitAsync(token);
            if (token.IsCancellationRequested || !ReferenceEquals(result, _result)) return;
            _folderFiles[path] = files;
            if (IsFolders && _folderPath == path) RefreshRows(preserveSelection: true);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(cancellation, _fileListCancellation)) _fileListStatus = "파일 읽기 중단";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            if (ReferenceEquals(cancellation, _fileListCancellation)) _fileListStatus = "파일 목록 접근 불가";
        }
        finally
        {
            if (ReferenceEquals(cancellation, _fileListCancellation))
            {
                if (_fileListStatus == "파일 읽는 중") _fileListStatus = "";
                Changed(nameof(ResultCount));
            }
        }
    }

    private void RefreshRows(bool preserveSelection = false)
    {
        var selection = preserveSelection ? SelectedRow : null;
        _currentFolderRow = _view == "folders" && _result is not null && _folderPath is not null &&
            _folders.TryGetValue(_folderPath, out var folder)
            ? new ResultRow(folder.Name, folder.Path, folder.TotalBytes, folder.FileCount, true, "폴더", Share(folder.TotalBytes))
            : null;
        SelectedRow = null;
        if (_result is null) { Rows = []; return; }
        IEnumerable<ResultRow> rows = _view switch
        {
            "cleanup" => [],
            "folders" => ScopeFolders().Select(n => new ResultRow(n.Name, n.Path, n.TotalBytes, n.FileCount, true, "폴더", Share(n.TotalBytes)))
                .Concat(_folderPath is not null && _folderFiles.TryGetValue(_folderPath, out var files)
                    ? files.Select(f => new ResultRow(f.Name, f.Path, f.Size, -1, false,
                        string.IsNullOrEmpty(f.Extension) ? "파일" : f.Extension, Share(f.Size)) { IsLiveMetadata = true }) : [])
                .OrderByDescending(row => row.Bytes).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            "types" => _result.FileTypes.Select(t => new ResultRow(string.IsNullOrEmpty(t.Extension) ? "확장자 없음" : t.Extension, "", t.TotalBytes, t.FileCount, false, "파일 형식", Share(t.TotalBytes))),
            _ => _result.LargestFiles.Select(f => new ResultRow(f.Name, f.Path, f.Size, -1, false, string.IsNullOrEmpty(f.Extension) ? "파일" : f.Extension, Share(f.Size)))
        };
        if (!string.IsNullOrWhiteSpace(_search)) rows = rows.Where(r => r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(_search, StringComparison.OrdinalIgnoreCase));
        Rows = rows.ToArray();
        if (selection is not null) SelectedRow = DisplayRows.FirstOrDefault(row => row.Path == selection.Path && row.Navigation == selection.Navigation);
    }

    private void RefreshDisplayRows()
    {
        _parentFolderRow = null;
        if (_view != "folders" || _result is null) DisplayRows = Rows;
        else
        {
            var aggregate = new ResultRow("전체", "", _result.TotalBytes, _result.FileCount, true, "폴더", Share(_result.TotalBytes));
            var current = _currentFolderRow ?? aggregate;
            var parent = _folderPath is not null && _parents.TryGetValue(_folderPath, out var parentPath) &&
                _folders.TryGetValue(parentPath, out var node)
                ? new ResultRow(node.Name, node.Path, node.TotalBytes, node.FileCount, true, "폴더", Share(node.TotalBytes)) : aggregate;
            _parentFolderRow = parent;
            DisplayRows = [current with { Name = ".", Navigation = FolderNavigationKind.Current },
                parent with { Name = "..", Navigation = FolderNavigationKind.Parent }, .. Rows];
        }
        Changed(nameof(DisplayRows));
    }

    private void RefreshMap()
    {
        if (_result is null) { MapItems = []; RefreshMapFreeSpace(); return; }
        var scope = DiskRootMapPath is { } rootPath && _folders.TryGetValue(rootPath, out var root) ? root.Children :
            _view == "folders" ? ScopeFolders() : _result.Roots.Count == 1 ? _result.Roots[0].Children : _result.Roots;
        var entries = scope.Where(n => n.TotalBytes > 0).OrderByDescending(n => n.TotalBytes).ToList();
        long scopeTotal = _view == "folders" && _folderPath is not null && _folders.TryGetValue(_folderPath, out var selected) ? selected.TotalBytes : _result.TotalBytes;
        var folderLimit = DiskRootMapPath is null ? 34 : 33;
        var maps = entries.Take(folderLimit).Select(n => new MapItem(n.Name, DisplayFormat.Bytes(n.TotalBytes), n.TotalBytes, n.Path, true)).ToList();
        var otherFolders = entries.Skip(folderLimit).Sum(n => n.TotalBytes);
        if (otherFolders > 0) maps.Add(new MapItem("그 외 폴더", DisplayFormat.Bytes(otherFolders), otherFolders, "", false));
        var direct = Math.Max(0, scopeTotal - entries.Sum(n => n.TotalBytes));
        if (direct > 0) maps.Add(new MapItem("직접 포함된 파일", DisplayFormat.Bytes(direct), direct, "", false));
        MapItems = maps;
        // Start after assigning saved totals: an immediately completed read must not be overwritten.
        RefreshMapFreeSpace();
    }

    public Task WaitForDiskSpaceAsync() => _diskSpaceTask;
    private void RefreshMapFreeSpace()
    {
        _diskSpaceCancellation?.Cancel(); _diskSpaceCancellation?.Dispose(); _diskSpaceCancellation = null;
        _diskSpaceTask = Task.CompletedTask;
        MapFreeSpaceText = ""; MapFreeSpaceNote = "";
        _mapIncludesFreeSpace = false; Changed(nameof(MapBasisText));
        Changed(nameof(ShowMapFreeSpace));
        if (ShowMapFreeSpace)
        {
            var path = _folderPath ?? (_result!.Roots.Count == 1 ? _result.Roots[0].Path : null);
            if (path is null) MapFreeSpaceText = "여러 분석 위치 · 개별 폴더 기준";
            else
            {
                MapFreeSpaceText = "확인 중";
                var request = _diskSpaceCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _diskSpaceTask = LoadMapFreeSpaceAsync(path, request);
            }
        }
        Changed(nameof(MapFreeSpaceText)); Changed(nameof(MapFreeSpaceNote));
    }
    private async Task LoadMapFreeSpaceAsync(string path, CancellationTokenSource request)
    {
        try
        {
            var space = await _diskSpaceReader!.ReadAsync(path, request.Token).WaitAsync(request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(request, _diskSpaceCancellation)) return;
            MapFreeSpaceText = DisplayFormat.Bytes(space.FreeBytes);
            MapFreeSpaceNote = $"{space.ReadAt:HH:mm:ss} 조회 · 해당 폴더의 디스크 기준" +
                (space.AvailableBytes < space.FreeBytes ? $" · 사용자 사용 가능 {DisplayFormat.Bytes(space.AvailableBytes)}" : "");
            if (DiskRootMapPath is not null)
            {
                _ = checked(MapItems.Sum(item => item.Size) + space.FreeBytes);
                if (space.FreeBytes > 0)
                    MapItems = [.. MapItems, new MapItem("여유 공간", DisplayFormat.Bytes(space.FreeBytes), space.FreeBytes, "", false) { IsFreeSpace = true }];
                _mapIncludesFreeSpace = true; Changed(nameof(MapBasisText));
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(request, _diskSpaceCancellation)) MapFreeSpaceText = "조회 시간 초과 또는 중단";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or System.Security.SecurityException)
        {
            if (ReferenceEquals(request, _diskSpaceCancellation)) MapFreeSpaceText = "확인 불가";
        }
        finally
        {
            if (ReferenceEquals(request, _diskSpaceCancellation)) { Changed(nameof(MapFreeSpaceText)); Changed(nameof(MapFreeSpaceNote)); }
        }
    }

    private void OpenRow(ResultRow? row)
    {
        if (row?.Navigation == FolderNavigationKind.Current)
        {
            SelectedRow = row;
            return;
        }
        if (row?.Navigation == FolderNavigationKind.Parent)
        {
            if (UpCommand.CanExecute(null)) UpCommand.Execute(null);
            return;
        }
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
