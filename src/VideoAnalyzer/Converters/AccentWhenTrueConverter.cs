using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VideoAnalyzer.Converters;

/// <summary>
/// Paints a toolbar icon with the accent colour while its toggle is on, so an active
/// mode is visible without adding a separate indicator.
/// </summary>
public sealed class AccentWhenTrueConverter : IValueConverter
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
        => value is true ? On : Off;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
