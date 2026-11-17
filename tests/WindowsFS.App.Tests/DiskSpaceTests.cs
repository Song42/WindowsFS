using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    private sealed class StubDiskSpaceReader : IDiskSpaceReader
    {
        public required Func<string, CancellationToken, Task<DiskSpace>> Read { get; set; }
        public List<(string Path, CancellationToken Token)> Calls { get; } = [];
        public Task<DiskSpace> ReadAsync(string folderPath, CancellationToken token)
        { Calls.Add((folderPath, token)); return Read(folderPath, token); }
    }
}
