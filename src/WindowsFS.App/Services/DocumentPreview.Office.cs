using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace WindowsFS.App.Services;

internal sealed partial class DocumentPreview
{
    private static Canvas PageCanvas(double width, double height) => new()
    { Width = Bound(width, 200), Height = Bound(height, 200), Background = Brushes.White, ClipToBounds = true };

    private static void Place(Canvas canvas, FrameworkElement item, double x, double y, double width, double height)
    {
        item.Width = Bound(width, 1); item.Height = Bound(height, 1);
        Canvas.SetLeft(item, Bound(x, -2000)); Canvas.SetTop(item, Bound(y, -2000));
        canvas.Children.Add(item);
    }

    private void Workbook()
    {
        var shared = Desc(OptionalXml("xl/sharedStrings.xml"), "si").Select(x => string.Concat(Desc(x, "t").Select(t => t.Value))).ToArray();
        var styles = OptionalXml("xl/styles.xml")?.Root;
        var formats = Children(Child(styles, "cellXfs"), "xf").ToArray();
        var fonts = Children(Child(styles, "fonts"), "font").ToArray();
        var fills = Children(Child(styles, "fills"), "fill").ToArray();
        var borders = Children(Child(styles, "borders"), "border").ToArray();
        var relationships = Relationships("xl/workbook.xml");
        var sheets = Desc(Xml("xl/workbook.xml"), "sheet").Where(s => Attr(s, "state") is not ("hidden" or "veryHidden")).ToArray();
        _limited |= sheets.Length > 10;
        foreach (var sheet in sheets.Take(10))
        {
            var id = Attr(sheet, "id");
            if (id is null || !relationships.TryGetValue(id, out var path)) continue;
            var xml = Xml(path);
            var rows = Desc(xml, "row").ToArray();
            var cells = rows.Take(200).SelectMany((r, index) => Children(r, "c").Take(50).Select(c => (Cell: c, Position: CellPosition(Attr(c, "r"), index)))).ToArray();
            _limited |= rows.Length > 200 || cells.Any(c => c.Position.Column >= 50 || c.Position.Row >= 200);
            var rowCount = Math.Clamp(cells.Select(c => c.Position.Row + 1).DefaultIfEmpty(1).Max(), 1, 200);
            var columnCount = Math.Clamp(cells.Select(c => c.Position.Column + 1).DefaultIfEmpty(1).Max(), 1, 50);
            var widths = Enumerable.Repeat(90d, columnCount).ToArray();
            var heights = Enumerable.Repeat(25d, rowCount).ToArray();
            foreach (var col in Desc(xml, "col"))
                for (var i = Math.Max(0, Integer(Attr(col, "min"), 1) - 1); i < Math.Min(columnCount, Integer(Attr(col, "max"), 1)); i++)
                    widths[i] = Attr(col, "hidden") == "1" ? 0 : Bound(Number(Attr(col, "width"), 12) * 7 + 5, 10, 400);
            for (var i = 0; i < Math.Min(rows.Length, 200); i++)
            {
                var number = Integer(Attr(rows[i], "r"), i + 1) - 1;
                if (number >= 0 && number < rowCount) heights[number] = Attr(rows[i], "hidden") == "1" ? 0 : Bound(Number(Attr(rows[i], "ht"), 18.75) * 4 / 3, 10, 200);
            }
            var merges = Desc(xml, "mergeCell").Select(m => (Attr(m, "ref") ?? "").Split(':')).Where(m => m.Length == 2)
                .Select(m => (Start: CellPosition(m[0], 0), End: CellPosition(m[1], 0))).Take(500).ToArray();
            var xOffsets = Offsets(widths); var yOffsets = Offsets(heights);
            var drawings = SheetPictures(xml, path, xOffsets, yOffsets);
            // Tile wide/tall sheets instead of shrinking an entire workbook to an unreadable thumbnail.
            var xTiles = Tiles(widths, 1040); var yTiles = Tiles(heights, 740);
            foreach (var (startRow, endRow) in yTiles)
            foreach (var (startColumn, endColumn) in xTiles)
            {
                token.ThrowIfCancellationRequested();
                if (_canvases.Count >= PageLimit) { _limited = true; return; }
                var canvas = PageCanvas(Math.Max(400, xOffsets[endColumn] - xOffsets[startColumn] + 40), Math.Max(240, yOffsets[endRow] - yOffsets[startRow] + 70));
                Place(canvas, new TextBlock { Text = Text(Attr(sheet, "name") ?? "시트"), Foreground = Brushes.Black, FontSize = 17, FontWeight = FontWeights.Bold }, 20, 12, canvas.Width - 40, 30);
                foreach (var item in cells.Where(c => c.Position.Row >= startRow && c.Position.Row < endRow && c.Position.Column >= startColumn && c.Position.Column < endColumn))
                {
                    LayoutElement();
                    var (row, column) = item.Position;
                    if (heights[row] == 0 || widths[column] == 0) continue;
                    if (merges.Any(m => row >= m.Start.Row && row <= m.End.Row && column >= m.Start.Column && column <= m.End.Column && (row != m.Start.Row || column != m.Start.Column))) continue;
                    var merge = merges.FirstOrDefault(m => m.Start == item.Position);
                    var endC = Math.Min(endColumn, merge == default ? column + 1 : merge.End.Column + 1);
                    var endR = Math.Min(endRow, merge == default ? row + 1 : merge.End.Row + 1);
                    if (endC <= column || endR <= row) continue;
                    var cell = item.Cell;
                    var xf = At(formats, Integer(Attr(cell, "s")));
                    var value = Child(cell, "v")?.Value ?? "";
                    if (Attr(cell, "t") == "s") value = At(shared, Integer(value, -1)) ?? "";
                    else if (Attr(cell, "t") == "inlineStr") value = string.Concat(Desc(cell, "t").Select(t => t.Value));
                    else if (Attr(cell, "t") == "b") value = value == "1" ? "TRUE" : "FALSE";
                    else value = CellNumber(value, Integer(Attr(xf, "numFmtId")));
                    var label = new TextBlock { Text = Text(value), Foreground = Brushes.Black, FontSize = 14, Padding = new Thickness(4, 2, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
                    var font = At(fonts, Integer(Attr(xf, "fontId")));
                    if (font is not null)
                    {
                        var run = new Run(); WordRunProperties(run, font);
                        label.FontWeight = run.FontWeight; label.FontStyle = run.FontStyle;
                        label.FontSize = Bound(Number(Attr(Child(font, "sz"), "val"), 11) * 4 / 3, 6, 144);
                        SetFont(run, Attr(Child(font, "name"), "val")); label.FontFamily = run.FontFamily;
                        label.Foreground = ColorBrush(Attr(Child(font, "color"), "rgb")) ?? Brushes.Black;
                    }
                    var alignment = Child(xf, "alignment");
                    label.TextWrapping = Attr(alignment, "wrapText") == "1" ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    label.TextAlignment = Attr(alignment, "horizontal") switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => double.TryParse(value, out _) ? TextAlignment.Right : TextAlignment.Left };
                    var border = At(borders, Integer(Attr(xf, "borderId")));
                    var container = new Border { Child = label, ClipToBounds = true, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(.5), Background = ColorBrush(Attr(Desc(At(fills, Integer(Attr(xf, "fillId"))), "fgColor").FirstOrDefault(), "rgb")) ?? Brushes.White };
                    if (border is not null && border.Elements().Any(b => Attr(b, "style") is not null))
                    {
                        double Edge(string side) => Attr(Child(border, side), "style") is { } style ? style is "medium" or "thick" or "double" ? 2 : 1 : .5;
                        container.BorderThickness = new Thickness(Edge("left"), Edge("top"), Edge("right"), Edge("bottom"));
                        container.BorderBrush = ColorBrush(Attr(Desc(border, "color").FirstOrDefault(), "rgb")) ?? Brushes.Black;
                    }
                    Place(canvas, container, 20 + xOffsets[column] - xOffsets[startColumn], 50 + yOffsets[row] - yOffsets[startRow], xOffsets[endC] - xOffsets[column], yOffsets[endR] - yOffsets[row]);
                }
                foreach (var (imagePath, x, y, width, height) in drawings)
                    if (x >= xOffsets[startColumn] && x < xOffsets[endColumn] && y >= yOffsets[startRow] && y < yOffsets[endRow] && Picture(imagePath, width, height) is { } picture)
                        Place(canvas, picture, 20 + x - xOffsets[startColumn], 50 + y - yOffsets[startRow], width, height);
                _canvases.Add(canvas);
            }
            _approximate |= Desc(xml, "conditionalFormatting").Any();
        }
    }

    private static T? At<T>(T[] array, int index) where T : class => index >= 0 && index < array.Length ? array[index] : null;
    private static (int Row, int Column) CellPosition(string? address, int fallback)
    {
        var column = 0; var offset = 0;
        foreach (var c in address ?? "") { if (!char.IsAsciiLetter(c)) break; column = Math.Min(10000, column * 26 + char.ToUpperInvariant(c) - 'A' + 1); offset++; }
        return (Math.Max(0, Integer(address?[offset..], fallback + 1) - 1), Math.Max(0, column - 1));
    }
    private static double[] Offsets(double[] values) { var offsets = new double[values.Length + 1]; for (var i = 0; i < values.Length; i++) offsets[i + 1] = offsets[i] + values[i]; return offsets; }
    private static List<(int Start, int End)> Tiles(double[] values, double maximum)
    {
        var result = new List<(int, int)>(); var start = 0; var length = 0d;
        for (var i = 0; i < values.Length; i++) { if (length + values[i] > maximum && i > start) { result.Add((start, i)); start = i; length = 0; } length += values[i]; }
        result.Add((start, values.Length)); return result;
    }
    private static string CellNumber(string value, int format)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return value;
        return format switch
        {
            1 => number.ToString("0", CultureInfo.InvariantCulture), 2 => number.ToString("0.00", CultureInfo.InvariantCulture),
            3 => number.ToString("#,##0", CultureInfo.InvariantCulture), 4 => number.ToString("#,##0.00", CultureInfo.InvariantCulture),
            9 => number.ToString("0%", CultureInfo.InvariantCulture), 10 => number.ToString("0.00%", CultureInfo.InvariantCulture),
            >= 14 and <= 22 when number >= 1 && number < 2958465 => DateTime.FromOADate(number).ToString(format >= 18 ? "HH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private List<(string Path, double X, double Y, double Width, double Height)> SheetPictures(XDocument sheet, string owner, double[] columns, double[] rows)
    {
        var result = new List<(string, double, double, double, double)>();
        var relationships = Relationships(owner);
        foreach (var drawing in Desc(sheet, "drawing"))
        {
            if (Attr(drawing, "id") is not { } id || !relationships.TryGetValue(id, out var path)) continue;
            var xml = Xml(path); var images = Relationships(path);
            foreach (var anchor in xml.Root?.Elements().Take(40) ?? [])
            {
                var imageId = Attr(Desc(anchor, "blip").FirstOrDefault(), "embed");
                if (imageId is null || !images.TryGetValue(imageId, out var imagePath)) { _approximate = true; continue; }
                (double X, double Y) Point(XElement? point) => (columns[Math.Clamp(Integer(Child(point, "col")?.Value), 0, columns.Length - 1)] + Number(Child(point, "colOff")?.Value) / 9525,
                    rows[Math.Clamp(Integer(Child(point, "row")?.Value), 0, rows.Length - 1)] + Number(Child(point, "rowOff")?.Value) / 9525);
                var start = Point(Child(anchor, "from")); var end = Point(Child(anchor, "to")); var extent = Child(anchor, "ext");
                result.Add((imagePath, start.X, start.Y, Bound(extent is null ? end.X - start.X : Number(Attr(extent, "cx")) / 9525, 1), Bound(extent is null ? end.Y - start.Y : Number(Attr(extent, "cy")) / 9525, 1)));
            }
        }
        return result;
    }

    private void Presentation()
    {
        var xml = Xml("ppt/presentation.xml"); var relationships = Relationships("ppt/presentation.xml");
        var size = Desc(xml, "sldSz").FirstOrDefault();
        var slides = Desc(xml, "sldId").ToArray(); _limited |= slides.Length > 20;
        foreach (var slide in slides.Take(20))
        {
            var id = slide.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Length > 0)?.Value;
            if (id is null || !relationships.TryGetValue(id, out var path)) continue;
            var content = Xml(path); var images = Relationships(path);
            var canvas = PageCanvas(Number(Attr(size, "cx"), 9144000) / 9525, Number(Attr(size, "cy"), 5143500) / 9525);
            canvas.Background = DrawingFill(Desc(content, "bgPr").FirstOrDefault()) ?? Brushes.White;
            foreach (var shape in Desc(content, "spTree").FirstOrDefault()?.Elements().Take(500) ?? [])
            {
                LayoutElement();
                var transform = Desc(shape, "xfrm").FirstOrDefault();
                var off = Child(transform, "off"); var ext = Child(transform, "ext");
                var x = Number(Attr(off, "x")) / 9525; var y = Number(Attr(off, "y")) / 9525;
                var width = Bound(Number(Attr(ext, "cx"), 3810000) / 9525, 1); var height = Bound(Number(Attr(ext, "cy"), 952500) / 9525, 1);
                if (shape.Name.LocalName == "pic")
                {
                    var imageId = Attr(Desc(shape, "blip").FirstOrDefault(), "embed");
                    if (Picture(imageId is not null && images.TryGetValue(imageId, out var imagePath) ? imagePath : null, width, height) is { } picture) Place(canvas, picture, x, y, width, height);
                }
                else if (shape.Name.LocalName == "sp")
                {
                    var label = DrawingText(Child(shape, "txBody"));
                    var properties = Child(shape, "spPr");
                    var box = new Border { Child = label, Background = DrawingFill(properties), BorderBrush = DrawingFill(Child(properties, "ln")), BorderThickness = new Thickness(Bound(Number(Attr(Child(properties, "ln"), "w")) / 9525, 0, 12)) };
                    var rotation = Number(Attr(transform, "rot")) / 60000;
                    if (rotation != 0) { box.RenderTransformOrigin = new Point(.5, .5); box.RenderTransform = new RotateTransform(rotation); }
                    Place(canvas, box, x, y, width, height);
                }
                else if (Desc(shape, "tbl").FirstOrDefault() is { } table) Place(canvas, DrawingTable(table), x, y, width, height);
                else if (shape.Name.LocalName is "grpSp" or "graphicFrame") _approximate = true;
            }
            _canvases.Add(canvas);
        }
    }

    private static Brush? DrawingFill(XElement? properties) => ColorBrush(Attr(Child(Child(properties, "solidFill"), "srgbClr"), "val"));
    private TextBlock DrawingText(XElement? body)
    {
        var label = new TextBlock { Foreground = Brushes.Black, FontSize = 24, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6), ClipToBounds = true };
        foreach (var paragraph in Children(body, "p"))
        {
            LayoutElement();
            if (label.Inlines.Count > 0) label.Inlines.Add(new LineBreak());
            var properties = Child(paragraph, "pPr");
            label.TextAlignment = Attr(properties, "algn") switch { "ctr" => TextAlignment.Center, "r" => TextAlignment.Right, _ => TextAlignment.Left };
            foreach (var child in paragraph.Elements())
            {
                if (child.Name.LocalName == "br") { label.Inlines.Add(new LineBreak()); continue; }
                if (child.Name.LocalName is not ("r" or "fld")) continue;
                var run = new Run(Text(Child(child, "t")?.Value ?? ""));
                var style = Child(child, "rPr") ?? Child(properties, "defRPr");
                run.FontSize = Bound(Number(Attr(style, "sz"), 1800) / 75, 6, 144);
                run.FontWeight = Attr(style, "b") == "1" ? FontWeights.Bold : FontWeights.Normal;
                run.FontStyle = Attr(style, "i") == "1" ? FontStyles.Italic : FontStyles.Normal;
                run.Foreground = DrawingFill(style) ?? Brushes.Black;
                SetFont(run, Attr(Child(style, "ea"), "typeface") ?? Attr(Child(style, "latin"), "typeface"));
                if (Attr(style, "u") is not (null or "none")) run.TextDecorations = TextDecorations.Underline;
                label.Inlines.Add(run);
            }
        }
        return label;
    }
    private Grid DrawingTable(XElement table)
    {
        var grid = new Grid { ClipToBounds = true }; var rowIndex = 0;
        foreach (var column in Children(Child(table, "tblGrid"), "gridCol").Take(50)) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Bound(Number(Attr(column, "w"), 1), 1, 1e9), GridUnitType.Star) });
        if (grid.ColumnDefinitions.Count == 0) grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var row in Children(table, "tr").Take(200))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Bound(Number(Attr(row, "h"), 1), 1, 1e9), GridUnitType.Star) }); var column = 0;
            foreach (var cell in Children(row, "tc").Take(grid.ColumnDefinitions.Count))
            {
                var box = new Border { Child = DrawingText(Child(cell, "txBody")), Background = DrawingFill(Child(cell, "tcPr")) ?? Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.5) };
                Grid.SetRow(box, rowIndex); Grid.SetColumn(box, column++); grid.Children.Add(box);
            }
            rowIndex++;
        }
        return grid;
    }
}
