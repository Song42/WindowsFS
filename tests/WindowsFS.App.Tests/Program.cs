using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsFS.App;
using WindowsFS.App.Controls;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;
using WindowsFS.Core.Persistence;

namespace WindowsFS.App.Tests;

internal static class Program
{
    private const string FixturePath = @"X:\WindowsFSFixture";

    [STAThread]
    private static int Main()
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
            ("Actual XAML realizes read-only drive and result progress bindings", XamlSmoke),
            ("Folder navigation, file-type totals, and filtered search stay consistent", Navigation),
            ("Treemap grouping preserves all represented bytes", GroupedMap),
            ("Changing target preserves a separately identified result scope", ResultScope),
            ("Cancellation returns a navigable partial result", Cancellation),
            ("Late progress from an earlier scan cannot overwrite a new scan", StaleProgress),
            ("Saved scans restore without enumeration and remain navigable", RestoreSavedScan),
            ("Completed scans save their scope and restore in another view model", SaveCompletedScan),
            ("Cancelled scans preserve the previous completed snapshot", PreserveCompletedSnapshot),
            ("Missing snapshots do not start an automatic scan", MissingSnapshot),
            ("Unreadable and corrupt snapshots permit an explicit fresh scan", SnapshotLoadFailure),
            ("A failed snapshot write retains both results and the previous save", SnapshotSaveFailure),
            ("Restoring a snapshot blocks overlapping restores and scans", RestoreConcurrency),
            ("Pending saves ignore late progress and can be awaited before closing", PendingSave)
        };
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
            var overviewButton = Descendants<Button>(content)
                .Single(button => Equals(button.CommandParameter, "overview"));
            var filesButton = Descendants<Button>(content)
                .Single(button => Equals(button.CommandParameter, "files"));
            var selectedColor = Color.FromRgb(0x30, 0x2A, 0x41);
            AssertColor(overviewButton.Background, selectedColor,
                "The selected navigation item did not receive its highlight.");
            var driveBars = Descendants<ProgressBar>(content).Where(bar => bar.DataContext is DriveItem).ToArray();
            Check(driveBars.Length == 1, "The actual drive template was not realized.");
            AssertOneWayValue(driveBars[0], 62.5);

            await vm.ScanAsync();
            Render(content);
            var grid = Descendants<DataGrid>(content).Single();
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
                Equal(vm.Rows.Count, grid.Items.Count);
                if (view == "files")
                {
                    AssertColor(filesButton.Background, selectedColor,
                        "The newly selected navigation item did not receive its highlight.");
                    Check(overviewButton.Background is SolidColorBrush { Color.A: 0 },
                        "The previous navigation item remained highlighted.");
                }
            }
            Check(!window.IsVisible, "The test must not display a native window.");
        }
        finally { window.Close(); }
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
            !vm.ChooseFolderCommand.CanExecute(null), "Conflicting actions remained enabled during restore.");
        var overlappingRestore = vm.RestoreLastScanAsync();
        await vm.ScanAsync();
        Equal(1, store.LoadCalls);
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
        var store = new StubStore
        {
            OnSave = (_, _) => { saveStarted.TrySetResult(); return finishSave.Task; }
        };
        var scanner = new StubScanner((_, _, _) => Task.FromResult(FixtureResult()));
        var vm = CreateViewModel(scanner, store);
        var scan = vm.ScanAsync();
        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(vm.IsSaving && !vm.CancelCommand.CanExecute(null),
                "A completed scan save must not be treated as a cancellable scan.");
            var closingWait = vm.WaitForPendingSaveAsync();
            Check(!closingWait.IsCompleted, "Closing could bypass an outstanding snapshot write.");
            scanner.ProgressReports.Single()!.Report(new ScanProgress(1, 1, 1, 0,
                TimeSpan.FromSeconds(99), "old progress"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Equal(DisplayFormat.Bytes(1_000), vm.TotalSize);
            Equal("4", vm.FileCount);
            finishSave.TrySetResult();
            await Task.WhenAll(scan, closingWait);
            Check(!vm.IsBusy && !vm.IsSaving, "The pending save did not settle.");
            Equal(1_000L, store.Saved!.Result.TotalBytes);
        }
        finally { finishSave.TrySetResult(); }
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
        content.Measure(new Size(1440, 920));
        content.Arrange(new Rect(0, 0, 1440, 920));
        content.UpdateLayout();
        DrainDispatcher();
        content.UpdateLayout();
        // In-memory only: ensure native draw and template code runs, without saving a capture.
        var bitmap = new RenderTargetBitmap(1440, 920, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
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
        public Func<CancellationToken, Task<SavedScan?>>? OnLoad { get; init; }
        public Func<SavedScan, CancellationToken, Task>? OnSave { get; init; }

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
    }
}
