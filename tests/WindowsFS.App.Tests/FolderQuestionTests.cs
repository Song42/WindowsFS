using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.Core;
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
        var files = await new ReadOnlyFileBrowser().ReadAsync(source);
        var input = FolderQuestions.BuildInput(folder, files, FolderQuestions.DeleteQuestion);
        using var json = JsonDocument.Parse(input);
        Equal(source, json.RootElement.GetProperty("folderPath").GetString()!);
        Equal("one.txt", json.RootElement.GetProperty("files")[0].GetProperty("name").GetString()!);
        Equal("photos", json.RootElement.GetProperty("folders")[0].GetProperty("name").GetString()!);
        Check(!input.Contains(privateContent), "File contents escaped into question metadata.");
        var handler = new InspectQuestionHandler(input);
        using var client = new HttpClient(handler);
        var answer = await FolderQuestions.SendAsync(client, "fixture-token", input, CancellationToken.None);
        Equal("fixture answer", answer);
        var result = new ScanResult([folder], [], [new FileTypeSummary(".txt", size, 1)], size, 1, 2, 0, TimeSpan.Zero, false);
        var store = new FileScanResultStore(Path.Combine(fixture.Root, "cache", "latest.scan.json.gz"));
        await store.SaveAsync(new SavedScan(result, source, DateTimeOffset.UtcNow));
        var scanner = new StubScanner((_, _, _) => Task.FromResult(result));
        var vm = CreateViewModel(scanner, store);
        await vm.RestoreLastScanAsync();
        vm.NavigateCommand.Execute("folders");
        vm.SelectedRow = vm.Rows.Single(row => row.Path == source);
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

    private sealed class InspectQuestionHandler(string expectedInput) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Equal(expectedInput, body.RootElement.GetProperty("input").GetString()!);
            Equal("gpt-6.2-sol", body.RootElement.GetProperty("model").GetString()!);
            Equal("medium", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString()!);
            Check(!body.RootElement.GetProperty("store").GetBoolean(), "OpenAI response storage was enabled.");
            Check(!body.RootElement.GetRawText().Contains("fixture-token"), "Credential appeared in the payload.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"fixture answer"}]}]}""") };
        }
    }
}
