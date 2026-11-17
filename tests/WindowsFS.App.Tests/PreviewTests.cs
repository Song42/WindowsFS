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
        WritePreviewPackage(word,
            ("word/styles.xml", "<styles><docDefaults><rPrDefault><rPr><sz val='20'/><color val='000000'/></rPr></rPrDefault></docDefaults><style styleId='Title'><rPr><b/><sz val='48'/><color val='FF0000'/></rPr></style></styles>"),
            ("word/_rels/document.xml.rels", "<Relationships><Relationship Id='picture' Target='media/picture.png'/><Relationship Id='external' Target='https://invalid.test/picture.png' TargetMode='External'/></Relationships>"),
            ("word/document.xml", """
            <w:document xmlns:w='urn:word' xmlns:r='urn:rels'><w:body>
              <w:p><w:pPr><w:pStyle w:val='Title'/></w:pPr><w:r><w:t>Styled title</w:t></w:r></w:p>
              <w:tbl><w:tblPr><w:tblBorders><w:top w:val='single' w:sz='12'/><w:bottom w:val='single' w:sz='12'/><w:insideV w:val='single' w:sz='12'/></w:tblBorders></w:tblPr>
                <w:tblGrid><w:gridCol w:w='3000'/><w:gridCol w:w='3000'/></w:tblGrid>
                <w:tr><w:tc><w:tcPr><w:shd w:fill='00FF00'/></w:tcPr><w:p><w:r><w:t>Green cell</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Second column</w:t></w:r></w:p></w:tc></w:tr>
              </w:tbl>
              <w:p><w:r><w:drawing><inline><extent cx='952500' cy='476250'/><blip r:embed='picture'/></inline></w:drawing></w:r></w:p>
              <w:p><w:pPr><w:pageBreakBefore/></w:pPr><w:r><w:rPr><w:color w:val='0000FF'/><w:sz w:val='48'/></w:rPr><w:t>Second page</w:t></w:r></w:p>
            </w:body></w:document>
            """));
        AddPreviewPicture(word, "word/media/picture.png");
        var sheet = Path.Combine(fixture.Root, "table.xlsx");
        WritePreviewPackage(sheet,
            ("xl/workbook.xml", "<workbook xmlns:r='urn:rels'><sheets><sheet name='Sheet A' r:id='r1'/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>"),
            ("xl/sharedStrings.xml", "<sst><si><t>공유 문자열</t></si></sst>"),
            ("xl/styles.xml", "<styleSheet><fonts><font><sz val='18'/><b/><color rgb='FFFF0000'/></font></fonts><fills><fill><patternFill patternType='solid'><fgColor rgb='FF00FF00'/></patternFill></fill></fills><cellXfs><xf fontId='0' fillId='0'/></cellXfs></styleSheet>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c r='A1' t='s'><v>0</v></c><c r='B1'><f>EXTERNAL()</f><v>42</v></c><c r='C1' t='inlineStr'><is><t>inline</t></is></c></row></sheetData></worksheet>"));
        var slides = Path.Combine(fixture.Root, "slides.pptx");
        WritePreviewPackage(slides,
            ("ppt/presentation.xml", "<presentation xmlns:r='urn:rels'><sldIdLst><sldId id='9' r:id='r2'/><sldId id='8' r:id='r1'/></sldIdLst></presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='r1' Target='slides/slide1.xml'/><Relationship Id='r2' Target='slides/slide2.xml'/></Relationships>"),
            ("ppt/slides/slide1.xml", "<slide><cSld><bg><bgPr><solidFill><srgbClr val='0000FF'/></solidFill></bgPr></bg><spTree/></cSld></slide>"),
            ("ppt/slides/slide2.xml", "<slide xmlns:r='urn:rels'><cSld><bg><bgPr><solidFill><srgbClr val='FF0000'/></solidFill></bgPr></bg><spTree><pic><spPr><xfrm><off x='952500' y='952500'/><ext cx='952500' cy='476250'/></xfrm></spPr><blipFill><blip r:embed='picture'/></blipFill></pic></spTree></cSld></slide>"),
            ("ppt/slides/_rels/slide2.xml.rels", "<Relationships><Relationship Id='picture' Target='../media/picture.png'/></Relationships>"));
        AddPreviewPicture(slides, "ppt/media/picture.png");
        var hangul = Path.Combine(fixture.Root, "hangul.hwpx");
        WritePreviewPackage(hangul, ("Contents/section10.xml", "<section><p><run charPrIDRef='blue'><t>Section ten</t></run></p></section>"),
            ("Contents/section2.xml", "<section><p><run charPrIDRef='red'><t>Section two</t><pic><curSz width='7500' height='3750'/><img binaryItemIDRef='image1'/></pic></run></p></section>"),
            ("Contents/header.xml", "<head><charPr id='red' height='2400' textColor='#FF0000'><bold/></charPr><charPr id='blue' height='2400' textColor='#0000FF'/></head>"),
            ("Contents/content.hpf", "<package><manifest><item id='image1' href='BinData/picture.png'/></manifest></package>"));
        AddPreviewPicture(hangul, "BinData/picture.png");
        var odt = Path.Combine(fixture.Root, "open.odt");
        WritePreviewPackage(odt, ("content.xml", "<document><automatic-styles><style name='red'><text-properties color='#FF0000' font-size='24pt' font-weight='bold'/></style><style name='green'><table-cell-properties background-color='#00FF00' border='1pt solid #000000'/></style></automatic-styles><body><text><p style-name='red'>Open document text</p><table><table-row><table-cell style-name='green'><p>Green cell</p></table-cell></table-row></table><p><frame width='100px' height='50px'><image href='Pictures/picture.png'/></frame></p></text></body></document>"));
        AddPreviewPicture(odt, "Pictures/picture.png");
        var ods = Path.Combine(fixture.Root, "open.ods");
        WritePreviewPackage(ods, ("content.xml", "<document><automatic-styles><style name='green'><table-cell-properties background-color='#00FF00'/></style></automatic-styles><body><spreadsheet><table><table-column number-columns-repeated='2'/><table-row><table-cell style-name='green' number-columns-spanned='2'><p>Merged cells</p></table-cell><covered-table-cell/></table-row><table-row><table-cell number-columns-repeated='2'><p>Value</p></table-cell></table-row></table></spreadsheet></body></document>"));
        var odp = Path.Combine(fixture.Root, "open.odp");
        WritePreviewPackage(odp, ("content.xml", "<document><automatic-styles><style name='red'><text-properties color='#FF0000' font-size='24pt'/></style></automatic-styles><body><presentation><page><frame x='50px' y='50px' width='100px' height='50px'><image href='Pictures/picture.png'/></frame><frame x='50px' y='150px' width='500px' height='100px'><text-box><p style-name='red'>Slide text</p></text-box></frame></page></presentation></body></document>"));
        AddPreviewPicture(odp, "Pictures/picture.png");
        var before = CapturePickerTree(fixture.Root);
        var reader = new FilePreviewReader();
        var first = await reader.ReadAsync(PreviewRow(word), 0, CancellationToken.None);
        Check(first.Image is { IsFrozen: true } && first.PageCount == 2, "Word did not paginate to detached page images: " + first.Text);
        Check(PreviewColorCount(first, 'r') > 100 && PreviewColorCount(first, 'g') > 1000 && PreviewColorCount(first, 'y') > 1000, "Word lost styled text, table fill, or embedded image.");
        var second = await reader.ReadAsync(PreviewRow(word), int.MaxValue, CancellationToken.None);
        Equal(1, second.PageIndex);
        Check(PreviewColorCount(second, 'b') > 100 && PreviewColorCount(second, 'g') == 0, "Word page navigation reused the first page.");
        var workbook = await reader.ReadAsync(PreviewRow(sheet), 0, CancellationToken.None);
        Check(PreviewColorCount(workbook, 'g') > 1000 && PreviewColorCount(workbook, 'r') > 50, "Spreadsheet cell fill/font formatting was lost: " + workbook.Text);
        var slide = await reader.ReadAsync(PreviewRow(slides), 0, CancellationToken.None);
        Equal(2, slide.PageCount);
        Check(PreviewColorCount(slide, 'r') > 10000 && PreviewColorCount(slide, 'y') > 1000, "Slide order, background, or embedded image was lost.");
        Check(PreviewColorCount(await reader.ReadAsync(PreviewRow(slides), 1, CancellationToken.None), 'b') > 10000, "Second slide was not rendered.");
        var hangulPage = await reader.ReadAsync(PreviewRow(hangul), 0, CancellationToken.None);
        Equal(2, hangulPage.PageCount);
        Check(PreviewColorCount(hangulPage, 'r') > 100 && PreviewColorCount(hangulPage, 'y') > 1000, "HWPX styles, embedded picture, or numeric section ordering was lost.");
        Check(PreviewColorCount(await reader.ReadAsync(PreviewRow(hangul), 1, CancellationToken.None), 'b') > 100, "HWPX second section was missing.");
        var open = await reader.ReadAsync(PreviewRow(odt), 0, CancellationToken.None);
        Check(PreviewColorCount(open, 'r') > 100 && PreviewColorCount(open, 'g') > 1000 && PreviewColorCount(open, 'y') > 1000, "ODT styled text/table/image was missing: " + open.Text);
        Check(PreviewColorCount(await reader.ReadAsync(PreviewRow(ods), 0, CancellationToken.None), 'g') > 1000, "ODS styled/merged cells were not rendered.");
        var openSlide = await reader.ReadAsync(PreviewRow(odp), 0, CancellationToken.None);
        Check(PreviewColorCount(openSlide, 'r') > 100 && PreviewColorCount(openSlide, 'y') > 1000, "ODP positioned text/image was not rendered.");
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Document previews modified or extracted files.");
    }

    private static void AddPreviewPicture(string package, string name)
    {
        using var archive = ZipFile.Open(package, ZipArchiveMode.Update);
        using var stream = archive.CreateEntry(name).Open();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 255, 255 }, 4)));
        encoder.Save(stream);
    }

    private static int PreviewColorCount(FilePreview preview, char color)
    {
        Check(preview.Image is { IsFrozen: true }, "Document did not render: " + preview.Text);
        var image = new FormatConvertedBitmap(preview.Image!, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0);
        var count = 0;
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var b = bytes[i]; var g = bytes[i + 1]; var r = bytes[i + 2];
            if (color switch { 'r' => r > 180 && g < 80 && b < 80, 'g' => g > 180 && r < 80 && b < 80, 'b' => b > 180 && r < 80 && g < 80, 'y' => r > 180 && g > 180 && b < 80, _ => false }) count++;
        }
        return count;
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
        Equal(TimeSpan.FromSeconds(30), FilePreviewReader.MaximumPlaybackDuration);
        var control = new FilePreviewControl { Preview = prepared };
        var host = (Border)control.FindName("MediaHost");
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

    private static async Task DocumentLayoutLimits()
    {
        using var fixture = new PickerFixture();
        var reader = new FilePreviewReader();
        var external = Path.Combine(fixture.Root, "external.docx");
        WritePreviewPackage(external,
            ("word/document.xml", "<document xmlns:r='urn:rels'><body><p><r><rPr><color val='FF0000'/><sz val='48'/><rFonts ascii='https://invalid.test/font.ttf#Font'/></rPr><t>Local text</t><drawing><inline><extent cx='952500' cy='952500'/><blip r:embed='remote'/></inline></drawing></r></p></body></document>"),
            ("word/_rels/document.xml.rels", "<Relationships><Relationship Id='remote' Target='https://invalid.test/image.png' TargetMode='External'/></Relationships>"));
        var deep = Path.Combine(fixture.Root, "deep.docx");
        WritePreviewPackage(deep, ("word/document.xml", "<document>" + string.Concat(Enumerable.Repeat("<sdt>", 70)) + "<p><t>deep</t></p>" + string.Concat(Enumerable.Repeat("</sdt>", 70)) + "</document>"));
        var repeated = Path.Combine(fixture.Root, "repeated.ods");
        WritePreviewPackage(repeated, ("content.xml", "<document><body><spreadsheet><table><table-row number-rows-repeated='1000000'><table-cell number-columns-repeated='1000000'><p/></table-cell></table-row></table></spreadsheet></body></document>"));
        var manyPages = Path.Combine(fixture.Root, "pages.docx");
        WritePreviewPackage(manyPages, ("word/document.xml", "<document><body>" + string.Concat(Enumerable.Range(0, 105).Select(i => $"<p><pPr><pageBreakBefore/></pPr><r><t>Page {i}</t></r></p>")) + "</body></document>"));
        var before = CapturePickerTree(fixture.Root);
        var local = await reader.ReadAsync(PreviewRow(external), 0, CancellationToken.None);
        Check(PreviewColorCount(local, 'r') > 100 && local.Text.Contains("미지원"), "External image/font references interfered with safe local rendering.");
        Check((await reader.ReadAsync(PreviewRow(deep), 0, CancellationToken.None)).Text.Contains("미리보기 불가"), "Excessively nested document was accepted.");
        Check((await reader.ReadAsync(PreviewRow(repeated), 0, CancellationToken.None)).Text.Contains("미리보기 불가"), "Repeated cells bypassed the layout budget.");
        var limited = await reader.ReadAsync(PreviewRow(manyPages), int.MaxValue, CancellationToken.None);
        Check(limited.HasImage && limited.PageCount == 100 && limited.PageIndex == 99 && limited.Text.Contains("범위 제한"), "Document page limit or clamped navigation was incorrect.");
        Check(before.SequenceEqual(CapturePickerTree(fixture.Root)), "Document limits or external references changed the source tree.");
    }

    private static async Task PreviewSelectionAndPages()
    {
        var pending = new TaskCompletionSource<FilePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new StubPreviewReader((_, _, _) => pending.Task);
        var vm = new MainViewModel(new StubScanner((_, _, _) => Task.FromResult(FixtureResult())), previewReader: reader);
        var first = PreviewRow($@"{FixturePath}\first.pdf");
        var second = PreviewRow($@"{FixturePath}\second.pdf");
        vm.SelectedRow = first;
        Equal(1, reader.Calls.Count);
        var loading = vm.WaitForPreviewAsync();
        reader.Read = (_, page, _) => Task.FromResult(new FilePreview($"page {page}", PageIndex: page, PageCount: 2));
        vm.SelectedRow = second;
        await loading;
        await vm.WaitForPreviewAsync();
        Equal(2, reader.Calls.Count);
        Check(reader.Calls[0].Token.IsCancellationRequested, "Selecting another file did not cancel its old preview.");
        pending.SetResult(new FilePreview("stale", PageCount: 3));
        DrainDispatcher();
        Check(vm.Preview.Text == "page 0" && vm.Preview.PageCount == 2, "A late preview replaced the automatically loaded current selection.");
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
            var navigation = (StackPanel)window.FindName("PreviewPageNavigation");
            Equal(HorizontalAlignment.Center, navigation.HorizontalAlignment);
            Check(!Descendants<Button>((DependencyObject)window.Content).Any(button => Equals(button.Content, "미리보기")), "The removed preview button is still present.");
            Check(!window.IsVisible, "Preview tests must not show a window.");
        }
        finally { window.Close(); }
        var calls = reader.Calls.Count;
        vm.SelectedRow = first with { IsDirectory = true };
        await vm.WaitForPreviewAsync();
        Equal(calls, reader.Calls.Count);
        Check(!vm.HasPreviewPages && !vm.Preview.HasImage, "Selecting a folder retained the previous preview.");
        vm.SelectedRow = PreviewRow($@"{FixturePath}\program.exe");
        await vm.WaitForPreviewAsync();
        Equal(calls, reader.Calls.Count);
        reader.Read = (_, _, _) => Task.FromResult(new FilePreview("single page", PageCount: 1));
        vm.SelectedRow = first;
        await vm.WaitForPreviewAsync();
        Check(!vm.HasPreviewPages && !vm.NextPreviewPageCommand.CanExecute(null), "A single-page preview exposed page navigation.");
    }

    private sealed class StubPreviewReader(Func<ResultRow, int, CancellationToken, Task<FilePreview>> read) : IFilePreviewReader
    {
        public Func<ResultRow, int, CancellationToken, Task<FilePreview>> Read { get; set; } = read;
        public List<(ResultRow Row, int Page, CancellationToken Token)> Calls { get; } = [];
        public Task<FilePreview> ReadAsync(ResultRow row, int pageIndex, CancellationToken token)
        { Calls.Add((row, pageIndex, token)); return Read(row, pageIndex, token); }
    }
}
