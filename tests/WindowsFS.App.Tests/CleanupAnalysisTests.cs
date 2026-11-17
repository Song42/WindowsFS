using System.IO;
using System.Windows;
using System.Windows.Controls;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;
using WindowsFS.Core.Cleanup;
using WindowsFS.Core.Persistence;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static CleanupContext CleanupFixtureContext() => new(@"X:\Windows", @"X:\Users", @"X:\ProgramData", [@"X:\Program Files"],
        [new("Google Chrome", @"X:\Program Files\Google\Chrome", "Fixture vendor"),
         new("App A", @"X:\SharedApp", "Vendor A"), new("App B", @"X:\SharedApp", "Vendor B")]);

    private static ScanResult CleanupFixtureResult()
    {
        static FolderNode Node(string path, long bytes, long count, params FolderNode[] children) =>
            new(path, bytes + children.Sum(child => child.TotalBytes), count + children.Sum(child => child.FileCount), children);
        var root = Node(@"X:\", 7, 1,
            Node(@"X:\Program Files\Google\Chrome", 300, 3),
            Node(@"X:\Users\Fixture\AppData\Local\Google\Chrome\User Data\Default", 50, 5,
                Node(@"X:\Users\Fixture\AppData\Local\Google\Chrome\User Data\Default\Cache", 1000, 10000,
                    Node(@"X:\Users\Fixture\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data", 2000, 20000))),
            Node(@"X:\Users\Fixture\Documents\Cache", 400, 4),
            Node(@"X:\Users\Fixture\AppData\Local\UnknownApp\Cache", 500, 5),
            Node(@"X:\Windows\Installer", 600, 6),
            Node(@"X:\Windows\WinSxS", 700, 7),
            Node(@"X:\Windows\Temp", 800, 8),
            Node(@"X:\SharedApp\Data", 900, 9),
            Node(@"X:\Program Files\Google\Chrome-copy", 100, 1));
        // No largest-file inventory: every small file must come from saved folder aggregates.
        return new([root], [], [new FileTypeSummary(".bin", root.TotalBytes, root.FileCount)], root.TotalBytes, root.FileCount, 12, 0, TimeSpan.FromSeconds(1), false);
    }

    private static async Task CleanupSavedGrouping()
    {
        using var fixture = new PickerFixture();
        var result = CleanupFixtureResult();
        var store = new FileScanResultStore(Path.Combine(fixture.Root, "fixture.scan.json.gz"));
        await store.SaveAsync(new SavedScan(result, @"X:\", DateTimeOffset.UtcNow));
        var restored = (await store.LoadAsync())!.Result;
        var before = CapturePickerTree(fixture.Root);
        var groups = CleanupAnalyzer.Analyze(restored, CleanupFixtureContext());
        Equal(result.TotalBytes, groups.Sum(group => group.Bytes));
        Equal(result.FileCount, groups.Sum(group => group.FileCount));
        var chrome = groups.Single(group => group.Name == "Google Chrome");
        Equal(3350L, chrome.Bytes);
        var cache = chrome.Buckets.Single(bucket => bucket.Policy.Id == "cache");
        Equal(3000L, cache.Bytes); Equal(30000L, cache.FileCount);
        Equal(2, cache.Locations.Count);
        Equal("정리 후보", cache.Policy.Assessment);
        Check(chrome.Buckets.Any(bucket => bucket.Policy.Id == "profile" && bucket.Policy.Assessment == "유지 권장"), "Browser profile was treated as disposable cache.");
        Check(groups.Single(group => group.Name == "Windows").Buckets.Where(bucket => bucket.Policy.Id is "installer" or "components")
            .All(bucket => bucket.Policy.Assessment == "유지 권장"), "Installer cache was treated as disposable.");
        Check(groups.Single(group => group.Name == "사용자 파일").Buckets.All(bucket => bucket.Policy.Assessment != "정리 후보"), "A user's Cache-named directory was disposable.");
        Check(groups.Single(group => group.Name == "UnknownApp · 앱 데이터").Buckets.All(bucket => bucket.Policy.Assessment == "판단 불가"), "An unknown app cache was guessed safe.");
        Check(groups.Any(group => group.Name.StartsWith("공유 설치 영역", StringComparison.Ordinal)), "Shared install ownership was assigned to one app.");
        Check(!chrome.Buckets.SelectMany(bucket => bucket.Locations).Any(location => location.Path.EndsWith("Chrome-copy", StringComparison.Ordinal)), "A prefix sibling was matched to the app.");
        var locations = groups.SelectMany(group => group.Buckets).SelectMany(bucket => bucket.Locations).ToArray();
        Equal(locations.Length, locations.Select(location => location.Path).Distinct(StringComparer.Ordinal).Count());
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Grouping mutated saved/source files.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { CleanupAnalyzer.Analyze(result, CleanupFixtureContext(), cancelled.Token); throw new Exception("Grouping ignored cancellation."); }
        catch (OperationCanceledException) { }
    }

    private static async Task CleanupNavigation()
    {
        var result = CleanupFixtureResult();
        var store = new StubStore(new SavedScan(result, @"X:\", DateTimeOffset.UtcNow));
        var reader = new StubCleanupReader { Read = _ => Task.FromResult(CleanupFixtureContext()) };
        var vm = new MainViewModel(new StubScanner((_, _, _) => throw new Exception("Cleanup must not scan.")), store, cleanupReader: reader);
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.RestoreLastScanAsync();
            Equal(0, reader.Calls);
            var content = (FrameworkElement)window.Content;
            Render(content);
            var button = (Button)window.FindName("DashboardCleanupButton");
            button.Command.Execute(button.CommandParameter);
            await vm.Cleanup.WaitAsync();
            Check(vm.IsCleanup && vm.ShowCleanupResults && !vm.ShowStandardResults && vm.IsDetailView, "Cleanup navigation did not switch the result view.");
            foreach (var size in new[] { new Size(1100, 760), new Size(1800, 1000) })
            {
                LayoutAt(content, size);
                var view = Descendants<CleanupAnalysisView>(content).Single();
                Check(IsEffectivelyVisible(view), "Cleanup view was not rendered.");
                Check(ReferenceEquals(view.DataContext, vm.Cleanup), "Cleanup view has the wrong data context.");
                Check(Descendants<Button>(view).Where(item => item.Content is string).All(item => Equals(item.Content, "폴더 탐색") || Equals(item.Content, "공식 문서 ↗")), "Cleanup view exposes an unexpected action.");
                var host = (FrameworkElement)window.FindName("CleanupAnalysis");
                Check(Math.Abs(view.ActualWidth - host.ActualWidth) < 1 && Math.Abs(view.ActualHeight - host.ActualHeight) < 1, "Cleanup view did not fill the available viewport.");
                Check(typeof(CleanupViewModel).GetProperties().Where(property => typeof(System.Windows.Input.ICommand).IsAssignableFrom(property.PropertyType))
                    .All(property => property.Name is "BrowseFolderCommand" or "OpenHelpCommand"), "Cleanup exposes a command outside read-only browsing and documentation.");
                var table = (DataGrid)view.FindName("CleanupBuckets");
                Check(table.IsReadOnly && !table.CanUserDeleteRows && !table.CanUserAddRows, "Cleanup table permits mutations.");
            }
            vm.Cleanup.Search = "Chrome";
            Equal(1, vm.Cleanup.Groups.Count);
            vm.Cleanup.SelectedBucket = vm.Cleanup.Buckets.Single(bucket => bucket.Bucket.Policy.Id == "cache");
            var location = vm.Cleanup.Locations[0];
            vm.Cleanup.BrowseFolderCommand.Execute(location);
            Check(vm.IsFolders && vm.Breadcrumb == location.Path, "Cleanup did not navigate the indexed folder.");
            vm.NavigateCommand.Execute("cleanup");
            await vm.Cleanup.WaitAsync();
            Equal(1, reader.Calls);
            Equal(0, store.SaveCalls); Equal(0, store.DeleteCalls);
            vm.Cleanup.Search = "not-a-matching-app";
            Check(vm.Cleanup.NoGroups && !vm.Cleanup.HasSelection, "Filtering retained another group's details.");
            await vm.DeleteSavedAnalysisAsync();
            Check(vm.ShowEmpty && vm.Cleanup.Groups.Count == 0, "Deleting the saved analysis retained cleanup inventories.");
            Check(!window.IsVisible, "Cleanup tests must not show a window.");
        }
        finally { window.Close(); }
    }

    private static async Task CleanupCancellation()
    {
        var pending = new TaskCompletionSource<CleanupContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new StubCleanupReader { Read = _ => pending.Task };
        var vm = new CleanupViewModel(reader, _ => throw new Exception("Unexpected navigation."));
        vm.Load(CleanupFixtureResult()); var old = vm.WaitAsync(); var token = reader.Token;
        vm.Reset();
        reader.Read = _ => Task.FromResult(CleanupContext.Empty);
        var fresh = CleanupFixtureResult() with { TotalBytes = 12, FileCount = 1, Roots = [new FolderNode(@"X:\Other", 12, 1)] };
        vm.Load(fresh); await vm.WaitAsync(); await old;
        pending.SetResult(CleanupFixtureContext()); DrainDispatcher();
        Check(token.IsCancellationRequested && vm.Groups.Count == 1 && vm.Groups[0].Group.Bytes == 12, "A stale cleanup result replaced the current analysis.");
        vm.Reset(); reader.Read = _ => throw new IOException("Synthetic registration failure");
        vm.Load(fresh); await vm.WaitAsync();
        Check(!vm.IsLoading && vm.Groups.Single().Name == "미분류" && vm.ScopeNote.Contains("접근 불가"), "Missing install metadata did not fall back to cautious grouping.");
    }

    private sealed class StubCleanupReader : ICleanupContextReader
    {
        public required Func<CancellationToken, Task<CleanupContext>> Read { get; set; }
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<CleanupContext> ReadAsync(CancellationToken token) { Calls++; Token = token; return Read(token); }
    }
}
