using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VideoAnalyzer.Controls;

namespace VideoAnalyzer.Converters;

/// <summary>
/// Lights the overlay button up while any overlay is active, so the toolbar shows at a glance
/// that something is drawn over the picture.
/// </summary>
public sealed class OverlayAccentConverter : IValueConverter
{
    private static readonly Brush On = Freeze(Color.FromRgb(0x7F, 0xB4, 0xFF));
    private static readonly Brush Off = Freeze(Color.FromRgb(0x6B, 0x76, 0x88));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is OverlayMode mode && mode != OverlayMode.None ? On : Off;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
