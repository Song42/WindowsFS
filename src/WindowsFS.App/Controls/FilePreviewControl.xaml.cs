using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private MediaElement? _player;

    public FilePreviewControl()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (_player is null) { _timer.Stop(); return; }
            if (_opening && DateTimeOffset.UtcNow > _openingDeadline)
            { ReleaseVideo(); VideoStatus.Text = "영상 읽기 시간 초과"; return; }
            if (!_opening && (_player.Position >= FilePreviewReader.MaximumVideoDuration || DateTimeOffset.UtcNow >= _playDeadline))
            { ReleaseVideo(); VideoStatus.Text = "30초 미리보기 완료"; }
        };
    }

    private void PlayClick(object sender, RoutedEventArgs e)
    {
        if (!IsVisible || Preview?.VideoPath is not { } path) return;
        try
        {
            if (_player is null)
            {
                FilePreviewReader.ValidateVideoFile(path);
                // Only an explicitly selected local video reaches the native read-only decoder. No shell execution or web source.
                _player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
                _player.MediaOpened += VideoOpened;
                _player.MediaEnded += VideoEnded;
                _player.MediaFailed += VideoFailed;
                VideoHost.Child = _player;
                _player.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                _opening = true;
                _openingDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            }
            VideoStatus.Text = "영상 미리보기 · 처음 30초";
            _playDeadline = DateTimeOffset.UtcNow.AddSeconds(30 - Math.Clamp(_player.Position.TotalSeconds, 0, 30));
            _player.Play();
            _timer.Start();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { ReleaseVideo(); VideoStatus.Text = "영상 접근 불가"; }
    }

    private void PauseClick(object sender, RoutedEventArgs e) { _player?.Pause(); if (!_opening) _timer.Stop(); }
    private void RestartClick(object sender, RoutedEventArgs e) { ReleaseVideo(); VideoStatus.Text = "영상 미리보기 · 처음 30초"; }
    private void VideoOpened(object sender, RoutedEventArgs e) { if (ReferenceEquals(sender, _player)) _opening = false; }
    private void VideoEnded(object sender, RoutedEventArgs e) { if (ReferenceEquals(sender, _player)) { ReleaseVideo(); VideoStatus.Text = "미리보기 완료"; } }
    private void VideoFailed(object? sender, ExceptionRoutedEventArgs e) { if (ReferenceEquals(sender, _player)) { ReleaseVideo(); VideoStatus.Text = "영상 미지원 코덱 또는 손상된 파일"; } }
    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!IsVisible) ReleaseVideo(); }
    private void PreviewUnloaded(object sender, RoutedEventArgs e) => ReleaseVideo();
    private void ReleaseVideo()
    {
        _timer.Stop();
        _opening = false;
        // No MediaElement exists before explicit playback; ordinary file selection never initializes native media.
        if (_player is { } player)
        {
            _player = null;
            player.MediaOpened -= VideoOpened;
            player.MediaEnded -= VideoEnded;
            player.MediaFailed -= VideoFailed;
            player.Close();
            player.Source = null;
            VideoHost.Child = null;
        }
        if (VideoStatus is not null) VideoStatus.Text = "";
    }
}
