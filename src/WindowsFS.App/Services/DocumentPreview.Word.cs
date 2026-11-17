using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace WindowsFS.App.Services;

internal sealed partial class DocumentPreview
{
    private readonly Dictionary<string, XElement> _wordStyles = new(StringComparer.Ordinal);
    private XElement? _wordDefaults;
    private Dictionary<string, string> _wordRelationships = [];

    private void Word()
    {
        var xml = Xml("word/document.xml");
        var styles = OptionalXml("word/styles.xml");
        _wordDefaults = Desc(styles, "docDefaults").FirstOrDefault();
        foreach (var style in Desc(styles, "style"))
            if (Attr(style, "styleId") is { } id) _wordStyles[id] = style;
        _wordRelationships = Relationships("word/document.xml");
        var section = Desc(xml, "sectPr").LastOrDefault();
        var pageSize = Child(section, "pgSz");
        var document = NewDocument(Number(Attr(pageSize, "w"), 11910) / 15, Number(Attr(pageSize, "h"), 16845) / 15);
        var margins = Child(section, "pgMar");
        if (margins is not null) document.PagePadding = new Thickness(
            Bound(Number(Attr(margins, "left"), 900) / 15, 0, document.PageWidth / 3),
            Bound(Number(Attr(margins, "top"), 900) / 15, 0, document.PageHeight / 3),
            Bound(Number(Attr(margins, "right"), 900) / 15, 0, document.PageWidth / 3),
            Bound(Number(Attr(margins, "bottom"), 900) / 15, 0, document.PageHeight / 3));
        WordBlocks(Child(xml.Root, "body") ?? xml.Root!, document.Blocks);
        _approximate |= Desc(xml, "chart").Any() || Desc(xml, "object").Any() || Desc(xml, "altChunk").Any();
    }

    private IEnumerable<XElement> WordStyleChain(string? id, HashSet<string>? seen = null)
    {
        seen ??= new(StringComparer.Ordinal);
        if (id is null || seen.Count >= 16 || !seen.Add(id) || !_wordStyles.TryGetValue(id, out var style)) yield break;
        foreach (var parent in WordStyleChain(Attr(Child(style, "basedOn"), "val"), seen)) yield return parent;
        yield return style;
    }

    private void WordBlocks(XElement parent, BlockCollection blocks)
    {
        foreach (var node in parent.Elements())
        {
            token.ThrowIfCancellationRequested();
            if (_characters >= TextLimit) { _limited = true; break; }
            switch (node.Name.LocalName)
            {
                case "p": blocks.Add(WordParagraph(node)); break;
                case "tbl": blocks.Add(WordTable(node)); break;
                case "sdt": case "sdtContent": case "customXml": WordBlocks(node, blocks); break;
            }
        }
    }

    private Paragraph WordParagraph(XElement node)
    {
        LayoutElement();
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        var properties = Child(node, "pPr");
        WordRunProperties(paragraph, Child(Child(_wordDefaults, "rPrDefault"), "rPr"));
        WordParagraphProperties(paragraph, Child(Child(_wordDefaults, "pPrDefault"), "pPr"));
        var styleId = Attr(Child(properties, "pStyle"), "val") ?? _wordStyles.Values.FirstOrDefault(s => Attr(s, "type") == "paragraph" && Attr(s, "default") == "1")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "styleId")?.Value;
        foreach (var style in WordStyleChain(styleId))
        {
            WordParagraphProperties(paragraph, Child(style, "pPr"));
            WordRunProperties(paragraph, Child(style, "rPr"));
        }
        WordParagraphProperties(paragraph, properties);
        if (Child(properties, "numPr") is not null)
        {
            paragraph.Inlines.Add(new Run("• "));
            _approximate = true; // Numbering definitions are not a layout engine.
        }
        WordInlines(node, paragraph.Inlines);
        return paragraph;
    }

    private static void WordParagraphProperties(Paragraph paragraph, XElement? properties)
    {
        if (properties is null) return;
        if (Child(properties, "jc") is { } alignment) paragraph.TextAlignment = Attr(alignment, "val") switch
        { "center" => TextAlignment.Center, "right" or "end" => TextAlignment.Right, "both" or "distribute" => TextAlignment.Justify, _ => TextAlignment.Left };
        var spacing = Child(properties, "spacing");
        if (spacing is not null)
        {
            paragraph.Margin = new Thickness(paragraph.Margin.Left, Bound(Number(Attr(spacing, "before")) / 15), 0, Bound(Number(Attr(spacing, "after")) / 15));
            var line = Number(Attr(spacing, "line"));
            if (line > 0) paragraph.LineHeight = Bound(Attr(spacing, "lineRule") is "exact" or "atLeast" ? line / 15 : line / 240 * paragraph.FontSize * 1.2, 1, 200);
        }
        if (Child(properties, "ind") is { } indent)
        {
            paragraph.Margin = new Thickness(Bound(Number(Attr(indent, "left")) / 15), paragraph.Margin.Top, Bound(Number(Attr(indent, "right")) / 15), paragraph.Margin.Bottom);
            paragraph.TextIndent = Bound((Number(Attr(indent, "firstLine")) - Number(Attr(indent, "hanging"))) / 15, -200, 200);
        }
        if (Enabled(Child(properties, "pageBreakBefore"))) paragraph.BreakPageBefore = true;
        paragraph.KeepWithNext = Enabled(Child(properties, "keepNext"));
        if (ColorBrush(Attr(Child(properties, "shd"), "fill")) is { } fill) paragraph.Background = fill;
    }

    private static void WordRunProperties(TextElement element, XElement? properties)
    {
        if (properties is null) return;
        if (Child(properties, "b") is { } bold) element.FontWeight = Enabled(bold) ? FontWeights.Bold : FontWeights.Normal;
        if (Child(properties, "i") is { } italic) element.FontStyle = Enabled(italic) ? FontStyles.Italic : FontStyles.Normal;
        var size = Attr(Child(properties, "sz"), "val");
        if (size is not null) element.FontSize = Bound(Number(size, 22) * 2 / 3, 6, 144);
        var fonts = Child(properties, "rFonts");
        SetFont(element, Attr(fonts, "eastAsia") ?? Attr(fonts, "ascii") ?? Attr(fonts, "hAnsi"));
        if (ColorBrush(Attr(Child(properties, "color"), "val")) is { } color) element.Foreground = color;
        if (ColorBrush(Attr(Child(properties, "shd"), "fill")) is { } background) element.Background = background;
        if (element is Inline inline)
        {
            if (Enabled(Child(properties, "u"))) inline.TextDecorations = TextDecorations.Underline;
            if (Enabled(Child(properties, "strike"))) inline.TextDecorations = TextDecorations.Strikethrough;
            inline.BaselineAlignment = Attr(Child(properties, "vertAlign"), "val") switch
            { "superscript" => BaselineAlignment.Superscript, "subscript" => BaselineAlignment.Subscript, _ => BaselineAlignment.Baseline };
        }
    }

    private static void SetFont(TextElement element, string? name)
    {
        // Never let a document supply a URI-backed font source or a font-file path.
        if (!string.IsNullOrWhiteSpace(name) && name.Length <= 100 && name.IndexOfAny(['/', '\\', ':', '#']) < 0)
            element.FontFamily = new FontFamily(name);
    }

    private void WordInlines(XElement parent, InlineCollection output)
    {
        foreach (var node in parent.Elements())
        {
            LayoutElement();
            switch (node.Name.LocalName)
            {
                case "r":
                    var span = new Span();
                    var properties = Child(node, "rPr");
                    foreach (var style in WordStyleChain(Attr(Child(properties, "rStyle"), "val"))) WordRunProperties(span, Child(style, "rPr"));
                    WordRunProperties(span, properties);
                    WordInlines(node, span.Inlines);
                    output.Add(span);
                    break;
                case "t": output.Add(new Run(Text(node.Value))); break;
                case "tab": output.Add(new Run("\t")); break;
                case "br": case "cr": output.Add(new LineBreak()); _approximate |= Attr(node, "type") == "page"; break;
                case "drawing": case "pict":
                    var id = Attr(Desc(node, "blip").FirstOrDefault(), "embed") ?? Attr(Desc(node, "imagedata").FirstOrDefault(), "id");
                    var extent = Desc(node, "extent").FirstOrDefault();
                    var picture = Picture(id is not null && _wordRelationships.TryGetValue(id, out var path) ? path : null,
                        Number(Attr(extent, "cx")) / 9525, Number(Attr(extent, "cy")) / 9525);
                    if (picture is not null) output.Add(new InlineUIContainer(picture));
                    _approximate |= Desc(node, "anchor").Any();
                    break;
                case "hyperlink": case "smartTag": case "ins": case "sdt": case "sdtContent": case "fldSimple": WordInlines(node, output); break;
            }
        }
    }

    private Table WordTable(XElement node)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var column in Children(Child(node, "tblGrid"), "gridCol").Take(50))
            table.Columns.Add(new TableColumn { Width = new GridLength(Bound(Number(Attr(column, "w"), 1440) / 15, 1, 1000)) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var rows = Children(node, "tr").ToArray();
        _limited |= rows.Length > 200;
        var merges = new Dictionary<int, TableCell>();
        foreach (var row in rows.Take(200))
        {
            token.ThrowIfCancellationRequested();
            var target = new TableRow(); group.Rows.Add(target);
            var column = 0;
            foreach (var cell in Children(row, "tc").Take(50))
            {
                var properties = Child(cell, "tcPr");
                var span = Math.Clamp(Integer(Attr(Child(properties, "gridSpan"), "val"), 1), 1, 50);
                var merge = Child(properties, "vMerge");
                if (merge is not null && Attr(merge, "val") != "restart" && merges.TryGetValue(column, out var previous))
                { previous.RowSpan++; column += span; continue; }
                var targetCell = new TableCell { ColumnSpan = span, Padding = new Thickness(5), BorderThickness = new Thickness(0) };
                LayoutElement();
                if (ColorBrush(Attr(Child(properties, "shd"), "fill")) is { } fill) targetCell.Background = fill;
                var borders = Child(properties, "tcBorders") ?? Child(Child(node, "tblPr"), "tblBorders");
                if (borders is not null)
                {
                    double Side(string side) { var border = Child(borders, side) ?? Child(borders, side is "left" or "right" ? "insideV" : "insideH"); return border is null || Attr(border, "val") is "nil" or "none" ? 0 : Bound(Number(Attr(border, "sz"), 4) / 6, 0, 8); }
                    targetCell.BorderThickness = new Thickness(Side("left"), Side("top"), Side("right"), Side("bottom"));
                    targetCell.BorderBrush = ColorBrush(borders.Elements().Select(b => Attr(b, "color")).FirstOrDefault(c => c is not null)) ?? Brushes.Black;
                }
                WordBlocks(cell, targetCell.Blocks);
                if (targetCell.Blocks.Count == 0) targetCell.Blocks.Add(new Paragraph());
                target.Cells.Add(targetCell);
                if (merge is not null) merges[column] = targetCell; else merges.Remove(column);
                column += span;
            }
        }
        return table;
    }
}
