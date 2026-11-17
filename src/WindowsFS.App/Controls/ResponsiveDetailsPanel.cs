using System.Windows;
using System.Windows.Controls;

namespace WindowsFS.App.Controls;

/// <summary>One or two columns, with each row sized to its tallest field.</summary>
public sealed class ResponsiveDetailsPanel : Panel
{
    private const double MinimumFieldWidth = 240;
    private const double ColumnGap = 16;

    private static int Columns(double width) => width >= MinimumFieldWidth * 2 + ColumnGap ? 2 : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinimumFieldWidth;
        var columns = Columns(width);
        var fieldWidth = Math.Max(0, (width - (columns - 1) * ColumnGap) / columns);
        double height = 0;
        for (var row = 0; row < InternalChildren.Count; row += columns)
        {
            double rowHeight = 0;
            for (var column = 0; column < columns && row + column < InternalChildren.Count; column++)
            {
                var child = InternalChildren[row + column];
                child.Measure(new Size(fieldWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var fieldWidth = Math.Max(0, (finalSize.Width - (columns - 1) * ColumnGap) / columns);
        double y = 0;
        for (var row = 0; row < InternalChildren.Count; row += columns)
        {
            double rowHeight = 0;
            for (var column = 0; column < columns && row + column < InternalChildren.Count; column++)
                rowHeight = Math.Max(rowHeight, InternalChildren[row + column].DesiredSize.Height);
            for (var column = 0; column < columns && row + column < InternalChildren.Count; column++)
                InternalChildren[row + column].Arrange(new Rect(column * (fieldWidth + ColumnGap), y, fieldWidth, rowHeight));
            y += rowHeight;
        }
        return finalSize;
    }
}
