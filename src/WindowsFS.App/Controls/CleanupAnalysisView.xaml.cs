using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App.Controls;

public partial class CleanupAnalysisView : UserControl
{
    public CleanupAnalysisView() => InitializeComponent();
    private double? _dragX;
    private double _dragWidth;
    private void SidebarDragStarted(object sender, DragStartedEventArgs e)
    {
        _dragX = Mouse.GetPosition(CleanupLayout).X;
        _dragWidth = SidebarColumn.ActualWidth;
    }
    private void SidebarDragDelta(object sender, DragDeltaEventArgs e)
    {
        var width = _dragX is double start ? _dragWidth + Mouse.GetPosition(CleanupLayout).X - start : SidebarColumn.ActualWidth + e.HorizontalChange;
        ResizeSidebar(width);
        e.Handled = true;
    }
    private void SidebarDragCompleted(object sender, DragCompletedEventArgs e) => _dragX = null;
    private void SidebarKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        ResizeSidebar(SidebarColumn.ActualWidth + (e.Key == Key.Right ? 10 : -10));
        e.Handled = true;
    }
    private void ResizeSidebar(double width)
    {
        if (DataContext is CleanupViewModel vm)
            vm.SidebarWidth = Math.Clamp(width, 280, CleanupSidebarWidthConverter.Maximum(CleanupLayout.ActualWidth));
    }
}

public sealed class CleanupSidebarWidthConverter : IMultiValueConverter
{
    internal static double Maximum(double available) => Math.Clamp(available - 580, 280, 720);
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        new GridLength(Math.Clamp(values[0] is double width ? width : 360, 280, Maximum(values[1] is double available ? available : 0)));
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CleanupSectionHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Math.Clamp(((value is double height ? height : 0) - 210) / 2, 0, 140);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
