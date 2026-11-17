using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App.Controls;

public partial class FolderPickerWindow : Window
{
    private readonly FolderPickerViewModel _viewModel;
    private bool _accepted;

    public FolderPickerWindow(string? initialPath, IEnumerable<string> drives)
        : this(new FolderPickerViewModel(initialPath, drives)) { }

    public FolderPickerWindow(FolderPickerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
    }

    public string? SelectedPath => _accepted ? _viewModel.SelectedPath : null;

    private async void WindowLoaded(object sender, RoutedEventArgs e) => await _viewModel.InitializeAsync();

    private void WindowClosed(object? sender, EventArgs e)
    {
        // Preserve an accepted path for the caller; no request remains pending after acceptance.
        if (!_accepted) _viewModel.Cancel();
    }

    private async void ConfirmClick(object sender, RoutedEventArgs e)
    {
        if (await _viewModel.ConfirmAsync() && IsLoaded)
        {
            _accepted = true;
            DialogResult = true;
        }
    }

    private async void PathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.IsIdle) return;
        e.Handled = true;
        await _viewModel.NavigateAsync(_viewModel.PathInput);
    }

    private async void FolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsIdle || ItemsControl.ContainerFromElement(FolderList, e.OriginalSource as DependencyObject) is not ListBoxItem { Content: FolderEntry entry }) return;
        e.Handled = true;
        await _viewModel.NavigateAsync(entry.Path);
    }

    private async void FolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.IsIdle || _viewModel.SelectedEntry is not { } entry) return;
        e.Handled = true;
        await _viewModel.NavigateAsync(entry.Path);
    }
}
