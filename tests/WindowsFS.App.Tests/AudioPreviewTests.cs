using System.IO;
using System.Windows;
using System.Windows.Controls;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static async Task AudioPreviews()
    {
        using var fixture = new PickerFixture();
        var reader = new FilePreviewReader();
        var raw = Path.Combine(fixture.Root, "audio.MP3");
        var tagged = Path.Combine(fixture.Root, "tagged.mp3");
        var footer = Path.Combine(fixture.Root, "footer.mp3");
        var vbr = Path.Combine(fixture.Root, "vbr.mp3");
        var mpeg2 = Path.Combine(fixture.Root, "mpeg2.mp3");
        var mpeg25 = Path.Combine(fixture.Root, "mpeg25.mp3");
        var frames = Mp3Frames();
        File.WriteAllBytes(raw, frames);
        File.WriteAllBytes(tagged, new byte[] { 73, 68, 51, 3, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0 }.Concat(frames).ToArray());
        File.WriteAllBytes(footer, new byte[] { 73, 68, 51, 4, 0, 0x10, 0, 0, 0, 0, 51, 68, 73, 4, 0, 0x10, 0, 0, 0, 0 }.Concat(frames).ToArray());
        File.WriteAllBytes(vbr, Mp3Frames(variableRate: true));
        File.WriteAllBytes(mpeg2, Mp3Frames(version: 2));
        File.WriteAllBytes(mpeg25, Mp3Frames(version: 0));
        var before = CapturePickerTree(fixture.Root);
        foreach (var path in new[] { raw, tagged, footer, vbr, mpeg2, mpeg25 })
        {
            var result = await reader.ReadAsync(PreviewRow(path), 0, CancellationToken.None);
            Equal(PreviewKind.Audio, FilePreviewReader.KindFor(PreviewRow(path)));
            Check(result.HasAudio && result.HasMedia && !result.HasVideo && result.AudioPath == path && result.MediaPath == path,
                "MP3 did not prepare local audio controls: " + result.Text);
            Check(!result.Text.Contains("30초"), "Audio preview still describes a playback time limit.");
        }
        var vm = new MainViewModel(new StubScanner((_, _, _) => Task.FromResult(FixtureResult())), previewReader: reader);
        vm.SelectedRow = PreviewRow(raw);
        await vm.WaitForPreviewAsync();
        Check(vm.Preview.HasAudio && !vm.HasPreviewPages, "Selecting MP3 did not automatically prepare audio.");
        var control = new FilePreviewControl { Preview = vm.Preview };
        Render(control);
        var host = (Border)control.FindName("MediaHost");
        var controls = (StackPanel)control.FindName("MediaControls");
        Equal(Visibility.Visible, controls.Visibility);
        Equal(0d, host.Height);
        Check(host.Child is null, "Audio selection initialized the player or started playback.");
        Check(!control.IsVisible, "Audio fixture must never appear or play sound.");
        ((Button)control.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Child is null, "Hidden audio controls started playback.");
        control.Preview = new FilePreview(VideoPath: Path.Combine(fixture.Root, "video.mp4"));
        Render(control);
        Equal(220d, host.Height);
        Check(host.Child is null, "Switching to video initialized a native player.");
        control.Preview = new FilePreview();
        Render(control);
        Equal(Visibility.Collapsed, controls.Visibility);
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Audio preview modified the source fixtures.");
    }

    private static async Task AudioPreviewLimits()
    {
        using var fixture = new PickerFixture();
        var reader = new FilePreviewReader();
        var path = Path.Combine(fixture.Root, "invalid.mp3");
        var missingFrame = Mp3Frames()[..420];
        var badFrame = Mp3Frames(); badFrame[417] = 0;
        byte[][] invalid =
        [
            "#EXTM3U\nhttps://invalid.test/audio.mp3"u8.ToArray(),
            "<asx><entry><ref href='https://invalid.test/audio'/></entry></asx>"u8.ToArray(),
            [73, 68, 51, 4, 0, 0, 0, 0, 0, 0], // ID3 alone is not audio.
            [73, 68, 51, 4, 0, 0, 128, 0, 0, 0], // Invalid synchsafe size.
            [73, 68, 51, 4, 0, 0, 127, 127, 127, 127], // Oversized tag.
            missingFrame, badFrame
        ];
        foreach (var bytes in invalid)
        {
            File.WriteAllBytes(path, bytes);
            var before = CapturePickerTree(fixture.Root);
            var result = await reader.ReadAsync(PreviewRow(path), 0, CancellationToken.None);
            Check(!result.HasMedia && result.Text.Contains("미리보기 불가"), "Non-audio or truncated MP3 reached the media player.");
            Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Rejected MP3 was changed.");
        }
        using (var output = File.Create(path)) output.SetLength(FilePreviewReader.MaximumFileBytes + 1);
        var oversized = await reader.ReadAsync(PreviewRow(path), 0, CancellationToken.None);
        Check(!oversized.HasAudio && oversized.Text.Contains("크기 제한"), "Audio size limit was bypassed.");
        Check(!ItemDetailsReader.SupportsPreview(PreviewRow(path) with { IsDirectory = true }), "MP3-named folder was previewable.");
    }

    private static byte[] Mp3Frames(bool variableRate = false, int version = 3)
    {
        // Synthetic Layer III frame boundaries; never sent to an actual audio output device.
        var frequency = version == 3 ? 44100 : version == 2 ? 22050 : 11025;
        var coefficient = version == 3 ? 144 : 72;
        var firstRate = version == 3 ? 128000 : 80000;
        var secondRate = variableRate ? 160000 : firstRate;
        var firstLength = coefficient * firstRate / frequency;
        var secondLength = coefficient * secondRate / frequency;
        var bytes = new byte[firstLength + secondLength];
        foreach (var offset in new[] { 0, firstLength })
        {
            bytes[offset] = 0xff;
            bytes[offset + 1] = (byte)(0xe3 | (version << 3));
            bytes[offset + 2] = (byte)(offset > 0 && variableRate ? 0xa0 : 0x90);
        }
        return bytes;
    }
}
