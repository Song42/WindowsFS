using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;
using WindowsFS.Core.Cleanup;
using WindowsFS.Core.Persistence;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static async Task FolderQuestionsRoundTrip()
    {
        using var fixture = new PickerFixture();
        var source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        const string privateContent = "fixture document contents are private";
        File.WriteAllText(Path.Combine(source, "one.txt"), privateContent);
        var size = Encoding.UTF8.GetByteCount(privateContent);
        var folder = new FolderNode(source, size, 1, [new FolderNode(Path.Combine(source, "photos"))]);
        var result = new ScanResult([folder], [], [new FileTypeSummary(".txt", size, 1)], size, 1, 2, 0, TimeSpan.Zero, false);
        var store = new FileScanResultStore(Path.Combine(fixture.Root, "cache", "latest.scan.json.gz"));
        await store.SaveAsync(new SavedScan(result, source, DateTimeOffset.UtcNow));
        var scanner = new StubScanner((_, _, _) => Task.FromResult(result));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var vm = new MainViewModel(scanner, store, fileBrowser: new ReadOnlyFileBrowser())
        {
            AskFolderAsync = (_, token) => { requests++; started.TrySetResult(); return reply.Task.WaitAsync(token); }
        };
        await vm.RestoreLastScanAsync();
        vm.NavigateCommand.Execute("folders");
        vm.SelectedRow = vm.Rows.Single(row => row.Path == source);
        var input = await vm.BuildQuestionInputAsync(vm.QuestionFolder!, FolderQuestions.DeleteQuestion, CancellationToken.None);
        using var json = JsonDocument.Parse(input);
        Equal(source, json.RootElement.GetProperty("folderPath").GetString()!);
        Equal("one.txt", json.RootElement.GetProperty("files")[0].GetProperty("name").GetString()!);
        Equal("photos", json.RootElement.GetProperty("folders")[0].GetProperty("name").GetString()!);
        Check(!input.Contains(privateContent), "File contents escaped into question metadata.");
        var handler = new InspectQuestionHandler(input);
        using var client = new HttpClient(handler);
        var answer = await FolderQuestions.SendAsync(client, "fixture-token", input, CancellationToken.None);
        Equal("fixture answer", answer);
        var oldNode = vm.QuestionFolder!;
        Check(await vm.RememberAnswerAsync(oldNode, FolderQuestions.DeleteQuestion, answer), "Answer save failed.");
        var window = new MainWindow { DataContext = vm };
        try
        {
            Render((FrameworkElement)window.Content);
            var sections = (ItemsControl)window.FindName("FolderQuestionSections");
            var expanders = Descendants<Expander>(sections).ToArray();
            Equal(2, expanders.Length);
            Check(expanders.All(e => !e.IsExpanded), "Folder selection opened a question.");
            var cached = expanders.Single(e => ((FolderQuestionSection)e.Header).Question == FolderQuestions.DeleteQuestion);
            var fresh = expanders.Single(e => ((FolderQuestionSection)e.Header).Question == FolderQuestions.PurposeQuestion);
            var cachedHeader = Descendants<ToggleButton>(cached).Single();
            var arrow = Descendants<System.Windows.Shapes.Path>(cachedHeader).Single();
            var down = arrow.Data.ToString();
            foreach (var name in new[] { "GeneralSection", "SystemInfoSection", "MediaInfoSection", "PreviewExpander" })
                Check(ReferenceEquals(((Expander)window.FindName(name)).Template, cached.Template), $"{name} has a different header template.");
            var general = (Expander)window.FindName("GeneralSection");
            var generalTitle = Descendants<TextBlock>(Descendants<ToggleButton>(general).First()).Single(t => t.Text == "General");
            var questionTitle = Descendants<TextBlock>(cachedHeader).Single(t => t.Text == FolderQuestions.DeleteQuestion);
            foreach (var title in new[] { generalTitle, questionTitle })
                Check(title.Foreground is System.Windows.Media.SolidColorBrush brush && brush.Color == System.Windows.Media.Colors.White,
                    "Section title did not render in white.");
            Check(arrow.TranslatePoint(new Point(arrow.ActualWidth, 0), cachedHeader).X <= questionTitle.TranslatePoint(new Point(), cachedHeader).X,
                "The chevron must precede the section title.");
            Check(Descendants<TextBlock>(cachedHeader).Where(t => t.Text.Length > 0).Select(t => t.Text)
                .SequenceEqual([FolderQuestions.DeleteQuestion]), "A cached question should only show its title.");
            Check(Descendants<TextBlock>(fresh).Any(t => t.Text == "요청하기"), "Missing new request label.");
            cachedHeader.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Render((FrameworkElement)window.Content);
            Check(cached.IsExpanded && arrow.Data.ToString() != down, "Header did not expand and switch to its up arrow.");
            Check(Descendants<TextBlock>(cached).Any(t => t.Text == answer), "Saved answer was not displayed.");
            Equal(0, requests);
            fresh.IsExpanded = true;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Render((FrameworkElement)window.Content);
            var skeleton = Descendants<Grid>(fresh).Single(grid => grid.Name == "AnswerSkeleton");
            Check(skeleton.Visibility == Visibility.Visible && skeleton.Children.OfType<Border>().Count() == 3,
                "Missing multiline answer skeleton.");
            fresh.IsExpanded = false;
            Render((FrameworkElement)window.Content);
            Check(Descendants<TextBlock>(Descendants<ToggleButton>(fresh).Single()).Where(t => t.Text.Length > 0).Select(t => t.Text)
                .SequenceEqual([FolderQuestions.PurposeQuestion]), "A pending question should only show its title.");
            fresh.IsExpanded = true;
            fresh.IsExpanded = false;
            var section = (FolderQuestionSection)fresh.DataContext;
            reply.SetResult("purpose answer");
            await section.Pending;
            await vm.WaitForPendingPersistenceAsync();
            Render((FrameworkElement)window.Content);
            Equal("", section.RequestStatus);
            Check(!fresh.IsExpanded && Descendants<TextBlock>(Descendants<ToggleButton>(fresh).Single())
                .Where(t => t.Text.Length > 0).Select(t => t.Text).SequenceEqual([FolderQuestions.PurposeQuestion]), "A reply did not clear the header status.");
            fresh.IsExpanded = true;
            Render((FrameworkElement)window.Content);
            Equal(Visibility.Collapsed, skeleton.Visibility);
            Check(Descendants<TextBlock>(fresh).Any(t => t.Text == "purpose answer"), "Missing inline answer.");
            Equal(1, requests);
        }
        finally { window.Close(); }
        var reopened = CreateViewModel(scanner, store);
        await reopened.RestoreLastScanAsync();
        Equal(answer, reopened.FindFolderAnswer(source, FolderQuestions.DeleteQuestion)!.Text);
        Equal("purpose answer", reopened.FindFolderAnswer(source, FolderQuestions.PurposeQuestion)!.Text);
        reopened.NavigateCommand.Execute("folders");
        reopened.SelectedRow = reopened.Rows.Single(row => row.Path == source);
        Check(reopened.QuestionSections.Count == 2 && reopened.QuestionSections.All(s => !s.IsExpanded && s.RequestStatus == ""),
            "Restored answers were not identified before expansion.");
        await reopened.ScanAsync();
        Check(reopened.FindFolderAnswer(source, FolderQuestions.DeleteQuestion) is null, "New scan retained an old answer.");
        Check(!await reopened.RememberAnswerAsync(oldNode, FolderQuestions.DeleteQuestion, "late"), "Stale answer entered a new scan.");
        Equal(0, (await store.LoadAsync())!.Answers.Length);
        Equal(privateContent, File.ReadAllText(Path.Combine(source, "one.txt")));
    }

    private static async Task ConcurrentFolderQuestions()
    {
        var result = FixtureResult();
        var root = result.Roots.Single();
        var saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new[] { new TaskCompletionSource<string>(), new TaskCompletionSource<string>() };
        var store = new StubStore(new SavedScan(result, root.Path, DateTimeOffset.UtcNow)) { OnSave = (_, _) => saveGate.Task };
        var calls = 0;
        var vm = new MainViewModel(new StubScanner((_, _, _) => Task.FromResult(result)), store)
        {
            AskFolderAsync = (input, _) =>
            {
                calls++;
                if (calls == 1) throw new FolderQuestionException("일시적인 요청 오류");
                using var json = JsonDocument.Parse(input);
                return replies[json.RootElement.GetProperty("question").GetString() == FolderQuestions.DeleteQuestion ? 0 : 1].Task;
            }
        };
        await vm.RestoreLastScanAsync();
        vm.NavigateCommand.Execute("folders");
        var rootRow = vm.Rows.Single();
        vm.SelectedRow = rootRow;
        var first = vm.QuestionSections[0];
        first.IsExpanded = true;
        await first.Pending;
        Equal("일시적인 요청 오류", first.Text);
        Equal("요청하기", first.RequestStatus);
        first.IsExpanded = false;
        first.IsExpanded = true;
        var second = vm.QuestionSections[1];
        second.IsExpanded = true;
        vm.OpenRowCommand.Execute(rootRow);
        vm.SelectedRow = vm.Rows.First();
        Check(vm.QuestionSections.All(s => !s.IsExpanded && s.Text.Length == 0), "Selection inherited another folder's requests.");
        vm.SelectedRow = vm.DisplayRows.Single(row => row.Navigation == FolderNavigationKind.Current);
        var revisited = vm.QuestionSections[0];
        Equal("", revisited.RequestStatus);
        revisited.IsExpanded = true;
        replies[0].SetResult("answer one");
        await first.Pending;
        await revisited.Pending;
        Equal("answer one", revisited.Text);
        replies[1].SetResult("answer two");
        await second.Pending;
        Equal("", vm.QuestionSections[1].RequestStatus);
        Equal(3, calls);
        Equal(1, store.SaveCalls);
        saveGate.SetResult();
        await vm.WaitForPendingPersistenceAsync();
        Equal(2, store.Saved!.Answers.Length);
        Check(store.Saved.Answers.All(a => a.Path == root.Path), "Answer saved against the wrong folder.");
    }

    private static async Task FolderQuestionCoverage()
    {
        var profile = CleanupFixtureResult().Roots.Single().Children.Single(child => child.Name == "Default");
        var context = CleanupFixtureContext();
        var classification = CleanupAnalyzer.DescribeFolder(profile, context);
        Equal("Google Chrome", classification.Selected.AppOrArea);
        Equal(3, classification.FoldersExamined);
        Equal(3000L, classification.Purposes.Single(p => p.Assessment == "정리 후보").Bytes);
        Equal(50L, classification.Purposes.Single(p => p.Assessment == "유지 권장").Bytes);
        Equal(profile.FileCount, classification.Purposes.Sum(p => p.Files));
        var now = DateTimeOffset.UtcNow;
        string Build(IReadOnlyList<FileRecord>? files) => FolderQuestions.BuildInput(profile, files,
            FolderQuestions.DeleteQuestion, classification, now.AddDays(-1), true, 17, now,
            files is null ? "조회 불가" : "조회 완료");
        using var failed = JsonDocument.Parse(Build(null));
        var coverage = failed.RootElement.GetProperty("coverage");
        Equal(JsonValueKind.Null, coverage.GetProperty("omittedFileNames").ValueKind);
        Equal("조회 불가", coverage.GetProperty("fileReadStatus").GetString()!);
        Equal(17L, coverage.GetProperty("skippedAcrossEntireScan").GetInt64());
        Check(coverage.GetProperty("scanCancelled").GetBoolean(), "Cancelled scan was represented as complete.");
        using var empty = JsonDocument.Parse(Build([]));
        Equal(0, empty.RootElement.GetProperty("coverage").GetProperty("immediateFilesAtRead").GetInt32());
        var files = Enumerable.Range(0, 160).Select(i => new FileRecord("unshared-file-path",
            new string('한', 190) + i, i, "")).ToArray();
        var input = Build(files);
        Check(FolderQuestions.InputBudgetUsed(input) <= FolderQuestions.SoftInputBudget, "Sample exceeded soft input limit.");
        Check(!input.Contains("Vendor A") && !input.Contains("unshared-file-path"), "Unrelated inventory was disclosed.");
        using var sample = JsonDocument.Parse(input);
        var sent = sample.RootElement.GetProperty("files").GetArrayLength();
        Check(sent > 0 && sent < 60, "Wide file names did not reduce the sample.");
        Equal(160 - sent, sample.RootElement.GetProperty("coverage").GetProperty("omittedFileNames").GetInt32());
        Equal(159, sample.RootElement.GetProperty("files")[0].GetProperty("bytes").GetInt32());
        using var client = new HttpClient(new InspectQuestionHandler("no request expected"));
        try
        {
            await FolderQuestions.SendAsync(client, "fixture-token", new string('한', 8000), CancellationToken.None);
            throw new Exception("Oversized input reached transport.");
        }
        catch (FolderQuestionException) { }
    }

    private sealed class InspectQuestionHandler(string expectedInput) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Equal(expectedInput, body.RootElement.GetProperty("input").GetString()!);
            Equal("gpt-6.2-sol", body.RootElement.GetProperty("model").GetString()!);
            Equal("medium", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString()!);
            Equal(2048, body.RootElement.GetProperty("max_output_tokens").GetInt32());
            Check(!body.RootElement.GetProperty("store").GetBoolean(), "OpenAI response storage was enabled.");
            Check(!body.RootElement.GetRawText().Contains("fixture-token"), "Credential appeared in the payload.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"fixture answer"}]}]}""") };
        }
    }
}
