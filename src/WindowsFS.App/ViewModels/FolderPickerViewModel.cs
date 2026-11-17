using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows.Input;
using WindowsFS.App.Services;

namespace WindowsFS.App.ViewModels;

public sealed class FolderPickerViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyFolderBrowser _browser;
    private CancellationTokenSource? _navigationCancellation;
    private long _request;
    private bool _closed;
    private bool _initialized;
    private bool _isLoading;
    private string _pathInput;
    private string _currentPath = "";
    private string? _parentPath;
    private string _status = "폴더 경로";
    private IReadOnlyList<FolderEntry> _entries = [];
    private FolderEntry? _selectedEntry;

    public FolderPickerViewModel(string? initialPath, IEnumerable<string> drives, IReadOnlyFolderBrowser? browser = null)
    {
        _browser = browser ?? new ReadOnlyFolderBrowser();
        Drives = drives.Distinct(StringComparer.Ordinal).ToArray();
        _pathInput = initialPath ?? Drives.FirstOrDefault() ?? "";
        GoCommand = new RelayCommand(async _ => await NavigateAsync(PathInput), _ => IsIdle && !string.IsNullOrWhiteSpace(PathInput));
        NavigateCommand = new RelayCommand(async path =>
        {
            if (path is string value) await NavigateAsync(value);
        }, _ => IsIdle);
        UpCommand = new RelayCommand(async _ =>
        {
            if (ParentPath is not null) await NavigateAsync(ParentPath);
        }, _ => IsIdle && ParentPath is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> Drives { get; }
    public ICommand GoCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand UpCommand { get; }
    public string? SelectedPath { get; private set; }
    public string PathInput
    {
        get => _pathInput;
        set
        {
            if (_pathInput == value) return;
            StopNavigation();
            _pathInput = value;
            SelectedPath = null;
            SelectedEntry = null;
            Changed();
            NotifySelection();
        }
    }
    public string CurrentPath { get => _currentPath; private set { _currentPath = value; Changed(); } }
    public string? ParentPath { get => _parentPath; private set { _parentPath = value; Changed(); CommandManager.InvalidateRequerySuggested(); } }
    public IReadOnlyList<FolderEntry> Entries { get => _entries; private set { _entries = value; Changed(); } }
    public FolderEntry? SelectedEntry
    {
        get => _selectedEntry;
        set { _selectedEntry = value; Changed(); NotifySelection(); }
    }
    public bool IsLoading { get => _isLoading; private set { _isLoading = value; Changed(); Changed(nameof(IsIdle)); NotifySelection(); } }
    public bool IsIdle => !_closed && !IsLoading;
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string SelectionPath => SelectedEntry?.Path ?? PathInput;
    public bool CanConfirm => IsIdle && !string.IsNullOrWhiteSpace(SelectionPath);

    public async Task InitializeAsync()
    {
        if (_initialized || _closed) return;
        _initialized = true;
        if (!string.IsNullOrWhiteSpace(PathInput)) await NavigateAsync(PathInput);
    }

    public Task<bool> NavigateAsync(string path) => ReadLocationAsync(path, confirm: false);

    public Task<bool> ConfirmAsync() => CanConfirm
        ? ReadLocationAsync(SelectionPath, confirm: true) : Task.FromResult(false);

    private async Task<bool> ReadLocationAsync(string path, bool confirm)
    {
        if (_closed) return false;
        StopNavigation();
        var request = ++_request;
        using var cancellation = new CancellationTokenSource();
        _navigationCancellation = cancellation;
        SelectedPath = null;
        SelectedEntry = null;
        _pathInput = path;
        Changed(nameof(PathInput));
        IsLoading = true;
        Status = "폴더 목록 로딩 중";
        try
        {
            var location = await _browser.ReadAsync(path, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || request != _request) return false;
            CurrentPath = location.Path;
            ParentPath = location.ParentPath;
            Entries = location.Entries;
            _pathInput = location.Path;
            Changed(nameof(PathInput));
            NotifySelection();
            Status = Entries.Count == 0 ? "하위 폴더 없음" : $"{Entries.Count:N0}개 폴더";
            if (confirm) SelectedPath = location.Path;
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            if (!_closed && request == _request) Status = "폴더 접근 불가 · 경로 또는 권한 오류";
            return false;
        }
        finally
        {
            if (ReferenceEquals(_navigationCancellation, cancellation)) _navigationCancellation = null;
            if (!_closed && request == _request) IsLoading = false;
        }
    }

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        SelectedPath = null;
        StopNavigation();
        NotifySelection();
    }

    private void StopNavigation()
    {
        _request++;
        _navigationCancellation?.Cancel();
        _navigationCancellation = null;
        IsLoading = false;
    }

    private void NotifySelection()
    {
        Changed(nameof(SelectionPath));
        Changed(nameof(CanConfirm));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
