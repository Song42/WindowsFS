using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using WindowsFS.App.Services;

namespace WindowsFS.App.ViewModels;

public sealed class FolderQuestionSection : INotifyPropertyChanged
{
    private readonly Func<Task<string>> _answer;
    private bool _expanded;
    private bool _answered;

    internal FolderQuestionSection(string question, Func<Task<string>> answer)
    {
        Question = question;
        _answer = answer;
    }

    public string Question { get; }
    public string RequestStatus { get; private set; } = "";
    internal void SetRequestStatus(string status)
    {
        if (RequestStatus == status) return;
        RequestStatus = status;
        Changed(nameof(RequestStatus));
    }

    public string Text { get; private set; } = "";
    public bool IsLoading { get; private set; }
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Changed(nameof(IsExpanded));
            if (value && !_answered && !IsLoading) Pending = LoadAsync();
        }
    }

    internal Task Pending { get; private set; } = Task.CompletedTask;
    private async Task LoadAsync()
    {
        Text = "";
        IsLoading = true;
        Changed(nameof(Text));
        Changed(nameof(IsLoading));
        try
        {
            Text = await _answer();
            _answered = true;
        }
        catch (FolderQuestionException ex) { Text = ex.Message; }
        catch (OperationCanceledException) { Text = "요청 중단 또는 시간 초과"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or System.Security.SecurityException)
        { Text = "요청 실패 · API 키 설정 및 연결 확인 필요"; }
        finally
        {
            IsLoading = false;
            Changed(nameof(Text));
            Changed(nameof(IsLoading));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
