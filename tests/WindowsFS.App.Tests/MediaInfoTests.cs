using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static async Task NativeMediaInfo()
    {
        using var fixture = new PickerFixture();
        var wav = Path.Combine(fixture.Root, "stereo.wav");
        using (var output = new BinaryWriter(File.Create(wav)))
        {
            output.Write("RIFF"u8); output.Write(192036); output.Write("WAVEfmt "u8); output.Write(16);
            output.Write((short)1); output.Write((short)2); output.Write(48000); output.Write(192000);
            output.Write((short)4); output.Write((short)16); output.Write("data"u8); output.Write(192000);
            output.Write(new byte[192000]);
        }
        var mp3 = Path.Combine(fixture.Root, "frames.mp3");
        File.WriteAllBytes(mp3, Enumerable.Range(0, 30).SelectMany(_ => Mp3Frames()).ToArray());
        var avi = Path.Combine(fixture.Root, "video.avi");
        File.WriteAllBytes(avi, SyntheticAvi());
        var before = CapturePickerTree(fixture.Root);
        var reader = new MediaInfoReader();
        var result = await reader.ReadAsync(PreviewRow(wav), CancellationToken.None);
        Check(result.Groups.Count >= 2, "Native WAV metadata missing: " + result.Status);
        var audio = result.Groups.Single(group => group.Title == "오디오 1");
        Check(audio.Fields.Any(field => field.Value.Contains("PCM")), "WAV codec missing.");
        Check(audio.Fields.Any(field => field.Label.Contains("Sampling rate") && field.Value.Contains("48.0")), "Sample rate missing.");
        Check(audio.Fields.Any(field => field.Label.Contains("Channel") && field.Value.Contains('2')), "Channel count missing.");
        Check(audio.Fields.Any(field => field.Label.Contains("Bit depth") && field.Value.Contains("16")), "Bit depth missing.");
        Check(audio.Fields.Any(field => field.Label == "Duration" && field.Value.Contains("1 s")), "Duration missing.");
        var compressed = await reader.ReadAsync(PreviewRow(mp3), CancellationToken.None);
        Check(compressed.Groups.Any(group => group.Title == "오디오 1" && group.Fields.Any(field => field.Value.Contains("MPEG Audio"))), "MP3 codec missing: " + compressed.Status);
        var video = await reader.ReadAsync(PreviewRow(avi), CancellationToken.None);
        var track = video.Groups.SingleOrDefault(group => group.Title == "영상 1");
        Check(track is not null && track.Fields.Any(field => field.Label == "Width" && field.Value.Contains("320")) &&
            track.Fields.Any(field => field.Label == "Height" && field.Value.Contains("240")), "AVI dimensions missing: " + video.Status);
        Check(track!.Fields.Any(field => field.Label == "Frame rate" && field.Value.Contains("30")), "AVI frame rate missing.");
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "MediaInfo changed the source tree.");
    }

    private static byte[] SyntheticAvi()
    {
        static byte[] Chunk(string name, Action<BinaryWriter> write)
        {
            using var body = new MemoryStream(); using (var writer = new BinaryWriter(body, Encoding.ASCII, true)) write(writer);
            using var chunk = new MemoryStream(); using var output = new BinaryWriter(chunk);
            output.Write(Encoding.ASCII.GetBytes(name)); output.Write((int)body.Length); output.Write(body.ToArray());
            if (body.Length % 2 != 0) output.Write((byte)0);
            return chunk.ToArray();
        }
        return Chunk("RIFF", w =>
        {
            w.Write("AVI "u8);
            w.Write(Chunk("LIST", hdrl =>
            {
                hdrl.Write("hdrl"u8);
                hdrl.Write(Chunk("avih", a => { a.Write(33333); a.Write(6912000); a.Write(0); a.Write(0); a.Write(30); a.Write(0); a.Write(1); a.Write(230400); a.Write(320); a.Write(240); a.Write(new byte[16]); }));
                hdrl.Write(Chunk("LIST", strl =>
                {
                    strl.Write("strl"u8);
                    strl.Write(Chunk("strh", s => { s.Write("vidsDIB "u8); s.Write(0); s.Write(0); s.Write(0); s.Write(1); s.Write(30); s.Write(0); s.Write(30); s.Write(230400); s.Write(-1); s.Write(0); s.Write((short)0); s.Write((short)0); s.Write((short)320); s.Write((short)240); }));
                    strl.Write(Chunk("strf", s => { s.Write(40); s.Write(320); s.Write(240); s.Write((short)1); s.Write((short)24); s.Write(0); s.Write(230400); s.Write(new byte[16]); }));
                }));
            }));
            w.Write(Chunk("LIST", movi => { movi.Write("movi"u8); movi.Write(Chunk("00db", frame => frame.Write(new byte[230400]))); }));
        });
    }

    private static async Task MediaInfoBoundaries()
    {
        using var fixture = new PickerFixture();
        var path = Path.Combine(fixture.Root, "reference.mp4");
        // A genuine ftyp followed by an external data reference, not just a renamed playlist.
        using var memory = new MemoryStream();
        using (var w = new BinaryWriter(memory, Encoding.ASCII, true))
        {
            void WriteBox(uint length, string type) { w.Write(System.Net.IPAddress.HostToNetworkOrder((int)length)); w.Write(Encoding.ASCII.GetBytes(type)); }
            WriteBox(16, "ftyp"); w.Write("isom"u8); w.Write(0);
            WriteBox(32, "moov"); WriteBox(24, "dref"); w.Write(0); w.Write(System.Net.IPAddress.HostToNetworkOrder(1));
            WriteBox(8, "url ");
        }
        var reader = new MediaInfoReader();
        static byte[] Box(string type, params byte[][] contents)
        {
            var data = contents.SelectMany(item => item).ToArray();
            return BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(data.Length + 8))
                .Concat(Encoding.ASCII.GetBytes(type)).Concat(data).ToArray();
        }
        var ftyp = Box("ftyp", "isom"u8.ToArray(), new byte[4]);
        var localReference = Box("dref", new byte[4], [0, 0, 0, 1], Box("url ", [0, 0, 0, 1]));
        var valid = ftyp.Concat(Box("moov", Box("trak", Box("mdia", Box("minf", Box("dinf", localReference)))))).ToArray();
        using (var stream = new MemoryStream(valid)) Equal("Mpeg4", MediaInfoReader.ValidateContainer(stream, CancellationToken.None));
        var external = ftyp.Concat(Box("moov", Box("trak", Box("mdia", Box("minf", Box("code", new byte[16])))))).ToArray();
        var compressed = ftyp.Concat(Box("moov", Box("cmov", new byte[16]))).ToArray();
        foreach (var bytes in new[] { "<asx><entry href='https://invalid.test/media'/></asx>"u8.ToArray(), memory.ToArray(), external, compressed, new byte[16] })
        {
            File.WriteAllBytes(path, bytes);
            var before = CapturePickerTree(fixture.Root);
            var result = await reader.ReadAsync(PreviewRow(path), CancellationToken.None);
            Check(result.Groups.Count == 0 && result.Status.Contains("읽기 불가"), "External or malformed input reached native parsing.");
            Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Rejected media changed the source tree.");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await reader.ReadAsync(PreviewRow(path), cancelled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Check(!MediaInfoReader.Supports(PreviewRow(path) with { IsDirectory = true }), "Folder classified as media.");
        Check(!MediaInfoReader.Supports(PreviewRow("notes.txt")), "Text classified as media.");
    }

    private static async Task MediaInfoSelection()
    {
        var pending = new TaskCompletionSource<MediaInfoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new StubMediaInfoReader { Read = (_, _) => pending.Task };
        var vm = new MainViewModel(new StubScanner((_, _, _) => Task.FromResult(FixtureResult())),
            previewReader: new StubPreviewReader((_, _, _) => Task.FromResult(new FilePreview())), mediaInfoReader: reader);
        vm.SelectedRow = PreviewRow($@"{FixturePath}\first.mp3");
        Check(vm.HasMediaInfo && !vm.MediaInfoExpanded && reader.Calls == 0, "Collapsed section read media.");
        var window = new MainWindow { DataContext = vm };
        try
        {
            Render((FrameworkElement)window.Content);
            var section = (Expander)window.FindName("MediaInfoSection");
            Check(!section.IsExpanded, "XAML default was expanded.");
            section.IsExpanded = true;
            Equal(1, reader.Calls);
            var firstLoad = vm.WaitForMediaInfoAsync();
            var firstToken = reader.LastToken;
            section.IsExpanded = false; section.IsExpanded = true;
            Equal(1, reader.Calls);
            reader.Read = (_, _) => Task.FromResult(new MediaInfoResult([new("오디오 1", [new("Format", "FLAC")])]));
            vm.SelectedRow = PreviewRow($@"{FixturePath}\second.flac");
            await firstLoad;
            Check(firstToken.IsCancellationRequested && vm.MediaInfoExpanded, "Selection did not preserve expansion and cancel old metadata.");
            await vm.WaitForMediaInfoAsync();
            pending.SetResult(new MediaInfoResult([new("stale", [])])); DrainDispatcher();
            Equal("오디오 1", vm.MediaInfoGroups.Single().Title);
            vm.MediaInfoExpanded = false; vm.MediaInfoExpanded = true;
            Equal(2, reader.Calls);
            Render((FrameworkElement)window.Content);
            Check(Descendants<TextBlock>(section).Any(text => text.Text == "FLAC"), "Actual XAML omitted track fields.");
            vm.SelectedRow = PreviewRow($@"{FixturePath}\notes.txt");
            Check(!vm.HasMediaInfo && vm.MediaInfoExpanded && vm.MediaInfoGroups.Count == 0, "Non-media retained metadata or reset expansion.");
            vm.SelectedRow = PreviewRow($@"{FixturePath}\third.flac");
            await vm.WaitForMediaInfoAsync();
            Equal(3, reader.Calls);
            vm.MediaInfoExpanded = false;
            vm.SelectedRow = PreviewRow($@"{FixturePath}\fourth.flac");
            Equal(3, reader.Calls);
            Check(!vm.MediaInfoExpanded, "Explicit collapse did not persist across selection.");
            Check(!window.IsVisible, "Tests must not show windows.");
        }
        finally { window.Close(); }
    }

    private sealed class StubMediaInfoReader : IMediaInfoReader
    {
        public required Func<ResultRow, CancellationToken, Task<MediaInfoResult>> Read { get; set; }
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task<MediaInfoResult> ReadAsync(ResultRow row, CancellationToken token) { Calls++; LastToken = token; return Read(row, token); }
    }
}
