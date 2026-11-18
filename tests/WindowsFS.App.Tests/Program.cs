using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using WindowsFS.App;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;
using WindowsFS.Core.Persistence;
using WindowsFS.Core.SystemItems;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private const string FixturePath = @"X:\WindowsFSFixture";

    [STAThread]
    private static int Main(string[] args)
    {
        // Do not run Application or show a Window: the fixture never enumerates real drives.
        var application = new WindowsFS.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var dispatcherErrors = new List<Exception>();
        application.DispatcherUnhandledException += (_, args) =>
        {
            dispatcherErrors.Add(args.Exception);
            args.Handled = true;
        };
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Cleanup groups every saved small file without double counts or unsafe cache guesses", CleanupSavedGrouping),
            ("Cleanup dashboard navigation and read-only grouping reuse saved analysis", CleanupNavigation),
            ("Cleanup cancellation rejects stale inventories after analysis changes", CleanupCancellation),
            ("Cleanup file metadata stays within saved locations and never changes source files", CleanupFileMetadata),
            ("Cleanup candidates, cached file lists, refresh and cancellation preserve scope", CleanupFileState),
            ("Cleanup filters and file list remain usable with a resizable sidebar", CleanupFilesLayout),
            ("Cleanup file selection shares details, previews and resizing without source changes", CleanupFileDetails),
            ("Cleanup preview pages, media and stale selection use the shared detail lifecycle", CleanupPreviewLifecycle),
            ("MediaInfo reads native audio and video tracks without source changes", NativeMediaInfo),
            ("MediaInfo rejects external references and observes cancellation", MediaInfoBoundaries),
            ("MediaInfo preserves section state, reads once and rejects stale selection results", MediaInfoSelection),
            ("Details and bounded text previews leave synthetic files unchanged", FileDetails),
            ("Image and PDF previews render bounded pages without source changes", ImageAndPdfPreviews),
            ("MP3 selection prepares compact audio controls without playback or source changes", AudioPreviews),
            ("MP3 previews reject playlists, broken frames and oversized inputs", AudioPreviewLimits),
            ("Audio and video timelines seek, pause and replay beyond thirty seconds", MediaPlaybackTimeline),
            ("Unknown media durations and decoder failures preserve playback lifecycle", UnknownMediaDuration),
            ("Office, HWPX and OpenDocument previews render formatted pages, tables and images without writes", DocumentPreviews),
            ("Document layout bounds reject expansion and external resources without source writes", DocumentLayoutLimits),
            ("Previews reject malformed, external and oversized content and never autoplay video", PreviewLimits),
            ("Preview selection cancellation, page bounds and actual XAML bindings stay consistent", PreviewSelectionAndPages),
            ("Actual XAML realizes a full-width dashboard and read-only result bindings", XamlSmoke),
            ("Maximized chrome fits monitor work areas at every taskbar edge and DPI", MaximizedWorkArea),
            ("Dashboard cards and return navigation retain saved analysis without scanning", DashboardNavigation),
            ("Page headings remain aligned and hide duplicate titles and saved-status footer", RefinedPageHeadings),
            ("Resizable space maps preserve width and reserve room for results and details", ResponsiveSpaceMap),
            ("Details resizing preserves width across selections without layout or scrollbar flicker", DetailsPanelResizing),
            ("Right-side donut legends stay centered and preserve row hit testing", DonutLegendCentering),
            ("Folder maps show current disk free space without changing chart totals or source files", FolderMapFreeSpace),
            ("Disk free-space reads reject stale responses and handle inaccessible and multiple volumes", DiskSpaceFailures),
            ("Drive-root charts retain free space, preserve bytes and restore subfolder proportions", RootFreeSpaceCharts),
            ("Folder questions send bounded metadata with Sol medium and restore answers with their scan", FolderQuestionsRoundTrip),
            ("Folder questions partition purposes and disclose missing samples within token budgets", FolderQuestionCoverage),
            ("File questions send only the selected path and reuse answers across all file views", FileQuestionPrivacy),
            ("Inline folder questions retain parallel answers across selection and retry failures", ConcurrentFolderQuestions),
            ("Breadcrumb navigation preserves exact indexed ancestors without scanning", BreadcrumbNavigation),
            ("Actual breadcrumb buttons navigate the saved folder tree", BreadcrumbXaml),
            ("Folder browsing reads metadata without changing the source tree", ReadOnlyFolderBrowsing),
            ("Immediate file browsing reads all file metadata without changing the source tree", ReadOnlyFileBrowsing),
            ("Folder lists include files, preserve selection, and reuse reads without rescanning", FolderFiles),
            ("Folder file reads ignore stale results and recover from inaccessible folders", FolderFileReadFailures),
            ("Folder picker navigation and confirmation select the requested directory", FolderPickerNavigation),
            ("Invalid picker paths never fall back to the previously visited folder", FolderPickerInvalidPaths),
            ("Folder picker ignores stale reads and completion after cancellation", FolderPickerCancellation),
            ("Actual folder picker XAML exposes navigation without file operations", FolderPickerXaml),
            ("Long file lists support precise wheel and scrollbar navigation at compact sizes", LongListScrolling),
            ("Folder navigation, file-type totals, and filtered search stay consistent", Navigation),
            ("Case-distinct folder branches remain independently navigable after scan and restore", CaseDistinctNavigation),
            ("Treemap grouping preserves all represented bytes", GroupedMap),
            ("Block and donut controls switch without rescanning or changing results", MapLayoutSwitching),
            ("Donut sectors preserve byte proportions and exclude the center from navigation", DonutGeometry),
            ("Donut rendering handles full circles, empty inputs and grouped small items", DonutEdgeCases),
            ("Changing target preserves a separately identified result scope", ResultScope),
            ("Cancellation returns a navigable partial result", Cancellation),
            ("Late progress from an earlier scan cannot overwrite a new scan", StaleProgress),
            ("Saved scans restore without enumeration and remain navigable", RestoreSavedScan),
            ("Completed scans save their scope and restore in another view model", SaveCompletedScan),
            ("Cancelled scans preserve the previous completed snapshot", PreserveCompletedSnapshot),
            ("An immediate scan can cancel and still delete an unrestored saved snapshot", DeleteUnrestoredSnapshotAfterCancellation),
            ("Missing snapshots do not start an automatic scan", MissingSnapshot),
            ("Unreadable and corrupt snapshots permit an explicit fresh scan", SnapshotLoadFailure),
            ("A failed snapshot write retains both results and the previous save", SnapshotSaveFailure),
            ("Restoring a snapshot blocks overlapping restores and scans", RestoreConcurrency),
            ("Pending saves ignore late progress and can be awaited before closing", PendingSave),
            ("Deleting a saved scan clears results and stays empty after restart", DeleteSavedScan),
            ("Failed deletion preserves the saved scan and current view", SnapshotDeleteFailure),
            ("Deletion is unavailable without a saved scan or a store", UnavailableSnapshotDeletion),
            ("Corrupt and unreadable saved scans can be deleted without scanning", DeleteUnreadableSnapshot),
            ("Pending deletion blocks conflicting operations and is awaited before closing", PendingDeletion),
            ("Actual XAML binds saved-analysis deletion and updates button availability", SnapshotDeletionXaml),
            ("System explanations match known selections without classifying user files", SystemItemSelection),
            ("Known folder explanations follow navigation and support dismissing", SystemFolderNavigation),
            ("Actual XAML renders the selected system explanation", SystemItemXaml)
        };
        if (args.Length == 2 && args[0] == "--filter")
            tests = tests.Where(test => test.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tests.Length == 0) return 2;
        var failures = 0;
        foreach (var test in tests)
        {
            dispatcherErrors.Clear();
            try
            {
                Pump(test.Run());
                DrainDispatcher();
                Check(dispatcherErrors.Count == 0, "The WPF dispatcher reported an unhandled exception.");
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine($"FAIL {test.Name}: {error.GetType().Name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} WPF tests passed.");
        application.Shutdown();
        Console.WriteLine("WPF application shutdown completed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task XamlSmoke()
    {
        var vm = CreateViewModel(new StubScanner((_, _, _) => Task.FromResult(FixtureResult())));
        var window = new MainWindow { DataContext = vm };
        try
        {
            AssertColor(window.Background, Color.FromRgb(0x11, 0x13, 0x19),
                "The application window did not inherit the dark surface style.");
            AssertColor(window.Foreground, Color.FromRgb(0xF0, 0xF1, 0xF5),
                "The application window did not inherit the light foreground style.");
            var content = (FrameworkElement)window.Content;
            Render(content);
            var main = (FrameworkElement)window.FindName("MainContent");
            Check(main.ActualWidth >= 1_440 * 0.9 && main.TranslatePoint(new Point(), content).X < 50,
                "The main content retained sidebar-sized spacing instead of using the window width.");
            Check(!Descendants<Button>(content).Any(button => ReferenceEquals(button.Command, vm.SelectDriveCommand)) &&
                !Descendants<ItemsControl>(content).Any(control => ReferenceEquals(control.ItemsSource, vm.Drives)) &&
                !Descendants<ProgressBar>(content).Any(bar => bar.DataContext is DriveItem),
                "The deferred drive picker or capacity cards remain in the main window.");

            await vm.ScanAsync();
            Render(content);
            var grid = Descendants<DataGrid>(content).Single();
            Check(grid.IsReadOnly && !grid.CanUserAddRows && !grid.CanUserDeleteRows && !grid.AllowDrop,
                "The analysis result list exposed an edit, create, delete, or drop operation.");
            Equal(4, grid.Items.Count);
            Check(grid.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow,
                "The actual result row template was not realized.");
            var resultBars = Descendants<ProgressBar>(grid).Where(bar => bar.DataContext is ResultRow).ToArray();
            Check(resultBars.Length > 0, "The result percentage template was not realized.");
            foreach (var bar in resultBars)
                AssertOneWayValue(bar, ((ResultRow)bar.DataContext).Share);
            Equal(1_000L, Descendants<SpaceMap>(content).Single().Items!.Sum(item => item.Size));

            foreach (var view in new[] { "files", "types", "folders", "overview" })
            {
                vm.NavigateCommand.Execute(view);
                Render(content);
                Equal(vm.DisplayRows.Count, grid.Items.Count);
            }
            Check(!window.IsVisible, "The test must not display a native window.");
        }
        finally { window.Close(); }
    }

    private static async Task DashboardNavigation()
    {
        var saved = new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddDays(-1));
        var store = new StubStore(saved);
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Dashboard navigation must not scan."));
        var vm = CreateViewModel(scanner, store);
        var window = new MainWindow { DataContext = vm };
        try
        {
            var content = (FrameworkElement)window.Content;
            Render(content);
            var navigation = (FrameworkElement)window.FindName("DashboardNavigation");
            var back = (Button)window.FindName("DashboardBackButton");
            var delete = (Button)window.FindName("DeleteSavedAnalysisButton");
            var cards = new[]
            {
                (Button: (Button)window.FindName("DashboardFilesButton"), View: "files"),
                (Button: (Button)window.FindName("DashboardFoldersButton"), View: "folders"),
                (Button: (Button)window.FindName("DashboardTypesButton"), View: "types")
            };
            Equal("대시보드", vm.PageTitle);
            Check(vm.IsOverview && !vm.IsDetailView && navigation.Visibility == Visibility.Visible &&
                back.Visibility == Visibility.Collapsed, "The initial dashboard or its return control is inconsistent.");
            foreach (var (button, view) in cards)
            {
                Check(ReferenceEquals(button.Command, vm.NavigateCommand) && Equals(button.CommandParameter, view),
                    "A dashboard card is not bound to its analysis page.");
                Check(button.IsEnabled, "An empty analysis stranded dashboard navigation.");
                button.Command!.Execute(button.CommandParameter);
                Render(content);
                Check(vm.IsDetailView && navigation.Visibility == Visibility.Collapsed && back.Visibility == Visibility.Visible,
                    "A detail page did not replace the dashboard navigation with its return control.");
                Check(vm.ShowEmpty && vm.Rows.Count == 0, "Opening an empty detail page fabricated results.");
                Check(delete.Visibility == Visibility.Visible && !delete.IsEnabled,
                    "The global saved-analysis action disappeared or became enabled without a snapshot.");
                back.Command!.Execute(back.CommandParameter);
                Render(content);
                Check(vm.IsOverview && navigation.Visibility == Visibility.Visible && back.Visibility == Visibility.Collapsed,
                    "The detail return control did not restore the dashboard.");
            }

            await vm.RestoreLastScanAsync();
            vm.SetMapLayoutCommand.Execute("donut");
            Render(content);
            var timestamp = vm.SnapshotDescription;
            var target = vm.TargetLabel;
            var scope = vm.ResultScope;
            Equal("4개 파일", vm.LargestFileCount);
            Equal("2개 형식", vm.FileTypeCount);
            Check(Descendants<TextBlock>(cards[0].Button).Any(text => text.Text == vm.LargestFileCount) &&
                Descendants<TextBlock>(cards[2].Button).Any(text => text.Text == vm.FileTypeCount),
                "Dashboard cards did not update their counts after a saved analysis loaded.");
            foreach (var (button, view) in cards)
            {
                button.Command!.Execute(button.CommandParameter);
                Render(content);
                Check(vm.IsDetailView && back.Visibility == Visibility.Visible && delete.IsEnabled,
                    "A populated detail page lost dashboard return or saved-analysis controls.");
                if (view == "files") Equal(saved.Result.LargestFiles.Count, vm.Rows.Count);
                if (view == "types") Equal(saved.Result.FileTypes.Count, vm.Rows.Count);
                if (view == "folders") Equal(saved.Result.Roots.Count, vm.Rows.Count);
                vm.Search = "fixture filter";
                back.Command!.Execute(back.CommandParameter);
                Render(content);
                Equal("", vm.Search);
                Equal(SpaceMapLayout.Donut, vm.MapLayout);
                Equal(timestamp, vm.SnapshotDescription);
                Equal(target, vm.TargetLabel);
                Equal(scope, vm.ResultScope);
                Check(vm.HasSavedAnalysis && vm.HasResult, "Navigation discarded the saved result.");
            }

            vm.NavigateMap(vm.MapItems.Single(item => item.IsDirectory));
            Render(content);
            Check(vm.IsFolders && back.Visibility == Visibility.Visible,
                "Folder entry from the dashboard map omitted the dashboard return control.");
            back.Command!.Execute(back.CommandParameter);
            Render(content);
            Check(vm.IsOverview && navigation.Visibility == Visibility.Visible, "Map navigation could not return to the dashboard.");
            Equal(1_000L, vm.MapItems.Sum(item => item.Size));
            Equal(SpaceMapLayout.Donut, vm.MapLayout);
            Equal(0, scanner.ProgressReports.Count);
            Equal(1, store.LoadCalls);
            Equal(0, store.SaveCalls);
            Equal(0, store.DeleteCalls);
            Check(!window.IsVisible, "Dashboard navigation tests must not display a desktop window.");
        }
        finally { window.Close(); }
    }

    private static async Task RefinedPageHeadings()
    {
        var store = new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddDays(-1)))
        {
            OnDelete = _ => Task.FromException(new IOException("Synthetic cache deletion failure"))
        };
        var vm = CreateViewModel(new StubScanner((_, _, _) =>
            throw new InvalidOperationException("Heading navigation must not scan.")), store);
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.RestoreLastScanAsync();
            var content = (FrameworkElement)window.Content;
            Render(content);
            var title = (TextBlock)window.FindName("PageTitleText");
            var back = (Button)window.FindName("DashboardBackButton");
            var footer = (TextBlock)window.FindName("FooterStatusText");
            var dashboardPosition = title.TranslatePoint(new Point(), content);
            Check(!vm.ShowStatus && !IsEffectivelyVisible(footer),
                "The saved-analysis status remains visible in the footer.");

            foreach (var view in new[] { "files", "folders", "types", "overview" })
            {
                vm.NavigateCommand.Execute(view);
                Render(content);
                var position = title.TranslatePoint(new Point(), content);
                Check(Math.Abs(position.X - dashboardPosition.X) < 0.5 &&
                    Math.Abs(position.Y - dashboardPosition.Y) < 0.5,
                    "The dashboard return control shifted the page heading.");
                if (vm.IsDetailView)
                {
                    var returnPosition = back.TranslatePoint(new Point(), content);
                    Check(IsEffectivelyVisible(back) && returnPosition.Y + back.ActualHeight <= dashboardPosition.Y,
                        "The return control overlaps the page heading instead of occupying separate navigation space.");
                }
                if (view == "files")
                {
                    Equal(1, Descendants<TextBlock>(content).Count(text =>
                        text.Text == "가장 큰 파일" && IsEffectivelyVisible(text)));
                    Check(!vm.ShowTableTitle && !IsEffectivelyVisible((TextBlock)window.FindName("TableTitleText")),
                        "The largest-files table repeats its page heading.");
                }
            }

            await vm.DeleteSavedAnalysisAsync();
            Render(content);
            Check(vm.ShowStatus && IsEffectivelyVisible(footer) && footer.Text == vm.Status &&
                footer.Text.Contains("실패", StringComparison.Ordinal),
                "Removing the saved-status footer also hid a functional failure status.");
        }
        finally { window.Close(); }
    }

    private static async Task ResponsiveSpaceMap()
    {
        var vm = CreateSystemViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.ScanAsync();
            vm.SetMapLayoutCommand.Execute("donut");
            var content = (FrameworkElement)window.Content;
            var mapPanel = (FrameworkElement)window.FindName("SpaceMapPanel");
            var details = (FrameworkElement)window.FindName("SystemInfoPanel");
            var column = (ColumnDefinition)window.FindName("MapColumn");
            var map = Descendants<SpaceMap>(content).Single();
            var originalItems = vm.MapItems;
            double previousDetailsWidth = 0;

            foreach (var width in new[] { 1_440d, 1_650d })
            {
                LayoutAt(content, new Size(width, 920));
                AssertFixedMap();
                vm.SelectedRow = vm.Rows.Single(row => row.Path == @"X:\pagefile.sys");
                LayoutAt(content, new Size(width, 920));
                Check(vm.HasSystemInfo && IsEffectivelyVisible(details), "The system description did not open.");
                Check(details.ActualWidth > previousDetailsWidth, "The details panel did not grow with the window.");
                previousDetailsWidth = details.ActualWidth;
                AssertFixedMap();
                Check(Descendants<DataGrid>(content).Single().ActualWidth >= 480,
                    "Showing the map and description squeezed the results below their usable width.");
            }

            LayoutAt(content, new Size(2400, 920));
            AssertFixedMap();
            var fields = Descendants<ResponsiveDetailsPanel>((FrameworkElement)window.FindName("DetailFields")).Single();
            Check(fields.Children.Count >= 3, "The responsive fields were not realized.");
            var first = fields.Children[0].TranslatePoint(new Point(), fields);
            var second = fields.Children[1].TranslatePoint(new Point(), fields);
            var third = fields.Children[2].TranslatePoint(new Point(), fields);
            Check(second.X > first.X && Math.Abs(second.Y - first.Y) < 0.5 && third.Y > first.Y,
                "Wide details did not form two columns in row order.");
            LayoutAt(content, new Size(1100, 760));
            first = fields.Children[0].TranslatePoint(new Point(), fields);
            second = fields.Children[1].TranslatePoint(new Point(), fields);
            Check(Math.Abs(second.X - first.X) < 0.5 && second.Y > first.Y,
                "Narrow details did not return to one column.");
            Check(!IsEffectivelyVisible(mapPanel) && column.ActualWidth == 0 && IsEffectivelyVisible(details),
                "A narrow window did not prioritize the results and system explanation.");
            Equal(SpaceMapLayout.Donut, map.LayoutMode);
            Check(ReferenceEquals(originalItems, vm.MapItems), "Resizing rebuilt or discarded the map's analysis.");

            LayoutAt(content, new Size(1440, 920));
            AssertFixedMap();
            vm.DismissSystemInfoCommand.Execute(null);
            LayoutAt(content, new Size(1100, 760));
            AssertFixedMap();
            Check(!IsEffectivelyVisible(details), "Dismissing the explanation left its sidebar visible.");
            var handle = (Thumb)window.FindName("MapResizeHandle");
            handle.RaiseEvent(new DragDeltaEventArgs(120, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            LayoutAt(content, new Size(1100, 760));
            Check(Math.Abs(mapPanel.ActualWidth - 480) < 0.5 && Math.Abs(column.ActualWidth - 496) < 0.5,
                "Dragging the resize handle did not resize the map and its column together.");
            vm.ShowSystemInfoCommand.Execute(null);
            LayoutAt(content, new Size(1650, 920));
            handle.RaiseEvent(new DragDeltaEventArgs(10000, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            LayoutAt(content, new Size(1650, 920));
            Check(IsEffectivelyVisible(mapPanel) && ((Grid)window.FindName("ResultColumns")).ColumnDefinitions[1].ActualWidth >= 479.5 && details.ActualWidth >= 325.5,
                "Resizing beyond available space hid the map or squeezed the other panels.");
            handle.RaiseEvent(new DragDeltaEventArgs(-10000, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            LayoutAt(content, new Size(1650, 920));
            Equal(SpaceMapVisibilityConverter.MinimumMapWidth, mapPanel.ActualWidth);
            handle.RaiseEvent(new DragDeltaEventArgs(200, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            LayoutAt(content, new Size(1100, 760));
            Check(!IsEffectivelyVisible(mapPanel) && !IsEffectivelyVisible(handle), "The resize handle remained when the map was hidden.");
            LayoutAt(content, new Size(1440, 920));
            Check(Math.Abs(mapPanel.ActualWidth - 480) < 0.5 && IsEffectivelyVisible(mapPanel), "A narrow window discarded the chosen map width.");
            Check(ReferenceEquals(originalItems, vm.MapItems), "Dragging the map divider rebuilt the analysis.");
            vm.NavigateCommand.Execute("files");
            LayoutAt(content, new Size(1440, 920));
            Check(column.ActualWidth == 0 && !IsEffectivelyVisible(mapPanel),
                "A page without a space map retained a blank fixed-width column.");

            void AssertFixedMap()
            {
                Check(IsEffectivelyVisible(mapPanel), "An adequately wide result view hid its space map.");
                Check(Math.Abs(mapPanel.ActualWidth - 360) < 0.5 && Math.Abs(column.ActualWidth - 376) < 0.5,
                    $"The map resized instead of retaining 360 pixels: panel {mapPanel.ActualWidth}, column {column.ActualWidth}.");
                Equal(SpaceMapLayout.Donut, map.LayoutMode);
            }
        }
        finally { window.Close(); }
    }

    private static async Task BreadcrumbNavigation()
    {
        const int depth = 32;
        var upperPaths = Enumerable.Range(0, depth).Select(index => $"Level{index:D2}").ToArray();
        var lowerPaths = upperPaths.ToArray();
        upperPaths[^1] = ". ";
        lowerPaths[^1] = "...";
        var upper = Branch("Case", upperPaths, 100);
        var lower = Branch("case", lowerPaths, 300);
        var root = new FolderNode(FixturePath, 400, 2, [upper, lower]);
        var result = new ScanResult([root], [], [new FileTypeSummary(".bin", 400, 2)],
            400, 2, 2 * (depth + 1) + 1, 0, TimeSpan.FromSeconds(1), false);
        var store = new StubStore(new SavedScan(result, FixturePath, DateTimeOffset.UtcNow.AddDays(-1)));
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Breadcrumbs must not enumerate."));
        var vm = CreateViewModel(scanner, store);
        await vm.RestoreLastScanAsync();
        vm.SelectDriveCommand.Execute(@"X:\NextAnalysis");
        vm.SetMapLayoutCommand.Execute("donut");
        var timestamp = vm.SnapshotDescription;
        var scope = vm.ResultScope;
        foreach (var branch in new[] { upper, lower })
        {
            var leaf = branch;
            while (leaf.Children.Count > 0) leaf = leaf.Children.Single();
            vm.NavigateMap(new MapItem(leaf.Name, "", leaf.TotalBytes, leaf.Path, true));
            Equal(depth + 3, vm.BreadcrumbItems.Count);
            Equal("전체", vm.BreadcrumbItems[0].Name);
            Check(vm.BreadcrumbItems[0].Path is null, "The aggregate breadcrumb is not distinct from filesystem roots.");
            Equal(FixturePath, vm.BreadcrumbItems[1].Name);
            Equal(FixturePath, vm.BreadcrumbItems[1].Path!);
            Equal(branch.Name, vm.BreadcrumbItems[2].Name);
            Equal(leaf.Path, vm.BreadcrumbItems[^1].Path!);
            Equal(leaf.Name, vm.BreadcrumbItems[^1].Name);
            Check(vm.BreadcrumbItems.Count(item => item.IsCurrent) == 1 && vm.BreadcrumbItems[^1].IsCurrent,
                "The breadcrumb trail does not identify exactly the current folder.");
            Check(!vm.BreadcrumbItems.Any(item => item.Path == @"X:\"),
                "Breadcrumbs invented a parent outside the analyzed root.");
            Check(!vm.NavigateBreadcrumbCommand.CanExecute(vm.BreadcrumbItems[^1]),
                "The already current breadcrumb remained an actionable navigation target.");
            vm.Search = "temporary filter";
            vm.NavigateBreadcrumbCommand.Execute(vm.BreadcrumbItems[2]);
            Equal(branch.Path, vm.Breadcrumb);
            Equal("", vm.Search);
            Equal(branch.Children.Single().Path, vm.Rows.Single().Path);
            Equal(branch.TotalBytes, vm.MapItems.Sum(item => item.Size));
            Check(!vm.NavigateBreadcrumbCommand.CanExecute(@"X:\") &&
                !vm.NavigateBreadcrumbCommand.CanExecute($@"{FixturePath}\CASE"),
                "Breadcrumb navigation accepted an unindexed or case-folded path.");
            vm.NavigateBreadcrumbCommand.Execute(@"X:\");
            Equal(branch.Path, vm.Breadcrumb);
            Equal(@"X:\NextAnalysis", vm.TargetLabel);
            Equal(scope, vm.ResultScope);
            Equal(timestamp, vm.SnapshotDescription);
            Equal(SpaceMapLayout.Donut, vm.MapLayout);
        }
        vm.NavigateBreadcrumbCommand.Execute(null);
        Equal(1, vm.BreadcrumbItems.Count);
        Check(vm.BreadcrumbItems[0].Path is null && vm.BreadcrumbItems[0].IsCurrent,
            "Returning to the aggregate view retained stale folder ancestors.");
        Equal(FixturePath, vm.Rows.Single().Path);
        Equal(0, scanner.ProgressReports.Count);
        Equal(1, store.LoadCalls);
        Equal(0, store.SaveCalls);
        await vm.DeleteSavedAnalysisAsync();
        Equal(0, vm.BreadcrumbItems.Count);
        Check(!vm.NavigateBreadcrumbCommand.CanExecute(FixturePath),
            "Deleting the analysis left its former folder index navigable.");

        static FolderNode Branch(string name, string[] descendants, long bytes)
        {
            var paths = new List<string> { $@"{FixturePath}\{name}" };
            foreach (var descendant in descendants) paths.Add(Path.Join(paths[^1], descendant));
            var node = new FolderNode(paths[^1], bytes, 1);
            for (var index = paths.Count - 2; index >= 0; index--) node = new FolderNode(paths[index], bytes, 1, [node]);
            return node;
        }
    }

    private static async Task BreadcrumbXaml()
    {
        var store = new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddDays(-1)));
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Breadcrumb buttons must not enumerate."));
        var vm = CreateViewModel(scanner, store);
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.RestoreLastScanAsync();
            vm.NavigateMap(vm.MapItems.Single(item => item.IsDirectory));
            vm.OpenRowCommand.Execute(vm.Rows.Single());
            var content = (FrameworkElement)window.Content;
            Render(content);
            var trail = (FrameworkElement)window.FindName("BreadcrumbTrail");
            Check(IsEffectivelyVisible(trail), "The current-path trail is absent from folder navigation.");
            var details = (FrameworkElement)window.FindName("SystemInfoPanel");
            Check(vm.HasDetails && IsEffectivelyVisible(details) &&
                vm.Details.Any(field => field.Label == "전체 경로" && field.Value == $@"{FixturePath}\Projects\Archive"),
                "Entering an ordinary folder discarded that folder's details.");
            var grid = (DataGrid)window.FindName("ResultsGrid");
            var dot = (ResultRow)grid.Items[0];
            var parent = (ResultRow)grid.Items[1];
            Equal(".", dot.Name);
            Equal("..", parent.Name);
            Equal(200L, dot.Bytes);
            Equal(600L, parent.Bytes);
            Equal(20d, dot.Share);
            Equal(60d, parent.Share);
            grid.SelectedItem = parent;
            Render(content);
            Equal("..", ((ResultRow)grid.Items[1]).Name);
            Equal("Projects", vm.Details.Single(field => field.Label == "이름").Value);
            Equal($@"{FixturePath}\Projects", vm.Details.Single(field => field.Label == "전체 경로").Value);
            Equal($@"{FixturePath}\Projects\Archive", vm.Breadcrumb);
            vm.DismissSystemInfoCommand.Execute(null);
            grid.SelectedItem = dot;
            Render(content);
            Check(vm.HasDetailPanel && vm.Details.Any(field => field.Label == "전체 경로" && field.Value == vm.Breadcrumb),
                "Selecting dot did not show the current folder details.");
            Check(grid.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow,
                "Dot is not a regular result row below the column headers.");
            vm.Search = "no matching folders";
            Render(content);
            Check(grid.Items.Count == 2 && ((ResultRow)grid.Items[0]).Navigation == FolderNavigationKind.Current &&
                ((ResultRow)grid.Items[1]).Navigation == FolderNavigationKind.Parent && IsEffectivelyVisible(details),
                "An empty folder list hid navigation entries or current-folder details.");
            var ancestor = Descendants<Button>(trail).Single(button =>
                button.DataContext is BreadcrumbItem item && item.Path == $@"{FixturePath}\Projects");
            Check(ReferenceEquals(ancestor.Command, vm.NavigateBreadcrumbCommand) && ancestor.IsEnabled,
                "An ancestor segment is not an enabled breadcrumb action.");
            ancestor.Command!.Execute(ancestor.CommandParameter);
            Render(content);
            Equal($@"{FixturePath}\Projects", vm.Breadcrumb);
            Equal("Archive", vm.Rows.Single().Name);
            var rootButton = Descendants<Button>(trail).Single(button =>
                button.DataContext is BreadcrumbItem item && item.Path == FixturePath);
            rootButton.Command!.Execute(rootButton.CommandParameter);
            Render(content);
            Equal(FixturePath, vm.Breadcrumb);
            Equal(2, vm.Rows.Count);
            foreach (var column in grid.Columns)
            {
                for (var direction = 0; direction < 2; direction++)
                {
                    typeof(DataGrid).GetMethod("OnSorting", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(grid, [new DataGridSortingEventArgs(column)]);
                    Render(content);
                    Equal(FolderNavigationKind.Current, ((ResultRow)grid.Items[0]).Navigation);
                    Equal(FolderNavigationKind.Parent, ((ResultRow)grid.Items[1]).Navigation);
                    var actual = grid.Items.Cast<ResultRow>().Skip(2).ToArray();
                    var comparison = new ResultRowComparer(column.SortMemberPath, column.SortDirection!.Value);
                    Check(actual.Zip(actual.Skip(1)).All(pair => comparison.Compare(pair.First, pair.Second) <= 0),
                        "Ordinary folders no longer obey column sorting.");
                }
            }
            vm.OpenRowCommand.Execute(grid.Items[1]);
            Render(content);
            Check(vm.BreadcrumbItems.Count == 1 && !vm.UpCommand.CanExecute(null) && grid.Items.Count >= 2,
                "Parent navigation did not stop at the analyzed scope.");
            Check(!vm.HasDetails && !IsEffectivelyVisible(details), "The aggregate view retained stale folder details.");
            Equal(0, scanner.ProgressReports.Count);
            Equal(0, store.SaveCalls);
            Check(!window.IsVisible, "The breadcrumb fixture must not open a desktop window.");
        }
        finally { window.Close(); }
    }

    private static async Task ReadOnlyFolderBrowsing()
    {
        using var fixture = new PickerFixture();
        var child = Path.Combine(fixture.Root, "alpha");
        var nested = Path.Combine(child, "nested");
        var empty = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(empty);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "keep.txt"), "Synthetic source content");
        await File.WriteAllBytesAsync(Path.Combine(nested, "keep.bin"), [1, 2, 3, 4, 5]);
        var before = CapturePickerTree(fixture.Root);
        var browser = new ReadOnlyFolderBrowser();

        var root = await browser.ReadAsync(fixture.Root);
        Equal(fixture.Root, root.Path);
        Check(root.Entries.Select(entry => entry.Name).Order().SequenceEqual(new[] { "alpha", "empty" }),
            "The folder picker exposed files or omitted immediate subdirectories.");
        var inside = await browser.ReadAsync(child);
        Equal(fixture.Root, inside.ParentPath!);
        Equal(1, inside.Entries.Count);
        Equal(nested, inside.Entries[0].Path);
        Equal(0, (await browser.ReadAsync(empty)).Entries.Count);

        var missing = Path.Combine(fixture.Root, "never-created");
        try
        {
            await browser.ReadAsync(missing);
            throw new InvalidOperationException("A missing directory unexpectedly became browsable.");
        }
        catch (IOException) { }
        Check(!Directory.Exists(missing), "Browsing a missing path created a directory.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await browser.ReadAsync(fixture.Root, cancellation.Token);
            throw new InvalidOperationException("A cancelled folder read unexpectedly succeeded.");
        }
        catch (OperationCanceledException) { }

        var after = CapturePickerTree(fixture.Root);
        Check(before.SequenceEqual(after), "Folder browsing changed names, contents, attributes, or write times in the source tree.");
    }

    private static async Task ReadOnlyFileBrowsing()
    {
        using var fixture = new PickerFixture();
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Root, "nested")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "sample.bin"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "no-extension"), "");
        var hidden = Path.Combine(fixture.Root, ".hidden.txt");
        await File.WriteAllTextAsync(hidden, "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        await File.WriteAllTextAsync(Path.Combine(nested, "nested.txt"), "not immediate");
        var before = CapturePickerTree(fixture.Root);
        var browser = new ReadOnlyFileBrowser();
        var files = await browser.ReadAsync(fixture.Root);
        Equal(3, files.Count);
        Equal(3L, files.Single(file => file.Name == "sample.bin").Size);
        Equal("", files.Single(file => file.Name == "no-extension").Extension);
        Check(files.Any(file => file.Path == hidden), "Hidden/system files were omitted.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await browser.ReadAsync(fixture.Root, cancellation.Token); throw new InvalidOperationException("Cancelled read succeeded."); }
        catch (OperationCanceledException) { }
        try { await browser.ReadAsync(Path.Combine(fixture.Root, "missing")); throw new InvalidOperationException("Missing folder read succeeded."); }
        catch (IOException) { }
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "File listing changed the synthetic source tree.");
    }

    private static async Task FolderFiles()
    {
        var path = $@"{FixturePath}\Projects";
        var pending = new TaskCompletionSource<IReadOnlyList<FileRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new StubFileBrowser((_, _) => pending.Task);
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Folder file navigation must not scan."));
        var store = new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow));
        var vm = new MainViewModel(scanner, store, fileBrowser: browser);
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.RestoreLastScanAsync();
            Equal(0, browser.Calls.Count);
            vm.NavigateMap(vm.MapItems.Single(item => item.IsDirectory));
            var content = (FrameworkElement)window.Content;
            Render(content);
            var grid = (DataGrid)window.FindName("ResultsGrid");
            grid.SelectedItem = vm.DisplayRows[1];
            var files = Enumerable.Range(0, 1_005).Select(index =>
                new FileRecord($@"{path}\file{index}.txt", $"file{index}.txt", index, ".txt")).ToArray();
            pending.SetResult(files);
            await vm.WaitForFolderFilesAsync();
            Render(content);
            Equal(1_006, vm.Rows.Count);
            Equal(1_008, grid.Items.Count);
            Equal(FolderNavigationKind.Current, ((ResultRow)grid.Items[0]).Navigation);
            Equal(FolderNavigationKind.Parent, ((ResultRow)grid.Items[1]).Navigation);
            Equal(FolderNavigationKind.Parent, vm.SelectedRow!.Navigation);
            Equal("WindowsFSFixture", vm.Details.Single(field => field.Label == "이름").Value);
            var file = vm.Rows.Single(row => row.Name == "file1004.txt");
            Check(!file.IsDirectory && file.IsLiveMetadata && file.Bytes == 1_004, "A file was presented as a folder or stale scan metadata.");
            grid.SelectedItem = file;
            Render(content);
            Equal(file.Name, vm.Details.Single(field => field.Label == "이름").Value);
            Check(vm.CanPreview && vm.HasDetails && vm.Details.Any(field => field.Label == "폴더를 연 시점 크기 · 논리적"),
                "File selection did not expose details and bounded preview availability.");
            vm.Search = "file1004";
            Equal(1, vm.Rows.Count);
            vm.NavigateCommand.Execute("files");
            Equal(4, vm.Rows.Count);
            vm.NavigateBreadcrumbCommand.Execute(path);
            await vm.WaitForFolderFilesAsync();
            Equal(1_006, vm.Rows.Count);
            Equal(1, browser.Calls.Count);
            Equal(0, scanner.ProgressReports.Count);
            Equal(0, store.SaveCalls);
            await vm.DeleteSavedAnalysisAsync();
            Equal(0, vm.DisplayRows.Count);
        }
        finally { window.Close(); }
    }

    private static async Task FolderFileReadFailures()
    {
        var path = $@"{FixturePath}\Projects";
        var late = new TaskCompletionSource<IReadOnlyList<FileRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = true;
        var browser = new StubFileBrowser((folder, _) => folder == path ? late.Task :
            fail ? Task.FromException<IReadOnlyList<FileRecord>>(new UnauthorizedAccessException()) : Task.FromResult<IReadOnlyList<FileRecord>>([]));
        var vm = new MainViewModel(new StubScanner((_, _, _) => throw new InvalidOperationException("Unexpected scan")),
            new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow)), fileBrowser: browser);
        await vm.RestoreLastScanAsync();
        vm.NavigateBreadcrumbCommand.Execute(path);
        var previousRead = vm.WaitForFolderFilesAsync();
        vm.NavigateBreadcrumbCommand.Execute($@"{FixturePath}\Empty");
        await vm.WaitForFolderFilesAsync();
        await previousRead;
        Check(browser.Calls[0].Token.IsCancellationRequested, "Leaving a folder did not cancel its file read.");
        Check(vm.ResultCount.Contains("접근 불가"), "Inaccessible files were silently presented as an empty folder.");
        late.SetResult([new FileRecord($@"{path}\late.txt", "late.txt", 3, ".txt")]);
        DrainDispatcher();
        Equal(0, vm.Rows.Count);
        fail = false;
        vm.NavigateCommand.Execute("overview");
        vm.NavigateCommand.Execute("folders");
        await vm.WaitForFolderFilesAsync();
        Equal("0개 항목", vm.ResultCount);
        Equal(3, browser.Calls.Count);
        vm.NavigateBreadcrumbCommand.Execute(path);
        await vm.WaitForFolderFilesAsync();
        Equal(4, browser.Calls.Count);
        Check(vm.Rows.Any(row => row.Name == "late.txt"), "A cancelled read was incorrectly cached or could not be retried.");
    }

    private static async Task FolderPickerNavigation()
    {
        var childPath = $@"{FixturePath}\Child";
        var grandchildPath = $@"{childPath}\Nested";
        var root = new FolderLocation(FixturePath, @"X:\", [new FolderEntry("Child", childPath)]);
        var child = new FolderLocation(childPath, FixturePath, [new FolderEntry("Nested", grandchildPath)]);
        var grandchild = new FolderLocation(grandchildPath, childPath, []);
        var browser = new StubFolderBrowser((path, _) => Task.FromResult(path switch
        {
            FixturePath => root,
            var value when value == childPath => child,
            var value when value == grandchildPath => grandchild,
            _ => throw new DirectoryNotFoundException("Synthetic missing directory")
        }));
        var vm = new FolderPickerViewModel(FixturePath, [FixturePath], browser);
        Equal(0, browser.Calls.Count);
        await vm.InitializeAsync();
        Equal(FixturePath, vm.CurrentPath);
        Equal(1, vm.Entries.Count);
        Check(vm.IsIdle && !vm.IsLoading, "Initial folder navigation did not settle.");
        Check(await vm.NavigateAsync(childPath), "An existing child could not be entered.");
        Equal(childPath, vm.CurrentPath);
        Equal(FixturePath, vm.ParentPath!);
        Check(await vm.NavigateAsync(vm.ParentPath!), "Parent navigation failed.");
        vm.SelectedEntry = vm.Entries.Single();
        Check(await vm.ConfirmAsync(), "A selected child directory could not be confirmed.");
        Equal(childPath, vm.SelectedPath!);
        Check(browser.Calls.Count(call => call.Path == childPath) >= 2,
            "Confirmation did not revalidate the selected directory.");

        var typed = new FolderPickerViewModel(FixturePath, [FixturePath], browser);
        await typed.InitializeAsync();
        typed.SelectedEntry = typed.Entries.Single();
        typed.PathInput = grandchildPath;
        Check(typed.SelectedEntry is null, "Typing a path retained an unrelated row selection.");
        Check(await typed.ConfirmAsync(), "A directly entered existing path could not be selected.");
        Equal(grandchildPath, typed.SelectedPath!);
    }

    private static async Task FolderPickerInvalidPaths()
    {
        foreach (var error in new Exception[]
        {
            new DirectoryNotFoundException(@"Synthetic missing X:\Private\directory"),
            new UnauthorizedAccessException(@"Synthetic denied X:\Private\directory"),
            new IOException(@"Synthetic unavailable X:\Private\directory"),
            new ArgumentException(@"Synthetic invalid X:\Private\directory")
        })
        {
            var browser = new StubFolderBrowser((path, _) => path == FixturePath
                ? Task.FromResult(new FolderLocation(FixturePath, @"X:\", []))
                : Task.FromException<FolderLocation>(error));
            var vm = new FolderPickerViewModel(FixturePath, [FixturePath], browser);
            await vm.InitializeAsync();
            vm.PathInput = $@"{FixturePath}\Invalid";
            Check(!await vm.ConfirmAsync(), "An invalid typed path confirmed the previous valid directory.");
            Check(vm.SelectedPath is null, "Failed confirmation exposed an old selection.");
            Equal(FixturePath, vm.CurrentPath);
            Check(vm.IsIdle && !vm.IsLoading, "A rejected path left the picker busy.");
            Check(!vm.Status.Contains("Synthetic", StringComparison.Ordinal) &&
                !vm.Status.Contains(@"X:\Private", StringComparison.Ordinal), "Raw folder errors leaked private paths.");
            vm.PathInput = FixturePath;
            Check(await vm.ConfirmAsync(), "A failed path selection prevented a valid retry.");
            Equal(FixturePath, vm.SelectedPath!);
        }

        var missingChild = $@"{FixturePath}\Removed";
        var vanished = new StubFolderBrowser((path, _) => path == FixturePath
            ? Task.FromResult(new FolderLocation(FixturePath, @"X:\", [new FolderEntry("Removed", missingChild)]))
            : Task.FromException<FolderLocation>(new DirectoryNotFoundException("Synthetic removed directory")));
        var selection = new FolderPickerViewModel(FixturePath, [FixturePath], vanished);
        await selection.InitializeAsync();
        selection.SelectedEntry = selection.Entries.Single();
        Check(!await selection.ConfirmAsync() && selection.SelectedPath is null,
            "A directory removed after listing was accepted without validation.");
    }

    private static async Task FolderPickerCancellation()
    {
        var slowPath = $@"{FixturePath}\Slow";
        var fastPath = $@"{FixturePath}\Fast";
        var slow = new TaskCompletionSource<FolderLocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new StubFolderBrowser((path, _) => path == slowPath ? slow.Task :
            Task.FromResult(new FolderLocation(path, FixturePath, [])));
        var vm = new FolderPickerViewModel(FixturePath, [FixturePath], browser);
        await vm.InitializeAsync();
        var older = vm.NavigateAsync(slowPath);
        Check(vm.IsLoading, "A pending directory read did not report its busy state.");
        Check(await vm.NavigateAsync(fastPath), "New navigation could not supersede a pending directory read.");
        Check(browser.Calls.Single(call => call.Path == slowPath).Token.IsCancellationRequested,
            "Superseded folder navigation was not cancelled.");
        slow.TrySetResult(new FolderLocation(slowPath, FixturePath, [new FolderEntry("Old", slowPath + "\\Old")]));
        await older;
        Equal(fastPath, vm.CurrentPath);
        Equal(0, vm.Entries.Count);
        Check(vm.IsIdle, "Stale completion left the picker busy.");

        var closing = new TaskCompletionSource<FolderLocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closingBrowser = new StubFolderBrowser((path, _) => path == slowPath ? closing.Task :
            Task.FromResult(new FolderLocation(path, FixturePath, [])));
        var cancelled = new FolderPickerViewModel(FixturePath, [FixturePath], closingBrowser);
        await cancelled.InitializeAsync();
        cancelled.PathInput = slowPath;
        var confirmation = cancelled.ConfirmAsync();
        cancelled.Cancel();
        Check(closingBrowser.Calls.Single(call => call.Path == slowPath).Token.IsCancellationRequested,
            "Closing the picker did not cancel outstanding validation.");
        closing.TrySetResult(new FolderLocation(slowPath, FixturePath, []));
        Check(!await confirmation && cancelled.SelectedPath is null,
            "A late directory read accepted a selection after cancellation.");
        Equal(FixturePath, cancelled.CurrentPath);
    }

    private static async Task FolderPickerXaml()
    {
        var browser = new StubFolderBrowser((path, _) => Task.FromResult(new FolderLocation(path, null,
            [new FolderEntry("Child", $@"{FixturePath}\Child")])));
        var vm = new FolderPickerViewModel(FixturePath, [FixturePath], browser);
        var window = new FolderPickerWindow(vm);
        try
        {
            var content = (FrameworkElement)window.Content;
            Render(content);
            Equal(0, browser.Calls.Count);
            await vm.InitializeAsync();
            Render(content);
            var list = Descendants<ListBox>(content).Single(control => ReferenceEquals(control.ItemsSource, vm.Entries));
            Equal(1, list.Items.Count);
            Check(!list.AllowDrop && list.ContextMenu is null && !window.AllowDrop,
                "The folder picker exposed shell drop or context-menu operations.");
            Check(!Descendants<FrameworkElement>(content).Any(element => element.AllowDrop),
                "A folder picker element accepted filesystem drops.");
            Check(!Descendants<Button>(content).Any(button => button.Content is string label &&
                new[] { "새 폴더", "삭제", "이름 변경", "잘라내기", "붙여넣기" }.Any(label.Contains)),
                "The folder picker exposes a filesystem mutation button.");
            Check(!window.IsVisible, "Folder picker tests must not display a native window.");
        }
        finally { window.Close(); }
    }

    private static async Task LongListScrolling()
    {
        var files = Enumerable.Range(0, 1_000).Select(index =>
            new FileRecord(index == 0 ? @"X:\pagefile.sys" : $@"X:\Fixture\{new string('p', 180)}\file-{index:D4}.bin",
                index == 0 ? "pagefile.sys" : $"file-{index:D4}.bin", 1_000 - index, ".bin")).ToArray();
        var bytes = files.Sum(file => file.Size);
        var result = new ScanResult([new FolderNode(@"X:\", bytes, files.Length)], files,
            [new FileTypeSummary(".bin", bytes, files.Length)], bytes, files.Length, 1, 0, TimeSpan.FromSeconds(1), false);
        var vm = new MainViewModel(new StubScanner((_, _, _) => Task.FromResult(result)), null,
            new SystemItemCatalog(@"X:\Windows", @"X:\"));
        vm.SelectDriveCommand.Execute(@"X:\");
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.ScanAsync();
            var content = (FrameworkElement)window.Content;
            foreach (var size in new[] { new Size(1440, 920), new Size(1100, 760) })
            foreach (var view in new[] { "overview", "files" })
            foreach (var infoVisible in new[] { false, true })
            {
                vm.NavigateCommand.Execute(view);
                vm.SelectedRow = infoVisible ? vm.Rows[0] : null;
                RenderSize(content, size);
                var mapColumn = (ColumnDefinition)window.FindName("MapColumn");
                var expectedMapVisible = view == "overview" && (!infoVisible || size.Width >= 1_440);
                Check(expectedMapVisible ? Math.Abs(mapColumn.ActualWidth - 376) < 0.5 : mapColumn.ActualWidth == 0,
                    "Map visibility left the wrong amount of layout space in the result view.");
                var main = (FrameworkElement)window.FindName("MainContent");
                Check(main.ActualWidth >= size.Width * 0.9 && main.TranslatePoint(new Point(), content).X < 50,
                    $"The content retained a sidebar gap at {size}.");
                if (view == "overview")
                {
                    foreach (var name in new[] { "DashboardFilesButton", "DashboardFoldersButton", "DashboardTypesButton" })
                    {
                        var card = (Button)window.FindName(name);
                        var position = card.TranslatePoint(new Point(), main);
                        Check(card.ActualWidth >= 200 && position.X >= 0 && position.X + card.ActualWidth <= main.ActualWidth + 1,
                            $"A dashboard entry was clipped or too narrow at {size}.");
                    }
                }
                var grid = Descendants<DataGrid>(content).Single();
                var scroller = Descendants<ScrollViewer>(grid).First();
                var scrollbar = Descendants<ScrollBar>(scroller).Single(bar => bar.Orientation == Orientation.Vertical);
                var presenter = Descendants<ScrollContentPresenter>(scroller).First();
                Check(presenter.ActualHeight >= grid.RowHeight * 4,
                    $"Only {presenter.ActualHeight / grid.RowHeight:0.0} rows fit in the file list at {size}.");
                Check(scroller.ScrollableHeight > 0, "The long list did not expose a scrolling range.");
                Check(Descendants<DataGridRow>(grid).Count() < 100, "The long list realized all rows instead of virtualizing.");
                scroller.ScrollToTop();
                RenderSize(content, size);
                var firstRow = Descendants<DataGridRow>(grid).First();
                firstRow.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
                RenderSize(content, size);
                Check(scroller.VerticalOffset > 0, "Mouse wheel did not move the file list.");
                Check(scroller.VerticalOffset <= scroller.ViewportHeight,
                    "A single wheel notch skipped more than an entire visible page.");
                scroller.ScrollToVerticalOffset(0.5);
                RenderSize(content, size);
                Check(Math.Abs(scroller.VerticalOffset - 0.5) < 0.01,
                    "File scrolling snapped to whole rows instead of preserving a partial-row offset.");
                var track = Descendants<Track>(scrollbar).Single();
                Check(track.Thumb.ActualHeight >= 24, "The scrollbar thumb is too small to target in a long file list.");
                track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, track.ActualHeight) { RoutedEvent = Thumb.DragDeltaEvent });
                track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, track.ActualHeight, false) { RoutedEvent = Thumb.DragCompletedEvent });
                RenderSize(content, size);
                Check(grid.ItemContainerGenerator.ContainerFromIndex(999) is DataGridRow,
                    "Dragging the scrollbar to the bottom did not reach the final file.");
                Check(Descendants<DataGridRow>(grid).Count() < 100,
                    "Scrollbar navigation stopped recycling file rows.");
            }
        }
        finally { window.Close(); }

        static void RenderSize(FrameworkElement content, Size size)
        {
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            DrainDispatcher();
            content.UpdateLayout();
        }
    }

    private static async Task Navigation()
    {
        var vm = CreateViewModel(new StubScanner((_, _, _) => Task.FromResult(FixtureResult())));
        await vm.ScanAsync();
        Check(vm.HasResult && !vm.IsBusy && !vm.ShowEmpty, "The scan did not settle into a result state.");
        Equal(1_000L, vm.MapItems.Sum(item => item.Size));
        vm.NavigateMap(vm.MapItems.Single(item => item.IsDirectory));
        Check(vm.IsFolders, "Map navigation did not select the folder feature.");
        Equal(600L, vm.MapItems.Sum(item => item.Size));
        Equal("Archive", vm.Rows.Single().Name);
        vm.OpenRowCommand.Execute(vm.Rows.Single());
        Equal(200L, vm.MapItems.Sum(item => item.Size));
        Equal(0, vm.Rows.Count);
        vm.UpCommand.Execute(null);
        Equal("Archive", vm.Rows.Single().Name);
        vm.HomeCommand.Execute(null);
        Equal(FixturePath, vm.Rows.Single().Path);

        vm.NavigateCommand.Execute("types");
        Equal(1_000L, vm.Rows.Sum(row => row.Bytes));
        Equal(4L, vm.Rows.Sum(row => row.Count));
        vm.SelectedRow = vm.Rows[0];
        Check(!vm.RevealCommand.CanExecute(null) && !vm.CopyPathCommand.CanExecute(null),
            "Aggregate file types must not offer filesystem actions.");
        vm.NavigateCommand.Execute("files");
        vm.Search = ".TXT";
        Equal("notes.txt", vm.Rows.Single().Name);
        vm.Search = "Archive";
        Equal("history.bin", vm.Rows.Single().Name);
        vm.Search = "not-in-fixture";
        Check(vm.NoRows, "Empty search results were not reported.");
    }

    private static async Task CaseDistinctNavigation()
    {
        var upperLeaf = new FolderNode($@"{FixturePath}\Case\Data\UpperOnly", 100, 1);
        var lowerLeaf = new FolderNode($@"{FixturePath}\case\Data\LowerOnly", 300, 1);
        var upperData = new FolderNode($@"{FixturePath}\Case\Data", 100, 1, [upperLeaf]);
        var lowerData = new FolderNode($@"{FixturePath}\case\Data", 300, 1, [lowerLeaf]);
        var upper = new FolderNode($@"{FixturePath}\Case", 100, 1, [upperData]);
        var lower = new FolderNode($@"{FixturePath}\case", 300, 1, [lowerData]);
        var root = new FolderNode(FixturePath, 400, 2, [upper, lower]);
        var result = new ScanResult([root], [], [new FileTypeSummary(".bin", 400, 2)],
            400, 2, 7, 0, TimeSpan.FromSeconds(1), false);
        var store = new StubStore();
        var vm = CreateViewModel(new StubScanner((_, _, _) => Task.FromResult(result)), store);
        await vm.ScanAsync();
        AssertBranches(vm);
        Equal(1, store.SaveCalls);

        var restoredScanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Restore must not enumerate."));
        var restored = CreateViewModel(restoredScanner, store);
        await restored.RestoreLastScanAsync();
        AssertBranches(restored);
        Equal(0, restoredScanner.ProgressReports.Count);

        void AssertBranches(MainViewModel target)
        {
            foreach (var branch in new[] { upper, lower })
            {
                target.NavigateCommand.Execute("overview");
                Equal(2, target.MapItems.Count(item => item.IsDirectory));
                target.NavigateMap(target.MapItems.Single(item => item.Path == branch.Path));
                Equal(branch.Path, target.Breadcrumb);
                Equal(branch.TotalBytes, target.MapItems.Sum(item => item.Size));
                Equal(branch.Children.Single().Path, target.Rows.Single().Path);

                target.OpenRowCommand.Execute(target.Rows.Single());
                var data = branch.Children.Single();
                Equal(data.Path, target.Breadcrumb);
                Equal(data.Children.Single().Path, target.Rows.Single().Path);
                Equal(branch.TotalBytes, target.MapItems.Sum(item => item.Size));
                target.OpenRowCommand.Execute(target.Rows.Single());
                Equal(data.Children.Single().Path, target.Breadcrumb);
                Equal(0, target.Rows.Count);

                target.UpCommand.Execute(null);
                Equal(data.Path, target.Breadcrumb);
                Equal(data.Children.Single().Path, target.Rows.Single().Path);
                target.UpCommand.Execute(null);
                Equal(branch.Path, target.Breadcrumb);
                Equal(data.Path, target.Rows.Single().Path);
                target.UpCommand.Execute(null);
                Equal(FixturePath, target.Breadcrumb);
                Equal(2, target.Rows.Count);
                Equal(400L, target.MapItems.Sum(item => item.Size));
            }
        }
    }

    private static async Task GroupedMap()
    {
        var children = Enumerable.Range(0, 50)
            .Select(index => new FolderNode($@"{FixturePath}\Folder{index:D2}", 10, 1)).ToArray();
        var root = new FolderNode(FixturePath, 550, 51, children);
        var result = new ScanResult([root], [], [new FileTypeSummary(".bin", 550, 51)],
            550, 51, 51, 0, TimeSpan.FromSeconds(1), false);
        var vm = CreateViewModel(new StubScanner((_, _, _) => Task.FromResult(result)));
        await vm.ScanAsync();
        Equal(36, vm.MapItems.Count);
        Equal(550L, vm.MapItems.Sum(item => item.Size));
        Check(vm.MapItems.Any(item => !item.IsDirectory && item.Size == 160),
            "The smaller folders were lost instead of grouped.");
        Check(vm.MapItems.Any(item => !item.IsDirectory && item.Size == 50),
            "Files directly in the selected folder were omitted.");
    }

    private static async Task MapLayoutSwitching()
    {
        var scanner = new StubScanner((_, _, _) => Task.FromResult(FixtureResult()));
        var store = new StubStore();
        var vm = CreateViewModel(scanner, store);
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.ScanAsync();
            var content = (FrameworkElement)window.Content;
            Render(content);
            var map = Descendants<SpaceMap>(content).Single();
            var binding = BindingOperations.GetBindingExpression(map, SpaceMap.LayoutModeProperty);
            Check(binding is not null && binding.Status == BindingStatus.Active,
                "The actual space map is not bound to the selected layout.");
            Equal(SpaceMapLayout.Blocks, vm.MapLayout);
            Equal(SpaceMapLayout.Blocks, map.LayoutMode);
            Check(vm.IsBlockMap && !vm.IsDonutMap, "The default layout selection is inconsistent.");

            var mapItems = vm.MapItems;
            var rows = vm.Rows;
            var scannedAt = vm.SnapshotDescription;
            var donutButton = Descendants<ButtonBase>(content).Single(button =>
                ReferenceEquals(button.Command, vm.SetMapLayoutCommand) && Equals(button.CommandParameter, "donut"));
            var blockButton = Descendants<ButtonBase>(content).Single(button =>
                ReferenceEquals(button.Command, vm.SetMapLayoutCommand) && Equals(button.CommandParameter, "blocks"));
            donutButton.Command!.Execute(donutButton.CommandParameter);
            Render(content);
            Equal(SpaceMapLayout.Donut, map.LayoutMode);
            Check(vm.IsDonutMap && !vm.IsBlockMap, "Donut selection was not reflected in the view model.");
            Check(ReferenceEquals(mapItems, vm.MapItems) && ReferenceEquals(rows, vm.Rows),
                "Changing the diagram unnecessarily rebuilt analysis results.");
            Equal(scannedAt, vm.SnapshotDescription);
            Equal(1_000L, map.Items!.Sum(item => item.Size));

            vm.NavigateMap(vm.MapItems.Single(item => item.IsDirectory));
            Equal(SpaceMapLayout.Donut, vm.MapLayout);
            Equal(600L, vm.MapItems.Sum(item => item.Size));
            vm.UpCommand.Execute(null);
            Equal(SpaceMapLayout.Donut, vm.MapLayout);
            foreach (var view in new[] { "files", "types", "folders", "overview" })
            {
                vm.NavigateCommand.Execute(view);
                Render(content);
                Equal(SpaceMapLayout.Donut, vm.MapLayout);
                Equal(SpaceMapLayout.Donut, map.LayoutMode);
            }
            blockButton.Command!.Execute(blockButton.CommandParameter);
            Render(content);
            Equal(SpaceMapLayout.Blocks, map.LayoutMode);
            Equal(1, scanner.ProgressReports.Count);
            Equal(1, store.SaveCalls);
            Equal(0, store.LoadCalls);
            Check(!window.IsVisible, "Layout switching must not display a test window.");
        }
        finally { window.Close(); }
    }

    private static Task DonutGeometry()
    {
        MapItem[] items =
        [
            new("Large", "60 B", 60, $@"{FixturePath}\Large", true),
            new("Medium", "30 B", 30, $@"{FixturePath}\Medium", true),
            new("Small", "10 B", 10, $@"{FixturePath}\Small", true)
        ];
        var map = new SpaceMap { LayoutMode = SpaceMapLayout.Donut, Items = items };
        var bitmap = RenderMap(map, new Size(340, 440));
        var geometries = MapField<Geometry[]>(map, "_segments");
        var center = MapField<Point>(map, "_donutCenter");
        var radius = (MapField<double>(map, "_innerRadius") + MapField<double>(map, "_outerRadius")) / 2;
        Equal(3, geometries.Length);
        Equal(100L, MapField<long>(map, "_totalBytes"));
        Equal(-1, MapHit(map, center));
        Equal(-1, MapHit(map, new Point(-1, -1)));
        var area = geometries.Sum(geometry => geometry.GetArea());
        var cumulative = 0d;
        MapItem? invoked = null;
        map.ItemInvoked += (_, item) => invoked = item;
        for (var index = 0; index < items.Length; index++)
        {
            var fraction = items[index].Size / 100d;
            Check(Math.Abs(geometries[index].GetArea() / area - fraction) < 0.01,
                "A donut sector does not represent its share of bytes.");
            Check(!geometries[index].FillContains(center), "A donut sector covers the center hole.");
            var angle = (cumulative + fraction / 2) * Math.Tau;
            var point = new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius);
            Equal(index, MapHit(map, point));
            Check(PixelAlpha(bitmap, point) > 0, "A clickable donut sector was not drawn.");
            MapSelectAndInvoke(map, MapHit(map, point));
            Check(ReferenceEquals(items[index], invoked), "The donut opened a different folder than the selected sector.");
            cumulative += fraction;
        }
        invoked = null;
        MapSelectAndInvoke(map, MapHit(map, center));
        Check(invoked is null, "The donut's center invoked a folder.");
        return Task.CompletedTask;
    }

    private static Task DonutEdgeCases()
    {
        var single = new MapItem("Only", "120 B", 120, $@"{FixturePath}\Only", true);
        var map = new SpaceMap { LayoutMode = SpaceMapLayout.Donut, Items = [single] };
        foreach (var size in new[] { new Size(340, 440), new Size(220, 180) })
        {
            RenderMap(map, size);
            var center = MapField<Point>(map, "_donutCenter");
            var outer = MapField<double>(map, "_outerRadius");
            var inner = MapField<double>(map, "_innerRadius");
            var radius = (inner + outer) / 2;
            var geometry = MapField<Geometry[]>(map, "_segments").Single();
            Check(Math.Abs(geometry.GetArea() / (Math.PI * (outer * outer - inner * inner)) - 1) < 0.01,
                "One item did not form a complete donut ring.");
            foreach (var vector in new[] { new Vector(1, 0), new Vector(0, 1), new Vector(-1, 0), new Vector(0, -1) })
                Equal(0, MapHit(map, center + vector * radius));
            Equal(-1, MapHit(map, center));
        }

        map.Items = [single with { Size = 0 }];
        RenderMap(map, new Size(340, 440));
        Equal(-1, MapHit(map, new Point(170, 100)));
        Equal(0, MapField<MapItem[]>(map, "_items").Length);
        map.Items = [];
        RenderMap(map, new Size(340, 440));
        Equal(-1, MapHit(map, new Point(170, 100)));

        var many = Enumerable.Range(1, 50)
            .Select(index => new MapItem($"Folder {index}", $"{index} B", index, $@"{FixturePath}\Folder{index}", true))
            .ToArray();
        map.Items = many;
        RenderMap(map, new Size(340, 440));
        var retained = MapField<MapItem[]>(map, "_items");
        Equal(36, retained.Length);
        Equal(many.Sum(item => item.Size), retained.Sum(item => item.Size));
        Equal(many.Sum(item => item.Size), MapField<long>(map, "_totalBytes"));
        Check(retained.Any(item => string.IsNullOrEmpty(item.Path)), "Small items were lost rather than grouped.");
        var segments = MapField<Geometry[]>(map, "_segments");
        Equal(retained.Length, segments.Length);
        var totalArea = segments.Sum(geometry => geometry.GetArea());
        for (var index = 0; index < retained.Length; index++)
            Check(Math.Abs(segments[index].GetArea() / totalArea - (double)retained[index].Size / many.Sum(item => item.Size)) < 0.01,
                "Grouping changed a donut sector's byte proportion.");
        return Task.CompletedTask;
    }

    private static RenderTargetBitmap RenderMap(SpaceMap map, Size size)
    {
        map.Measure(size);
        map.Arrange(new Rect(size));
        map.UpdateLayout();
        DrainDispatcher();
        map.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(map);
        return bitmap;
    }

    // Hit testing and the rendered annular geometries have no public testing API.
    private static T MapField<T>(SpaceMap map, string name) =>
        (T)typeof(SpaceMap).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map)!;
    private static int MapHit(SpaceMap map, Point point) =>
        (int)typeof(SpaceMap).GetMethod("HitTile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(map, [point])!;
    private static void MapSelectAndInvoke(SpaceMap map, int index)
    {
        typeof(SpaceMap).GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(map, [index]);
        typeof(SpaceMap).GetMethod("InvokeSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(map, null);
    }
    private static byte PixelAlpha(BitmapSource bitmap, Point point)
    {
        var bytes = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), bytes, 4, 0);
        return bytes[3];
    }

    private static async Task ResultScope()
    {
        var vm = CreateViewModel(new StubScanner((_, _, _) => Task.FromResult(FixtureResult())));
        await vm.ScanAsync();
        var originalScope = vm.ResultScope;
        vm.SelectDriveCommand.Execute(@"Y:\OtherFixture");
        Equal(@"Y:\OtherFixture", vm.TargetLabel);
        Equal(originalScope, vm.ResultScope);
        Check(vm.HasResult, "Choosing another target should preserve the identified previous result.");
    }

    private static async Task Cancellation()
    {
        var scanner = new StubScanner(async (_, _, cancellationToken) =>
        {
            var completion = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() =>
                completion.TrySetResult(FixtureResult() with { IsCancelled = true }));
            return await completion.Task;
        });
        var vm = CreateViewModel(scanner);
        var pending = vm.ScanAsync();
        Check(vm.IsBusy && vm.CancelCommand.CanExecute(null), "Cancellation was not available during a scan.");
        vm.CancelCommand.Execute(null);
        await pending;
        Check(vm.HasResult && !vm.IsBusy, "Cancellation lost the partial result.");
        Equal("부분 결과", vm.ScanState);
        Equal(1_000L, vm.MapItems.Sum(item => item.Size));
    }

    private static async Task StaleProgress()
    {
        var secondCompletion = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var scanner = new StubScanner((_, _, _) => ++calls == 1
            ? Task.FromResult(FixtureResult()) : secondCompletion.Task);
        var vm = CreateViewModel(scanner);
        await vm.ScanAsync();
        var oldProgress = scanner.ProgressReports[0];
        var secondScan = vm.ScanAsync();
        oldProgress!.Report(new ScanProgress(99_999, 99_999, 1, 0, TimeSpan.FromSeconds(99), "old fixture"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Equal(DisplayFormat.Bytes(0), vm.TotalSize);
        Equal("0", vm.FileCount);
        secondCompletion.SetResult(FixtureResult());
        await secondScan;
        oldProgress.Report(new ScanProgress(99_999, 99_999, 1, 0, TimeSpan.FromSeconds(99), "old fixture"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Equal(DisplayFormat.Bytes(1_000), vm.TotalSize);
        Equal("4", vm.FileCount);
    }

    private static async Task RestoreSavedScan()
    {
        var capturedAt = new DateTimeOffset(2026, 11, 10, 1, 23, 45, TimeSpan.Zero);
        var saved = new SavedScan(FixtureResult(), FixturePath, capturedAt);
        var store = new StubStore(saved);
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Restore must not enumerate."));
        var vm = CreateViewModel(scanner, store);

        await vm.RestoreLastScanAsync();

        Equal(1, store.LoadCalls);
        Equal(0, store.SaveCalls);
        Equal(0, scanner.ProgressReports.Count);
        Check(vm.HasSavedAnalysis && vm.DeleteSavedAnalysisCommand.CanExecute(null),
            "A restored snapshot was not available for deletion.");
        Check(vm.HasResult && !vm.IsBusy && !vm.IsRestoring && !vm.ShowEmpty,
            "A stored snapshot did not become an interactive result.");
        Equal("저장된 분석", vm.ScanState);
        Equal(FixturePath, vm.TargetLabel);
        Check(vm.ResultScope.Contains(FixturePath, StringComparison.Ordinal), "The saved target was lost.");
        Check(vm.SnapshotDescription.Contains(capturedAt.ToLocalTime().ToString("yyyy-MM-dd"), StringComparison.Ordinal),
            "The saved scan date must remain visible instead of implying live data.");
        Equal(DisplayFormat.Bytes(1_000), vm.TotalSize);
        Equal("4", vm.FileCount);
        Equal(1_000L, vm.MapItems.Sum(item => item.Size));
        vm.NavigateMap(vm.MapItems.Single(item => item.IsDirectory));
        Equal("Archive", vm.Rows.Single().Name);
        vm.OpenRowCommand.Execute(vm.Rows.Single());
        Equal(200L, vm.MapItems.Sum(item => item.Size));
        vm.UpCommand.Execute(null);
        Equal("Archive", vm.Rows.Single().Name);
        vm.NavigateCommand.Execute("types");
        Equal(1_000L, vm.Rows.Sum(row => row.Bytes));
        vm.NavigateCommand.Execute("files");
        vm.Search = "history";
        Equal("history.bin", vm.Rows.Single().Name);
        Equal(0, scanner.ProgressReports.Count);
    }

    private static async Task SaveCompletedScan()
    {
        var store = new StubStore();
        var scanner = new StubScanner((_, _, _) => Task.FromResult(FixtureResult()));
        var vm = CreateViewModel(scanner, store);
        var started = DateTimeOffset.UtcNow;

        await vm.ScanAsync();

        Equal(1, store.SaveCalls);
        Check(store.Saved is not null, "A completed scan was not saved.");
        Check(vm.HasSavedAnalysis && vm.DeleteSavedAnalysisCommand.CanExecute(null),
            "A newly saved snapshot was not available for deletion.");
        var saved = store.Saved!;
        Equal(FixturePath, saved.SelectedPath!);
        Equal(1_000L, saved.Result.TotalBytes);
        Equal(4L, saved.Result.FileCount);
        Check(!saved.Result.IsCancelled, "A complete save was marked partial.");
        Check(saved.ScannedAtUtc >= started && saved.ScannedAtUtc <= DateTimeOffset.UtcNow,
            "The saved scan timestamp is not from this scan.");

        var restoredScanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Restart must use the save."));
        var restored = CreateViewModel(restoredScanner, store);
        await restored.RestoreLastScanAsync();
        Equal(0, restoredScanner.ProgressReports.Count);
        Equal(vm.TotalSize, restored.TotalSize);
        Equal(vm.FileCount, restored.FileCount);
        Equal(vm.ResultScope, restored.ResultScope);
        Equal(1, store.SaveCalls);

        // A null selected path means the all-drives scope, not an empty target string.
        vm.SelectDriveCommand.Execute(null);
        await vm.ScanAsync();
        Check(store.Saved!.SelectedPath is null, "The all-drives scope was not preserved.");
        var allDrivesRestored = CreateViewModel(restoredScanner, store);
        await allDrivesRestored.RestoreLastScanAsync();
        Check(allDrivesRestored.IsAllDrivesSelected, "The all-drives selection was not restored.");
        Equal("모든 로컬 드라이브", allDrivesRestored.TargetLabel);
    }

    private static async Task PreserveCompletedSnapshot()
    {
        var previous = new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddDays(-1));
        var store = new StubStore(previous);
        var vm = CreateViewModel(new StubScanner((_, _, _) =>
            Task.FromResult(FixtureResult() with { IsCancelled = true })), store);
        await vm.RestoreLastScanAsync();
        await vm.ScanAsync();

        Equal(0, store.SaveCalls);
        Check(ReferenceEquals(previous, store.Saved), "A partial scan overwrote the last completed save.");
        Check(vm.HasResult && !vm.IsBusy, "The user lost the in-session partial result.");
        Equal("부분 결과", vm.ScanState);
        Equal(1_000L, vm.MapItems.Sum(item => item.Size));

        var restored = CreateViewModel(new StubScanner((_, _, _) =>
            throw new InvalidOperationException("Restore must not enumerate.")), store);
        await restored.RestoreLastScanAsync();
        Equal("저장된 분석", restored.ScanState);
        Equal(DisplayFormat.Bytes(previous.Result.TotalBytes), restored.TotalSize);
        Check(vm.HasSavedAnalysis && vm.DeleteSavedAnalysisCommand.CanExecute(null),
            "A partial result hid the older completed snapshot from deletion.");
        await vm.DeleteSavedAnalysisAsync();
        Equal(1, store.DeleteCalls);
        Check(store.Saved is null && !vm.HasSavedAnalysis && !vm.HasResult,
            "Deleting after cancellation left either the old save or partial result visible.");
    }

    private static async Task MissingSnapshot()
    {
        var store = new StubStore();
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Missing save must not trigger a scan."));
        var vm = CreateViewModel(scanner, store);
        await vm.RestoreLastScanAsync();
        Equal(1, store.LoadCalls);
        Equal(0, scanner.ProgressReports.Count);
        Check(vm.ShowEmpty && !vm.HasResult && !vm.IsBusy && !vm.IsRestoring,
            "A first launch should remain ready for explicit user input.");
    }

    private static async Task SnapshotLoadFailure()
    {
        foreach (var error in new Exception[]
        {
            new InvalidDataException("Synthetic corrupt snapshot"),
            new IOException("Synthetic cache read failure"),
            new UnauthorizedAccessException("Synthetic cache permission failure")
        })
        {
            var store = new StubStore { OnLoad = _ => Task.FromException<SavedScan?>(error) };
            var scanner = new StubScanner((_, _, _) => Task.FromResult(FixtureResult()));
            var vm = CreateViewModel(scanner, store);
            await vm.RestoreLastScanAsync();
            Equal(0, scanner.ProgressReports.Count);
            Check(!vm.IsRestoring && !vm.IsBusy && vm.ShowEmpty,
                "A cache failure left the app stuck while loading.");
            Check(vm.Status.Contains("저장", StringComparison.Ordinal), "A failed restore was not explained.");
            Check(!vm.Status.Contains("Synthetic", StringComparison.Ordinal), "Raw cache errors leaked into the user-facing status.");
            await vm.ScanAsync();
            Equal(1, scanner.ProgressReports.Count);
            Check(vm.HasResult && !vm.IsBusy, "An explicit scan could not recover from a bad cache.");
        }
    }

    private static async Task SnapshotSaveFailure()
    {
        var previous = new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddDays(-1));
        var store = new StubStore(previous)
        {
            OnSave = (_, _) => Task.FromException(new IOException("Synthetic cache write failure"))
        };
        var vm = CreateViewModel(new StubScanner((_, _, _) => Task.FromResult(FixtureResult())), store);
        await vm.ScanAsync();
        Equal(1, store.SaveCalls);
        Check(ReferenceEquals(previous, store.Saved), "A failed save replaced the previous snapshot.");
        Check(vm.HasResult && !vm.IsBusy && !vm.IsRestoring, "A save failure discarded usable scan results.");
        Equal(DisplayFormat.Bytes(1_000), vm.TotalSize);
        Equal(4, vm.Rows.Count);
        Check(vm.Status.Contains("저장", StringComparison.Ordinal), "A failed save was not explained.");
        Check(!vm.Status.Contains("Synthetic", StringComparison.Ordinal), "Raw save errors leaked into the status.");
        Equal(0, store.LoadCalls);
        Check(vm.HasSavedAnalysis && vm.DeleteSavedAnalysisCommand.CanExecute(null),
            "A failed immediate scan save hid the existing snapshot that was never restored.");
        await vm.DeleteSavedAnalysisAsync();
        Check(store.Saved is null && !vm.HasSavedAnalysis && !vm.HasResult,
            "The previous snapshot could not be deleted after an immediate scan failed to save.");
    }

    private static async Task DeleteUnrestoredSnapshotAfterCancellation()
    {
        var previous = new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddDays(-1));
        var store = new StubStore(previous);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new StubScanner(async (_, _, token) =>
        {
            using var registration = token.Register(() => finish.TrySetResult(FixtureResult() with { IsCancelled = true }));
            started.TrySetResult();
            return await finish.Task;
        });
        var vm = CreateViewModel(scanner, store);
        var pending = vm.ScanAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(vm.IsBusy && vm.HasSavedAnalysis && !vm.DeleteSavedAnalysisCommand.CanExecute(null),
                "An immediate scan did not track the unrestored save or allowed deletion while scanning.");
            await vm.DeleteSavedAnalysisAsync();
            await vm.RestoreLastScanAsync();
            Equal(0, store.DeleteCalls);
            Equal(0, store.LoadCalls);
            vm.CancelCommand.Execute(null);
            await pending;
            Equal(0, store.SaveCalls);
            Check(ReferenceEquals(previous, store.Saved), "Cancellation replaced the unrestored snapshot.");
            Check(vm.HasResult && vm.HasSavedAnalysis && vm.DeleteSavedAnalysisCommand.CanExecute(null),
                "Cancelling an immediate scan made the unrestored snapshot unavailable for deletion.");
            Equal("부분 결과", vm.ScanState);
            await vm.DeleteSavedAnalysisAsync();
            Equal(1, store.DeleteCalls);
            Check(store.Saved is null && !vm.HasSavedAnalysis && !vm.HasResult,
                "Deletion left the unrestored save or partial result behind.");
            Equal(1, scanner.ProgressReports.Count);
        }
        finally { finish.TrySetResult(FixtureResult() with { IsCancelled = true }); }
    }

    private static async Task RestoreConcurrency()
    {
        var completion = new TaskCompletionSource<SavedScan?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new StubStore { OnLoad = _ => completion.Task };
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("A scan overlapped restoration."));
        var vm = CreateViewModel(scanner, store);
        var pending = vm.RestoreLastScanAsync();
        Check(vm.IsRestoring, "The app did not report an outstanding snapshot load.");
        Check(!vm.ScanCommand.CanExecute(null) && !vm.SelectDriveCommand.CanExecute(null) &&
            !vm.ChooseFolderCommand.CanExecute(null) && !vm.DeleteSavedAnalysisCommand.CanExecute(null),
            "Conflicting actions remained enabled during restore.");
        var overlappingRestore = vm.RestoreLastScanAsync();
        await vm.ScanAsync();
        await vm.DeleteSavedAnalysisAsync();
        Equal(1, store.LoadCalls);
        Equal(0, store.DeleteCalls);
        Equal(0, scanner.ProgressReports.Count);
        completion.SetResult(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await Task.WhenAll(pending, overlappingRestore);
        Check(vm.HasResult && !vm.IsRestoring, "A pending snapshot load did not settle.");
        Check(vm.SelectDriveCommand.CanExecute(null) && vm.ChooseFolderCommand.CanExecute(null),
            "Target selection did not recover after restore.");
    }

    private static async Task PendingSave()
    {
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddDays(-1)))
        {
            OnSave = (_, _) => { saveStarted.TrySetResult(); return finishSave.Task; }
        };
        var scanner = new StubScanner((_, _, _) => Task.FromResult(FixtureResult()));
        var vm = CreateViewModel(scanner, store);
        await vm.RestoreLastScanAsync();
        var scan = vm.ScanAsync();
        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(vm.IsSaving && !vm.CancelCommand.CanExecute(null),
                "A completed scan save must not be treated as a cancellable scan.");
            var closingWait = vm.WaitForPendingSaveAsync();
            Check(!closingWait.IsCompleted, "Closing could bypass an outstanding snapshot write.");
            var persistenceWait = vm.WaitForPendingPersistenceAsync();
            Check(!persistenceWait.IsCompleted, "The persistence wait did not include the outstanding save.");
            Check(!vm.DeleteSavedAnalysisCommand.CanExecute(null), "Deletion remained enabled during a save.");
            await vm.DeleteSavedAnalysisAsync();
            await vm.RestoreLastScanAsync();
            await vm.ScanAsync();
            Equal(0, store.DeleteCalls);
            Equal(1, store.LoadCalls);
            Equal(1, scanner.ProgressReports.Count);
            scanner.ProgressReports.Single()!.Report(new ScanProgress(1, 1, 1, 0,
                TimeSpan.FromSeconds(99), "old progress"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Equal(DisplayFormat.Bytes(1_000), vm.TotalSize);
            Equal("4", vm.FileCount);
            finishSave.TrySetResult();
            await Task.WhenAll(scan, closingWait, persistenceWait);
            Check(!vm.IsBusy && !vm.IsSaving, "The pending save did not settle.");
            Equal(1_000L, store.Saved!.Result.TotalBytes);
        }
        finally { finishSave.TrySetResult(); }
    }

    private static async Task DeleteSavedScan()
    {
        var saved = new SavedScan(SystemFixtureResult(), @"X:\", DateTimeOffset.UtcNow.AddDays(-1));
        var store = new StubStore(saved);
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Deletion must not enumerate."));
        var vm = new MainViewModel(scanner, store, new SystemItemCatalog(@"X:\Windows", @"X:\"));
        var empty = new MainViewModel(scanner, null, new SystemItemCatalog(@"X:\Windows", @"X:\"));
        await vm.RestoreLastScanAsync();
        vm.NavigateMap(vm.MapItems.Single(item => item.Path == @"X:\Windows"));
        vm.Search = "System32";
        vm.SelectedRow = vm.Rows.Single();
        Check(vm.HasSystemInfo && vm.SelectedRow is not null && vm.UpCommand.CanExecute(null),
            "The deletion fixture must contain a system selection and folder navigation.");
        vm.SelectDriveCommand.Execute(@"X:\NextScan");
        vm.SetMapLayoutCommand.Execute("donut");

        await vm.DeleteSavedAnalysisAsync();

        Equal(1, store.DeleteCalls);
        Equal(0, store.SaveCalls);
        Equal(0, scanner.ProgressReports.Count);
        Check(store.Saved is null && !vm.HasSavedAnalysis && !vm.HasResult && vm.ShowEmpty && vm.IsIdle,
            "Successful deletion did not clear the saved and displayed analysis.");
        Check(!vm.IsDeleting && !vm.DeleteSavedAnalysisCommand.CanExecute(null),
            "Deletion did not settle into the unavailable state.");
        Equal(0, vm.Rows.Count);
        Equal(0, vm.MapItems.Count);
        Check(vm.SelectedRow is null && vm.SystemInfo is null && !vm.HasSystemInfo,
            "Deleted metadata remained selected or explained.");
        Equal(empty.TotalSize, vm.TotalSize);
        Equal(empty.FileCount, vm.FileCount);
        Equal(empty.FolderCount, vm.FolderCount);
        Equal(empty.Skipped, vm.Skipped);
        Equal(empty.Elapsed, vm.Elapsed);
        Equal(empty.CurrentPath, vm.CurrentPath);
        Equal(empty.SnapshotDescription, vm.SnapshotDescription);
        Equal(empty.ResultScope, vm.ResultScope);
        Equal(empty.Search, vm.Search);
        Equal(empty.Breadcrumb, vm.Breadcrumb);
        Check(!vm.UpCommand.CanExecute(null), "Deleted folder navigation remained active.");
        Equal(@"X:\NextScan", vm.TargetLabel);
        Equal(SpaceMapLayout.Donut, vm.MapLayout);
        foreach (var view in new[] { "folders", "types", "files", "overview" })
        {
            vm.NavigateCommand.Execute(view);
            Equal(0, vm.Rows.Count);
            Equal(0, vm.MapItems.Count);
            Check(vm.SystemInfo is null, "Switching views restored deleted metadata.");
        }

        var restarted = CreateViewModel(scanner, store);
        await restarted.RestoreLastScanAsync();
        Check(restarted.ShowEmpty && !restarted.HasSavedAnalysis && !restarted.HasResult,
            "A new view model restored a deleted snapshot.");
        Equal(0, scanner.ProgressReports.Count);
    }

    private static async Task SnapshotDeleteFailure()
    {
        foreach (var error in new Exception[]
        {
            new IOException(@"Synthetic deletion failure X:\Private\cache.gz"),
            new UnauthorizedAccessException(@"Synthetic deletion denial X:\Private\cache.gz"),
            new System.Security.SecurityException(@"Synthetic deletion denial X:\Private\cache.gz")
        })
        {
            var previous = new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow.AddDays(-1));
            var store = new StubStore(previous) { OnDelete = _ => Task.FromException(error) };
            var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Deletion must not enumerate."));
            var vm = CreateViewModel(scanner, store);
            await vm.RestoreLastScanAsync();
            vm.NavigateCommand.Execute("files");
            vm.Search = "history";
            vm.SelectedRow = vm.Rows.Single();
            var selected = vm.SelectedRow;
            var rows = vm.Rows;
            var map = vm.MapItems;
            var description = vm.SnapshotDescription;
            var scope = vm.ResultScope;

            await vm.DeleteSavedAnalysisAsync();

            Equal(1, store.DeleteCalls);
            Check(ReferenceEquals(previous, store.Saved), "A failed deletion lost the saved snapshot.");
            Check(vm.HasResult && vm.HasSavedAnalysis && vm.IsIdle && !vm.IsDeleting &&
                vm.DeleteSavedAnalysisCommand.CanExecute(null), "Deletion failure did not leave a usable result and retry.");
            Check(ReferenceEquals(rows, vm.Rows) && ReferenceEquals(map, vm.MapItems) &&
                ReferenceEquals(selected, vm.SelectedRow), "Deletion failure changed the current view or selection.");
            Equal("history", vm.Search);
            Equal(description, vm.SnapshotDescription);
            Equal(scope, vm.ResultScope);
            Equal(DisplayFormat.Bytes(1_000), vm.TotalSize);
            Check(vm.Status.Contains("삭제", StringComparison.Ordinal), "Deletion failure was not described.");
            Check(!vm.Status.Contains("Synthetic", StringComparison.Ordinal) &&
                !vm.Status.Contains(@"X:\Private", StringComparison.Ordinal), "A raw deletion error exposed private paths.");
            await vm.WaitForPendingPersistenceAsync();
            Equal(0, scanner.ProgressReports.Count);
        }
    }

    private static async Task UnavailableSnapshotDeletion()
    {
        var scanner = new StubScanner((_, _, _) => Task.FromResult(FixtureResult()));
        var store = new StubStore();
        var vm = CreateViewModel(scanner, store);
        await vm.RestoreLastScanAsync();
        Check(!vm.HasSavedAnalysis && !vm.DeleteSavedAnalysisCommand.CanExecute(null),
            "A missing snapshot enabled deletion.");
        await vm.DeleteSavedAnalysisAsync();
        Equal(0, store.DeleteCalls);
        Equal(0, scanner.ProgressReports.Count);

        var withoutStore = CreateViewModel(scanner);
        await withoutStore.ScanAsync();
        Check(!withoutStore.HasSavedAnalysis && !withoutStore.DeleteSavedAnalysisCommand.CanExecute(null),
            "An unsaved result without a store enabled saved-analysis deletion.");
        await withoutStore.DeleteSavedAnalysisAsync();
        Check(withoutStore.HasResult, "An unavailable delete discarded an unsaved result.");
    }

    private static async Task DeleteUnreadableSnapshot()
    {
        foreach (var error in new Exception[]
        {
            new InvalidDataException("Synthetic corrupt snapshot"),
            new IOException("Synthetic unreadable snapshot"),
            new UnauthorizedAccessException("Synthetic unreadable snapshot"),
            new System.Security.SecurityException("Synthetic unreadable snapshot")
        })
        {
            var loads = 0;
            var store = new StubStore
            {
                OnLoad = _ => ++loads == 1 ? Task.FromException<SavedScan?>(error) : Task.FromResult<SavedScan?>(null)
            };
            var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("Recovery must not enumerate."));
            var vm = CreateViewModel(scanner, store);
            await vm.RestoreLastScanAsync();
            Check(!vm.HasResult && vm.HasSavedAnalysis && vm.DeleteSavedAnalysisCommand.CanExecute(null),
                "An unreadable snapshot was not available for deletion.");

            await vm.DeleteSavedAnalysisAsync();

            Equal(1, store.DeleteCalls);
            Check(!vm.HasSavedAnalysis && !vm.IsDeleting && vm.ShowEmpty, "Unreadable cache deletion did not finish.");
            var restarted = CreateViewModel(scanner, store);
            await restarted.RestoreLastScanAsync();
            Check(restarted.ShowEmpty && !restarted.HasSavedAnalysis, "Deleted unreadable cache remained available.");
            Equal(0, scanner.ProgressReports.Count);
        }
    }

    private static async Task PendingDeletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow))
        {
            OnDelete = _ => completion.Task
        };
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("A scan overlapped deletion."));
        var vm = CreateViewModel(scanner, store);
        await vm.RestoreLastScanAsync();
        var deletion = vm.DeleteSavedAnalysisAsync();
        try
        {
            Check(vm.IsDeleting && !vm.IsIdle && vm.HasResult && vm.HasSavedAnalysis,
                "A pending deletion cleared results before the store confirmed success.");
            Check(!vm.DeleteSavedAnalysisCommand.CanExecute(null) && !vm.ScanCommand.CanExecute(null) &&
                !vm.ChooseFolderCommand.CanExecute(null) && !vm.SelectDriveCommand.CanExecute(null),
                "Conflicting actions remained enabled during deletion.");
            await vm.DeleteSavedAnalysisAsync();
            await vm.RestoreLastScanAsync();
            await vm.ScanAsync();
            Equal(1, store.DeleteCalls);
            Equal(1, store.LoadCalls);
            Equal(0, scanner.ProgressReports.Count);
            var closingWait = vm.WaitForPendingPersistenceAsync();
            Check(!closingWait.IsCompleted, "Closing could bypass an outstanding snapshot deletion.");
            completion.TrySetResult();
            await Task.WhenAll(deletion, closingWait);
            Check(vm.IsIdle && !vm.IsDeleting && !vm.HasResult && !vm.HasSavedAnalysis,
                "Pending deletion did not settle and clear the analysis.");
        }
        finally { completion.TrySetResult(); }
    }

    private static async Task SnapshotDeletionXaml()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow))
        {
            OnDelete = _ => completion.Task
        };
        var scanner = new StubScanner((_, _, _) => throw new InvalidOperationException("The delete button must not scan."));
        var vm = CreateViewModel(scanner, store);
        var window = new MainWindow { DataContext = vm };
        try
        {
            var content = (FrameworkElement)window.Content;
            Render(content);
            var button = window.FindName("DeleteSavedAnalysisButton") as Button ??
                throw new InvalidOperationException("The saved-analysis delete button is missing from XAML.");
            Check(button.Visibility == Visibility.Visible && !button.IsEnabled,
                "The saved-analysis delete button must remain visible but disabled without a snapshot.");
            var binding = BindingOperations.GetBindingExpression(button, Button.CommandProperty);
            Check(binding is not null && binding.Status == BindingStatus.Active &&
                ReferenceEquals(button.Command, vm.DeleteSavedAnalysisCommand), "The XAML delete command is not bound.");
            Check(button.ToolTip is string tooltip && tooltip.Contains("파일", StringComparison.Ordinal),
                "The delete control must explain its effect on source files.");

            await vm.RestoreLastScanAsync();
            Render(content);
            Check(button.IsEnabled, "The delete button did not enable after loading a snapshot.");
            button.Command.Execute(button.CommandParameter);
            Render(content);
            Equal(1, store.DeleteCalls);
            Check(vm.IsDeleting && !button.IsEnabled, "The delete button allowed duplicate pending deletions.");
            completion.TrySetResult();
            await vm.WaitForPendingPersistenceAsync();
            Render(content);
            Check(!button.IsEnabled && !vm.HasResult && !vm.HasSavedAnalysis,
                "The delete button or visible analysis remained active after deletion.");
            Equal(0, Descendants<DataGrid>(content).Single().Items.Count);
            Check(Descendants<TextBlock>((Button)window.FindName("DashboardFilesButton")).Any(text => text.Text == "0개 파일") &&
                Descendants<TextBlock>((Button)window.FindName("DashboardTypesButton")).Any(text => text.Text == "0개 형식"),
                "Dashboard cards retained counts from the deleted analysis.");
            Equal(0, scanner.ProgressReports.Count);
            Check(!window.IsVisible, "The delete-control fixture must not open a desktop window.");
        }
        finally
        {
            completion.TrySetResult();
            await vm.WaitForPendingPersistenceAsync();
            window.Close();
        }
    }

    private static async Task SystemItemSelection()
    {
        var vm = CreateSystemViewModel();
        await vm.ScanAsync();
        Check(!vm.HasSystemInfo && vm.SystemInfo is null, "An unselected overview should not guess a system item.");

        vm.SelectedRow = vm.Rows.Single(row => row.Path == @"X:\pagefile.sys");
        Check(vm.SystemInfo is not null && vm.HasSystemInfo && vm.CanShowSystemInfo,
            "The known page file did not receive a system explanation.");
        Check(vm.OpenSystemHelpCommand.CanExecute(null), "A known system item should offer its official reference.");
        Check(vm.IsMapVisible, "Selecting a system explanation must not unconditionally hide the map.");
        var info = vm.SystemInfo!;
        Check(new[] { info.Title, info.Purpose, info.ManagedBy, info.Necessity, info.CleanupAdvice, info.MatchBasis }
            .All(text => !string.IsNullOrWhiteSpace(text)), "The explanation omitted a requested field.");
        Check(Uri.TryCreate(info.SourceUrl, UriKind.Absolute, out var source) && source.Scheme == Uri.UriSchemeHttps,
            "A known item must provide an HTTPS reference without using the selected file path.");

        vm.DismissSystemInfoCommand.Execute(null);
        Check(!vm.HasSystemInfo && vm.CanShowSystemInfo && vm.IsMapVisible,
            "Dismissing an explanation should restore the map without losing the item metadata.");
        vm.ShowSystemInfoCommand.Execute(null);
        Check(vm.HasSystemInfo && vm.IsMapVisible, "Reopening an explanation unexpectedly disabled the map.");

        foreach (var path in new[] { @"X:\Windows\System32\personal-notes.txt",
                     @"X:\Users\Sample\Documents\pagefile.sys", @"X:\Users\Sample\Documents\report.docx" })
        {
            vm.SelectedRow = vm.Rows.Single(row => row.Path == path);
            Check(vm.SystemInfo is null && !vm.HasSystemInfo && !vm.CanShowSystemInfo,
                "A user file inherited system classification from a name or ancestor directory.");
            Check(!vm.OpenSystemHelpCommand.CanExecute(null), "An unknown item should not offer unrelated system help.");
        }

        vm.SelectedRow = vm.Rows.Single(row => row.Path == @"X:\pagefile.sys");
        vm.NavigateCommand.Execute("types");
        vm.SelectedRow = vm.Rows[0];
        Check(vm.SystemInfo is null && !vm.HasSystemInfo && !vm.CanShowSystemInfo,
            "An extension aggregate retained a stale system explanation.");
    }

    private static async Task SystemFolderNavigation()
    {
        var vm = CreateSystemViewModel();
        await vm.ScanAsync();
        var userNotes = vm.Rows.Single(row => row.Path == @"X:\Windows\System32\personal-notes.txt");
        vm.NavigateMap(vm.MapItems.Single(item => item.Path == @"X:\Windows"));
        Check(vm.IsFolders && vm.SystemInfo is not null && vm.HasSystemInfo,
            "Entering the known Windows folder did not expose its own explanation.");
        var windowsTitle = vm.SystemInfo!.Title;
        vm.OpenRowCommand.Execute(vm.Rows.Single(row => row.Path == @"X:\Windows\System32"));
        Check(vm.SystemInfo is not null && vm.SystemInfo.Title != windowsTitle,
            "The explanation did not follow navigation to System32.");
        var system32Title = vm.SystemInfo!.Title;
        vm.SelectedRow = userNotes;
        Check(vm.SystemInfo is null && !vm.HasSystemInfo && !vm.CanShowSystemInfo,
            "An unknown selected file inherited its currently open system folder's explanation.");
        vm.SelectedRow = null;
        Equal(system32Title, vm.SystemInfo!.Title);
        vm.DismissSystemInfoCommand.Execute(null);
        Check(!vm.HasSystemInfo && vm.CanShowSystemInfo, "A current-folder explanation could not be dismissed.");
        vm.ShowSystemInfoCommand.Execute(null);
        Check(vm.HasSystemInfo, "A current-folder explanation could not be reopened.");
        vm.UpCommand.Execute(null);
        Equal(windowsTitle, vm.SystemInfo!.Title);
        vm.UpCommand.Execute(null);
        Check(vm.SystemInfo is null && !vm.HasSystemInfo, "An unknown drive root retained its child's explanation.");
        vm.OpenRowCommand.Execute(vm.Rows.Single(row => row.Path == @"X:\Users"));
        Check(vm.SystemInfo is null && !vm.HasSystemInfo, "The user profile tree was mislabeled as a system item.");
    }

    private static async Task SystemItemXaml()
    {
        var vm = CreateSystemViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.ScanAsync();
            vm.SelectedRow = vm.Rows.Single(row => row.Path == @"X:\pagefile.sys");
            var content = (FrameworkElement)window.Content;
            Render(content);
            var panel = window.FindName("SystemInfoPanel") as FrameworkElement;
            Check(panel is not null && panel.Visibility == Visibility.Visible,
                "The system explanation panel is not displayed for a known selection.");
            var systemSection = (Expander)window.FindName("SystemInfoSection");
            var previewSection = (Border)window.FindName("PreviewSection");
            var previewExpander = (Expander)window.FindName("PreviewExpander");
            Check(!systemSection.IsExpanded && !previewExpander.IsExpanded && !vm.MediaInfoExpanded,
                "Optional detail sections must start collapsed.");
            Equal(Visibility.Collapsed, previewSection.Visibility);
            var general = (Expander)window.FindName("GeneralSection");
            Check(general.IsExpanded && vm.GeneralExpanded, "General must start expanded.");
            Check(!Descendants<Expander>(general).Any(), "Optional sections remain inside General.");
            general.IsExpanded = false;
            systemSection.IsExpanded = true;
            Render(content);
            var info = vm.SystemInfo!;
            var visibleText = Descendants<TextBlock>(panel!).Select(text => text.Text).ToArray();
            foreach (var expected in new[] { info.Title, info.Purpose, info.ManagedBy,
                         info.Necessity, info.CleanupAdvice, info.MatchBasis })
                Check(visibleText.Contains(expected), "A requested explanation field is not bound in the actual XAML.");

            vm.SelectedRow = vm.Rows.Single(row => row.Path.EndsWith("report.docx", StringComparison.Ordinal));
            Render(content);
            Equal(Visibility.Visible, panel!.Visibility);
            Equal(Visibility.Visible, previewSection.Visibility);
            Check(!general.IsExpanded && !vm.GeneralExpanded, "Changing selection reset General's collapsed state.");
            general.IsExpanded = true;
            Check(systemSection.IsExpanded && vm.SystemInfoExpanded, "Hidden system explanation lost expansion state.");
            Equal(Visibility.Collapsed, ((Border)systemSection.Parent).Visibility);
            previewExpander.IsExpanded = true;
            vm.SelectedRow = vm.SelectedRow! with { IsDirectory = true };
            Render(content);
            Equal(Visibility.Collapsed, previewSection.Visibility);
            Check(vm.PreviewExpanded, "Folder selection reset preview expansion.");
            Check(general.IsExpanded && vm.GeneralExpanded, "Folder selection reset General's expanded state.");
            vm.SelectedRow = vm.Rows.Single(row => row.Path == @"X:\pagefile.sys");
            Render(content);
            Check(systemSection.IsExpanded && vm.SystemInfoExpanded, "System explanation did not reopen in its preserved state.");
            vm.SelectedRow = vm.Rows.Single(row => row.Path.EndsWith("report.docx", StringComparison.Ordinal));
            Render(content);
            Check(previewExpander.IsExpanded && vm.PreviewExpanded, "Supported preview did not preserve expansion after a folder.");
            previewExpander.IsExpanded = false;
            vm.SelectedRow = vm.Rows.Single(row => row.Path == @"X:\pagefile.sys");
            vm.SelectedRow = vm.Rows.Single(row => row.Path.EndsWith("report.docx", StringComparison.Ordinal));
            Check(!vm.PreviewExpanded, "Explicit preview collapse did not persist.");
            Check(vm.HasDetails && !vm.HasSystemInfo && vm.Details.Any(field => field.Value == vm.SelectedRow.Path), "A normal file lost its own details.");
            vm.DismissSystemInfoCommand.Execute(null);
            Render(content);
            Equal(Visibility.Collapsed, panel.Visibility);
            vm.ShowSystemInfoCommand.Execute(null);
            Render(content);
            Equal(Visibility.Visible, panel.Visibility);
            Check(!window.IsVisible, "The explanation test must not open a desktop window.");
        }
        finally { window.Close(); }
    }

    private static async Task FileDetails()
    {
        using var fixture = new PickerFixture();
        var path = Path.Combine(fixture.Root, "sample.txt");
        var original = "sample preview\n" + new string('a', 80000);
        File.WriteAllText(path, original);
        var modified = File.GetLastWriteTimeUtc(path);
        var row = new ResultRow("sample.txt", path, 12, -1, false, ".txt", 10);
        var reader = new ItemDetailsReader();
        var fields = await reader.RunAsync(() => reader.ReadMetadata(row), CancellationToken.None);
        Check(fields.Any(field => field.Label.Contains("현재 크기") && field.Value != "12 B"), "Live metadata did not reflect the current size.");
        var preview = await reader.RunAsync(() => reader.ReadPreview(row), CancellationToken.None);
        Check(preview.StartsWith("sample preview") && preview.EndsWith("[앞부분 64 KB]") && preview.Length < 66000, "The preview was not bounded.");
        Equal(original, File.ReadAllText(path));
        Equal(modified, File.GetLastWriteTimeUtc(path));
        Check(!ItemDetailsReader.SupportsPreview(row with { Path = Path.Combine(fixture.Root, "sample.exe") }), "Executable preview was enabled.");
        Check(reader.ReadMetadata(row with { Path = Path.Combine(fixture.Root, "missing.txt") }).Any(field => field.Label == "현재 상태"), "Missing metadata did not produce a status.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var invoked = false;
        try { await reader.RunAsync(() => invoked = true, canceled.Token); }
        catch (OperationCanceledException) { }
        Check(!invoked, "A canceled details read still accessed the filesystem.");
    }

    private static MainViewModel CreateSystemViewModel()
    {
        var vm = new MainViewModel(new StubScanner((_, _, _) => Task.FromResult(SystemFixtureResult())),
            null, new SystemItemCatalog(@"X:\Windows", @"X:\"));
        vm.SelectDriveCommand.Execute(@"X:\");
        return vm;
    }

    private static ScanResult SystemFixtureResult()
    {
        var system32 = new FolderNode(@"X:\Windows\System32", 100, 1);
        var windows = new FolderNode(@"X:\Windows", 100, 1, [system32]);
        var documents = new FolderNode(@"X:\Users\Sample\Documents", 500, 2);
        var profile = new FolderNode(@"X:\Users\Sample", 500, 2, [documents]);
        var users = new FolderNode(@"X:\Users", 500, 2, [profile]);
        var root = new FolderNode(@"X:\", 1_000, 4, [users, windows]);
        return new ScanResult([root],
            [new FileRecord(@"X:\pagefile.sys", "pagefile.sys", 400, ".sys"),
             new FileRecord(@"X:\Users\Sample\Documents\report.docx", "report.docx", 300, ".docx"),
             new FileRecord(@"X:\Users\Sample\Documents\pagefile.sys", "pagefile.sys", 200, ".sys"),
             new FileRecord(@"X:\Windows\System32\personal-notes.txt", "personal-notes.txt", 100, ".txt")],
            [new FileTypeSummary(".sys", 600, 2), new FileTypeSummary(".docx", 300, 1),
             new FileTypeSummary(".txt", 100, 1)],
            1_000, 4, 6, 0, TimeSpan.FromSeconds(1), false);
    }

    private static MainViewModel CreateViewModel(IDiskScanner scanner, IScanResultStore? store = null)
    {
        var vm = new MainViewModel(scanner, store);
        vm.Drives.Add(new DriveItem(FixturePath, "Fixture volume (X:)", "Fixture capacity", 62.5));
        vm.SelectDriveCommand.Execute(FixturePath);
        return vm;
    }

    private static ScanResult FixtureResult()
    {
        var archive = new FolderNode($@"{FixturePath}\Projects\Archive", 200, 1);
        var projects = new FolderNode($@"{FixturePath}\Projects", 600, 3, [archive]);
        var empty = new FolderNode($@"{FixturePath}\Empty");
        var root = new FolderNode(FixturePath, 1_000, 4, [projects, empty]);
        return new ScanResult([root],
            [new FileRecord($@"{FixturePath}\large.bin", "large.bin", 400, ".bin"),
             new FileRecord($@"{projects.Path}\project.bin", "project.bin", 300, ".bin"),
             new FileRecord($@"{archive.Path}\history.bin", "history.bin", 200, ".bin"),
             new FileRecord($@"{projects.Path}\notes.txt", "notes.txt", 100, ".txt")],
            [new FileTypeSummary(".bin", 900, 3), new FileTypeSummary(".txt", 100, 1)],
            1_000, 4, 4, 0, TimeSpan.FromSeconds(1), false);
    }

    private static void AssertOneWayValue(ProgressBar bar, double expected)
    {
        var expression = BindingOperations.GetBindingExpression(bar, RangeBase.ValueProperty);
        Check(expression is not null && expression.Status == BindingStatus.Active,
            "The percentage binding is not active.");
        Equal(BindingMode.OneWay, expression!.ParentBinding.Mode);
        Equal(expected, bar.Value);
    }

    private static void AssertColor(Brush brush, Color expected, string message) =>
        Check(brush is SolidColorBrush solid && solid.Color == expected, message);

    private static void Render(FrameworkElement content)
    {
        LayoutAt(content, new Size(1440, 920));
        // In-memory only: ensure native draw and template code runs, without saving a capture.
        var bitmap = new RenderTargetBitmap(1440, 920, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
    }

    private static void LayoutAt(FrameworkElement content, Size size)
    {
        content.Measure(size);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        DrainDispatcher();
        content.UpdateLayout();
    }

    private static bool IsEffectivelyVisible(DependencyObject element)
    {
        // Fixtures deliberately never show their Window; inspect its content's visibility only.
        for (DependencyObject? current = element; current is not null && current is not Window;
             current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timeout.Tick += (_, _) => frame.Continue = false;
            timeout.Start();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            Check(task.IsCompleted, "The WPF test timed out.");
        }
        task.GetAwaiter().GetResult();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual) where T : notnull =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");

    private static string[] CapturePickerTree(string root) => Directory.EnumerateFileSystemEntries(root, "*",
            SearchOption.AllDirectories).Prepend(root).Order(StringComparer.Ordinal).Select(path =>
        {
            var attributes = File.GetAttributes(path);
            var contents = (attributes & FileAttributes.Directory) != 0 ? "directory" :
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            return $"{Path.GetRelativePath(root, path)}|{attributes}|{File.GetLastWriteTimeUtc(path).Ticks}|{contents}";
        }).ToArray();

    private sealed class PickerFixture : IDisposable
    {
        private readonly string _baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WindowsFSPickerTests"));
        public string Root { get; }

        public PickerFixture()
        {
            Root = Path.Combine(_baseDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            Check(path.StartsWith(_baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "The fixture cleanup target escaped its dedicated temporary directory.");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class StubFileBrowser(Func<string, CancellationToken, Task<IReadOnlyList<FileRecord>>> read) : IReadOnlyFileBrowser
    {
        public List<(string Path, CancellationToken Token)> Calls { get; } = [];
        public Task<IReadOnlyList<FileRecord>> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add((path, cancellationToken));
            return read(path, cancellationToken);
        }
    }

    private sealed class StubFolderBrowser(Func<string, CancellationToken, Task<FolderLocation>> read) : IReadOnlyFolderBrowser
    {
        public List<(string Path, CancellationToken Token)> Calls { get; } = [];
        public Task<FolderLocation> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add((path, cancellationToken));
            return read(path, cancellationToken);
        }
    }

    private sealed class StubScanner(Func<IEnumerable<string>, IProgress<ScanProgress>?, CancellationToken, Task<ScanResult>> scan) : IDiskScanner
    {
        public List<IProgress<ScanProgress>?> ProgressReports { get; } = [];
        public Task<ScanResult> ScanAsync(IEnumerable<string> roots, IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ProgressReports.Add(progress);
            return scan(roots, progress, cancellationToken);
        }
    }

    private sealed class StubStore(SavedScan? initial = null) : IScanResultStore
    {
        public SavedScan? Saved { get; private set; } = initial;
        public int LoadCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public Func<CancellationToken, Task<SavedScan?>>? OnLoad { get; init; }
        public Func<SavedScan, CancellationToken, Task>? OnSave { get; init; }
        public Func<CancellationToken, Task>? OnDelete { get; init; }

        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved is not null);

        public Task<SavedScan?> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return OnLoad?.Invoke(cancellationToken) ?? Task.FromResult(Saved);
        }

        public async Task SaveAsync(SavedScan snapshot, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (OnSave is not null) await OnSave(snapshot, cancellationToken);
            Saved = snapshot;
        }

        public async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (OnDelete is not null) await OnDelete(cancellationToken);
            Saved = null;
        }
    }
}
