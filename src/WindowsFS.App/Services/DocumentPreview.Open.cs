using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace WindowsFS.App.Services;

internal sealed partial class DocumentPreview
{
    private readonly Dictionary<string, XElement> _openStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hangulImages = new(StringComparer.Ordinal);
    private XDocument? _hangulHeader;

    private static double Length(string? text, double fallback = 0)
    {
        if (string.IsNullOrEmpty(text)) return fallback;
        var suffix = new string(text.SkipWhile(c => char.IsAsciiDigit(c) || c is '.' or '-' or '+').ToArray());
        var number = Number(text[..(text.Length - suffix.Length)], fallback);
        return Bound(number * (suffix switch { "cm" => 96 / 2.54, "mm" => 96 / 25.4, "in" => 96, "pt" => 4d / 3, "pc" => 16, _ => 1 }));
    }

    private void OpenDocument(string extension)
    {
        var xml = Xml("content.xml"); var styles = OptionalXml("styles.xml");
        foreach (var node in Desc(styles, "style").Concat(Desc(xml, "style")))
            if (Attr(node, "name") is { } name) _openStyles[name] = node;
        var pageStyle = Desc(styles, "page-layout-properties").FirstOrDefault() ?? Desc(xml, "page-layout-properties").FirstOrDefault();
        var width = Length(Attr(pageStyle, "page-width"), 794); var height = Length(Attr(pageStyle, "page-height"), 1123);
        var body = Child(xml.Root, "body") ?? xml.Root!;
        if (extension.Equals(".odp", StringComparison.OrdinalIgnoreCase))
        {
            var pages = Desc(body, "page").ToArray(); _limited |= pages.Length > 20;
            foreach (var page in pages.Take(20))
            {
                var canvas = PageCanvas(width, height);
                foreach (var frame in Children(page, "frame").Take(200))
                {
                    var x = Length(Attr(frame, "x")); var y = Length(Attr(frame, "y"));
                    var w = Length(Attr(frame, "width"), 300); var h = Length(Attr(frame, "height"), 100);
                    if (Child(frame, "image") is { } image && Picture(PackagePath("", Attr(image, "href")), w, h) is { } picture) Place(canvas, picture, x, y, w, h);
                    else if (Child(frame, "text-box") is { } box)
                    {
                        var label = new TextBlock { Foreground = Brushes.Black, FontSize = 20, TextWrapping = TextWrapping.Wrap };
                        foreach (var paragraph in Children(box, "p"))
                        {
                            if (label.Inlines.Count > 0) label.Inlines.Add(new LineBreak());
                            var span = new Span(); OpenStyle(span, paragraph); OpenInlines(paragraph, span.Inlines); label.Inlines.Add(span);
                        }
                        Place(canvas, label, x, y, w, h);
                    }
                }
                _canvases.Add(canvas);
            }
        }
        else
        {
            var document = NewDocument(width, height, Length(Attr(pageStyle, "margin"), 50));
            OpenBlocks(body, document.Blocks);
        }
        _approximate |= Desc(body, "object").Any() || Desc(body, "object-ole").Any();
    }

    private IEnumerable<XElement> OpenStyleChain(string? name, HashSet<string>? seen = null)
    {
        seen ??= new(StringComparer.Ordinal);
        if (name is null || seen.Count >= 16 || !seen.Add(name) || !_openStyles.TryGetValue(name, out var style)) yield break;
        foreach (var parent in OpenStyleChain(Attr(style, "parent-style-name"), seen)) yield return parent;
        yield return style;
    }

    private void OpenStyle(TextElement element, XElement node)
    {
        foreach (var style in OpenStyleChain(Attr(node, "style-name")))
        {
            var text = Child(style, "text-properties");
            if (Attr(text, "font-size") is { } size) element.FontSize = Bound(Length(size), 6, 144);
            if (Attr(text, "font-weight") is { } weight) element.FontWeight = weight == "bold" ? FontWeights.Bold : FontWeights.Normal;
            if (Attr(text, "font-style") is { } italic) element.FontStyle = italic == "italic" ? FontStyles.Italic : FontStyles.Normal;
            SetFont(element, Attr(text, "font-family")?.Trim('\'') ?? Attr(text, "font-name"));
            if (ColorBrush(Attr(text, "color")) is { } foreground) element.Foreground = foreground;
            if (ColorBrush(Attr(text, "background-color")) is { } background) element.Background = background;
            if (element is Inline inline && Attr(text, "text-underline-style") is not (null or "none")) inline.TextDecorations = TextDecorations.Underline;
            if (element is Paragraph paragraph && Child(style, "paragraph-properties") is { } properties)
            {
                paragraph.TextAlignment = Attr(properties, "text-align") switch { "center" => TextAlignment.Center, "end" or "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
                paragraph.Margin = new Thickness(Length(Attr(properties, "margin-left")), Length(Attr(properties, "margin-top")), Length(Attr(properties, "margin-right")), Length(Attr(properties, "margin-bottom"), 5));
                paragraph.BreakPageBefore = Attr(properties, "break-before") == "page";
            }
            if (element is TableCell cell && Child(style, "table-cell-properties") is { } cellProperties)
            {
                cell.Background = ColorBrush(Attr(cellProperties, "background-color")) ?? Brushes.Transparent;
                var border = Attr(cellProperties, "border");
                if (border is not null && border != "none")
                { cell.BorderThickness = new Thickness(Length(border.Split(' ')[0], .5)); cell.BorderBrush = ColorBrush(border.Split(' ').Last()) ?? Brushes.Black; }
            }
        }
    }

    private void OpenBlocks(XElement parent, BlockCollection blocks)
    {
        foreach (var node in parent.Elements())
        {
            LayoutElement();
            if (_characters >= TextLimit) { _limited = true; break; }
            switch (node.Name.LocalName)
            {
                case "p": case "h":
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                    if (node.Name.LocalName == "h") { paragraph.FontSize = 24; paragraph.FontWeight = FontWeights.Bold; }
                    OpenStyle(paragraph, node); OpenInlines(node, paragraph.Inlines); blocks.Add(paragraph); break;
                case "table":
                    var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
                    foreach (var column in Children(node, "table-column").Take(50))
                    {
                        var style = OpenStyleChain(Attr(column, "style-name")).LastOrDefault();
                        var count = Math.Clamp(Integer(Attr(column, "number-columns-repeated"), 1), 1, 50 - table.Columns.Count);
                        for (var i = 0; i < count; i++) table.Columns.Add(new TableColumn { Width = new GridLength(Length(Attr(Child(style, "table-column-properties"), "column-width"), 90)) });
                        if (table.Columns.Count >= 50) { _limited = true; break; }
                    }
                    var group = new TableRowGroup(); table.RowGroups.Add(group);
                    foreach (var row in Desc(node, "table-row").Where(r => r.Ancestors().First(a => a.Name.LocalName == "table") == node).Take(200))
                    {
                        var repeats = Math.Clamp(Integer(Attr(row, "number-rows-repeated"), 1), 1, 200);
                        _limited |= Integer(Attr(row, "number-rows-repeated"), 1) > 200;
                        for (var r = 0; r < repeats && group.Rows.Count < 200; r++)
                        {
                            var target = new TableRow(); group.Rows.Add(target);
                            foreach (var cell in Children(row, "table-cell").Take(50))
                            {
                                var copies = Math.Clamp(Integer(Attr(cell, "number-columns-repeated"), 1), 1, 50);
                                for (var c = 0; c < copies && target.Cells.Count < 50; c++)
                                {
                                    LayoutElement();
                                    var targetCell = new TableCell { Padding = new Thickness(4), ColumnSpan = Math.Clamp(Integer(Attr(cell, "number-columns-spanned"), 1), 1, 50), RowSpan = Math.Clamp(Integer(Attr(cell, "number-rows-spanned"), 1), 1, 200) };
                                    OpenStyle(targetCell, cell); OpenBlocks(cell, targetCell.Blocks);
                                    if (targetCell.Blocks.Count == 0) targetCell.Blocks.Add(new Paragraph(new Run(Text(Attr(cell, "string-value") ?? Attr(cell, "value") ?? ""))));
                                    target.Cells.Add(targetCell);
                                }
                            }
                        }
                    }
                    blocks.Add(table); break;
                case "frame":
                    var imageParagraph = new Paragraph(); OpenInlines(parent: new XElement("p", new XElement(node)), imageParagraph.Inlines); blocks.Add(imageParagraph); break;
                case "text": case "spreadsheet": case "section": case "list": case "list-item": case "list-header": OpenBlocks(node, blocks); break;
            }
        }
    }

    private void OpenInlines(XElement parent, InlineCollection output)
    {
        foreach (var node in parent.Nodes())
        {
            LayoutElement();
            if (node is XText text) { output.Add(new Run(Text(text.Value))); continue; }
            if (node is not XElement element) continue;
            switch (element.Name.LocalName)
            {
                case "s": output.Add(new Run(Text(new string(' ', Math.Clamp(Integer(Attr(element, "c"), 1), 1, 100))))); break;
                case "tab": output.Add(new Run("\t")); break;
                case "line-break": output.Add(new LineBreak()); break;
                case "frame":
                    var image = Child(element, "image");
                    if (Picture(PackagePath("", Attr(image, "href")), Length(Attr(element, "width")), Length(Attr(element, "height"))) is { } picture) output.Add(new InlineUIContainer(picture));
                    break;
                case "span": case "a":
                    var span = new Span(); OpenStyle(span, element); OpenInlines(element, span.Inlines); output.Add(span); break;
            }
        }
    }

    private void Hangul()
    {
        _hangulHeader = OptionalXml("Contents/header.xml");
        foreach (var item in Desc(OptionalXml("Contents/content.hpf"), "item"))
            if (Attr(item, "id") is { } id && PackagePath("Contents/", Attr(item, "href")) is { } path)
                _hangulImages[id] = archive.GetEntry(path) is not null ? path : PackagePath("", Attr(item, "href")) ?? path;
        var sections = archive.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => Integer(new string(System.IO.Path.GetFileNameWithoutExtension(e.FullName).Where(char.IsAsciiDigit).ToArray()), int.MaxValue)).ToArray();
        if (sections.Length == 0) throw new System.IO.InvalidDataException("Missing document body.");
        _limited |= sections.Length > 20;
        foreach (var section in sections.Take(20))
        {
            var xml = Xml(section.FullName); var page = Desc(xml, "pagePr").FirstOrDefault();
            var document = NewDocument(Number(Attr(page, "width"), 59528) / 75, Number(Attr(page, "height"), 84188) / 75);
            HangulBlocks(xml.Root!, document.Blocks);
        }
    }

    private void HangulBlocks(XElement parent, BlockCollection blocks)
    {
        foreach (var node in parent.Elements())
        {
            LayoutElement();
            if (_characters >= TextLimit) { _limited = true; break; }
            if (node.Name.LocalName == "p")
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 5) };
                var properties = Desc(_hangulHeader, "paraPr").FirstOrDefault(p => Attr(p, "id") == Attr(node, "paraPrIDRef"));
                paragraph.TextAlignment = Attr(Child(properties, "align"), "horizontal") switch { "CENTER" => TextAlignment.Center, "RIGHT" => TextAlignment.Right, "JUSTIFY" => TextAlignment.Justify, _ => TextAlignment.Left };
                foreach (var run in Children(node, "run"))
                {
                    var style = Desc(_hangulHeader, "charPr").FirstOrDefault(p => Attr(p, "id") == Attr(run, "charPrIDRef"));
                    var span = new Span { FontSize = Bound(Number(Attr(style, "height"), 1000) / 75, 6, 144), FontWeight = Child(style, "bold") is not null ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = Child(style, "italic") is not null ? FontStyles.Italic : FontStyles.Normal, Foreground = ColorBrush(Attr(style, "textColor")) ?? Brushes.Black };
                    var fontId = Attr(Child(style, "fontRef"), "hangul");
                    var fontFace = Desc(_hangulHeader, "fontface").FirstOrDefault(f => Attr(f, "lang") == "HANGUL");
                    SetFont(span, Attr(Children(fontFace, "font").FirstOrDefault(f => Attr(f, "id") == fontId), "face"));
                    foreach (var item in run.Elements())
                    {
                        if (item.Name.LocalName == "t") span.Inlines.Add(new Run(Text(item.Value)));
                        else if (item.Name.LocalName == "pic")
                        {
                            var reference = Attr(Desc(item, "img").FirstOrDefault(), "binaryItemIDRef"); var size = Child(item, "curSz");
                            if (Picture(reference is not null && _hangulImages.TryGetValue(reference, out var path) ? path : null, Number(Attr(size, "width")) / 75, Number(Attr(size, "height")) / 75) is { } picture) span.Inlines.Add(new InlineUIContainer(picture));
                        }
                    }
                    paragraph.Inlines.Add(span);
                }
                blocks.Add(paragraph);
                foreach (var table in Desc(node, "tbl").Where(t => !t.Ancestors().TakeWhile(a => a != node).Any(a => a.Name.LocalName == "tbl"))) HangulTable(table, blocks);
            }
        }
    }

    private void HangulTable(XElement node, BlockCollection blocks)
    {
        var table = new Table { CellSpacing = 0 }; var group = new TableRowGroup(); table.RowGroups.Add(group);
        foreach (var row in Children(node, "tr").Take(200))
        {
            var target = new TableRow(); group.Rows.Add(target);
            foreach (var cell in Children(row, "tc").Take(50))
            {
                LayoutElement();
                var span = Child(cell, "cellSpan");
                var targetCell = new TableCell { Padding = new Thickness(4), BorderBrush = Brushes.Black, BorderThickness = new Thickness(.5), ColumnSpan = Math.Clamp(Integer(Attr(span, "colSpan"), 1), 1, 50), RowSpan = Math.Clamp(Integer(Attr(span, "rowSpan"), 1), 1, 200) };
                var borderFill = Desc(_hangulHeader, "borderFill").FirstOrDefault(b => Attr(b, "id") == Attr(cell, "borderFillIDRef"));
                targetCell.Background = ColorBrush(Attr(Desc(borderFill, "winBrush").FirstOrDefault(), "faceColor")) ?? Brushes.Transparent;
                foreach (var list in Children(cell, "subList")) HangulBlocks(list, targetCell.Blocks);
                target.Cells.Add(targetCell);
            }
        }
        blocks.Add(table);
    }
}
