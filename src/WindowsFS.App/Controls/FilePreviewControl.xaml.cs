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
        typeof(FilePreviewControl), new PropertyMetadata(null, (owner, _) => ((FilePreviewControl)owner).ReleaseMedia()));
    public FilePreview? Preview { get => (FilePreview?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTimeOffset _openingDeadline;
    private DateTimeOffset _playDeadline;
    private bool _opening;
    private MediaElement? _player;
    private string MediaLabel => Preview?.HasAudio == true ? "오디오" : "영상";

    public FilePreviewControl()
    {
        InitializeComponent();
        _timer.Tick += (_, _) =>
        {
            if (_player is null) { _timer.Stop(); return; }
            if (_opening && DateTimeOffset.UtcNow > _openingDeadline)
            { ReleaseMedia(); MediaStatus.Text = $"{MediaLabel} 읽기 시간 초과"; return; }
            if (!_opening && (_player.Position >= FilePreviewReader.MaximumPlaybackDuration || DateTimeOffset.UtcNow >= _playDeadline))
            { ReleaseMedia(); MediaStatus.Text = "30초 미리보기 완료"; }
        };
    }

    private void PlayClick(object sender, RoutedEventArgs e)
    {
        if (!IsVisible || Preview?.MediaPath is not { } path) return;
        try
        {
            if (_player is null)
            {
                if (Preview.HasAudio) FilePreviewReader.ValidateAudioFile(path);
                else FilePreviewReader.ValidateVideoFile(path);
                // Only explicitly played local media reaches the decoder. No shell execution or web source.
                _player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
                _player.MediaOpened += MediaOpened;
                _player.MediaEnded += MediaEnded;
                _player.MediaFailed += MediaFailed;
                MediaHost.Child = _player;
                _player.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                _opening = true;
                _openingDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            }
            MediaStatus.Text = $"{MediaLabel} 미리보기 · 처음 30초";
            _playDeadline = DateTimeOffset.UtcNow.AddSeconds(30 - Math.Clamp(_player.Position.TotalSeconds, 0, 30));
            _player.Play();
            _timer.Start();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { ReleaseMedia(); MediaStatus.Text = $"{MediaLabel} 접근 불가"; }
    }

    private void PauseClick(object sender, RoutedEventArgs e) { _player?.Pause(); if (!_opening) _timer.Stop(); }
    private void RestartClick(object sender, RoutedEventArgs e) { ReleaseMedia(); MediaStatus.Text = $"{MediaLabel} 미리보기 · 처음 30초"; }
    private void MediaOpened(object sender, RoutedEventArgs e) { if (ReferenceEquals(sender, _player)) _opening = false; }
    private void MediaEnded(object sender, RoutedEventArgs e) { if (ReferenceEquals(sender, _player)) { ReleaseMedia(); MediaStatus.Text = "미리보기 완료"; } }
    private void MediaFailed(object? sender, ExceptionRoutedEventArgs e) { if (ReferenceEquals(sender, _player)) { ReleaseMedia(); MediaStatus.Text = $"{MediaLabel} 미지원 코덱 또는 손상된 파일"; } }
    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!IsVisible) ReleaseMedia(); }
    private void PreviewUnloaded(object sender, RoutedEventArgs e) => ReleaseMedia();
    private void ReleaseMedia()
    {
        _timer.Stop();
        _opening = false;
        // No MediaElement exists before explicit playback; ordinary file selection never initializes native media.
        if (_player is { } player)
        {
            _player = null;
            player.MediaOpened -= MediaOpened;
            player.MediaEnded -= MediaEnded;
            player.MediaFailed -= MediaFailed;
            player.Close();
            player.Source = null;
            MediaHost.Child = null;
        }
        if (MediaStatus is not null) MediaStatus.Text = "";
    }
}
