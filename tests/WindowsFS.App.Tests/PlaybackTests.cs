using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static Task MediaPlaybackTimeline()
    {
        using var fixture = new PickerFixture();
        var audio = Path.Combine(fixture.Root, "timeline.mp3");
        var video = Path.Combine(fixture.Root, "timeline.mp4");
        File.WriteAllBytes(audio, Mp3Frames());
        File.WriteAllBytes(video, [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0]);
        var before = CapturePickerTree(fixture.Root);
        foreach (var preview in new[] { new FilePreview(AudioPath: audio), new FilePreview(VideoPath: video) })
        {
            var player = new StubMediaPlayer { Duration = TimeSpan.FromMinutes(3) };
            var created = 0;
            var control = new FilePreviewControl(() => { created++; return player; }) { Preview = preview };
            try
            {
                Render(control);
                var slider = (Slider)control.FindName("PlaybackPosition");
                var current = (TextBlock)control.FindName("CurrentPlaybackTime");
                var total = (TextBlock)control.FindName("TotalPlaybackTime");
                Equal(0, created);
                Check(!slider.IsEnabled && slider.IsMoveToPointEnabled, "Timeline initialized before explicit playback or disabled track clicks.");
                control.PlayMedia(); // Injected fake only: never create a MediaElement or play sound.
                player.RaiseOpened();
                Equal(1, created); Equal(preview.MediaPath!, player.OpenedPath!);
                Check(slider.IsEnabled, "Known duration did not enable seeking.");
                Equal(180d, slider.Maximum); Equal("03:00", total.Text);
                player.AdvanceTo(TimeSpan.FromSeconds(65));
                control.UpdatePlayback();
                Equal("01:05", current.Text); Equal(65d, slider.Value);
                Check(player.Playing && player.Closes == 0, "Playback still stopped after 30 seconds.");
                Equal(0, player.Seeks); // Timer synchronization must not seek back into the decoder.
                slider.Value = 90;
                Equal(TimeSpan.FromSeconds(90), player.Position); Equal(1, player.Seeks);
                slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                slider.Value = 120;
                player.AdvanceTo(TimeSpan.FromSeconds(91));
                control.UpdatePlayback();
                Equal(120d, slider.Value); Equal("02:00", current.Text); Equal(1, player.Seeks);
                slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Equal(TimeSpan.FromSeconds(120), player.Position); Equal(2, player.Seeks);
                slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                slider.Value = 150;
                slider.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
                Equal(120d, slider.Value); Equal(2, player.Seeks);
                ((Button)control.FindName("PauseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                slider.Value = 50;
                Check(!player.Playing, "Seeking while paused resumed audio/video.");
                Equal(TimeSpan.FromSeconds(50), player.Position);
                control.PlayMedia();
                Check(player.Playing, "Paused media did not resume.");
                player.RaiseEnded();
                Equal("03:00", current.Text); Equal(180d, slider.Value);
                Check(!player.Playing && slider.IsEnabled && player.Closes == 0, "End-of-file discarded the seekable timeline.");
                control.PlayMedia();
                Equal(TimeSpan.Zero, player.Position);
                Check(player.Playing, "End-of-file replay did not restart.");
                player.AdvanceTo(TimeSpan.FromSeconds(60));
                ((Button)control.FindName("RestartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Equal(TimeSpan.Zero, player.Position); Equal("00:00", current.Text); Equal("03:00", total.Text);
                Check(!player.Playing, "Restart did not leave media paused at the beginning.");
                player.RejectSeek = true;
                slider.Value = 80;
                Check(!slider.IsEnabled && ((TextBlock)control.FindName("MediaStatus")).Text.Contains("미지원"), "Unsupported seeking escaped as an error.");
                control.Preview = new FilePreview();
                Equal(1, player.Closes); Equal("00:00", current.Text); Equal("--:--", total.Text);
                Check(!slider.IsEnabled && slider.Value == 0 && ((Border)control.FindName("MediaHost")).Child is null, "Selection change retained media resources or timeline.");
                player.RaiseOpened(); player.RaiseEnded();
                Equal("--:--", total.Text);
                Check(!control.IsVisible, "Playback tests must never display a window.");
            }
            finally { control.Preview = new FilePreview(); }
        }
        Equal("01:05", FilePreviewControl.FormatTime(TimeSpan.FromSeconds(65)));
        Equal("1:02:03", FilePreviewControl.FormatTime(new TimeSpan(1, 2, 3)));
        Equal("27:02:03", FilePreviewControl.FormatTime(new TimeSpan(27, 2, 3)));
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Timeline operations changed source fixtures.");
        return Task.CompletedTask;
    }

    private static Task UnknownMediaDuration()
    {
        using var fixture = new PickerFixture();
        var audio = Path.Combine(fixture.Root, "unknown.mp3");
        File.WriteAllBytes(audio, Mp3Frames());
        var player = new StubMediaPlayer();
        var control = new FilePreviewControl(() => player) { Preview = new FilePreview(AudioPath: audio) };
        try
        {
            control.PlayMedia();
            ((Button)control.FindName("PauseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            player.RaiseOpened();
            Check(!player.Playing, "Opening media resumed playback after a pending pause.");
            control.PlayMedia();
            player.AdvanceTo(TimeSpan.FromSeconds(95)); control.UpdatePlayback();
            Equal("01:35", ((TextBlock)control.FindName("CurrentPlaybackTime")).Text);
            Equal("--:--", ((TextBlock)control.FindName("TotalPlaybackTime")).Text);
            Check(!((Slider)control.FindName("PlaybackPosition")).IsEnabled && player.Playing, "Unknown duration enabled seeking or limited playback.");
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Equal(1, player.Closes);
            Check(!player.Playing, "Unloading the preview retained playback.");
            control.PlayMedia(); player.RaiseFailed();
            Equal(2, player.Closes);
            Check(!((Slider)control.FindName("PlaybackPosition")).IsEnabled, "Decoder failure retained the timeline.");
        }
        finally { control.Preview = new FilePreview(); }
        return Task.CompletedTask;
    }

    private sealed class StubMediaPlayer : IMediaPreviewPlayer
    {
        private TimeSpan _position;
        public FrameworkElement View { get; } = new Border();
        public TimeSpan Position { get => _position; set { if (RejectSeek) throw new NotSupportedException(); _position = value; Seeks++; } }
        public TimeSpan? Duration { get; set; }
        public bool Playing { get; private set; }
        public bool RejectSeek { get; set; }
        public int Seeks { get; private set; }
        public int Closes { get; private set; }
        public string? OpenedPath { get; private set; }
        public event EventHandler? Opened;
        public event EventHandler? Ended;
        public event EventHandler? Failed;
        public void Open(string path) => OpenedPath = path;
        public void Play() => Playing = true;
        public void Pause() => Playing = false;
        public void Close() { Playing = false; Closes++; }
        public void AdvanceTo(TimeSpan position) => _position = position;
        public void RaiseOpened() => Opened?.Invoke(this, EventArgs.Empty);
        public void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);
        public void RaiseFailed() => Failed?.Invoke(this, EventArgs.Empty);
    }
}
