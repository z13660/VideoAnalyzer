using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoAnalyzer.Models;
using VideoAnalyzer.Services;

namespace VideoAnalyzer.Controls;

public enum GraphKind
{
    Bitrate,
    FrameType,
    Qp,
    Motion,
    Gop,
    Reorder
}

/// <summary>
/// One row of the analyser graph stack.
///
/// The row is drawn as two cached layers. The static layer holds the background, the title and
/// the axis labels — everything that does not depend on where the view is looking. The data
/// layer holds the plot itself, rendered for a time range that reaches <see cref="CacheMargin"/>
/// past both edges of the visible view and blitted at an offset.
///
/// That split is what makes a playhead pinned to the centre affordable: with the view scrolling
/// under a stationary playhead the visible range changes on every frame, and re-rendering
/// thousands of bars per frame would not keep up. Instead each frame costs one blit, and the
/// data layer is only rebuilt once the view has travelled a third of a span.
/// </summary>
public sealed class FrameGraph : FrameworkElement
{
    // ---- palette ---------------------------------------------------------
    private static readonly Brush BackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x08, 0x0A, 0x0C)));
    private static readonly Brush TitleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA8, 0xAE, 0xB4)));
    private static readonly Brush AxisBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8C, 0x93, 0x99)));
    private static readonly Brush ValueBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF6)));
    private static readonly Brush PlayheadBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush ValueChipBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC4, 0x0B, 0x0E, 0x13)));
    private static readonly Brush AvgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)));

    private static readonly Brush BarBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB4, 0xB9, 0xBE)));
    private static readonly Brush BarDimBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x60)));
    private static readonly Brush IBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x2B, 0x2B)));
    private static readonly Brush PBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8B, 0xE8)));
    private static readonly Brush BBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x35, 0xC7, 0x59)));
    private static readonly Brush KeyBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)));
    private static readonly Brush QpLineBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0xD2, 0x3A)));
    private static readonly Brush QpBandBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0x7A, 0x74, 0x10)));
    private static readonly Brush MotionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0xB4, 0x24)));
    private static readonly Brush GopBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0xC7, 0xC7)));
    private static readonly Brush ReorderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x1E)));
    private static readonly Brush ReorderLineBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA5, 0x6B, 0xF0)));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    // ---- dependency properties -------------------------------------------
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(FrameGraph),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(GraphKind), typeof(FrameGraph),
        new FrameworkPropertyMetadata(GraphKind.Bitrate, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(
        nameof(Result), typeof(AnalysisResult), typeof(FrameGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(FrameGraph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewStartProperty = DependencyProperty.Register(
        nameof(ViewStart), typeof(double), typeof(FrameGraph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewEndProperty = DependencyProperty.Register(
        nameof(ViewEnd), typeof(double), typeof(FrameGraph),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// True while the host holds the playhead in the middle of the view. It decides what a drag
    /// means: with the playhead fixed the strip is what moves, so a drag grabs the strip and the
    /// content travels with the cursor; with the playhead free, the playhead follows the cursor.
    /// </summary>
    public static readonly DependencyProperty PinPlayheadProperty = DependencyProperty.Register(
        nameof(PinPlayhead), typeof(bool), typeof(FrameGraph),
        new FrameworkPropertyMetadata(true));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public GraphKind Kind { get => (GraphKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public AnalysisResult? Result { get => (AnalysisResult?)GetValue(ResultProperty); set => SetValue(ResultProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double ViewStart { get => (double)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => (double)GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }
    public bool PinPlayhead { get => (bool)GetValue(PinPlayheadProperty); set => SetValue(PinPlayheadProperty, value); }

    // ---- geometry --------------------------------------------------------
    private const double AxisWidth = 74;

    /// <summary>
    /// The plot reaches from <see cref="TopPad"/> down to <see cref="BottomPad"/> from the
    /// bottom edge. The band is deliberately wide: the rows are only 42 px tall, and the
    /// picture-type lanes need enough height to be told apart.
    /// </summary>
    private const double TopPad = 12;
    private const double BottomPad = 4;

    /// <summary>
    /// Extra time rendered on either side of the visible range, as a fraction of the span.
    /// The data layer stays valid while the view travels this far, so a scrolling playhead
    /// pays for one rebuild per margin of travel instead of one per frame.
    /// </summary>
    private const double CacheMargin = 0.4;

    private RenderTargetBitmap? _staticLayer;
    private RenderTargetBitmap? _dataLayer;
    private double _layerW = -1, _layerH = -1;
    private double _dataStart, _dataEnd, _dataSpan = -1, _dataDipW;
    private bool _stale = true;
    private Geometry? _plotClip;
    private double _clipW = -1;

    /// <summary>Rebuild timings, logged while they are still interesting.</summary>
    private static int _logBudget = 40;

    public FrameGraph()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        MinHeight = 40;
        Cursor = Cursors.Hand;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var result = Result;

        EnsureLayers(w, h, dpi);

        if (_staticLayer is not null)
            dc.DrawImage(_staticLayer, new Rect(0, 0, _staticLayer.PixelWidth / dpi, _staticLayer.PixelHeight / dpi));

        double plotW = PlotWidth(w);
        double span = Math.Max(1e-6, ViewEnd - ViewStart);

        if (_dataLayer is not null && result is not null && result.FrameCount > 0)
        {
            // Where the left edge of the data layer lands inside the plot: the layer starts
            // before the view, so this is negative. Snapped to a whole device pixel — the layer
            // holds one-pixel bars, and a fractional offset would smear them into grey while
            // scrolling.
            double x = (_dataStart - ViewStart) / span * plotW;
            x = Math.Round(x * dpi) / dpi;

            dc.PushClip(PlotClip(plotW, h));
            dc.DrawImage(_dataLayer, new Rect(x, 0, _dataDipW, _dataLayer.PixelHeight / dpi));
            dc.Pop();
        }

        if (result is null || result.FrameCount == 0) return;

        double px = (Position - ViewStart) / span * plotW;
        if (px >= 0 && px <= plotW)
            dc.DrawRectangle(PlayheadBrush, null, new Rect(px, 0, 1, h));

        DrawCurrentValue(dc, result, plotW, dpi);
    }

    private Geometry PlotClip(double plotW, double h)
    {
        if (_plotClip is null || Math.Abs(_clipW - plotW) > 0.5)
        {
            var g = new RectangleGeometry(new Rect(0, 0, plotW, h));
            g.Freeze();
            _plotClip = g;
            _clipW = plotW;
        }
        return _plotClip;
    }

    private void DrawCurrentValue(DrawingContext dc, AnalysisResult result, double plotW, double dpi)
    {
        var frame = result.FrameAt(Position);
        if (frame is null) return;

        string text = Kind switch
        {
            GraphKind.Bitrate => $"{frame.BitrateKbps:0} kbps",
            GraphKind.Qp => $"qp {frame.QpAvg ?? 0:0.#}",
            GraphKind.Motion => $"{frame.MotionMean ?? 0:0.##} px",
            GraphKind.Gop => $"+{frame.GopPosition}",
            GraphKind.Reorder => $"{(frame.ReorderDelta >= 0 ? "+" : "")}{frame.ReorderDelta}",
            _ => frame.TypeLetter
        };

        var ft = Txt.Make(text, 12.5, ValueBrush, dpi, bold: true);
        double x = Math.Max(4, (plotW - ft.Width) / 2);
        double y = 2;

        // translucent chip keeps the value legible over whatever the trace is doing
        dc.DrawRectangle(ValueChipBrush, null, new Rect(x - 6, y - 2, ft.Width + 12, ft.Height + 3));
        dc.DrawText(ft, new Point(x, y));
    }

    // ------------------------------------------------------------------ cache

    private void EnsureLayers(double w, double h, double dpi)
    {
        bool resized = Math.Abs(_layerW - w) > 0.5 || Math.Abs(_layerH - h) > 0.5;

        if (_stale || resized || _staticLayer is null)
        {
            BuildStatic(w, h, dpi);
            _dataLayer = null;
            _stale = false;
        }

        var result = Result;
        if (result is null || result.FrameCount == 0)
        {
            _dataLayer = null;
            return;
        }

        double span = Math.Max(1e-6, ViewEnd - ViewStart);
        bool staleData = _dataLayer is null
                         || Math.Abs(_dataSpan - span) > span * 1e-6
                         || ViewStart < _dataStart
                         || ViewEnd > _dataEnd;

        if (staleData) BuildData(w, h, dpi, span);
    }

    /// <summary>Background, title and axis labels. Independent of the visible time range.</summary>
    private void BuildStatic(double w, double h, double dpi)
    {
        _layerW = w;
        _layerH = h;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(BackBrush, null, new Rect(0, 0, w, h));

            var ft = Txt.Make(Title, 12, TitleBrush, dpi);
            dc.DrawText(ft, new Point(6, 0));

            var r = Result;
            if (r is not null && r.FrameCount > 0)
            {
                switch (Kind)
                {
                    case GraphKind.Bitrate: DrawBitrateAxis(dc, r, w, h, dpi); break;
                    case GraphKind.FrameType: DrawFrameTypeAxis(dc, w, h, dpi); break;
                    case GraphKind.Qp: DrawQpAxis(dc, r, w, h, dpi); break;
                    case GraphKind.Motion: DrawMotionAxis(dc, r, w, h, dpi); break;
                    case GraphKind.Gop: DrawGopAxis(dc, r, w, h, dpi); break;
                    case GraphKind.Reorder: DrawReorderAxis(dc, r, w, h, dpi); break;
                }
            }
        }

        _staticLayer = Snapshot(visual, w, h, dpi);
    }

    /// <summary>The plot itself, rendered for a range wider than the view.</summary>
    private void BuildData(double w, double h, double dpi, double span)
    {
        double plotW = PlotWidth(w);
        double pxPerSec = plotW / span;
        double margin = span * CacheMargin;

        _dataStart = ViewStart - margin;
        _dataEnd = ViewEnd + margin;
        _dataSpan = span;

        double dipW = (_dataEnd - _dataStart) * pxPerSec;
        var map = new TimeMap(_dataStart, _dataEnd, pxPerSec);
        var r = Result!;

        var visual = new DrawingVisual();
        var clock = Stopwatch.StartNew();
        using (var dc = visual.RenderOpen())
        {
            switch (Kind)
            {
                case GraphKind.Bitrate: DrawBitrate(dc, r, map, h); break;
                case GraphKind.FrameType: DrawFrameTypes(dc, r, map, h); break;
                case GraphKind.Qp: DrawQp(dc, r, map, h); break;
                case GraphKind.Motion: DrawMotion(dc, r, map, h); break;
                case GraphKind.Gop: DrawGop(dc, r, map, h); break;
                case GraphKind.Reorder: DrawReorder(dc, r, map, h); break;
            }
        }

        _dataLayer = Snapshot(visual, dipW, h, dpi);
        _dataDipW = _dataLayer.PixelWidth / dpi;

        clock.Stop();
        double ms = clock.Elapsed.TotalMilliseconds;
        if (ms >= 12 || _logBudget > 0)
        {
            if (_logBudget > 0) _logBudget--;
            AppLog.Write($"graph: {Kind} layer {_dataLayer.PixelWidth}x{_dataLayer.PixelHeight} " +
                         $"for {span:0.###}s rebuilt in {ms:0.0} ms");
        }
    }

    private static RenderTargetBitmap Snapshot(DrawingVisual visual, double w, double h, double dpi)
    {
        int pw = Math.Max(1, (int)Math.Ceiling(w * dpi));
        int ph = Math.Max(1, (int)Math.Ceiling(h * dpi));

        var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Drops both cached layers. Also forces a repaint, so a streaming analysis
    /// shows up on screen without waiting for some unrelated property to change.</summary>
    public void InvalidateCache()
    {
        _stale = true;
        InvalidateVisual();
    }

    private double PlotWidth(double w) => Math.Max(1, w - AxisWidth);

    private void DrawAxisLabel(DrawingContext dc, string text, double x, double y, double dpi, Brush? brush = null)
    {
        var ft = Txt.Make(text, AxisFontSize, brush ?? AxisBrush, dpi);
        dc.DrawText(ft, new Point(x, Math.Max(0, y - ft.Height / 2)));
    }

    private const double AxisFontSize = 10.5;

    /// <summary>
    /// Evenly spread positions for the scale labels down a row.
    ///
    /// The rows are 42 px tall and the bitrate scale needs four labels, so they end up about
    /// nine pixels apart. That is tight, but it is what the reference strip does, and the
    /// alternative — dropping the intermediate steps — loses the shape of the log scale.
    /// </summary>
    private static double[] LabelYs(double h, int count)
    {
        var ys = new double[count];
        double step = (h - 6) / count;
        for (int i = 0; i < count; i++) ys[i] = 3 + step * i;
        return ys;
    }

    /// <summary>
    /// A point within a fraction of a pixel of the previous one adds nothing but cost. Stroking
    /// a geometry is the expensive part of drawing a line row, and at the zoom levels where a
    /// whole clip fits in the strip most of the points are redundant.
    /// </summary>
    private static bool SameSpot(double x, double y, double px, double py)
        => Math.Abs(x - px) < 0.4 && Math.Abs(y - py) < 0.4;

    // ---------------------------------------------------------------- kinds

    private void DrawBitrate(DrawingContext dc, AnalysisResult r, in TimeMap map, double h)
    {
        double top = TopPad, bottom = h - BottomPad;
        double max = Math.Max(1, r.MaxBitrateKbps);
        double logMax = Math.Log2(max + 1);

        var (from, to) = Visible(r, map);
        for (int i = from; i < to; i++)
        {
            var f = r.Frames[i];
            double norm = Math.Log2(Math.Max(0, f.BitrateKbps) + 1) / logMax;
            double bh = norm * (bottom - top);
            double x = map.XOf(r, i);
            double barW = Math.Max(0.6, map.WidthOf(r, i));
            var brush = f.Type == PicType.I ? BarBrush : BarDimBrush;
            dc.DrawRectangle(brush, null, new Rect(x, bottom - bh, Math.Max(0.6, barW - 0.2), bh));
        }
    }

    private void DrawBitrateAxis(DrawingContext dc, AnalysisResult r, double w, double h, double dpi)
    {
        double max = Math.Max(1, r.MaxBitrateKbps);
        var ys = LabelYs(h, 4);
        double x = w - AxisWidth + 6;

        // log2 steps, matching the reference layout
        DrawAxisLabel(dc, $"{max:0}", x, ys[0], dpi);
        DrawAxisLabel(dc, $"{max / 2:0}", x, ys[1], dpi);
        DrawAxisLabel(dc, $"{max / 4:0}", x, ys[2], dpi);
        DrawAxisLabel(dc, $"avg {r.AvgBitrateKbps:0}", x, ys[3], dpi, AvgBrush);
    }

    private void DrawFrameTypes(DrawingContext dc, AnalysisResult r, in TimeMap map, double h)
    {
        double top = TopPad + 2, bottom = h - BottomPad;

        // three stacked lanes: I on top, P middle, B bottom (like the reference strip)
        double lane = (bottom - top) / 3.0;

        var (from, to) = Visible(r, map);
        for (int i = from; i < to; i++)
        {
            var f = r.Frames[i];
            double x = map.XOf(r, i);
            double barW = Math.Max(0.7, map.WidthOf(r, i));
            double y = f.Type switch
            {
                PicType.I => top,
                PicType.P => top + lane,
                _ => top + lane * 2
            };
            var brush = f.Type switch
            {
                PicType.I => IBrush,
                PicType.P => PBrush,
                _ => BBrush
            };
            dc.DrawRectangle(brush, null, new Rect(x, y, Math.Max(0.7, barW - 0.2), lane - 1));

            if (f.KeyFrame)
                dc.DrawRectangle(KeyBrush, null, new Rect(x, top - 2, 1.4, bottom - top + 4));
        }
    }

    private void DrawFrameTypeAxis(DrawingContext dc, double w, double h, double dpi)
    {
        double top = TopPad + 2, bottom = h - BottomPad;
        double lane = (bottom - top) / 3.0;

        // one letter per lane, in that lane's colour: without it the three rows of marks are
        // indistinguishable from each other
        DrawAxisLabel(dc, "I", w - AxisWidth + 6, top + lane * 0.5, dpi, IBrush);
        DrawAxisLabel(dc, "P", w - AxisWidth + 6, top + lane * 1.5, dpi, PBrush);
        DrawAxisLabel(dc, "B", w - AxisWidth + 6, top + lane * 2.5, dpi, BBrush);
    }

    private void DrawQp(DrawingContext dc, AnalysisResult r, in TimeMap map, double h)
    {
        double top = TopPad, bottom = h - BottomPad;
        double maxQp = Math.Max(1, r.MaxQp);

        var (from, to) = Visible(r, map);

        // min-max band
        var band = new StreamGeometry();
        using (var g = band.Open())
        {
            bool started = false;
            double px = 0, py = 0;
            for (int i = from; i < to; i++)
            {
                var f = r.Frames[i];
                double x = map.XOf(r, i);
                double yMax = bottom - (f.QpMax ?? 0) / maxQp * (bottom - top);
                if (started && SameSpot(x, yMax, px, py)) continue;
                if (!started) { g.BeginFigure(new Point(x, yMax), false, false); started = true; }
                else g.LineTo(new Point(x, yMax), true, false);
                px = x; py = yMax;
            }
            px = py = 0;
            for (int i = to - 1; i >= from; i--)
            {
                var f = r.Frames[i];
                double x = map.XOf(r, i);
                double yMin = bottom - (f.QpMin ?? 0) / maxQp * (bottom - top);
                if (SameSpot(x, yMin, px, py)) continue;
                g.LineTo(new Point(x, yMin), true, false);
                px = x; py = yMin;
            }
        }
        band.Freeze();
        dc.DrawGeometry(QpBandBrush, null, band);

        // average line
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            bool started = false;
            double px = 0, py = 0;
            for (int i = from; i < to; i++)
            {
                var f = r.Frames[i];
                if (!f.QpAvg.HasValue) continue;
                double x = map.XOf(r, i);
                double y = bottom - f.QpAvg.Value / maxQp * (bottom - top);
                if (started && SameSpot(x, y, px, py)) continue;
                if (!started) { g.BeginFigure(new Point(x, y), false, false); started = true; }
                else g.LineTo(new Point(x, y), true, false);
                px = x; py = y;
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(QpLineBrush, 1), line);
    }

    private void DrawQpAxis(DrawingContext dc, AnalysisResult r, double w, double h, double dpi)
    {
        double maxQp = Math.Max(1, r.MaxQp);
        var ys = LabelYs(h, 3);
        double x = w - AxisWidth + 6;

        DrawAxisLabel(dc, $"{maxQp:0}", x, ys[0], dpi);
        DrawAxisLabel(dc, $"{maxQp / 2:0}", x, ys[1], dpi);
        DrawAxisLabel(dc, "0", x, ys[2], dpi);
    }

    private void DrawMotion(DrawingContext dc, AnalysisResult r, in TimeMap map, double h)
    {
        double top = TopPad, bottom = h - BottomPad;
        double max = Math.Max(1, r.MaxMotion);

        var (from, to) = Visible(r, map);

        // One bar per frame, drawn the way the GOP row draws: zoomed in far enough that the
        // frames are tens of pixels apart, a one-pixel spike is just a hairline and the value
        // cannot be read off it. Filling also costs an order of magnitude less than stroking.
        for (int i = from; i < to; i++)
        {
            var f = r.Frames[i];
            double v = f.MotionMean ?? 0;
            if (v <= 0) continue;

            double x = map.XOf(r, i);
            double y = bottom - Math.Min(1, v / max) * (bottom - top);
            double barW = Math.Max(0.7, map.WidthOf(r, i) * 0.8);
            dc.DrawRectangle(MotionBrush, null, new Rect(x, y, barW, bottom - y));
        }
    }

    private void DrawMotionAxis(DrawingContext dc, AnalysisResult r, double w, double h, double dpi)
    {
        double max = Math.Max(1, r.MaxMotion);
        var ys = LabelYs(h, 2);
        double x = w - AxisWidth + 6;

        DrawAxisLabel(dc, $"{max:0.#} px", x, ys[0], dpi);
        DrawAxisLabel(dc, "0", x, ys[1], dpi);
    }

    private void DrawGop(DrawingContext dc, AnalysisResult r, in TimeMap map, double h)
    {
        double top = TopPad, bottom = h - BottomPad;
        int max = Math.Max(1, r.MaxGop);

        var (from, to) = Visible(r, map);
        for (int i = from; i < to; i++)
        {
            var f = r.Frames[i];
            if (f.GopPosition == 0) continue;
            double x = map.XOf(r, i);
            double y = bottom - f.GopPosition / (double)max * (bottom - top);
            double wBar = Math.Max(0.7, map.WidthOf(r, i) * 0.8);
            dc.DrawRectangle(GopBrush, null, new Rect(x, y, wBar, bottom - y));
        }
    }

    private void DrawGopAxis(DrawingContext dc, AnalysisResult r, double w, double h, double dpi)
    {
        int max = Math.Max(1, r.MaxGop);
        var ys = LabelYs(h, 2);
        double x = w - AxisWidth + 6;

        DrawAxisLabel(dc, $"{max}", x, ys[0], dpi);
        DrawAxisLabel(dc, "0", x, ys[1], dpi);
    }

    private void DrawReorder(DrawingContext dc, AnalysisResult r, in TimeMap map, double h)
    {
        double top = TopPad, bottom = h - BottomPad;
        int max = Math.Max(1, r.MaxReorder);
        int min = Math.Min(0, r.MinReorder);
        int range = Math.Max(1, max - min);

        var (from, to) = Visible(r, map);
        double mid = bottom - (0 - min) / (double)range * (bottom - top);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool started = false;
            double px = 0, py = 0;
            for (int i = from; i < to; i++)
            {
                var f = r.Frames[i];
                double x = map.XOf(r, i);
                double y = bottom - (f.ReorderDelta - min) / (double)range * (bottom - top);
                if (started && SameSpot(x, y, px, py)) continue;
                if (!started) { g.BeginFigure(new Point(x, y), false, false); started = true; }
                else g.LineTo(new Point(x, y), true, false);
                px = x; py = y;
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(ReorderLineBrush, 1), geo);

        // discard / corrupt markers ride on the baseline
        for (int i = from; i < to; i++)
        {
            var f = r.Frames[i];
            if (!f.Corrupt && !f.Discard && !f.Interlaced && !f.RepeatPict && !f.Brain) continue;
            double x = map.XOf(r, i);
            var brush = f.Corrupt ? IBrush : f.Discard ? ReorderBrush : f.Brain ? QpLineBrush : GopBrush;
            double wFlag = Math.Max(0.7, map.WidthOf(r, i) * 0.8);
            dc.DrawRectangle(brush, null, new Rect(x, mid + 1, wFlag, 3));
        }
    }

    private void DrawReorderAxis(DrawingContext dc, AnalysisResult r, double w, double h, double dpi)
    {
        double top = TopPad, bottom = h - BottomPad;
        int max = Math.Max(1, r.MaxReorder);
        int min = Math.Min(0, r.MinReorder);
        int range = Math.Max(1, max - min);
        double mid = bottom - (0 - min) / (double)range * (bottom - top);

        var ys = LabelYs(h, 2);
        double x = w - AxisWidth + 6;
        DrawAxisLabel(dc, $"+{max}", x, ys[0], dpi);
        DrawAxisLabel(dc, "0", x, Math.Min(mid, h - 10), dpi);
    }

    // ------------------------------------------------------------------ time

    /// <summary>
    /// Time to x for one rendered layer: <c>x = (t - Start) * PxPerSec</c>. Frames are placed
    /// by their own presentation time rather than at an even step, because containers do not
    /// guarantee even spacing — the clip this was written against has 40 ms between every
    /// frame except the last, which sits 160 ms after its predecessor.
    /// </summary>
    private readonly struct TimeMap
    {
        public readonly double Start;
        public readonly double End;
        public readonly double PxPerSec;

        public TimeMap(double start, double end, double pxPerSec)
        {
            Start = start;
            End = end;
            PxPerSec = pxPerSec;
        }

        public double X(double t) => (t - Start) * PxPerSec;
        public double XOf(AnalysisResult r, int i) => X(r.Frames[i].PtsTime);

        /// <summary>Width of a frame's mark: the gap to the next frame, or the mean gap at the end.</summary>
        public double WidthOf(AnalysisResult r, int i)
        {
            double x = XOf(r, i);
            double next = i + 1 < r.FrameCount
                ? XOf(r, i + 1)
                : X(r.Frames[i].PtsTime + r.AverageFrameInterval);
            return next - x;
        }
    }

    /// <summary>
    /// Frames that can touch the layer. One frame beyond each edge is included, because a mark
    /// that starts before the left edge still reaches into view.
    /// </summary>
    private static (int from, int to) Visible(AnalysisResult r, in TimeMap map)
    {
        int from = Math.Max(0, r.IndexAt(map.Start) - 1);
        int to = Math.Min(r.FrameCount, r.IndexAt(map.End) + 2);
        if (to <= from) to = Math.Min(r.FrameCount, from + 1);
        return (from, to);
    }

    // ----------------------------------------------------------------- input

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
        var pos = e.GetPosition(this);
        double seconds = TimeAt(pos.X);
        ZoomRequested?.Invoke(this, (seconds, e.Delta > 0 ? 0.6 : 1.0 / 0.6));
        e.Handled = true;
    }

    private double TimeAt(double x)
    {
        double plotW = PlotWidth(ActualWidth);
        double frac = Math.Clamp(x / Math.Max(1, plotW), 0, 1);
        return ViewStart + frac * (ViewEnd - ViewStart);
    }

    /// <summary>
    /// Time the gesture points at, measured from where the gesture started rather than from the
    /// live mapping — the view may have scrolled underneath since.
    ///
    /// With the playhead pinned the strip is what moves, so a drag grabs the strip and the
    /// content travels with the cursor: drag right and earlier frames come back under the
    /// playhead. With the playhead free, the playhead itself is what the cursor drags.
    /// </summary>
    private double DragTime(double x)
    {
        double travel = (x - _dragStartX) / Math.Max(1, PlotWidth(ActualWidth)) * _dragSpan;
        return PinPlayhead ? _dragStartPosition - travel : _dragStartTime + travel;
    }
}
