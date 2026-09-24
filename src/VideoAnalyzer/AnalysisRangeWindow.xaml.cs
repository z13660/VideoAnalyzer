using System.Globalization;
using System.Windows;
using VideoAnalyzer.Controls;

namespace VideoAnalyzer;

/// <summary>
/// Asks which stretch of the clip the deep passes should cover.
///
/// The range can always be set by zooming the timeline and asking for the visible slice, but
/// that only works when the interesting part is what happens to be on screen. Typing the two
/// times is what makes the range a setting rather than a side effect of navigation, so the
/// fields open prefilled with the visible range — the common case is still one click.
/// </summary>
public partial class AnalysisRangeWindow : Window
{
    private readonly double _duration;
    private readonly double _viewFrom;
    private readonly double _viewTo;

    /// <summary>Set when the dialog is accepted.</summary>
    public double RangeFrom { get; private set; }

    /// <summary>Set when the dialog is accepted.</summary>
    public double RangeTo { get; private set; }

    public AnalysisRangeWindow(double durationSeconds, double viewFrom, double viewTo)
    {
        InitializeComponent();

        _duration = Math.Max(0.1, durationSeconds);
        _viewFrom = Math.Clamp(viewFrom, 0, _duration);
        _viewTo = Math.Clamp(viewTo, _viewFrom, _duration);

        DurationText.Text = $"素材时长 {TimelineStrip.FormatClock(_duration, true)}";
        Show(_viewFrom, _viewTo);

        Loaded += (_, _) =>
        {
            FromBox.Focus();
            FromBox.SelectAll();
        };
    }

    private void Show(double from, double to)
    {
        FromBox.Text = TimelineStrip.FormatClock(from, true);
        ToBox.Text = TimelineStrip.FormatClock(to, true);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnUseViewClick(object sender, RoutedEventArgs e) => Show(_viewFrom, _viewTo);

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseTime(FromBox.Text, out var from) || !TryParseTime(ToBox.Text, out var to))
        {
            Complain("时间看不懂：秒数、mm:ss 或 hh:mm:ss 都行，例如 12.5 或 0:12.5。");
            return;
        }

        from = Math.Clamp(from, 0, _duration);
        to = Math.Clamp(to, 0, _duration);

        if (to - from < 0.1)
        {
            Complain($"结束时间要比起始时间至少晚 0.1 秒（当前 {to - from:0.###} 秒）。");
            return;
        }

        RangeFrom = from;
        RangeTo = to;
        DialogResult = true;
    }

    private void Complain(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Accepts the shapes the timeline prints: <c>12.5</c>, <c>0:12.5</c>, <c>00:00:12.5</c>.
    /// The last group may carry the fraction, the ones before it are whole minutes and hours.
    /// </summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        var parts = (text ?? string.Empty).Trim().Split(':');
        if (parts.Length is 0 or > 3) return false;

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return false;
            if (v < 0) return false;
            total = total * 60 + v;
        }

        seconds = total;
        return true;
    }
}
