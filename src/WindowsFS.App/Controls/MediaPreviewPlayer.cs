using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsFS.App.Controls;

// Test playback and seeking without audio output or native media initialization.
internal interface IMediaPreviewPlayer
{
    FrameworkElement View { get; }
    TimeSpan Position { get; set; }
    TimeSpan? Duration { get; }
    event EventHandler? Opened;
    event EventHandler? Ended;
    event EventHandler? Failed;
    void Open(string path);
    void Play();
    void Pause();
    void Close();
}

internal sealed class MediaPreviewPlayer : IMediaPreviewPlayer
{
    private readonly MediaElement _element = new()
    { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, ScrubbingEnabled = true };

    public MediaPreviewPlayer()
    {
        _element.MediaOpened += (_, _) => Opened?.Invoke(this, EventArgs.Empty);
        _element.MediaEnded += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
        _element.MediaFailed += (_, _) => Failed?.Invoke(this, EventArgs.Empty);
    }

    public FrameworkElement View => _element;
    public TimeSpan Position { get => _element.Position; set => _element.Position = value; }
    public TimeSpan? Duration => _element.NaturalDuration.HasTimeSpan ? _element.NaturalDuration.TimeSpan : null;
    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler? Failed;
    public void Open(string path) => _element.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
    public void Play() => _element.Play();
    public void Pause() => _element.Pause();
    public void Close() { _element.Close(); _element.Source = null; }
}
