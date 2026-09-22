using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VideoAnalyzer.Converters;

/// <summary>
/// Shows an element when the bound boolean is false. Used to swap the play and pause
/// glyphs of the transport button without duplicating the whole button in a template.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
