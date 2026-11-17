using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;

namespace WindowsFS.App.Services;

/// <summary>
/// Bounded, in-memory document renderer. Only explicit XML properties and embedded raster images
/// become WPF objects; document XAML, external resources, formulas and macros are never loaded.
/// </summary>
internal sealed partial class DocumentPreview(ZipArchive archive, CancellationToken token)
{
    private const int TextLimit = 64 * 1024;
    private const int PageLimit = 100;
    private long _expandedBytes;
    private int _characters, _elements, _layoutElements;
    private long _imagePixels;
    private bool _limited;
    private bool _approximate;
    private readonly Dictionary<string, XDocument> _xml = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapSource> _images = new(StringComparer.Ordinal);
    private readonly List<FlowDocument> _documents = [];
    private readonly List<FrameworkElement> _canvases = [];

    public static Task<FilePreview> ReadAsync(Stream source, string extension, int pageIndex, CancellationToken token)
    {
        var completion = new TaskCompletionSource<FilePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        // WPF layout needs STA ownership, but never a window, app dispatcher, or an Office process.
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
                if (archive.Entries.Count > 20_000) throw new InvalidDataException("Too many document parts.");
                var renderer = new DocumentPreview(archive, token);
                switch (extension.ToLowerInvariant())
                {
                    case ".docx": case ".docm": renderer.Word(); break;
                    case ".xlsx": case ".xlsm": renderer.Workbook(); break;
                    case ".pptx": case ".pptm": renderer.Presentation(); break;
                    case ".hwpx": renderer.Hangul(); break;
                    default: renderer.OpenDocument(extension); break;
                }
                completion.TrySetResult(renderer.Render(pageIndex));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        }) { IsBackground = true, Name = "WindowsFS document preview" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private byte[] Part(string name, int limit)
    {
        token.ThrowIfCancellationRequested();
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("Missing document part.");
        if (entry.Length > limit || _expandedBytes + entry.Length > 32L * 1024 * 1024)
            throw new InvalidDataException("Expanded document limit exceeded.");
        using var input = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (memory.Length + count > limit || (_expandedBytes += count) > 32L * 1024 * 1024)
                throw new InvalidDataException("Expanded document limit exceeded.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    private XDocument Xml(string name)
    {
        if (_xml.TryGetValue(name, out var cached)) return cached;
        using var memory = new MemoryStream(Part(name, 2 * 1024 * 1024));
        using var reader = XmlReader.Create(memory, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024, MaxCharactersFromEntities = 0
        });
        var document = XDocument.Load(reader);
        // Bound both traversal depth and the number of layout objects before recursive processing.
        foreach (var node in document.Descendants())
        {
            token.ThrowIfCancellationRequested();
            if (++_elements > 40_000 || node.Ancestors().Take(65).Count() > 64)
                throw new InvalidDataException("Document complexity limit exceeded.");
        }
        _xml.Add(name, document);
        return document;
    }

    private XDocument? OptionalXml(string name) => archive.GetEntry(name) is null ? null : Xml(name);
    private static XElement? Child(XContainer? node, string name) => node?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    private static IEnumerable<XElement> Children(XContainer? node, string name) => node?.Elements().Where(x => x.Name.LocalName == name) ?? [];
    private static IEnumerable<XElement> Desc(XContainer? node, string name) => node?.Descendants().Where(x => x.Name.LocalName == name) ?? [];
    private static string? Attr(XElement? node, string name) => node?.Attributes().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
    private static double Number(string? text, double fallback = 0) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
    private static int Integer(string? text, int fallback = 0) => int.TryParse(text, out var n) ? n : fallback;
    private static double Bound(double n, double min = 0, double max = 2000) => Math.Clamp(n, min, max);
    private static bool Enabled(XElement? node) => node is not null && Attr(node, "val") is not ("0" or "false" or "off" or "none");
    private static Brush? ColorBrush(string? value)
    {
        if (value is null or "auto" or "transparent" or "none") return null;
        var hex = value.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    private Dictionary<string, string> Relationships(string owner)
    {
        var slash = owner.LastIndexOf('/');
        var directory = owner[..(slash + 1)];
        var document = OptionalXml(directory + "_rels/" + owner[(slash + 1)..] + ".rels");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document?.Root?.Elements() ?? [])
        {
            var id = Attr(item, "Id");
            var path = PackagePath(directory, Attr(item, "Target"));
            if (id is not null && path is not null && !string.Equals(Attr(item, "TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) result[id] = path;
        }
        return result;
    }

    private static string? PackagePath(string directory, string? target)
    {
        if (string.IsNullOrEmpty(target) || target.Contains(':') || target.Contains('\\') || target.StartsWith("//")) return null;
        var parts = new List<string>();
        foreach (var part in (target.StartsWith('/') ? target[1..] : directory + target).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private string Text(string value)
    {
        token.ThrowIfCancellationRequested();
        var count = Math.Min(value.Length, TextLimit - _characters);
        _limited |= count != value.Length;
        _characters += count;
        return value[..count];
    }

    private void LayoutElement()
    {
        token.ThrowIfCancellationRequested();
        if (++_layoutElements > 12_000) throw new InvalidDataException("Document layout limit exceeded.");
    }

    private Image? Picture(string? path, double width = 0, double height = 0)
    {
        if (path is null || archive.GetEntry(path) is null) { _approximate = true; return null; }
        if (!_images.TryGetValue(path, out var bitmap))
        {
            if (_images.Count >= 40) { _limited = true; return null; }
            // Embedded SVG, EMF, WMF, and other active/vector formats are never handed to shell handlers.
            if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff"))
            { _approximate = true; return null; }
            using var stream = new MemoryStream(Part(path, 8 * 1024 * 1024));
            bitmap = FilePreviewReader.DecodeImage(stream);
            if ((_imagePixels += (long)bitmap.PixelWidth * bitmap.PixelHeight) > 16_000_000)
                throw new InvalidDataException("Embedded image pixel limit exceeded.");
            _images.Add(path, bitmap);
        }
        if (width <= 0) width = Math.Min(bitmap.PixelWidth, 600);
        if (height <= 0) height = width * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth);
        var scale = Math.Min(1, 700 / Math.Max(width, height));
        return new Image { Source = bitmap, Width = Bound(width * scale, 1), Height = Bound(height * scale, 1), Stretch = Stretch.Fill };
    }

    private FlowDocument NewDocument(double width = 794, double height = 1123, double margin = 60)
    {
        var document = new FlowDocument
        {
            PageWidth = Bound(width, 200), PageHeight = Bound(height, 200), PagePadding = new Thickness(margin),
            ColumnWidth = 2000, FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
            Foreground = Brushes.Black, Background = Brushes.White, IsHyphenationEnabled = false
        };
        _documents.Add(document);
        return document;
    }

    private FilePreview Render(int requestedPage)
    {
        var pages = new List<(DocumentPaginator Paginator, int Index)>();
        foreach (var document in _documents)
        {
            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            if (paginator is DynamicDocumentPaginator dynamic) dynamic.IsBackgroundPaginationEnabled = false;
            for (var index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                using var page = paginator.GetPage(index);
                if (page == DocumentPage.Missing) break;
                if (pages.Count >= PageLimit) { _limited = true; break; }
                pages.Add((paginator, index));
            }
            if (_limited && pages.Count >= PageLimit) break;
        }
        var count = pages.Count + _canvases.Count;
        if (count == 0) return new FilePreview("문서 내용 없음");
        var selected = Math.Clamp(requestedPage, 0, count - 1);
        BitmapSource bitmap;
        if (selected < pages.Count)
        {
            var (paginator, index) = pages[selected];
            using var page = paginator.GetPage(index);
            bitmap = Raster(page.Visual, page.Size);
        }
        else
        {
            var canvas = _canvases[selected - pages.Count];
            var size = new Size(canvas.Width, canvas.Height);
            canvas.Measure(size); canvas.Arrange(new Rect(size)); canvas.UpdateLayout();
            bitmap = Raster(canvas, size);
        }
        var status = _limited ? "미리보기 범위 제한" : "";
        if (_approximate) status += (status.Length > 0 ? " · " : "") + "일부 개체·서식 미지원";
        return new FilePreview(status, Image: bitmap, PageIndex: selected, PageCount: count);
    }

    private BitmapSource Raster(Visual visual, Size size)
    {
        token.ThrowIfCancellationRequested();
        var scale = Math.Min(1.5, 1600 / Math.Max(size.Width, size.Height));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(size));
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }
}
