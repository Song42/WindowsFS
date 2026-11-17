using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsFS.App.Controls;

public sealed record MapItem(string Label, string Detail, long Size, string Path, bool IsDirectory)
{
    public bool IsFreeSpace { get; init; }
}

public enum SpaceMapLayout { Blocks, Donut }

/// <summary>Bounded, retained-data space charts. They never access the file system.</summary>
public sealed class SpaceMap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<MapItem>), typeof(SpaceMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, ItemsChanged));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode), typeof(SpaceMapLayout), typeof(SpaceMap),
        new FrameworkPropertyMetadata(SpaceMapLayout.Blocks, FrameworkPropertyMetadataOptions.AffectsRender, LayoutModeChanged),
        value => value is SpaceMapLayout.Blocks or SpaceMapLayout.Donut);

    private static readonly Brush[] Palette =
    [
        FrozenBrush("#B7A5F5"), FrozenBrush("#7EAAE8"), FrozenBrush("#63B9A9"),
        FrozenBrush("#DB9F8A"), FrozenBrush("#C2B178"), FrozenBrush("#929DDD"),
        FrozenBrush("#91C8C4"), FrozenBrush("#BA9EBF")
    ];
    private static readonly Brush Ink = FrozenBrush("#172033");
    private static readonly Brush MutedInk = FrozenBrush("#39445A");
    private static readonly Brush EmptyInk = FrozenBrush("#9BA4B5");
    private static readonly Brush FreeSpaceFill = FrozenBrush("#AAB5C5");
    private static readonly Brush HoverOverlay = FrozenBrush("#20FFFFFF");
    private static readonly Brush LightInk = FrozenBrush("#F0F1F5");
    private static readonly Pen SegmentBorder = FrozenPen("#1A1D25", 1);
    private static readonly Pen SelectionBorder = FrozenPen("#FFFFFF", 2);
    private static readonly Typeface TextFace = new("Segoe UI, Malgun Gothic");
    private static readonly Typeface HeadingFace = new(new FontFamily("Segoe UI, Malgun Gothic"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private MapItem[] _items = [];
    private Rect[] _bounds = [];
    private Geometry[] _segments = [];
    private Rect[] _legendBounds = [];
    private Point _donutCenter;
    private double _outerRadius;
    private double _innerRadius;
    private long _totalBytes;
    private Size _layoutSize;
    private bool _layoutDirty = true;
    private Rect _legendArea;
    private int _legendCount;
    private int _hovered = -1;
    private int _selected = -1;

    public SpaceMap()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "용량 지도 · 방향키: 항목 선택 · Enter: 폴더 열기");
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 20000);
    }

    public IEnumerable<MapItem>? Items
    {
        get => (IEnumerable<MapItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public SpaceMapLayout LayoutMode
    {
        get => (SpaceMapLayout)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public event EventHandler<MapItem>? ItemInvoked;

    private static void ItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var map = (SpaceMap)sender;
        var ordered = (map.Items ?? []).Where(item => item.Size > 0)
            // Keep free space identifiable even when tiny folders are grouped or the legend is short.
            .OrderByDescending(item => item.IsFreeSpace).ThenByDescending(item => item.Size).ToArray();
        // Keep drawing and hit testing bounded without discarding represented bytes.
        if (ordered.Length > 36)
        {
            var remainingBytes = ordered.Skip(35).Sum(item => item.Size);
            ordered = [.. ordered.Take(35), new MapItem("기타", "나머지 항목", remainingBytes, "", false)];
        }
        map._items = ordered;
        map._bounds = new Rect[ordered.Length];
        map._totalBytes = ordered.Sum(item => item.Size);
        map.ResetInteraction();
    }

    private static void LayoutModeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SpaceMap)sender).ResetInteraction();

    private void ResetInteraction()
    {
        _segments = [];
        _legendBounds = [];
        _layoutDirty = true;
        _hovered = -1;
        _selected = -1;
        ToolTip = null;
        Cursor = Cursors.Arrow;
        AutomationProperties.SetName(this, "용량 지도 · 방향키: 항목 선택 · Enter: 폴더 열기");
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        // A transparent fill makes empty space and tiny tiles participate in hit testing.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_items.Length == 0)
        {
            DrawCentered(drawingContext, "표시할 용량 없음", 15, ActualHeight / 2 - 22);
            DrawCentered(drawingContext, "0 B 항목 제외", 12, ActualHeight / 2 + 7);
            return;
        }

        if (LayoutMode == SpaceMapLayout.Donut)
        {
            DrawDonut(drawingContext);
            return;
        }

        LayoutTiles(0, _items.Length, new Rect(0, 0, ActualWidth, ActualHeight));
        for (var index = 0; index < _items.Length; index++)
        {
            var rect = _bounds[index];
            if (rect.Width < 3 || rect.Height < 3) continue;
            rect.Inflate(-2, -2);
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
            drawingContext.DrawRoundedRectangle(ItemFill(index), null, rect, 6, 6);
            if (_hovered == index)
                drawingContext.DrawRoundedRectangle(HoverOverlay, null, rect, 6, 6);
            if (IsKeyboardFocused && _selected == index)
                drawingContext.DrawRoundedRectangle(null, new Pen(Brushes.White, 2), rect, 6, 6);
            if (rect.Width < 45 || rect.Height < 24) continue;

            var inset = rect.Width < 100 ? 8 : 12;
            drawingContext.PushClip(new RectangleGeometry(rect));
            var label = Text(_items[index].Label, rect.Width > 170 ? 14 : 12, Ink, HeadingFace);
            label.MaxTextWidth = Math.Max(1, rect.Width - inset * 2);
            label.MaxTextHeight = 23;
            label.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(label, new Point(rect.Left + inset, rect.Top + 9));
            if (rect.Height >= 52)
            {
                var detail = Text(_items[index].Detail, 11, MutedInk, TextFace);
                detail.MaxTextWidth = Math.Max(1, rect.Width - inset * 2);
                detail.MaxTextHeight = 18;
                detail.Trimming = TextTrimming.CharacterEllipsis;
                drawingContext.DrawText(detail, new Point(rect.Left + inset, rect.Top + 31));
            }
            drawingContext.Pop();
        }
    }

    private void DrawDonut(DrawingContext drawingContext)
    {
        EnsureDonutLayout();
        for (var index = 0; index < _segments.Length; index++)
        {
            drawingContext.DrawGeometry(ItemFill(index), SegmentBorder, _segments[index]);
            if (_hovered == index) drawingContext.DrawGeometry(HoverOverlay, null, _segments[index]);
            if (IsKeyboardFocused && _selected == index)
                drawingContext.DrawGeometry(null, SelectionBorder, _segments[index]);
        }

        var highlighted = _hovered >= 0 ? _hovered : _selected;
        if (_innerRadius >= 24)
        {
            var width = _innerRadius * 2 - 16;
            drawingContext.PushClip(new EllipseGeometry(_donutCenter, _innerRadius - 4, _innerRadius - 4));
            DrawLine(drawingContext, highlighted >= 0 ? _items[highlighted].Label : "합계",
                new Rect(_donutCenter.X - width / 2, _donutCenter.Y - 32, width, 20), 12, EmptyInk, TextFace, TextAlignment.Center);
            DrawLine(drawingContext, FormatBytes(highlighted >= 0 ? _items[highlighted].Size : _totalBytes),
                new Rect(_donutCenter.X - width / 2, _donutCenter.Y - 10, width, 28), _innerRadius >= 60 ? 21 : 16,
                LightInk, HeadingFace, TextAlignment.Center);
            DrawLine(drawingContext, highlighted >= 0 ? Share(_items[highlighted]) : $"{_items.Length:N0}개 항목",
                new Rect(_donutCenter.X - width / 2, _donutCenter.Y + 19, width, 18), 11, EmptyInk, TextFace, TextAlignment.Center);
            drawingContext.Pop();
        }

        for (var index = 0; index < _legendCount; index++)
        {
            var row = _legendBounds[index];
            if (_hovered == index || IsKeyboardFocused && _selected == index)
                drawingContext.DrawRoundedRectangle(HoverOverlay, null, row, 4, 4);
            drawingContext.DrawRoundedRectangle(ItemFill(index), null,
                new Rect(row.Left + 4, row.Top + 7, 8, 8), 2, 2);
            var labelWidth = Math.Max(1, row.Width - 115);
            DrawLine(drawingContext, _items[index].Label, new Rect(row.Left + 20, row.Top + 2, labelWidth, 20),
                11, LightInk, TextFace);
            DrawLine(drawingContext, $"{FormatBytes(_items[index].Size)} · {Share(_items[index])}",
                new Rect(row.Right - 100, row.Top + 2, 96, 20), 10, EmptyInk, TextFace, TextAlignment.Right);
        }
        if (_legendCount < _items.Length && _legendArea.Height >= 18)
            DrawLine(drawingContext, $"외 {_items.Length - _legendCount:N0}개 · 방향키 탐색",
                new Rect(_legendArea.Left + 4, _legendArea.Top + _legendCount * 24, _legendArea.Width - 8, 18),
                10, EmptyInk, TextFace);
    }

    private void EnsureDonutLayout()
    {
        if (!_layoutDirty && _layoutSize == RenderSize) return;
        _layoutSize = RenderSize;
        _layoutDirty = false;
        _segments = new Geometry[_items.Length];
        _legendBounds = new Rect[_items.Length];
        Array.Fill(_legendBounds, Rect.Empty);
        Rect chartArea;
        if (ActualWidth >= 480 && ActualHeight >= 160)
        {
            chartArea = new Rect(0, 0, ActualWidth * 0.54, ActualHeight);
            _legendArea = new Rect(chartArea.Right + 12, 8, Math.Max(1, ActualWidth - chartArea.Width - 12), ActualHeight - 16);
        }
        else
        {
            var chartHeight = Math.Min(ActualHeight, Math.Max(Math.Min(160, ActualHeight), ActualHeight * 0.64));
            chartArea = new Rect(0, 0, ActualWidth, chartHeight);
            _legendArea = new Rect(0, chartHeight + 8, ActualWidth, Math.Max(0, ActualHeight - chartHeight - 8));
        }
        _outerRadius = Math.Max(0, Math.Min(chartArea.Width, chartArea.Height) / 2 - 5);
        _innerRadius = _outerRadius * 0.62;
        _donutCenter = new Point(chartArea.Left + chartArea.Width / 2, chartArea.Top + chartArea.Height / 2);
        var start = -Math.PI / 2;
        for (var index = 0; index < _items.Length; index++)
        {
            var end = index == _items.Length - 1 ? Math.PI * 1.5 : start + Math.Tau * _items[index].Size / _totalBytes;
            _segments[index] = CreateSegment(start, end, _items.Length == 1);
            start = end;
        }
        _legendCount = Math.Min(_items.Length, (int)(_legendArea.Height / 24));
        if (_legendCount < _items.Length)
            _legendCount = Math.Max(0, Math.Min(_items.Length, (int)((_legendArea.Height - 18) / 24)));
        if (ActualWidth >= 480 && ActualHeight >= 160)
        {
            var contentHeight = _legendCount * 24 + (_legendCount < _items.Length ? 18 : 0);
            _legendArea.Y += Math.Max(0, (_legendArea.Height - contentHeight) / 2);
            _legendArea.Height = Math.Min(_legendArea.Height, contentHeight);
        }
        for (var index = 0; index < _legendCount; index++)
            _legendBounds[index] = new Rect(_legendArea.Left, _legendArea.Top + index * 24, _legendArea.Width, 24);
    }

    private Geometry CreateSegment(double start, double end, bool fullRing)
    {
        if (fullRing)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(_donutCenter, _outerRadius, _outerRadius));
            ring.Children.Add(new EllipseGeometry(_donutCenter, _innerRadius, _innerRadius));
            ring.Freeze();
            return ring;
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(AtRadius(_outerRadius, start), isFilled: true, isClosed: true);
            context.ArcTo(AtRadius(_outerRadius, end), new Size(_outerRadius, _outerRadius), 0,
                end - start > Math.PI, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            context.LineTo(AtRadius(_innerRadius, end), isStroked: true, isSmoothJoin: false);
            context.ArcTo(AtRadius(_innerRadius, start), new Size(_innerRadius, _innerRadius), 0,
                end - start > Math.PI, SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private Point AtRadius(double radius, double angle) =>
        new(_donutCenter.X + Math.Cos(angle) * radius, _donutCenter.Y + Math.Sin(angle) * radius);

    private void DrawLine(DrawingContext drawingContext, string value, Rect area, double size,
        Brush brush, Typeface typeface, TextAlignment alignment = TextAlignment.Left)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        var text = Text(value, size, brush, typeface);
        text.MaxTextWidth = area.Width;
        text.MaxTextHeight = area.Height;
        text.Trimming = TextTrimming.CharacterEllipsis;
        text.TextAlignment = alignment;
        drawingContext.DrawText(text, area.TopLeft);
    }

    private Brush ItemFill(int index) => _items[index].IsFreeSpace ? FreeSpaceFill : Palette[index % Palette.Length];
    private string Share(MapItem item) => $"{(double)item.Size / _totalBytes * 100:0.#}%";

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var number = (double)value;
        var unit = 0;
        while (number >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
        return $"{number.ToString(unit == 0 ? "N0" : "0.##", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private void LayoutTiles(int start, int count, Rect area)
    {
        if (count == 1)
        {
            _bounds[start] = area;
            return;
        }
        var total = 0d;
        for (var i = start; i < start + count; i++) total += _items[i].Size;
        var first = (double)_items[start].Size;
        var split = 1;
        while (split < count - 1)
        {
            var next = first + _items[start + split].Size;
            if (Math.Abs(next - total / 2) > Math.Abs(first - total / 2)) break;
            first = next;
            split++;
        }
        var ratio = first / total;
        if (area.Width >= area.Height)
        {
            var width = area.Width * ratio;
            LayoutTiles(start, split, new Rect(area.Left, area.Top, width, area.Height));
            LayoutTiles(start + split, count - split,
                new Rect(area.Left + width, area.Top, area.Width - width, area.Height));
        }
        else
        {
            var height = area.Height * ratio;
            LayoutTiles(start, split, new Rect(area.Left, area.Top, area.Width, height));
            LayoutTiles(start + split, count - split,
                new Rect(area.Left, area.Top + height, area.Width, area.Height - height));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTile(e.GetPosition(this));
        if (hit == _hovered) return;
        _hovered = hit;
        Cursor = hit >= 0 && _items[hit].IsDirectory && !string.IsNullOrEmpty(_items[hit].Path) ? Cursors.Hand : Cursors.Arrow;
        ToolTip = hit >= 0 ? Describe(_items[hit]) : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = -1;
        ToolTip = null;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Select(HitTile(e.GetPosition(this)));
        InvokeSelected();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Length == 0) return;
        if (e.Key is Key.Right or Key.Down)
            Select((_selected + 1) % _items.Length);
        else if (e.Key is Key.Left or Key.Up)
            Select((_selected <= 0 ? _items.Length : _selected) - 1);
        else if (e.Key is Key.Enter or Key.Space)
            InvokeSelected();
        else if (e.Key == Key.Home)
            Select(0);
        else if (e.Key == Key.End)
            Select(_items.Length - 1);
        else return;
        _hovered = -1;
        ToolTip = null;
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_selected < 0 && _items.Length > 0) Select(0);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    private void Select(int index)
    {
        _selected = index;
        if (index >= 0)
            AutomationProperties.SetName(this, "용량 지도: " + Describe(_items[index]));
        else AutomationProperties.SetName(this, "용량 지도 · 방향키: 항목 선택 · Enter: 폴더 열기");
        InvalidateVisual();
    }

    private void InvokeSelected()
    {
        if (_selected >= 0 && _items[_selected].IsDirectory && !string.IsNullOrEmpty(_items[_selected].Path))
            ItemInvoked?.Invoke(this, _items[_selected]);
    }

    private int HitTile(Point point)
    {
        if (LayoutMode == SpaceMapLayout.Donut)
        {
            if (_items.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) return -1;
            EnsureDonutLayout();
            for (var index = 0; index < _legendBounds.Length; index++)
                if (_legendBounds[index].Contains(point)) return index;
            for (var index = 0; index < _segments.Length; index++)
                if (_segments[index].FillContains(point)) return index;
            return -1;
        }
        for (var i = 0; i < _bounds.Length; i++)
            if (_bounds[i].Contains(point)) return i;
        return -1;
    }

    private string Describe(MapItem item) => string.IsNullOrEmpty(item.Path)
        ? $"{item.Label}\n{FormatBytes(item.Size)} · {Share(item)}"
        : $"{item.Label}\n{FormatBytes(item.Size)} · {Share(item)}\n{item.Path}";

    private FormattedText Text(string value, double size, Brush brush, Typeface typeface) => new(
        value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawCentered(DrawingContext drawingContext, string value, double size, double top)
    {
        var text = Text(value, size, EmptyInk, TextFace);
        text.MaxTextWidth = Math.Max(1, ActualWidth - 24);
        text.TextAlignment = TextAlignment.Center;
        drawingContext.DrawText(text, new Point(12, top));
    }

    private static SolidColorBrush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string value, double thickness)
    {
        var pen = new Pen(FrozenBrush(value), thickness);
        pen.Freeze();
        return pen;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpaceMapPeer(this);

    private sealed class SpaceMapPeer(SpaceMap owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(SpaceMap);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
    }
}
