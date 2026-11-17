using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WindowsFS.App.Controls;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static async Task DetailsPanelResizing()
    {
        var vm = CreateSystemViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.ScanAsync();
            vm.SelectedRow = vm.Rows.Single(row => row.Path == @"X:\pagefile.sys");
            var content = (FrameworkElement)window.Content;
            var size = new Size(2000, 920);
            LayoutAt(content, size);
            var details = (FrameworkElement)window.FindName("SystemInfoPanel");
            var map = (FrameworkElement)window.FindName("SpaceMapPanel");
            var grid = (Grid)window.FindName("ResultColumns");
            var handle = (Thumb)window.FindName("DetailsResizeHandle");
            var mapHandle = (Thumb)window.FindName("MapResizeHandle");
            var results = (DataGrid)window.FindName("ResultsGrid");
            var scroller = Descendants<ScrollViewer>(results).First();
            var before = details.ActualWidth;
            Drag(handle, -70);
            Check(Math.Abs(details.ActualWidth - before - 70) < 1, "Dragging the details boundary did not change its width.");
            var chosen = window.DetailsPanelWidth;
            vm.SelectedRow = new ResultRow("Folder", $@"{FixturePath}\Folder", 100, 1, true, "폴더", 10);
            LayoutAt(content, size);
            Equal(chosen, details.ActualWidth);
            vm.SelectedRow = vm.Rows.Single(row => row.Path == @"X:\pagefile.sys");
            LayoutAt(content, size);
            Equal(chosen, details.ActualWidth);
            vm.DismissSystemInfoCommand.Execute(null);
            LayoutAt(content, size);
            Check(!IsEffectivelyVisible(handle), "Hidden details retained a resize handle.");
            vm.ShowSystemInfoCommand.Execute(null);
            LayoutAt(content, size);
            Equal(chosen, details.ActualWidth);

            // Repeated fractional changes at both limits must not toggle map visibility or horizontal scrollbars.
            foreach (var boundary in new[] { handle, mapHandle })
            {
                Drag(boundary, ReferenceEquals(boundary, handle) ? -10000 : 10000);
                for (var i = 0; i < 30; i++)
                {
                    Drag(boundary, i % 2 == 0 ? -0.75 : 0.75);
                    Check(IsEffectivelyVisible(map), "Resizing at the limit flickered the space map.");
                    Equal(Visibility.Collapsed, scroller.ComputedHorizontalScrollBarVisibility);
                    Check(grid.ColumnDefinitions[1].ActualWidth >= 480, "Resizing squeezed the result list.");
                    Check(details.TranslatePoint(new Point(), grid).X + details.ActualWidth <= grid.ActualWidth + 0.5,
                        "Resizing overflowed the result viewport.");
                }
            }
            var preferred = window.DetailsPanelWidth;
            LayoutAt(content, new Size(1100, 760));
            Check(!IsEffectivelyVisible(map), "The compact window did not make room for the chosen details width.");
            Equal(preferred, window.DetailsPanelWidth);
            LayoutAt(content, size);
            Equal(preferred, details.ActualWidth);
            Check(!window.IsVisible, "Panel tests must not show a window.");

            void Drag(Thumb thumb, double delta)
            {
                thumb.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                LayoutAt(content, size);
            }
        }
        finally { window.Close(); }
    }

    private static Task DonutLegendCentering()
    {
        var items = Enumerable.Range(1, 20).Select(index => new MapItem($"Folder {index}", "", index,
            $@"{FixturePath}\Folder{index}", true)).ToArray();
        var map = new SpaceMap { Items = items, LayoutMode = SpaceMapLayout.Donut };
        foreach (var size in new[] { new Size(700, 1000), new Size(500, 300), new Size(600, 160) })
        {
            RenderMap(map, size);
            var area = MapField<Rect>(map, "_legendArea");
            var center = MapField<Point>(map, "_donutCenter");
            Check(Math.Abs(area.Top + area.Height / 2 - center.Y) < 0.5, "The right legend was not vertically centered with the donut.");
            Check(area.Top >= 0 && area.Bottom <= size.Height, "The centered legend overflowed the viewport.");
            var bounds = MapField<Rect[]>(map, "_legendBounds");
            var count = MapField<int>(map, "_legendCount");
            for (var index = 0; index < count; index++)
                Equal(index, MapHit(map, new Point(bounds[index].Left + 8, bounds[index].Top + 12)));
        }
        RenderMap(map, new Size(340, 600));
        var below = MapField<Rect>(map, "_legendArea");
        Check(below.Top > MapField<Point>(map, "_donutCenter").Y + MapField<double>(map, "_outerRadius"),
            "The narrow legend stopped appearing below the donut.");
        return Task.CompletedTask;
    }
}
