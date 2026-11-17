using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsFS.App.Services;

namespace WindowsFS.App.Controls;

public partial class FilePreviewControl : UserControl
{
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(nameof(Preview), typeof(FilePreview),
        typeof(FilePreviewControl), new PropertyMetadata(null, (owner, _) => ((FilePreviewControl)owner).ReleaseVideo()));
    public FilePreview? Preview { get => (FilePreview?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTimeOffset _openingDeadline;
    private DateTimeOffset _playDeadline;
    private bool _opening;

    public FilePreviewControl()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (_opening && DateTimeOffset.UtcNow > _openingDeadline)
            { ReleaseVideo(); VideoStatus.Text = "영상 읽기 시간 초과"; return; }
            if (!_opening && (VideoPlayer.Position >= FilePreviewReader.MaximumVideoDuration || DateTimeOffset.UtcNow >= _playDeadline))
            { ReleaseVideo(); VideoStatus.Text = "30초 미리보기 완료"; }
        };
    }

    private void PlayClick(object sender, RoutedEventArgs e)
    {
        if (!IsVisible || Preview?.VideoPath is not { } path) return;
        try
        {
            if (VideoPlayer.Source is null)
            {
                FilePreviewReader.ValidateVideoFile(path);
                // Only an explicitly selected local video reaches the native read-only decoder. No shell execution or web source.
                VideoPlayer.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                _opening = true;
                _openingDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            }
            VideoStatus.Text = "영상 미리보기 · 처음 30초";
            _playDeadline = DateTimeOffset.UtcNow.AddSeconds(30 - Math.Clamp(VideoPlayer.Position.TotalSeconds, 0, 30));
            VideoPlayer.Play();
            _timer.Start();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { ReleaseVideo(); VideoStatus.Text = "영상 접근 불가"; }
    }

    private void PauseClick(object sender, RoutedEventArgs e) { VideoPlayer.Pause(); if (!_opening) _timer.Stop(); }
    private void RestartClick(object sender, RoutedEventArgs e) { ReleaseVideo(); VideoStatus.Text = "영상 미리보기 · 처음 30초"; }
    private void VideoOpened(object sender, RoutedEventArgs e) => _opening = false;
    private void VideoEnded(object sender, RoutedEventArgs e) { ReleaseVideo(); VideoStatus.Text = "미리보기 완료"; }
    private void VideoFailed(object sender, ExceptionRoutedEventArgs e) { ReleaseVideo(); VideoStatus.Text = "영상 미지원 코덱 또는 손상된 파일"; }
    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!IsVisible) ReleaseVideo(); }
    private void PreviewUnloaded(object sender, RoutedEventArgs e) => ReleaseVideo();
    private void ReleaseVideo()
    {
        _timer.Stop();
        _opening = false;
        if (VideoPlayer is null) return;
        VideoPlayer.Close();
        VideoPlayer.Source = null;
        VideoStatus.Text = "";
    }
}
