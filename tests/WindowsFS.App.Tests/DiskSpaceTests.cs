using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;
using WindowsFS.Core.Persistence;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static async Task FolderMapFreeSpace()
    {
        using var fixture = new PickerFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "fixture.txt"), "read-only disk metadata");
        var before = CapturePickerTree(fixture.Root);
        var native = new DiskSpaceReader();
        var actual = await native.ReadAsync(fixture.Root, CancellationToken.None);
        Check(actual.FreeBytes >= 0 && actual.AvailableBytes >= 0, "Native free-space values were invalid.");
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Reading volume metadata mutated the fixture.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await native.ReadAsync(fixture.Root, cancelled.Token); throw new Exception("Native query ignored cancellation."); }
        catch (OperationCanceledException) { }
        try { await native.ReadAsync("relative", CancellationToken.None); throw new Exception("Relative path fell back to the current drive."); }
        catch (ArgumentException) { }

        var reader = new StubDiskSpaceReader { Read = (_, _) => Task.FromResult(new DiskSpace(4096, 2048, DateTimeOffset.Now)) };
        var store = new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow));
        var vm = new MainViewModel(new StubScanner((_, _, _) => throw new Exception("Free-space display must not rescan.")), store, diskSpaceReader: reader);
        await vm.RestoreLastScanAsync(); Equal(0, reader.Calls.Count);
        vm.NavigateMap(new MapItem("Projects", "", 600, $@"{FixturePath}\Projects", true));
        await vm.WaitForDiskSpaceAsync();
        Equal($@"{FixturePath}\Projects", reader.Calls.Single().Path);
        Equal("4 KB", vm.MapFreeSpaceText);
        Check(vm.MapFreeSpaceNote.Contains("사용자 사용 가능 2 KB") && vm.MapFreeSpaceNote.Contains("조회"), "Quota or live-read context was missing.");
        Equal(600L, vm.MapItems.Sum(item => item.Size));
        var items = vm.MapItems;
        vm.DismissSystemInfoCommand.Execute(null);
        var window = new MainWindow { DataContext = vm };
        try
        {
            var content = (FrameworkElement)window.Content;
            foreach (var layout in new[] { "blocks", "donut" })
            {
                vm.SetMapLayoutCommand.Execute(layout);
                LayoutAt(content, new Size(1100, 760));
                var footer = (Border)window.FindName("MapFreeSpace");
                Check(IsEffectivelyVisible(footer) && Descendants<TextBlock>(footer).Any(text => text.Text == "4 KB"), "Free-space readout was missing from a map layout.");
                var panel = (Border)window.FindName("SpaceMapPanel");
                var bottom = footer.TranslatePoint(new Point(0, footer.ActualHeight), panel).Y;
                Check(bottom <= panel.ActualHeight && Descendants<SpaceMap>(panel).Single().ActualHeight >= 80, "Free-space readout overflowed the compact map.");
                Check(ReferenceEquals(items, vm.MapItems), "Free-space display changed chart proportions.");
            }
            Equal(1, reader.Calls.Count); Equal(0, store.SaveCalls); Equal(0, store.DeleteCalls);
            vm.NavigateCommand.Execute("files");
            Check(!vm.ShowMapFreeSpace && vm.MapFreeSpaceText == "", "Leaving folder view retained free space.");
            Check(!window.IsVisible, "Free-space tests must not show a window.");
        }
        finally { vm.Cancel(); window.Close(); }
    }

    private static async Task DiskSpaceFailures()
    {
        var pending = new TaskCompletionSource<DiskSpace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new StubDiskSpaceReader { Read = (_, _) => pending.Task };
        var store = new StubStore(new SavedScan(FixtureResult(), FixturePath, DateTimeOffset.UtcNow));
        var vm = new MainViewModel(new StubScanner((_, _, _) => throw new Exception("Unexpected scan.")), store, diskSpaceReader: reader);
        await vm.RestoreLastScanAsync(); vm.NavigateCommand.Execute("folders");
        var old = vm.WaitForDiskSpaceAsync(); var token = reader.Calls.Single().Token;
        reader.Read = (_, _) => Task.FromResult(new DiskSpace(0, 0, DateTimeOffset.Now));
        vm.NavigateMap(new MapItem("Projects", "", 600, $@"{FixturePath}\Projects", true));
        await vm.WaitForDiskSpaceAsync(); await old;
        pending.SetResult(new DiskSpace(999, 999, DateTimeOffset.Now)); DrainDispatcher();
        Check(token.IsCancellationRequested && vm.MapFreeSpaceText == "0 B", "A late response replaced the current disk or zero free space was hidden.");
        reader.Read = (_, _) => throw new IOException("Synthetic inaccessible volume");
        vm.NavigateCommand.Execute("folders"); await vm.WaitForDiskSpaceAsync();
        Equal("확인 불가", vm.MapFreeSpaceText); Equal("", vm.MapFreeSpaceNote);
        reader.Read = (_, _) => new TaskCompletionSource<DiskSpace>().Task;
        vm.NavigateCommand.Execute("folders");
        await vm.WaitForDiskSpaceAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Check(vm.MapFreeSpaceText.Contains("시간 초과"), "A blocked query kept loading indefinitely.");
        await vm.DeleteSavedAnalysisAsync();
        Check(!vm.ShowMapFreeSpace && vm.MapFreeSpaceText == "", "Deleting the saved analysis retained its disk readout.");

        var result = FixtureResult();
        var multi = result with { Roots = [.. result.Roots, new FolderNode(@"Y:\Fixture", 0, 0)] };
        var multiple = new MainViewModel(new StubScanner((_, _, _) => throw new Exception("Unexpected scan.")),
            new StubStore(new SavedScan(multi, "fixture volumes", DateTimeOffset.UtcNow)), diskSpaceReader: reader);
        var calls = reader.Calls.Count;
        await multiple.RestoreLastScanAsync(); multiple.NavigateCommand.Execute("folders");
        await multiple.WaitForDiskSpaceAsync();
        Equal(calls, reader.Calls.Count);
        Check(multiple.MapFreeSpaceText.Contains("여러 분석 위치"), "Multiple volumes were incorrectly represented by one drive's free space.");
        vm.Cancel(); multiple.Cancel();
    }

    private static async Task RootFreeSpaceCharts()
    {
        var children = Enumerable.Range(0, 42).Select(index => new FolderNode($@"X:\Folder{index}", 100 + index, 1)).ToArray();
        var used = children.Sum(folder => folder.TotalBytes) + 50;
        var root = new FolderNode(@"X:\", used, 43, children);
        var result = new ScanResult([root], [], [new FileTypeSummary(".bin", used, 43)], used, 43, 43, 1, TimeSpan.Zero, false);
        var store = new StubStore(new SavedScan(result, root.Path, DateTimeOffset.UtcNow));
        var reader = new StubDiskSpaceReader { Read = (_, _) => Task.FromResult(new DiskSpace(40, 20, DateTimeOffset.Now)) };
        var vm = new MainViewModel(new StubScanner((_, _, _) => throw new Exception("Chart updates must not rescan.")), store, diskSpaceReader: reader);
        await vm.RestoreLastScanAsync(); vm.NavigateCommand.Execute("folders"); await vm.WaitForDiskSpaceAsync();
        // The single analyzed drive at the top level is also a drive-root chart.
        var free = vm.MapItems.Single(item => item.IsFreeSpace);
        Equal(40L, free.Size); Check(!free.IsDirectory && free.Path == "", "Free space became a navigable folder.");
        Equal(used + 40, vm.MapItems.Sum(item => item.Size));
        Check(vm.MapItems.Count <= 36 && vm.MapItems.Any(item => item.Label == "직접 포함된 파일"), "Free space displaced direct-file totals.");
        Check(!vm.MapItems.Any(item => item.Path == root.Path), "The root itself hid its child-folder distribution.");
        Check(vm.MapBasisText.Contains("분석된 용량 + 조회 시점 여유 공간"), "Root chart implied that saved totals equal physical disk usage.");
        var map = new SpaceMap { Items = vm.MapItems };
        MapItem? invoked = null;
        map.ItemInvoked += (_, item) => invoked = item;
        foreach (var layout in new[] { SpaceMapLayout.Blocks, SpaceMapLayout.Donut })
        {
            map.LayoutMode = layout; RenderMap(map, new Size(340, 440));
            var items = MapField<MapItem[]>(map, "_items");
            Check(items[0].IsFreeSpace && items.Count(item => item.IsFreeSpace) == 1, "Small free space was grouped or hidden behind the short legend.");
            Equal(used + 40, MapField<long>(map, "_totalBytes"));
            if (layout == SpaceMapLayout.Donut)
            {
                var segments = MapField<Geometry[]>(map, "_segments");
                Check(Math.Abs(segments[0].GetArea() / segments.Sum(segment => segment.GetArea()) - 40d / (used + 40)) < 0.005,
                    "Free-space donut angle did not match its byte share.");
                Check(!MapField<Rect[]>(map, "_legendBounds")[0].IsEmpty, "Free-space legend was not visible.");
            }
            else
            {
                var bounds = MapField<Rect[]>(map, "_bounds");
                Check(Math.Abs(bounds[0].Width * bounds[0].Height / bounds.Sum(rect => rect.Width * rect.Height) - 40d / (used + 40)) < 0.005,
                    "Free-space block area did not match its byte share.");
            }
            MapSelectAndInvoke(map, 0); Check(invoked is null, "Free-space selection opened a folder.");
        }
        var calls = reader.Calls.Count;
        vm.NavigateMap(free); Equal(calls, reader.Calls.Count);
        // Also protect the bounded control when callers supply more than its usual 36 items.
        map.Items = children.Select(folder => new MapItem(folder.Name, "", folder.TotalBytes, folder.Path, true)).Append(free);
        RenderMap(map, new Size(340, 440));
        Equal(36, MapField<MapItem[]>(map, "_items").Length);
        Check(MapField<MapItem[]>(map, "_items").Any(item => ReferenceEquals(item, free)), "Overflow grouping absorbed free space.");
        Equal(children.Sum(folder => folder.TotalBytes) + 40, MapField<long>(map, "_totalBytes"));

        vm.NavigateMap(new MapItem("Folder0", "", 100, children[0].Path, true)); await vm.WaitForDiskSpaceAsync();
        Equal(100L, vm.MapItems.Sum(item => item.Size));
        Check(vm.MapItems.All(item => !item.IsFreeSpace) && vm.MapFreeSpaceText == "40 B", "A subfolder chart included volume free space.");
        Check(!vm.MapBasisText.Contains("여유 공간"), "A subfolder retained the root percentage basis.");
        reader.Read = (_, _) => Task.FromResult(new DiskSpace(0, 0, DateTimeOffset.Now));
        vm.NavigateMap(new MapItem("Root", "", used, root.Path, true)); await vm.WaitForDiskSpaceAsync();
        Equal(used, vm.MapItems.Sum(item => item.Size)); Equal("0 B", vm.MapFreeSpaceText);
        Check(vm.MapItems.All(item => !item.IsFreeSpace), "Zero free space produced an invalid chart region.");
        reader.Read = (_, _) => throw new IOException("Synthetic unavailable disk");
        vm.NavigateMap(new MapItem("Root", "", used, root.Path, true)); await vm.WaitForDiskSpaceAsync();
        Check(vm.MapItems.All(item => !item.IsFreeSpace) && vm.MapFreeSpaceText == "확인 불가", "Failed query retained old free space.");

        var ready = new TaskCompletionSource<DiskSpace>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Read = (_, _) => ready.Task;
        vm.NavigateMap(new MapItem("Root", "", used, root.Path, true));
        Check(vm.MapItems.All(item => !item.IsFreeSpace), "A loading query reused stale free space.");
        ready.SetResult(new DiskSpace(70, 70, DateTimeOffset.Now)); await vm.WaitForDiskSpaceAsync();
        Equal(used + 70, vm.MapItems.Sum(item => item.Size));
        Equal(1, vm.MapItems.Count(item => item.IsFreeSpace));

        var pending = new TaskCompletionSource<DiskSpace>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Read = (_, _) => pending.Task;
        vm.NavigateMap(new MapItem("Root", "", used, root.Path, true)); var old = vm.WaitForDiskSpaceAsync();
        reader.Read = (_, _) => Task.FromResult(new DiskSpace(70, 70, DateTimeOffset.Now));
        vm.NavigateMap(new MapItem("Folder0", "", 100, children[0].Path, true));
        await vm.WaitForDiskSpaceAsync(); await old;
        pending.SetResult(new DiskSpace(999, 999, DateTimeOffset.Now)); DrainDispatcher();
        Check(vm.MapItems.All(item => !item.IsFreeSpace) && vm.MapFreeSpaceText == "70 B", "Late root data leaked into the subfolder chart.");

        var scoped = result with { Roots = [children[0]], TotalBytes = 100, FileCount = 1, FolderCount = 1 };
        var scopedVm = new MainViewModel(new StubScanner((_, _, _) => throw new Exception("Unexpected scan.")),
            new StubStore(new SavedScan(scoped, children[0].Path, DateTimeOffset.UtcNow)), diskSpaceReader: reader);
        await scopedVm.RestoreLastScanAsync(); scopedVm.NavigateCommand.Execute("folders"); await scopedVm.WaitForDiskSpaceAsync();
        Check(scopedVm.MapItems.All(item => !item.IsFreeSpace), "An analyzed subfolder was mistaken for a disk root.");
        Equal(used, root.TotalBytes); Equal(0, store.SaveCalls); Equal(0, store.DeleteCalls);
        vm.Cancel(); scopedVm.Cancel();
    }

    private sealed class StubDiskSpaceReader : IDiskSpaceReader
    {
        public required Func<string, CancellationToken, Task<DiskSpace>> Read { get; set; }
        public List<(string Path, CancellationToken Token)> Calls { get; } = [];
        public Task<DiskSpace> ReadAsync(string folderPath, CancellationToken token)
        { Calls.Add((folderPath, token)); return Read(folderPath, token); }
    }
}
