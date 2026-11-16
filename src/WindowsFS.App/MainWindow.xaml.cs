using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindowsFS.App.Controls;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ShowMap))
                MapColumn.Width = _viewModel.ShowMap ? new GridLength(0.85, GridUnitType.Star) : new GridLength(0);
        };
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--scan");
        await _viewModel.InitializeAsync(index >= 0 && index + 1 < args.Length ? args[index + 1] : null);
    }

    private void WindowClosing(object? sender, CancelEventArgs e) => _viewModel.Cancel();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void MapItemInvoked(object? sender, MapItem e) => _viewModel.NavigateMap(e);
    private void ResultsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsGrid, e.OriginalSource as DependencyObject) is DataGridRow row)
            _viewModel.OpenRowCommand.Execute(row.Item);
    }
    private void ResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _viewModel.OpenRowCommand.Execute(ResultsGrid.SelectedItem); e.Handled = true; }
    }
}
