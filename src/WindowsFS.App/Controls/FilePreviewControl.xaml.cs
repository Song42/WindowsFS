using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WindowsFS.App.Services;

namespace WindowsFS.App.Controls;

public partial class FilePreviewControl : UserControl
{
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(nameof(Preview), typeof(FilePreview),
        typeof(FilePreviewControl), new PropertyMetadata(null, (owner, _) => ((FilePreviewControl)owner).ReleaseMedia()));
    public FilePreview? Preview { get => (FilePreview?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Func<IMediaPreviewPlayer> _createPlayer;
    private DateTimeOffset _openingDeadline;
    private bool _opening, _playing, _ended, _dragging, _updatingPosition;
    private TimeSpan? _duration;
    private IMediaPreviewPlayer? _player;
    private string MediaLabel => Preview?.HasAudio == true ? "오디오" : "영상";

    public FilePreviewControl() : this(() => new MediaPreviewPlayer()) { }

    internal FilePreviewControl(Func<IMediaPreviewPlayer> createPlayer)
    {
        _createPlayer = createPlayer;
        InitializeComponent();
        PlaybackPosition.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SeekStarted));
        PlaybackPosition.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SeekCompleted));
        _timer.Tick += (_, _) => UpdatePlayback();
    }

    internal void UpdatePlayback()
    {
        if (_player is null) { _timer.Stop(); return; }
        if (_opening && DateTimeOffset.UtcNow > _openingDeadline)
        { ReleaseMedia(); MediaStatus.Text = $"{MediaLabel} 읽기 시간 초과"; return; }
        if (!_opening && !_dragging) ShowPosition(_ended ? _duration ?? _player.Position : _player.Position);
    }

    private void PlayClick(object sender, RoutedEventArgs e) { if (IsVisible) PlayMedia(); }

    internal void PlayMedia()
    {
        if (Preview?.MediaPath is not { } path) return;
        try
        {
            if (_player is null)
            {
                if (Preview.HasAudio) FilePreviewReader.ValidateAudioFile(path);
                else FilePreviewReader.ValidateVideoFile(path);
                // Selection alone never creates a decoder or plays sound. No shell execution or web source.
                _player = _createPlayer();
                _player.Opened += MediaOpened;
                _player.Ended += MediaEnded;
                _player.Failed += MediaFailed;
                MediaHost.Child = _player.View;
                _opening = true;
                _openingDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
                _playing = true;
                _player.Open(path);
            }
            if (_player is null) return;
            if (_ended) { _player.Position = TimeSpan.Zero; _ended = false; ShowPosition(TimeSpan.Zero); }
            _playing = true;
            MediaStatus.Text = $"{MediaLabel} 재생 중";
            _player.Play();
            _timer.Start();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or InvalidOperationException)
        { ReleaseMedia(); MediaStatus.Text = $"{MediaLabel} 접근 불가"; }
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        _playing = false;
        _player.Pause();
        if (!_opening) { _timer.Stop(); UpdatePlayback(); }
        MediaStatus.Text = "일시정지";
    }

    private void RestartClick(object sender, RoutedEventArgs e)
    {
        if (_player is null || _opening || _duration is null) { ReleaseMedia(); return; }
        _playing = false; _ended = false;
        _player.Pause(); _timer.Stop(); Seek(0);
    }

    private void MediaOpened(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player)) return;
        _opening = false;
        _duration = _player!.Duration is { Ticks: > 0 } duration ? duration : null;
        _updatingPosition = true;
        PlaybackPosition.Maximum = _duration?.TotalSeconds ?? 1;
        PlaybackPosition.IsEnabled = _duration is not null;
        TotalPlaybackTime.Text = _duration is { } total ? FormatTime(total) : "--:--";
        _updatingPosition = false;
        ShowPosition(_player.Position);
        if (!_playing) { _player.Pause(); _timer.Stop(); }
    }

    private void MediaEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player)) return;
        _playing = false; _ended = true;
        _timer.Stop(); _player!.Pause();
        ShowPosition(_duration ?? _player.Position);
        MediaStatus.Text = "재생 완료";
    }

    private void MediaFailed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player)) return;
        ReleaseMedia(); MediaStatus.Text = $"{MediaLabel} 미지원 코덱 또는 손상된 파일";
    }

    private void SeekStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void SeekCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        if (e.Canceled) UpdatePlayback();
        else Seek(PlaybackPosition.Value);
    }

    private void SeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPosition || _duration is null) return;
        CurrentPlaybackTime.Text = FormatTime(SeekPosition(e.NewValue));
        // Commit thumb dragging once; track clicks and keyboard navigation seek immediately.
        if (!_dragging) Seek(e.NewValue);
    }

    private void Seek(double seconds)
    {
        if (_player is null || _opening || _duration is null) return;
        var position = SeekPosition(seconds);
        try
        {
            _player.Position = position;
            _ended = false;
            ShowPosition(position);
            MediaStatus.Text = _playing ? $"{MediaLabel} 재생 중" : "일시정지";
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            PlaybackPosition.IsEnabled = false;
            ShowPosition(_player.Position);
            MediaStatus.Text = "재생 위치 이동 미지원";
        }
    }

    private TimeSpan SeekPosition(double seconds) => _duration is { } duration && seconds >= duration.TotalSeconds
        ? duration : TimeSpan.FromSeconds(Math.Max(0, seconds));

    private void ShowPosition(TimeSpan position)
    {
        position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (_duration is { } duration && position > duration) position = duration;
        _updatingPosition = true;
        if (_duration is not null) PlaybackPosition.Value = position.TotalSeconds;
        CurrentPlaybackTime.Text = FormatTime(position);
        _updatingPosition = false;
    }

    internal static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{(long)time.TotalMinutes:00}:{time.Seconds:00}";

    private void PreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!IsVisible) ReleaseMedia(); }
    private void PreviewUnloaded(object sender, RoutedEventArgs e) => ReleaseMedia();

    private void ReleaseMedia()
    {
        _timer.Stop();
        _opening = false; _playing = false; _ended = false; _dragging = false; _duration = null;
        if (_player is { } player)
        {
            _player = null;
            player.Opened -= MediaOpened;
            player.Ended -= MediaEnded;
            player.Failed -= MediaFailed;
            player.Close();
            MediaHost.Child = null;
        }
        if (PlaybackPosition is not null)
        {
            _updatingPosition = true;
            PlaybackPosition.IsEnabled = false; PlaybackPosition.Value = 0; PlaybackPosition.Maximum = 1;
            CurrentPlaybackTime.Text = "00:00"; TotalPlaybackTime.Text = "--:--";
            _updatingPosition = false;
        }
        if (MediaStatus is not null) MediaStatus.Text = "";
    }
}
