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
    private string? _input;
    private bool _reading;
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

    private async void UpdateQuestion()
    {
        _input = null;
        Send.IsEnabled = false;
        Status.Text = "";
        if (_vm.FindFolderAnswer(_folder.Path, Question) is { } cached)
        {
            SectionTitle.Text = "이 분석의 답변";
            Body.Text = cached.Text;
            return;
        }
        SectionTitle.Text = "OpenAI 전송 내용 · 바로 아래 파일과 폴더 각각 최대 60개";
        if (!_ready)
        {
            Body.Text = "파일 목록 조회 중";
            if (_reading) return;
            _reading = true;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            try { _files = await _vm.QuestionFilesAsync(_folder, budget.Token); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or System.Security.SecurityException)
            { _files = null; }
            _ready = true;
            if (!_lifetime.IsCancellationRequested) UpdateQuestion();
            return;
        }
        _input = FolderQuestions.BuildInput(_folder, _files, Question);
        Body.Text = _input;
        if (_files is null) Status.Text = "현재 파일 목록 조회 불가";
        Send.IsEnabled = true;
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_input is not { } input || _sending) return;
        _sending = true;
        var question = Question;
        var token = _lifetime.Token;
        Send.IsEnabled = DeleteChoice.IsEnabled = PurposeChoice.IsEnabled = false;
        Status.Text = "답변 요청 중";
        try
        {
            var answer = await FolderQuestions.AskAsync(input, token);
            token.ThrowIfCancellationRequested();
            _input = null;
            Body.Text = answer;
            SectionTitle.Text = "OpenAI 답변";
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
            Send.IsEnabled = _input is not null;
            DeleteChoice.IsEnabled = PurposeChoice.IsEnabled = true;
        }
    }
}
