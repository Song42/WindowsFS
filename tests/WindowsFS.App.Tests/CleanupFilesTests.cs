using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;
using WindowsFS.Core.Cleanup;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static async Task CleanupFileMetadata()
    {
        using var fixture = new PickerFixture();
        var child = Directory.CreateDirectory(Path.Combine(fixture.Root, "saved")).FullName;
        var added = Directory.CreateDirectory(Path.Combine(fixture.Root, "new-folder")).FullName;
        File.WriteAllBytes(Path.Combine(fixture.Root, "small.bin"), new byte[3]);
        File.WriteAllBytes(Path.Combine(child, "large.bin"), new byte[13]);
        File.WriteAllBytes(Path.Combine(added, "excluded.bin"), new byte[99]);
        var policy = CleanupAnalyzer.Analyze(CleanupFixtureResult(), CleanupFixtureContext())[0].Buckets[0].Policy;
        var bucket = new CleanupBucket(policy, "Fixture", [new(fixture.Root, 3, 1), new(child, 13, 1), new(child, 13, 1), new(Path.Combine(fixture.Root, "missing"), 0, 0)]);
        var before = CapturePickerTree(fixture.Root);
        var reader = new CleanupFilesReader(new ReadOnlyFileBrowser());
        var result = await reader.ReadAsync(bucket, CancellationToken.None);
        Equal(2, result.Files.Count); Equal(1, result.SkippedLocations);
        Equal(13L, result.Files[0].Size); Equal(Path.Combine(child, "large.bin"), result.Files[0].Path);
        Check(!result.Limited && before.SequenceEqual(CapturePickerTree(fixture.Root)), "Metadata enumeration modified source files or read a new subtree.");
        File.WriteAllBytes(Path.Combine(child, "new-file.bin"), new byte[7]);
        result = await reader.ReadAsync(bucket, CancellationToken.None);
        Equal(3, result.Files.Count);
        Check(result.Files.All(file => !file.Path.StartsWith(added, StringComparison.Ordinal)), "New directories leaked into a saved bucket.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await reader.ReadAsync(bucket, cancelled.Token); throw new Exception("File metadata ignored cancellation."); }
        catch (OperationCanceledException) { }
        var escaped = new CleanupFilesReader(new StubFileBrowser((_, _) => Task.FromResult<IReadOnlyList<FileRecord>>([new(Path.Combine(added, "excluded.bin"), "excluded.bin", 99, ".bin")])));
        Equal(0, (await escaped.ReadAsync(new CleanupBucket(policy, "Fixture", [new(child, 13, 1)]), CancellationToken.None)).Files.Count);
        var capped = new CleanupFilesReader(new StubFileBrowser((path, _) => Task.FromResult<IReadOnlyList<FileRecord>>(
            Enumerable.Range(0, CleanupFilesReader.MaximumFiles + 1).Select(index => new FileRecord(Path.Combine(path, $"{index}.bin"), $"{index}.bin", 1, ".bin")).ToArray())));
        var limited = await capped.ReadAsync(new CleanupBucket(policy, "Fixture", [new(child, 13, 1)]), CancellationToken.None);
        Equal(CleanupFilesReader.MaximumFiles, limited.Files.Count);
        Check(limited.Limited, "A capped file list was presented as complete.");
    }

    private static async Task CleanupFileState()
    {
        var reader = new StubCleanupFilesReader();
        var vm = new CleanupViewModel(new StubCleanupReader { Read = _ => Task.FromResult(CleanupFixtureContext()) }, _ => { }, reader);
        vm.Load(CleanupFixtureResult()); await vm.WaitAsync(); await vm.WaitForFilesAsync();
        vm.Search = "Chrome"; vm.CandidatesOnly = true;
        Equal(1, vm.Groups.Count); Equal(1, vm.Buckets.Count);
        Equal(3000L, vm.Groups[0].Group.Bytes); Equal(30000L, vm.Groups[0].Group.FileCount);
        Check(vm.Buckets.All(bucket => bucket.Assessment == "정리 후보"), "Non-candidates appeared in the candidate filter.");
        await vm.WaitForFilesAsync();
        var calls = reader.Calls;
        vm.CandidatesOnly = false; vm.CandidatesOnly = true;
        await vm.WaitForFilesAsync(); Equal(calls, reader.Calls);
        reader.Bytes = 42;
        vm.RefreshFilesCommand.Execute(null); await vm.WaitForFilesAsync();
        Equal(calls + 1, reader.Calls); Equal(42L, vm.Files.Single().Bytes);
        Check(vm.FilesStatus.Contains("저장된 집계와 차이 있음"), "Live files were presented as the saved aggregate.");
        var pending = new TaskCompletionSource<CleanupFilesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Pending = pending.Task;
        vm.RefreshFilesCommand.Execute(null);
        var old = vm.WaitForFilesAsync(); var token = reader.Token;
        vm.Search = "no-match";
        await old;
        Check(token.IsCancellationRequested && vm.Files.Count == 0 && !vm.FilesLoading, "Changing selection did not cancel the old file list.");
        pending.SetResult(new([new(@"X:\stale.bin", "stale.bin", 99, ".bin")], 0, false, DateTimeOffset.Now));
        DrainDispatcher(); Equal(0, vm.Files.Count);
        reader.Pending = null;
        vm.Search = "";
        Check(vm.Groups.All(group => group.Group.Buckets.All(bucket => bucket.Policy.Assessment == "정리 후보")), "Clearing search lost the candidate filter.");
        vm.SidebarWidth = 480; vm.Reset();
        Equal(480d, vm.SidebarWidth); Check(vm.CandidatesOnly, "Reset discarded the filter preference.");
        Equal(0, vm.Files.Count);
    }

    private static async Task CleanupFilesLayout()
    {
        var reader = new StubCleanupFilesReader { FileCount = 600 };
        var vm = new CleanupViewModel(new StubCleanupReader { Read = _ => Task.FromResult(CleanupFixtureContext()) }, _ => { }, reader);
        vm.Load(CleanupFixtureResult()); await vm.WaitAsync();
        vm.Search = "Chrome"; vm.CandidatesOnly = true; await vm.WaitForFilesAsync();
        var view = new CleanupAnalysisView { DataContext = vm };
        LayoutAt(view, new Size(1600, 650));
        var column = (ColumnDefinition)view.FindName("SidebarColumn");
        Equal(360d, column.ActualWidth);
        var handle = (Thumb)view.FindName("SidebarResizeHandle");
        handle.RaiseEvent(new DragDeltaEventArgs(180, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        LayoutAt(view, new Size(1600, 650)); Equal(540d, column.ActualWidth);
        var table = (DataGrid)view.FindName("CleanupFiles");
        Equal(600, table.Items.Count);
        Check(table.IsReadOnly && !table.CanUserAddRows && !table.CanUserDeleteRows, "Cleanup files allow mutations.");
        Check(table.Columns.Select(item => item.Header).SequenceEqual(new[] { "이름", "크기", "경로" }), "File metadata columns are missing.");
        var filter = (CheckBox)view.FindName("CandidatesFilter");
        filter.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        Check(!vm.CandidatesOnly && vm.Buckets.Count > 1, "Candidate toggle did not update the view model.");
        foreach (var width in new[] { 1000d, 1600d, 1150d, 1600d })
        {
            LayoutAt(view, new Size(width, 650));
            Equal(540d, vm.SidebarWidth);
            Check(column.ActualWidth <= width - 580 && table.ActualWidth > 400, "Sidebar squeezed the file list.");
            Check(Descendants<ScrollViewer>(table).All(scroll => scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible), "Resizing introduced a horizontal scrollbar.");
            var rows = Descendants<DataGridRow>(table).ToArray();
            Check(rows.Length < 100, "File rows lost virtualization.");
        }
        Equal(540d, column.ActualWidth);
        foreach (var expander in Descendants<Expander>(view)) expander.IsExpanded = true;
        LayoutAt(view, new Size(1000, 650));
        Check(table.ActualHeight >= 60, "Expanded explanations hid the file list.");
        vm.Cancel();
    }

    private sealed class StubCleanupFilesReader : ICleanupFilesReader
    {
        public int Calls { get; private set; }
        public int FileCount { get; set; } = 1;
        public long Bytes { get; set; } = 1;
        public CancellationToken Token { get; private set; }
        public Task<CleanupFilesResult>? Pending { get; set; }
        public Task<CleanupFilesResult> ReadAsync(CleanupBucket bucket, CancellationToken token)
        {
            Calls++; Token = token;
            return Pending ?? Task.FromResult(new CleanupFilesResult(Enumerable.Range(0, FileCount).Select(index =>
                new FileRecord(Path.Combine(bucket.Locations[0].Path, $"fixture-{index}.bin"), $"fixture-{index}.bin", Bytes, ".bin")).ToArray(), 0, false, DateTimeOffset.Now));
        }
    }
}
