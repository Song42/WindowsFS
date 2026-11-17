using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using WindowsFS.App.Controls;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _closeAfterPersistence;
    private bool _waitingForPersistence;
    private HwndSource? _windowSource;
    private Thumb? _resizeThumb;
    private double _resizePointerX, _resizeStartWidth;
    public static readonly DependencyProperty MapPanelWidthProperty = DependencyProperty.Register(
        nameof(MapPanelWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(SpaceMapVisibilityConverter.MapWidth));
    public double MapPanelWidth
    {
        get => (double)GetValue(MapPanelWidthProperty);
        private set => SetValue(MapPanelWidthProperty, value);
    }
    public static readonly DependencyProperty DetailsPanelWidthProperty = DependencyProperty.Register(
        nameof(DetailsPanelWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(double.NaN));
    public double DetailsPanelWidth
    {
        get => (double)GetValue(DetailsPanelWidthProperty);
        private set => SetValue(DetailsPanelWidthProperty, value);
    }
    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        DataContext = _viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessage);
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WindowSizing.GetMinMaxInfoMessage)
            WindowSizing.UpdateMaximizedBounds(window, lParam);
        // Let WPF process the corrected bounds too, preserving its layout and resize constraints.
        return 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_windowSource is { IsDisposed: false }) _windowSource.RemoveHook(WindowMessage);
        _windowSource = null;
        base.OnClosed(e);
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--scan");
        await _viewModel.InitializeAsync(index >= 0 && index + 1 < args.Length ? args[index + 1] : null);
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.Cancel();
        if (_closeAfterPersistence || (!_viewModel.IsSaving && !_viewModel.IsDeleting)) return;
        e.Cancel = true;
        if (_waitingForPersistence) return;
        _waitingForPersistence = true;
        await _viewModel.WaitForPendingPersistenceAsync();
        _closeAfterPersistence = true;
        Close();
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void BreadcrumbScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentWidthChange != 0 && sender is ScrollViewer scroll) scroll.ScrollToRightEnd();
    }
    private void MapItemInvoked(object? sender, MapItem e) => _viewModel.NavigateMap(e);
    private void PanelResizeStarted(object sender, DragStartedEventArgs e)
    {
        _resizeThumb = (Thumb)sender;
        _resizePointerX = Mouse.GetPosition(ResultColumns).X;
        _resizeStartWidth = ReferenceEquals(sender, MapResizeHandle) ? MapPanelWidth : SystemInfoPanel.ActualWidth;
    }
    private void PanelResizeCompleted(object sender, DragCompletedEventArgs e) => _resizeThumb = null;
    private void MapResizeDragDelta(object sender, DragDeltaEventArgs e) => SetMapWidth(ReferenceEquals(_resizeThumb, sender)
        ? _resizeStartWidth + Mouse.GetPosition(ResultColumns).X - _resizePointerX : MapPanelWidth + e.HorizontalChange);
    private void DetailsResizeDragDelta(object sender, DragDeltaEventArgs e) => SetDetailsWidth(ReferenceEquals(_resizeThumb, sender)
        ? _resizeStartWidth - (Mouse.GetPosition(ResultColumns).X - _resizePointerX) : SystemInfoPanel.ActualWidth - e.HorizontalChange);
    private void MapResizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        SetMapWidth(MapPanelWidth + (e.Key == Key.Left ? -10 : 10));
        e.Handled = true;
    }
    private void DetailsResizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        SetDetailsWidth(SystemInfoPanel.ActualWidth + (e.Key == Key.Left ? 10 : -10));
        e.Handled = true;
    }
    private void SetMapWidth(double width)
    {
        if (!double.IsFinite(width) || SpaceMapPanel.Visibility != Visibility.Visible) return;
        var details = DataContext is MainViewModel { HasDetailPanel: true }
            ? double.IsFinite(DetailsPanelWidth) ? DetailsPanelWidth + 16 : SpaceMapVisibilityConverter.SystemPanelColumnWidth : 0;
        var reserved = SpaceMapVisibilityConverter.MinimumListWidth + 16 + details + SpaceMapVisibilityConverter.ResizeSlack;
        var maximum = Math.Max(SpaceMapVisibilityConverter.MinimumMapWidth,
            Math.Min(SpaceMapVisibilityConverter.MaximumMapWidth, Math.Floor(ResultColumns.ActualWidth - reserved)));
        MapPanelWidth = Math.Floor(Math.Clamp(width, SpaceMapVisibilityConverter.MinimumMapWidth, maximum));
    }
    private void SetDetailsWidth(double width)
    {
        if (!double.IsFinite(width) || DataContext is not MainViewModel { HasDetailPanel: true }) return;
        var reserved = SpaceMapVisibilityConverter.MinimumListWidth + 16 + SpaceMapVisibilityConverter.ResizeSlack +
            (SpaceMapPanel.Visibility == Visibility.Visible ? MapPanelWidth + 16 : 0);
        var minimum = SpaceMapVisibilityConverter.SystemPanelColumnWidth - 16;
        var maximum = Math.Max(minimum, Math.Floor(ResultColumns.ActualWidth - reserved));
        DetailsPanelWidth = Math.Floor(Math.Clamp(width, minimum, maximum));
    }
    private void ResultsSorting(object sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not MainViewModel { IsFolders: true }) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in grid.Columns) column.SortDirection = null;
        if (CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view)
            view.CustomSort = new ResultRowComparer(e.Column.SortMemberPath, direction);
        e.Column.SortDirection = direction;
    }
    private void ResultsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsGrid, e.OriginalSource as DependencyObject) is DataGridRow row)
            ((MainViewModel)DataContext).OpenRowCommand.Execute(row.Item);
    }
    private void ResultsMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsGrid, e.OriginalSource as DependencyObject) is DataGridRow
            { IsSelected: true, Item: ResultRow { Navigation: FolderNavigationKind.Current } current } &&
            DataContext is MainViewModel vm)
            vm.SelectedRow = current;
    }
    private void ResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ((MainViewModel)DataContext).OpenRowCommand.Execute(ResultsGrid.SelectedItem); e.Handled = true; }
    }
}
