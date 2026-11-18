using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;

namespace WindowsFS.App.Controls;

public partial class FolderQuestionWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly FolderNode _folder;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<FileRecord>? _files;
    private bool _ready;
    private bool _sending;
    private string Question => DeleteChoice.IsChecked == true ? FolderQuestions.DeleteQuestion : FolderQuestions.PurposeQuestion;

    internal FolderQuestionWindow(MainViewModel vm, FolderNode folder)
    {
        _vm = vm;
        _folder = folder;
        InitializeComponent();
        FolderPath.Text = folder.Path;
        DeleteChoice.Content = FolderQuestions.DeleteQuestion;
        PurposeChoice.Content = FolderQuestions.PurposeQuestion;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateQuestion();
    private void OnChoice(object sender, RoutedEventArgs e) { if (IsLoaded) UpdateQuestion(); }
    private void OnClosed(object? sender, EventArgs e) { _lifetime.Cancel(); _lifetime.Dispose(); }

    private void UpdateQuestion()
    {
        var answer = _vm.FindFolderAnswer(_folder.Path, Question);
        Body.Text = answer?.Text ?? "";
        AnswerPanel.Visibility = answer is null ? Visibility.Collapsed : Visibility.Visible;
        Send.Visibility = answer is null ? Visibility.Visible : Visibility.Collapsed;
        Send.IsEnabled = answer is null;
        Status.Text = answer is null ? "" : "이 분석의 답변";
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_sending || _vm.FindFolderAnswer(_folder.Path, Question) is not null) return;
        _sending = true;
        var question = Question;
        var token = _lifetime.Token;
        Send.IsEnabled = DeleteChoice.IsEnabled = PurposeChoice.IsEnabled = false;
        Status.Text = "답변 요청 중";
        try
        {
            if (!_ready)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(TimeSpan.FromSeconds(8));
                try { _files = await _vm.QuestionFilesAsync(_folder, budget.Token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or System.Security.SecurityException)
                { _files = null; }
                token.ThrowIfCancellationRequested();
                _ready = _files is not null;
            }
            var input = FolderQuestions.BuildInput(_folder, _files, question);
            var answer = await FolderQuestions.AskAsync(input, token);
            token.ThrowIfCancellationRequested();
            Body.Text = answer;
            AnswerPanel.Visibility = Visibility.Visible;
            Send.Visibility = Visibility.Collapsed;
            var saved = await _vm.RememberAnswerAsync(_folder, question, answer);
            Status.Text = saved ? "분석에 저장됨" : "현재 세션에만 보관됨";
        }
        catch (FolderQuestionException ex) { Status.Text = ex.Message; }
        catch (OperationCanceledException) { Status.Text = "요청 중단 또는 시간 초과"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or System.Security.SecurityException)
        { Status.Text = "요청 실패 · API 키 설정 및 연결 확인 필요"; }
        finally
        {
            _sending = false;
            Send.IsEnabled = Send.Visibility == Visibility.Visible;
            DeleteChoice.IsEnabled = PurposeChoice.IsEnabled = true;
        }
    }
}
