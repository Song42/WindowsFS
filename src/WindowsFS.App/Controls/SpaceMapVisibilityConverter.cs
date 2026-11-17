using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WindowsFS.App.Controls;

/// <summary>Keeps the map width stable and reserves usable space for the result list.</summary>
public sealed class SpaceMapVisibilityConverter : IMultiValueConverter
{
    public const double MapWidth = 360;
    public const double MapColumnWidth = MapWidth + 16;
    public const double MinimumListWidth = 480;
    public const double SystemPanelColumnWidth = 326 + 16;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = values.Length >= 3 && values[0] is true && values[1] is bool hasSystemInfo &&
            values[2] is double width && double.IsFinite(width) &&
            width >= MapColumnWidth + MinimumListWidth + (hasSystemInfo ? SystemPanelColumnWidth : 0);
        return parameter as string == "Width"
            ? new GridLength(visible ? MapColumnWidth : 0)
            : visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
