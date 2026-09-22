using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoAnalyzer.Models;
using VideoAnalyzer.Services;

namespace VideoAnalyzer.Controls;

/// <summary>Time ruler under the graph stack: tick labels, playhead and a time read-out.</summary>
public sealed class TimelineStrip : FrameworkElement
{
    private static readonly Brush BackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x0D)));
    private static readonly Brush TickBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x74, 0x7A)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xA7)));
    private static readonly Brush PlayheadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush ChipBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x23)));
    private static readonly Brush ChipTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static readonly double[] NiceSteps = { 0.04, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(TimelineStrip),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewStartProperty = DependencyProperty.Register(
        nameof(ViewStart), typeof(double), typeof(TimelineStrip),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewEndProperty = DependencyProperty.Register(
        nameof(ViewEnd), typeof(double), typeof(TimelineStrip),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Length of the clip. The view is allowed to reach past both ends — that is what keeps a
    /// pinned playhead exactly centred from the first frame to the last — so the ruler needs to
    /// know where the clip stops before it starts labelling empty time.
    /// </summary>
    public static readonly DependencyProperty ClipSecondsProperty = DependencyProperty.Register(
        nameof(ClipSeconds), typeof(double), typeof(TimelineStrip),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// True while the host holds the playhead in the middle of the view. It decides what a drag
    /// means: with the playhead fixed the strip is what moves, so a drag grabs the strip and the
    /// content travels with the cursor; with the playhead free, the playhead follows the cursor.
    /// </summary>
    public static readonly DependencyProperty PinPlayheadProperty = DependencyProperty.Register(
        nameof(PinPlayhead), typeof(bool), typeof(TimelineStrip),
        new PropertyMetadata(true));

    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double ViewStart { get => (double)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => (double)GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }
    public double ClipSeconds { get => (double)GetValue(ClipSecondsProperty); set => SetValue(ClipSecondsProperty, value); }
    public bool PinPlayhead { get => (bool)GetValue(PinPlayheadProperty); set => SetValue(PinPlayheadProperty, value); }

    /// <summary>Raised continuously while dragging; the host shows a cheap preview.</summary>
    public event EventHandler<double>? ScrubPreview;

    /// <summary>Raised on release (or a plain click); the host performs the real seek.</summary>
    public event EventHandler<double>? ScrubCommit;

    /// <summary>Raised on the wheel: seconds under the cursor plus the zoom factor.</summary>
    public event EventHandler<(double seconds, double factor)>? ZoomRequested;

    private const double DragThreshold = 4;     // DIP: below this a press counts as a click

    private bool _dragging, _dragged;
    private double _dragStartX, _dragStartTime, _dragStartPosition, _dragSpan;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        double x = e.GetPosition(this).X;
        _dragging = true;
        _dragged = false;
        CaptureMouse();

        // A press moves nothing by itself. What it means is only known on release, when it turns
        // out to have been a click (seek to the point pressed) or a drag (move the strip).
        _dragStartX = x;
        _dragStartTime = TimeAt(x);
        _dragStartPosition = Position;
        _dragSpan = Math.Max(1e-6, ViewEnd - ViewStart);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;

        double x = e.GetPosition(this).X;
        if (!_dragged && Math.Abs(x - _dragStartX) < DragThreshold) return;

        _dragged = true;
        ScrubPreview?.Invoke(this, DragTime(x));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        double x = e.GetPosition(this).X;
        ScrubCommit?.Invoke(this, _dragged ? DragTime(x) : _dragStartTime);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomRequested?.Invoke(this, (TimeAt(e.GetPosition(this).X), e.Delta > 0 ? 0.6 : 1.0 / 0.6));
        e.Handled = true;
    }

    private double TimeAt(double x)
    {
        double frac = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
        return ViewStart + frac * (ViewEnd - ViewStart);
    }

    /// <summary>
    /// Time the gesture points at, measured from where the gesture started rather than from the
    /// live ruler mapping — the view may have scrolled underneath since.
    ///
    /// With the playhead pinned the strip is what moves, so a drag grabs the strip and the
    /// content travels with the cursor: drag right and earlier frames come back under the
    /// playhead. Reading the time off the ruler instead would scroll the view on every preview
    /// and feed that scroll back into the cursor, which is what once made a press on 2 s commit
    /// as 4 s. With the playhead free, the playhead itself is what the cursor drags.
    /// </summary>
    private double DragTime(double x)
    {
        double travel = (x - _dragStartX) / Math.Max(1, ActualWidth) * _dragSpan;
        return PinPlayhead ? _dragStartPosition - travel : _dragStartTime + travel;
    }

    public TimelineStrip()
    {
        Height = 24;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(BackBrush, null, new Rect(0, 0, w, h));

        double span = Math.Max(1e-6, ViewEnd - ViewStart);
        double step = NiceSteps[^1];
        foreach (var s in NiceSteps)
        {
            if (s / span * w >= 62) { step = s; break; }
        }

        double first = Math.Ceiling(ViewStart / step) * step;
        double clip = ClipSeconds;
        for (double t = first; t <= ViewEnd + 1e-9; t += step)
        {
            // The pinned-playhead view runs past both ends of the clip so the playhead can stay
            // centred; there is no time to label out there, and labelling it would read as a
            // longer file than the one that is loaded.
            if (t < -1e-9) continue;
            if (clip > 0 && t > clip + 1e-6) continue;

            double x = (t - ViewStart) / span * w;
            if (x < 0 || x > w) continue;

            dc.DrawRectangle(TickBrush, null, new Rect(x, h - 7, 1, 5));

            var ft = Txt.Make(FormatTick(t, step), 11.5, LabelBrush, dpi);
            double tx = Math.Clamp(x - ft.Width / 2, 1, Math.Max(1, w - ft.Width - 1));
            dc.DrawText(ft, new Point(tx, 2));
        }

        double px = (Position - ViewStart) / span * w;
        if (px < 0 || px > w) return;

        dc.DrawRectangle(PlayheadBrush, null, new Rect(px, 0, 1, h));

        var text = Txt.Make(FormatClock(Position, true), 12.5, ChipTextBrush, dpi, bold: true);
        double chipW = text.Width + 10, chipH = text.Height + 2;
        double chipX = Math.Clamp(px - chipW / 2, 0, Math.Max(0, w - chipW));
        dc.DrawRectangle(ChipBrush, null, new Rect(chipX, h - chipH - 1, chipW, chipH));
        dc.DrawText(text, new Point(chipX + 5, h - chipH));
    }

    public static string FormatClock(double seconds, bool withMillis = false)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        string head = t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
        return withMillis ? $"{head}.{t.Milliseconds:000}" : head;
    }

    /// <summary>
    /// Ruler label. Once the ticks are closer than a second, plain m:ss would print the
    /// same text several times in a row, so the fraction is shown instead.
    /// </summary>
    private static string FormatTick(double seconds, double step)
    {
        if (step >= 1) return FormatClock(seconds);

        if (seconds < 60)
            return step >= 0.1 ? $"{seconds:0.0}s" : $"{seconds:0.00}s";

        return step >= 0.1
            ? FormatClock(seconds) + $".{Math.Floor(seconds * 10) % 10:0}"
            : FormatClock(seconds) + $".{Math.Floor(seconds * 100) % 100:00}";
    }
}
