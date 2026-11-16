using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsFS.App.Controls;

public sealed record MapItem(string Label, string Detail, long Size, string Path, bool IsDirectory);

/// <summary>A bounded, retained-data treemap. It never accesses the file system.</summary>
public sealed class SpaceMap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<MapItem>), typeof(SpaceMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, ItemsChanged));

    private static readonly Brush[] Palette =
    [
        FrozenBrush("#B7A5F5"), FrozenBrush("#7EAAE8"), FrozenBrush("#63B9A9"),
        FrozenBrush("#DB9F8A"), FrozenBrush("#C2B178"), FrozenBrush("#929DDD"),
        FrozenBrush("#91C8C4"), FrozenBrush("#BA9EBF")
    ];
    private static readonly Brush Ink = FrozenBrush("#172033");
    private static readonly Brush MutedInk = FrozenBrush("#39445A");
    private static readonly Brush EmptyInk = FrozenBrush("#9BA4B5");
    private static readonly Brush HoverOverlay = FrozenBrush("#20FFFFFF");
    private static readonly Typeface TextFace = new("Segoe UI, Malgun Gothic");
    private static readonly Typeface HeadingFace = new(new FontFamily("Segoe UI, Malgun Gothic"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private MapItem[] _items = [];
    private Rect[] _bounds = [];
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

    public event EventHandler<MapItem>? ItemInvoked;

    private static void ItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var map = (SpaceMap)sender;
        var ordered = (map.Items ?? []).Where(item => item.Size > 0)
            .OrderByDescending(item => item.Size).ToArray();
        // Keep drawing and hit testing bounded without discarding represented bytes.
        if (ordered.Length > 36)
        {
            var remainingBytes = ordered.Skip(35).Sum(item => item.Size);
            ordered = [.. ordered.Take(35), new MapItem("기타", "나머지 항목", remainingBytes, "", false)];
        }
        map._items = ordered;
        map._bounds = new Rect[ordered.Length];
        map._hovered = -1;
        map._selected = -1;
        map.ToolTip = null;
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

        LayoutTiles(0, _items.Length, new Rect(0, 0, ActualWidth, ActualHeight));
        for (var index = 0; index < _items.Length; index++)
        {
            var rect = _bounds[index];
            if (rect.Width < 3 || rect.Height < 3) continue;
            rect.Inflate(-2, -2);
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
            drawingContext.DrawRoundedRectangle(Palette[index % Palette.Length], null, rect, 6, 6);
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
        Cursor = hit >= 0 && !string.IsNullOrEmpty(_items[hit].Path) ? Cursors.Hand : Cursors.Arrow;
        ToolTip = hit >= 0 ? Describe(_items[hit]) : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = -1;
        ToolTip = null;
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
        InvalidateVisual();
    }

    private void InvokeSelected()
    {
        if (_selected >= 0 && !string.IsNullOrEmpty(_items[_selected].Path))
            ItemInvoked?.Invoke(this, _items[_selected]);
    }

    private int HitTile(Point point)
    {
        for (var i = 0; i < _bounds.Length; i++)
            if (_bounds[i].Contains(point)) return i;
        return -1;
    }

    private static string Describe(MapItem item) => string.IsNullOrEmpty(item.Path)
        ? $"{item.Label}\n{item.Detail}"
        : $"{item.Label}\n{item.Detail}\n{item.Path}";

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

    protected override AutomationPeer OnCreateAutomationPeer() => new SpaceMapPeer(this);

    private sealed class SpaceMapPeer(SpaceMap owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(SpaceMap);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
    }
}
