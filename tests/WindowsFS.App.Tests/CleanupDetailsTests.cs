using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;
using WindowsFS.Core.Cleanup;
using WindowsFS.Core.Persistence;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static async Task CleanupFileDetails()
    {
        using var fixture = new PickerFixture();
        var path = Path.Combine(fixture.Root, "notes.txt");
        File.WriteAllText(path, "Synthetic cleanup preview.");
        var size = new FileInfo(path).Length;
        var result = new ScanResult([new FolderNode(fixture.Root, size, 1)], [], [new FileTypeSummary(".txt", size, 1)], size, 1, 1, 0, TimeSpan.Zero, false);
        var before = CapturePickerTree(fixture.Root);
        var store = new StubStore(new SavedScan(result, fixture.Root, DateTimeOffset.UtcNow));
        var vm = new MainViewModel(new StubScanner((_, _, _) => throw new Exception("Selection must not rescan.")), store,
            detailsReader: new ItemDetailsReader(), fileBrowser: new ReadOnlyFileBrowser());
        var window = new MainWindow { DataContext = vm };
        try
        {
            await vm.RestoreLastScanAsync();
            vm.NavigateCommand.Execute("cleanup"); await vm.Cleanup.WaitAsync(); await vm.Cleanup.WaitForFilesAsync();
            vm.PreviewExpanded = true; vm.GeneralExpanded = false;
            var content = (FrameworkElement)window.Content;
            LayoutAt(content, new Size(1800, 1000));
            var view = Descendants<CleanupAnalysisView>(content).Single();
            var table = (DataGrid)view.FindName("CleanupFiles");
            table.SetCurrentValue(Selector.SelectedItemProperty, vm.Cleanup.Files.Single());
            await vm.WaitForPreviewAsync();
            Check(vm.IsCleanup && vm.HasDetailPanel && vm.CanPreview, "Cleanup file selection did not open the shared details.");
            Check(vm.Details.Any(field => field.Label == "전체 경로" && field.Value == path), "Selected path was missing from General.");
            Check(vm.Details.Any(field => field.Label == "목록 조회 시점 크기 · 논리적"), "Live file size was labeled as a saved scan.");
            Check(vm.PreviewText.Contains("Synthetic cleanup preview."), "Cleanup file did not use the real text preview reader.");
            Check(vm.PreviewExpanded && !vm.GeneralExpanded, "Selection discarded shared section preferences.");
            LayoutAt(content, new Size(1800, 1000));
            var panel = (Border)window.FindName("SystemInfoPanel");
            var preview = (FilePreviewControl)window.FindName("ItemPreview");
            Check(IsEffectivelyVisible(panel) && IsEffectivelyVisible(preview) && ReferenceEquals(preview.Preview, vm.Preview), "Shared preview panel was hidden or unbound.");
            var handle = (Thumb)window.FindName("DetailsResizeHandle");
            handle.RaiseEvent(new DragDeltaEventArgs(-80, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            LayoutAt(content, new Size(1800, 1000));
            var preferred = window.DetailsPanelWidth;
            foreach (var viewport in new[] { new Size(1100, 760), new Size(1800, 1000), new Size(1400, 920) })
            {
                LayoutAt(content, viewport);
                Check(IsEffectivelyVisible(panel) && panel.ActualWidth >= 325, "Cleanup hid or squeezed the shared details panel.");
                Check(view.ActualWidth >= SpaceMapVisibilityConverter.MinimumCleanupWidth && table.ActualWidth >= 360, "Details squeezed the cleanup file list.");
                Check(Descendants<ScrollViewer>(table).All(scroll => scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible), "Cleanup selection introduced horizontal scrollbars.");
                Equal(preferred, window.DetailsPanelWidth);
            }
            vm.DismissSystemInfoCommand.Execute(null); Check(!vm.HasDetailPanel, "Close did not hide cleanup details.");
            vm.Cleanup.SelectedFile = vm.Cleanup.Files.Single();
            Check(vm.HasDetailPanel, "Reselecting the same file did not reopen details.");
            vm.Cleanup.RefreshFilesCommand.Execute(null); await vm.Cleanup.WaitForFilesAsync();
            Check(!vm.HasDetailPanel && vm.Cleanup.SelectedFile is null && vm.PreviewText.Length == 0, "Refreshing files retained stale details.");
            vm.Cleanup.SelectedFile = vm.Cleanup.Files.Single(); await vm.WaitForPreviewAsync();
            vm.Cleanup.Search = "no-match";
            Check(!vm.HasDetailPanel && !vm.CanPreview, "Filtering out the bucket retained its details.");
            Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Cleanup details or previews mutated source files.");
            Equal(0, store.SaveCalls); Equal(0, store.DeleteCalls);
            Check(!window.IsVisible, "Cleanup tests must not show a window.");
        }
        finally { vm.Cancel(); window.Close(); }
    }

    private static async Task CleanupPreviewLifecycle()
    {
        var pending = new TaskCompletionSource<FilePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previews = new StubPreviewReader((_, _, _) => pending.Task);
        var media = new StubMediaInfoReader { Read = (_, _) => Task.FromResult(new MediaInfoResult([new("Audio", [new("Format", "MP3")])])) };
        var browser = new StubFileBrowser((path, _) => Task.FromResult<IReadOnlyList<FileRecord>>(
            new[] { "first.pdf", "second.pdf", "sound.mp3", "other.bin" }.Select(name => new FileRecord(Path.Combine(path, name), name, 100, Path.GetExtension(name))).ToArray()));
        var vm = new MainViewModel(new StubScanner((_, _, _) => throw new Exception("Selection must not scan.")),
            new StubStore(new SavedScan(CleanupFixtureResult(), @"X:\", DateTimeOffset.UtcNow)),
            fileBrowser: browser, previewReader: previews, mediaInfoReader: media,
            cleanupReader: new StubCleanupReader { Read = _ => Task.FromResult(CleanupFixtureContext()) });
        await vm.RestoreLastScanAsync();
        vm.NavigateCommand.Execute("cleanup"); await vm.Cleanup.WaitAsync(); await vm.Cleanup.WaitForFilesAsync();
        CleanupFileRow File(string name) => vm.Cleanup.Files.First(file => file.Name == name);
        vm.Cleanup.SelectedFile = File("first.pdf");
        var old = vm.WaitForPreviewAsync(); var token = previews.Calls.Last().Token;
        previews.Read = (row, page, _) => Task.FromResult(new FilePreview(row.Name, PageIndex: page, PageCount: 2));
        vm.Cleanup.SelectedFile = File("second.pdf");
        await old; await vm.WaitForPreviewAsync();
        pending.SetResult(new FilePreview("stale")); DrainDispatcher();
        Check(token.IsCancellationRequested && vm.PreviewText == "second.pdf", "Old cleanup preview replaced the selected file.");
        vm.NextPreviewPageCommand.Execute(null); await vm.WaitForPreviewAsync();
        Equal("2 / 2", vm.PreviewPageLabel);
        vm.MediaInfoExpanded = true;
        vm.Cleanup.SelectedFile = File("sound.mp3"); await vm.WaitForMediaInfoAsync();
        Check(vm.HasMediaInfo && vm.MediaInfoGroups.Single().Title == "Audio", "Cleanup selection bypassed shared MediaInfo.");
        vm.Cleanup.SelectedFile = File("other.bin");
        Check(!vm.CanPreview && !vm.HasMediaInfo && !vm.HasPreviewPages, "Unsupported file retained media or preview state.");
        vm.Cleanup.SelectedFile = new CleanupFileRow(new(@"X:\outside.txt", "outside.txt", 1, ".txt"));
        Equal("other.bin", vm.Cleanup.SelectedFile!.Name);
        vm.NavigateCommand.Execute("overview");
        Check(!vm.HasDetailPanel, "Leaving cleanup retained cleanup details on the dashboard.");
        vm.NavigateCommand.Execute("cleanup");
        Check(vm.HasDetailPanel, "Returning to the selected cleanup file lost its details.");
        vm.Cleanup.Reset();
        Check(!vm.HasDetailPanel && vm.Details.Count == 0, "Reset retained cleanup details.");
        vm.Cancel();
    }
}
