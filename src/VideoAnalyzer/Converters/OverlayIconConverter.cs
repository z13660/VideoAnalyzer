using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VideoAnalyzer.Controls;

namespace VideoAnalyzer.Converters;

/// <summary>
/// Picks the overlay button's glyph from the current mode.
///
/// The button cycles through four states, so a fixed glyph left the user guessing which one was
/// active. Each mode now has its own mark, which makes the cycle readable at a glance.
/// </summary>
public sealed class OverlayIconConverter : IValueConverter
{
    // three arrows of increasing length
    private static readonly Geometry Vectors = Geometry.Parse(
        "M 0.5,3.4 L 4.6,3.4 M 2.8,1.6 L 4.8,3.4 L 2.8,5.2 " +
        "M 0.5,6.9 L 7.6,6.9 M 5.8,5.1 L 7.8,6.9 L 5.8,8.7 " +
        "M 0.5,10.4 L 10.6,10.4 M 8.8,8.6 L 10.8,10.4 L 8.8,12.2");

    // scattered specks of varying size
    private static readonly Geometry Noise = Geometry.Parse(
        "M 2.4,3.2 A 1.3,1.3 0 1 1 2.3,3.2 Z M 7.2,2.2 A 0.9,0.9 0 1 1 7.1,2.2 Z " +
        "M 5.2,7.2 A 1.7,1.7 0 1 1 5.1,7.2 Z M 10.4,6.1 A 1.1,1.1 0 1 1 10.3,6.1 Z " +
        "M 2.6,10.4 A 1.4,1.4 0 1 1 2.5,10.4 Z M 8.2,10.8 A 0.9,0.9 0 1 1 8.1,10.8 Z");

    // 3x3 block grid
    private static readonly Geometry Grid = Geometry.Parse(
        "M 0.5,0.5 L 12.5,0.5 L 12.5,12.5 L 0.5,12.5 Z " +
        "M 4.5,0.5 L 4.5,12.5 M 8.5,0.5 L 8.5,12.5 " +
        "M 0.5,4.5 L 12.5,4.5 M 0.5,8.5 L 12.5,8.5");

    // a crossed-out frame
    private static readonly Geometry Off = Geometry.Parse(
        "M 0.8,1.2 L 12.2,1.2 L 12.2,12.4 L 0.8,12.4 Z M 1.8,11.6 L 11.2,2.2");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            OverlayMode.MotionVectors => Vectors,
            OverlayMode.BlockNoise => Noise,
            OverlayMode.MacroblockGrid => Grid,
            _ => Off
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
