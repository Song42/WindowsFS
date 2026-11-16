using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using WindowsFS.App.Controls;
using WindowsFS.Core;

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
    private CancellationTokenSource? _scanCancellation;
    private ScanResult? _result;
    private Dictionary<string, FolderNode> _folders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _parents = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedPath;
    private string? _folderPath;
    private string _resultScope = "";
    private string _view = "overview";
    private string _search = "";
    private bool _busy;
    private bool _drivesLoaded;
    private string _status = "분석할 드라이브나 폴더를 선택하세요.";
    private string _currentPath = "메타데이터만 읽고, 분석 결과는 메모리에 보관합니다.";
    private long _bytes, _files, _folderCount, _skipped;
    private TimeSpan _elapsed;
    private IReadOnlyList<ResultRow> _rows = [];
    private IReadOnlyList<MapItem> _mapItems = [];
    private ResultRow? _selectedRow;

    public MainViewModel() : this(new DiskScanner()) { }
    public MainViewModel(IDiskScanner scanner)
    {
        _scanner = scanner;
        ScanCommand = new RelayCommand(async _ => await ScanAsync(), _ => !IsBusy && _drivesLoaded && (_selectedPath is not null || Drives.Count > 0));
        CancelCommand = new RelayCommand(_ => { _scanCancellation?.Cancel(); Status = "중지하는 중… 지금까지의 결과를 정리합니다."; }, _ => IsBusy);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !IsBusy);
        SelectDriveCommand = new RelayCommand(p => SelectPath(p as string), _ => !IsBusy);
        NavigateCommand = new RelayCommand(p => { _view = p as string ?? "overview"; _search = ""; Changed(nameof(Search)); RefreshView(); });
        OpenRowCommand = new RelayCommand(p => OpenRow(p as ResultRow ?? SelectedRow), _ => !IsBusy);
        RevealCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedRow is { Path.Length: > 0 } && _view != "types");
        CopyPathCommand = new RelayCommand(_ => CopySelectedPath(), _ => SelectedRow is { Path.Length: > 0 } && _view != "types");
        UpCommand = new RelayCommand(_ => { _folderPath = _folderPath is not null && _parents.TryGetValue(_folderPath, out var parent) ? parent : null; RefreshView(); }, _ => _folderPath is not null);
        HomeCommand = new RelayCommand(_ => { _folderPath = null; RefreshView(); });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DriveItem> Drives { get; } = [];
    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand SelectDriveCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand OpenRowCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand HomeCommand { get; }
    public bool IsBusy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(IsIdle)); Changed(nameof(ScanLabel)); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsIdle => !IsBusy;
    public bool HasResult => _result is not null;
    public bool ShowEmpty => _result is null;
    public bool ShowMap => _view is "overview" or "folders";
    public bool ShowFolderNavigation => _view == "folders";
    public bool IsOverview => _view == "overview";
    public bool IsFiles => _view == "files";
    public bool IsFolders => _view == "folders";
    public bool IsTypes => _view == "types";
    public bool IsAllDrivesSelected => _selectedPath is null;
    public string ResultScope => $"현재 결과 · {_resultScope}";
    public string ScanLabel => _result is null ? "분석 시작" : "다시 분석";
    public string TargetLabel => _selectedPath ?? "모든 로컬 드라이브";
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string CurrentPath { get => _currentPath; private set { _currentPath = value; Changed(); } }
    public string TotalSize => DisplayFormat.Bytes(_bytes);
    public string FileCount => _files.ToString("N0");
    public string FolderCount => $"{_folderCount:N0}개 폴더";
    public string Elapsed => _elapsed.TotalHours >= 1 ? _elapsed.ToString(@"h\:mm\:ss") : _elapsed.ToString(@"mm\:ss");
    public string Speed => _elapsed.TotalSeconds > 0 ? $"{_files / _elapsed.TotalSeconds:N0} 파일/초" : "분석 대기";
    public string Skipped => _skipped.ToString("N0");
    public string ScanState => IsBusy ? "분석 중" : _result is null ? "준비됨" : _result.IsCancelled ? "부분 결과" : "분석 완료";
    public string PageTitle => _view switch { "files" => "가장 큰 파일", "folders" => "폴더 탐색", "types" => "파일 형식", _ => "공간 분석" };
    public string PageDescription => _view switch { "files" => "전체 분석에서 가장 큰 파일 최대 1,000개 · 크기순", "folders" => "하위 폴더를 두 번 클릭해 더 깊이 살펴보세요.", "types" => "확장자별로 합산한 파일 크기 · 크기순", _ => "무엇이 공간을 차지하는지, 한눈에 확인하세요." };
    public string TableTitle => _view switch { "folders" => "하위 폴더", "types" => "형식별 사용량", _ => "가장 큰 파일" };
    public string TableHint => _view == "folders" ? "직접 포함된 파일은 지도에서 별도로 표시됩니다." : _view == "types" ? "확장자가 없는 파일도 포함합니다." : "상위 1,000개 · 파일명 또는 경로 검색";
    public string Breadcrumb => _folderPath ?? "분석한 모든 드라이브 / 폴더";
    public string MapTitle => _view == "folders" ? "이 폴더의 공간 구성" : "용량 지도";
    public string ResultCount => $"{Rows.Count:N0}개 항목";
    public string Search { get => _search; set { if (_search == value) return; _search = value; Changed(); RefreshRows(); } }
    public IReadOnlyList<ResultRow> Rows { get => _rows; private set { _rows = value; Changed(); Changed(nameof(ResultCount)); Changed(nameof(NoRows)); } }
    public bool NoRows => _result is not null && Rows.Count == 0;
    public IReadOnlyList<MapItem> MapItems { get => _mapItems; private set { _mapItems = value; Changed(); } }
    public ResultRow? SelectedRow { get => _selectedRow; set { _selectedRow = value; Changed(); CommandManager.InvalidateRequerySuggested(); } }

    public async Task InitializeAsync(string? initialPath = null)
    {
        var items = await Task.Run(() =>
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
        foreach (var drive in items) Drives.Add(drive);
        _drivesLoaded = true;
        CommandManager.InvalidateRequerySuggested();
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            SelectPath(initialPath);
            await ScanAsync();
        }
    }

    public async Task ScanAsync()
    {
        if (IsBusy) return;
        var roots = _selectedPath is null ? Drives.Select(d => d.Path).ToArray() : [_selectedPath];
        if (roots.Length == 0) { Status = "분석 가능한 드라이브가 없습니다. 폴더를 선택하세요."; return; }
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        IsBusy = true;
        _result = null;
        _resultScope = TargetLabel;
        Changed(nameof(ResultScope));
        _folders.Clear(); _parents.Clear(); _folderPath = null;
        Rows = []; MapItems = [];
        _bytes = _files = _folderCount = _skipped = 0; _elapsed = TimeSpan.Zero;
        Status = "파일 내용을 열지 않고 저장 공간을 분석하고 있습니다…";
        CurrentPath = TargetLabel;
        NotifyStatistics(); Changed(nameof(HasResult)); Changed(nameof(ShowEmpty)); Changed(nameof(NoRows));
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (!ReferenceEquals(_scanCancellation, cancellation) || !IsBusy) return;
                _bytes = p.TotalBytes; _files = p.FileCount; _folderCount = p.FolderCount; _skipped = p.SkippedCount; _elapsed = p.Elapsed;
                CurrentPath = p.CurrentPath;
                NotifyStatistics();
            });
            _result = await _scanner.ScanAsync(roots, progress, cancellation.Token);
            _bytes = _result.TotalBytes; _files = _result.FileCount; _folderCount = _result.FolderCount; _skipped = _result.SkippedCount; _elapsed = _result.Elapsed;
            // Build lookup tables off the dispatcher, including very large folder trees.
            var result = _result;
            var index = await Task.Run(() => IndexFolders(result.Roots));
            _folders = index.Folders; _parents = index.Parents;
            Status = result.IsCancelled ? "분석을 중지했습니다. 지금까지 확인한 부분 결과입니다." : $"분석 완료 · {result.FileCount:N0}개 파일을 확인했습니다.";
            CurrentPath = _skipped > 0 ? $"권한 제한·링크·오프라인 항목 {_skipped:N0}개 제외 · 파일의 논리적 크기 기준" : "파일의 논리적 크기 기준 · 실제 디스크 사용량과 다를 수 있습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            Status = "분석을 완료하지 못했습니다. 경로와 접근 권한을 확인한 뒤 다시 시도하세요.";
            CurrentPath = "드라이브 연결이 끊겼거나 경로를 읽을 수 없습니다.";
        }
        finally
        {
            _scanCancellation = null;
            IsBusy = false;
            NotifyStatistics(); Changed(nameof(HasResult)); Changed(nameof(ShowEmpty)); Changed(nameof(NoRows)); Changed(nameof(ScanLabel));
            RefreshView();
        }
    }

    public void Cancel() => _scanCancellation?.Cancel();

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
        var folders = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        var dialog = new OpenFolderDialog { Title = "분석할 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        SelectPath(dialog.FolderName);
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
        foreach (var name in new[] { nameof(PageTitle), nameof(PageDescription), nameof(TableTitle), nameof(TableHint), nameof(ShowMap), nameof(ShowFolderNavigation), nameof(Breadcrumb), nameof(MapTitle), nameof(IsOverview), nameof(IsFiles), nameof(IsFolders), nameof(IsTypes) }) Changed(name);
        RefreshRows(); RefreshMap();
        CommandManager.InvalidateRequerySuggested();
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
        catch (Win32Exception) { Status = "파일 탐색기를 열 수 없습니다."; }
    }

    private void CopySelectedPath()
    {
        if (SelectedRow is not { Path.Length: > 0 } row) return;
        try { Clipboard.SetText(row.Path); Status = "선택한 경로를 클립보드에 복사했습니다."; }
        catch (System.Runtime.InteropServices.COMException) { Status = "클립보드를 사용할 수 없습니다. 잠시 후 다시 시도하세요."; }
    }

    private void NotifyStatistics()
    {
        foreach (var name in new[] { nameof(TotalSize), nameof(FileCount), nameof(FolderCount), nameof(Elapsed), nameof(Speed), nameof(Skipped), nameof(ScanState) }) Changed(name);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
