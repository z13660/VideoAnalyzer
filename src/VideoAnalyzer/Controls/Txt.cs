using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VideoAnalyzer.Controls;

/// <summary>Shared typography for the hand-drawn analyser widgets.</summary>
internal static class Txt
{
    public static readonly Typeface Mono = new(
        new FontFamily("Consolas, Cascadia Mono, Courier New"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static readonly Typeface MonoBold = new(
        new FontFamily("Consolas, Cascadia Mono, Courier New"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static FormattedText Make(string text, double size, Brush brush, double pixelsPerDip, bool bold = false)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
               bold ? MonoBold : Mono, size, brush, pixelsPerDip);
}
