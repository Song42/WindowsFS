using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using WindowsFS.App.Controls;
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
        var vm = new MainViewModel(scanner, store, fileBrowser: new ReadOnlyFileBrowser());
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
        Check(vm.HasQuestionFolder, "Indexed selection did not expose folder questions.");
        var oldNode = vm.QuestionFolder!;
        Check(await vm.RememberAnswerAsync(oldNode, FolderQuestions.DeleteQuestion, answer), "Answer save failed.");
        var dialog = new FolderQuestionWindow(vm, oldNode);
        try
        {
            dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Equal(answer, dialog.Body.Text);
            Equal(Visibility.Visible, dialog.AnswerPanel.Visibility);
            Equal(Visibility.Collapsed, dialog.Send.Visibility);
            dialog.PurposeChoice.IsChecked = true;
            // Unshown test windows have IsLoaded=false; run the same initial-view update.
            dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Equal("", dialog.Body.Text);
            Equal(Visibility.Collapsed, dialog.AnswerPanel.Visibility);
            Check(dialog.Send.IsEnabled, "An unanswered question cannot be sent.");
        }
        finally { dialog.Close(); }
        var reopened = CreateViewModel(scanner, store);
        await reopened.RestoreLastScanAsync();
        Equal(answer, reopened.FindFolderAnswer(source, FolderQuestions.DeleteQuestion)!.Text);
        Check(reopened.FindFolderAnswer(source, FolderQuestions.PurposeQuestion) is null, "Different questions shared an answer.");
        await reopened.ScanAsync();
        Check(reopened.FindFolderAnswer(source, FolderQuestions.DeleteQuestion) is null, "New scan retained an old answer.");
        Check(!await reopened.RememberAnswerAsync(oldNode, FolderQuestions.DeleteQuestion, "late"), "Stale answer entered a new scan.");
        Equal(0, (await store.LoadAsync())!.Answers.Length);
        Equal(privateContent, File.ReadAllText(Path.Combine(source, "one.txt")));
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
