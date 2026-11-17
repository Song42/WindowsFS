using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WindowsFS.App.Controls;

/// <summary>Keeps the map width stable and reserves usable space for the result list.</summary>
public sealed class SpaceMapVisibilityConverter : IMultiValueConverter
{
    public const double MapWidth = 360;
    public const double MinimumMapWidth = 280;
    public const double MaximumMapWidth = 960;
    public const double MinimumListWidth = 480;
    public const double MinimumCleanupWidth = 680;
    public const double SystemPanelColumnWidth = 326 + 16;
    public const double ResizeSlack = 4;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var mapWidth = values.Length >= 4 && values[3] is double requested && double.IsFinite(requested)
            ? Math.Clamp(requested, MinimumMapWidth, MaximumMapWidth) : MapWidth;
        var columnWidth = mapWidth + 16;
        var width = values.Length >= 3 && values[2] is double available && double.IsFinite(available) ? available : 0;
        var hasDetails = values.Length >= 2 && values[1] is true;
        var minimumList = values.Length >= 6 && values[5] is true ? MinimumCleanupWidth : MinimumListWidth;
        var customDetails = values.Length >= 5 && values[4] is double preferred && double.IsFinite(preferred)
            ? Math.Max(SystemPanelColumnWidth, preferred + 16) : double.NaN;
        var reservedDetails = hasDetails ? double.IsFinite(customDetails) ? customDetails : SystemPanelColumnWidth : 0;
        var visible = values.Length > 0 && values[0] is true &&
            width >= columnWidth + minimumList + reservedDetails + ResizeSlack;
        var remaining = Math.Max(0, width - (visible ? columnWidth : 0));
        var details = hasDetails ? Math.Min(Math.Max(0, remaining - minimumList),
            double.IsFinite(customDetails) ? customDetails : Math.Max(SystemPanelColumnWidth, remaining / 2.4)) : 0;
        return (parameter as string) switch
        {
            "Width" => new GridLength(visible ? columnWidth : 0),
            "DetailsWidth" => new GridLength(Math.Floor(details)),
            _ => visible ? Visibility.Visible : Visibility.Collapsed
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
