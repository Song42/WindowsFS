using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsFS.App;
using WindowsFS.App.Controls;
using WindowsFS.App.Services;
using WindowsFS.App.ViewModels;
using WindowsFS.Core;

namespace WindowsFS.App.Tests;

internal static partial class Program
{
    private static ResultRow PreviewRow(string path) => new(Path.GetFileName(path), path, 0, -1, false, Path.GetExtension(path), 0);

    private static async Task ImageAndPdfPreviews()
    {
        using var fixture = new PickerFixture();
        var png = Path.Combine(fixture.Root, "image.png");
        var encoder = new PngBitmapEncoder();
        var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, 8);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(png)) encoder.Save(output);
        var pdf = Path.Combine(fixture.Root, "pages.pdf");
        WritePreviewPdf(pdf);
        var before = CapturePickerTree(fixture.Root);
        var reader = new FilePreviewReader();
        var image = await reader.ReadAsync(PreviewRow(png), 0, CancellationToken.None);
        Check(image.Image is { IsFrozen: true, PixelWidth: 2, PixelHeight: 1 }, "PNG preview did not decode to a detached frozen bitmap.");
        var first = await reader.ReadAsync(PreviewRow(pdf), 0, CancellationToken.None);
        var second = await reader.ReadAsync(PreviewRow(pdf), 1, CancellationToken.None);
        Equal(2, first.PageCount);
        Equal(0, first.PageIndex);
        Equal(1, second.PageIndex);
        Check(first.Image is { IsFrozen: true } && second.Image is { IsFrozen: true }, "PDF pages did not render to images.");
        Check(first.Image!.PixelWidth <= 1600 && first.Image.PixelHeight <= 1600, "PDF preview exceeded its render bounds.");
        static byte[] Pixel(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            converted.CopyPixels(new Int32Rect(converted.PixelWidth / 2, converted.PixelHeight / 2, 1, 1), pixel, 4, 0);
            return pixel;
        }
        Check(!Pixel(first.Image).SequenceEqual(Pixel(second.Image!)), "PDF navigation returned the same page twice.");
        var last = await reader.ReadAsync(PreviewRow(pdf), int.MaxValue, CancellationToken.None);
        Equal(1, last.PageIndex);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Check(!Pixel(first.Image).SequenceEqual(Pixel(second.Image!)), "Rendered pages retained disposed decoder streams.");
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Image or PDF preview changed the source files.");
    }

    private static void WritePreviewPdf(string path)
    {
        using var output = new MemoryStream();
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        string[] bodies =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>",
            "",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 6 0 R >>",
            ""
        ];
        var offsets = new List<long>();
        for (var index = 0; index < bodies.Length; index++)
        {
            offsets.Add(output.Position);
            Write($"{index + 1} 0 obj\n");
            if (index is 3 or 5)
            {
                var drawing = index == 3 ? "1 0 0 rg 0 0 200 100 re f\n" : "0 0 1 rg 0 0 200 100 re f\n";
                Write($"<< /Length {drawing.Length} >>\nstream\n{drawing}endstream");
            }
            else Write(bodies[index]);
            Write("\nendobj\n");
        }
        var xref = output.Position;
        Write("xref\n0 7\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void WritePreviewPackage(string path, params (string Name, string Xml)[] parts)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var part in parts)
        {
            using var writer = new StreamWriter(archive.CreateEntry(part.Name).Open(), new UTF8Encoding(false));
            writer.Write(part.Xml);
        }
    }

    private static async Task DocumentPreviews()
    {
        using var fixture = new PickerFixture();
        var word = Path.Combine(fixture.Root, "word.docx");
        WritePreviewPackage(word, ("word/document.xml", "<w:document xmlns:w='urn:word'><w:body><w:p><w:r><w:t>문서 본문</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>표 내용</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>"));
        var sheet = Path.Combine(fixture.Root, "table.xlsx");
        WritePreviewPackage(sheet,
            ("xl/workbook.xml", "<workbook xmlns:r='urn:rels'><sheets><sheet name='Sheet A' r:id='r1'/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>"),
            ("xl/sharedStrings.xml", "<sst><si><t>공유 문자열</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c r='A1' t='s'><v>0</v></c><c r='B1'><f>EXTERNAL()</f><v>42</v></c><c r='C1' t='inlineStr'><is><t>inline</t></is></c></row></sheetData></worksheet>"));
        var slides = Path.Combine(fixture.Root, "slides.pptx");
        WritePreviewPackage(slides,
            ("ppt/presentation.xml", "<presentation xmlns:r='urn:rels'><sldIdLst><sldId id='9' r:id='r2'/><sldId id='8' r:id='r1'/></sldIdLst></presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='r1' Target='slides/slide1.xml'/><Relationship Id='r2' Target='slides/slide2.xml'/></Relationships>"),
            ("ppt/slides/slide1.xml", "<slide><p><r><t>Second</t></r></p></slide>"),
            ("ppt/slides/slide2.xml", "<slide><p><r><t>First</t></r></p></slide>"));
        var hangul = Path.Combine(fixture.Root, "hangul.hwpx");
        WritePreviewPackage(hangul, ("Contents/section10.xml", "<section><p><run><t>Section ten</t></run></p></section>"),
            ("Contents/section2.xml", "<section><p><run><t>Section two</t></run></p></section>"));
        var odt = Path.Combine(fixture.Root, "open.odt");
        WritePreviewPackage(odt, ("content.xml", "<document><h>Title</h><p>Open document text</p></document>"));
        var before = CapturePickerTree(fixture.Root);
        var reader = new FilePreviewReader();
        var wordText = (await reader.ReadAsync(PreviewRow(word), 0, CancellationToken.None)).Text;
        Check(wordText.Contains("문서 본문") && wordText.Contains("표 내용"), "DOCX body/table text was missing.");
        var sheetText = (await reader.ReadAsync(PreviewRow(sheet), 0, CancellationToken.None)).Text;
        Check(sheetText.Contains("Sheet A") && sheetText.Contains("A1: 공유 문자열") && sheetText.Contains("B1: 42") && sheetText.Contains("C1: inline") && !sheetText.Contains("EXTERNAL"), "Spreadsheet values or shared strings were incorrect.");
        var slideText = (await reader.ReadAsync(PreviewRow(slides), 0, CancellationToken.None)).Text;
        Check(slideText.IndexOf("First", StringComparison.Ordinal) >= 0 && slideText.IndexOf("First", StringComparison.Ordinal) < slideText.IndexOf("Second", StringComparison.Ordinal), "Presentation order was not preserved.");
        var hangulText = (await reader.ReadAsync(PreviewRow(hangul), 0, CancellationToken.None)).Text;
        Check(hangulText.IndexOf("Section two", StringComparison.Ordinal) >= 0 && hangulText.IndexOf("Section two", StringComparison.Ordinal) < hangulText.IndexOf("Section ten", StringComparison.Ordinal), "HWPX section order was incorrect.");
        Check((await reader.ReadAsync(PreviewRow(odt), 0, CancellationToken.None)).Text.Contains("Open document text"), "ODT text was missing.");
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Document previews modified or extracted files.");
    }

    private static async Task PreviewLimits()
    {
        using var fixture = new PickerFixture();
        var reader = new FilePreviewReader();
        foreach (var extension in new[] { ".png", ".pdf", ".docx" })
        {
            var path = Path.Combine(fixture.Root, "broken" + extension);
            File.WriteAllText(path, "not a valid document");
            var preview = await reader.ReadAsync(PreviewRow(path), 0, CancellationToken.None);
            Check(preview.Text.Contains("미리보기 불가") && !preview.HasImage, "Malformed data was not handled.");
        }
        var xml = Path.Combine(fixture.Root, "entity.docx");
        WritePreviewPackage(xml, ("word/document.xml", "<!DOCTYPE document [<!ENTITY external SYSTEM 'file:///never-read'>]><document><p><t>&external;</t></p></document>"));
        Check((await reader.ReadAsync(PreviewRow(xml), 0, CancellationToken.None)).Text.Contains("미리보기 불가"), "External XML entities were accepted.");
        var bomb = Path.Combine(fixture.Root, "expanded.docx");
        WritePreviewPackage(bomb, ("word/document.xml", "<document><p><t>" + new string('x', 3 * 1024 * 1024) + "</t></p></document>"));
        Check((await reader.ReadAsync(PreviewRow(bomb), 0, CancellationToken.None)).Text.Contains("미리보기 불가"), "Oversized ZIP expansion was accepted.");
        var limited = Path.Combine(fixture.Root, "limited.docx");
        WritePreviewPackage(limited, ("word/document.xml", "<document><p><t>" + new string('x', 80_000) + "</t></p></document>"));
        var text = (await reader.ReadAsync(PreviewRow(limited), 0, CancellationToken.None)).Text;
        Check(text.Length < 66_000 && text.Contains("범위 제한"), "Document text was not bounded.");
        var oversized = Path.Combine(fixture.Root, "large.pdf");
        using (var output = File.Create(oversized)) output.SetLength(FilePreviewReader.MaximumFileBytes + 1);
        Check((await reader.ReadAsync(PreviewRow(oversized), 0, CancellationToken.None)).Text.Contains("크기 제한"), "Oversized source was accepted.");
        var video = Path.Combine(fixture.Root, "sample.mp4");
        File.WriteAllText(video, "<asx><entry><ref href='https://invalid.test/video'/></entry></asx>");
        Check(!(await reader.ReadAsync(PreviewRow(video), 0, CancellationToken.None)).HasVideo, "A disguised remote playlist reached the video player.");
        File.WriteAllBytes(video, [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0]);
        var before = CapturePickerTree(fixture.Root);
        var prepared = await reader.ReadAsync(PreviewRow(video), 0, CancellationToken.None);
        Equal(video, prepared.VideoPath!);
        Equal(TimeSpan.FromSeconds(30), FilePreviewReader.MaximumVideoDuration);
        var control = new FilePreviewControl { Preview = prepared };
        var host = (Border)control.FindName("VideoHost");
        Check(host.Child is null, "Preparing a video initialized the native media player before explicit playback.");
        control.Preview = new FilePreview("another selection");
        Check(host.Child is null, "Replacing a preview retained its media player.");
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Preview preparation changed the fixtures.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await reader.ReadAsync(PreviewRow(video), 0, cancellation.Token); throw new InvalidOperationException("Cancelled preview ran."); }
        catch (OperationCanceledException) { }
        Check(!ItemDetailsReader.SupportsPreview(PreviewRow(video) with { IsDirectory = true }), "Folder preview was enabled.");
        Check(!ItemDetailsReader.SupportsPreview(PreviewRow(Path.Combine(fixture.Root, "script.exe"))), "Executable preview was enabled.");
    }

    private static async Task PreviewSelectionAndPages()
    {
        var pending = new TaskCompletionSource<FilePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new StubPreviewReader((_, _, _) => pending.Task);
        var vm = new MainViewModel(new StubScanner((_, _, _) => Task.FromResult(FixtureResult())), previewReader: reader);
        var first = PreviewRow($@"{FixturePath}\first.pdf");
        var second = PreviewRow($@"{FixturePath}\second.pdf");
        vm.SelectedRow = first;
        Equal(0, reader.Calls.Count);
        vm.PreviewCommand.Execute(null);
        var loading = vm.WaitForPreviewAsync();
        vm.SelectedRow = second;
        await loading;
        Check(reader.Calls[0].Token.IsCancellationRequested, "Selecting another file did not cancel its old preview.");
        pending.SetResult(new FilePreview("stale", PageCount: 3));
        DrainDispatcher();
        Check(!vm.Preview.Text.Contains("stale") && !vm.HasPreviewPages, "A late preview replaced the current selection.");
        reader.Read = (_, page, _) => Task.FromResult(new FilePreview($"page {page}", PageIndex: page, PageCount: 2));
        vm.PreviewCommand.Execute(null);
        await vm.WaitForPreviewAsync();
        Equal("1 / 2", vm.PreviewPageLabel);
        Check(!vm.PreviousPreviewPageCommand.CanExecute(null) && vm.NextPreviewPageCommand.CanExecute(null), "First-page navigation bounds were wrong.");
        vm.NextPreviewPageCommand.Execute(null);
        await vm.WaitForPreviewAsync();
        Equal("2 / 2", vm.PreviewPageLabel);
        Check(vm.PreviousPreviewPageCommand.CanExecute(null) && !vm.NextPreviewPageCommand.CanExecute(null), "Last-page navigation bounds were wrong.");
        var window = new MainWindow { DataContext = vm };
        try
        {
            Render((FrameworkElement)window.Content);
            var preview = (FilePreviewControl)window.FindName("ItemPreview");
            Check(ReferenceEquals(vm.Preview, preview.Preview), "The actual detail panel did not bind its preview.");
            Check(!window.IsVisible, "Preview tests must not show a window.");
        }
        finally { window.Close(); }
    }

    private sealed class StubPreviewReader(Func<ResultRow, int, CancellationToken, Task<FilePreview>> read) : IFilePreviewReader
    {
        public Func<ResultRow, int, CancellationToken, Task<FilePreview>> Read { get; set; } = read;
        public List<(ResultRow Row, int Page, CancellationToken Token)> Calls { get; } = [];
        public Task<FilePreview> ReadAsync(ResultRow row, int pageIndex, CancellationToken token)
        { Calls.Add((row, pageIndex, token)); return Read(row, pageIndex, token); }
    }
}
