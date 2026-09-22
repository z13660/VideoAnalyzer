using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoAnalyzer.Models;
using VideoAnalyzer.Services;

namespace VideoAnalyzer.Controls;

public enum OverlayMode
{
    None,
    BlockNoise,
    MacroblockGrid,
    MotionVectors
}

/// <summary>
/// The picture surface. Draws the decoded frame and stamps the analyser overlays on top:
/// resolution / motion banner, the picture-type badge, the forward / backward prediction
/// indicator and the optional block-noise dot field.
/// </summary>
public sealed class VideoSurface : FrameworkElement
{
    private static readonly Brush BannerBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xC8, 0xF0, 0xF2, 0xF4)));
    private static readonly Brush BannerShadow = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00)));
    private static readonly Brush DotBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GridStrongBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF)));

    /// <summary>Wash over a macroblock whose edges stand out from its interior.</summary>
    private static readonly Brush SeamBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x9A, 0x2E)));

    /// <summary>How much stronger a block's edges have to be before it is called a seam.</summary>
    private const double SeamRatio = 1.35;
    private static readonly Brush HintBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x60, 0x66)));

    private static readonly Brush IBox = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x1E, 0x1E)));
    private static readonly Brush PBox = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x4E, 0xC8)));
    private static readonly Brush BBox = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x9E, 0x3C)));
    private static readonly Brush UnknownBox = Freeze(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
    private static readonly Brush BadgeText = Freeze(new SolidColorBrush(Color.FromRgb(0x08, 0x0A, 0x0C)));

    // motion vector overlay
    private static readonly Brush VectorFwdBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x35, 0xFF, 0x7A)));
    private static readonly Brush VectorBwdBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0x5C, 0xE0)));
    private static readonly Brush VectorStillBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xE0, 0x50)));
    private static readonly Brush VectorHaloBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    // Pens / geometry reused across playback frames (see DrawMotionVectors)
    private (double cw, double ch, double pen) _vectorKey = (-1, -1, -1);
    private Pen _fwdPen = new(VectorFwdBrush, 1);
    private Pen _bwdPen = new(VectorBwdBrush, 1);
    private Pen _stillPen = new(VectorStillBrush, 1);
    private Pen _haloPen = new(VectorHaloBrush, 1);
    private readonly Dictionary<int, StreamGeometry> _headCache = new();

    // per-macroblock blocking ratios for the grid overlay, kept for the frame they describe
    private float[]? _blocking;
    private byte[]? _blockingSource;
    private int _blockingCell, _blockingCols, _blockingRows;

    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _previewBitmap;
    private ImageSource? _preview;
    private byte[]? _lastBuffer;
    private float[]? _activity;
    private byte[]? _activitySource;
    private int _actCols, _actRows;

    public static readonly DependencyProperty FrameInfoProperty = DependencyProperty.Register(
        nameof(FrameInfo), typeof(FrameInfo), typeof(VideoSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media), typeof(MediaInfo), typeof(VideoSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register(
        nameof(Overlay), typeof(OverlayMode), typeof(VideoSurface),
        new FrameworkPropertyMetadata(OverlayMode.BlockNoise, FrameworkPropertyMetadataOptions.AffectsRender));

    public FrameInfo? FrameInfo { get => (FrameInfo?)GetValue(FrameInfoProperty); set => SetValue(FrameInfoProperty, value); }
    public MediaInfo? Media { get => (MediaInfo?)GetValue(MediaProperty); set => SetValue(MediaProperty, value); }
    public OverlayMode Overlay { get => (OverlayMode)GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }

    public VideoSurface()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>Publishes a decoded BGRA frame. The buffer is not retained.</summary>
    public void PushFrame(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return;

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);

        // The block-activity map is only needed for the block-noise overlay and costs a
        // full pixel sweep, so it is computed lazily and cached against the frame buffer
        // identity. That keeps playback smooth while another overlay is active.
        _activity = null;
        _lastBuffer = bgra;
        _activitySource = null;
        InvalidateVisual();
    }

    public void ClearFrame()
    {
        _bitmap = null;
        _activity = null;
        _activitySource = null;
        _blocking = null;
        _blockingSource = null;
        _lastBuffer = null;
        _preview = null;
        _previewBitmap = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Shows a low resolution thumbnail while the user drags the playhead. Seeking restarts
    /// the decoder, so during a drag the surface shows the nearest filmstrip frame instead;
    /// the frame accurate picture replaces it once the real seek lands.
    /// </summary>
    public void ShowPreview(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return;

        if (_previewBitmap is null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
            _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

        _previewBitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        _preview = _previewBitmap;
        InvalidateVisual();
    }

    public void ClearPreview()
    {
        if (_preview is null) return;
        _preview = null;
        InvalidateVisual();
    }

    public bool IsPreviewing => _preview is not null;

    /// <summary>Builds the block-noise map for the frame currently on screen, once.</summary>
    private float[]? ActivityMap()
    {
        if (_activity is not null && ReferenceEquals(_activitySource, _lastBuffer)) return _activity;
        if (_lastBuffer is null || _bitmap is null) return null;

        var gray = BlockAnalysis.ToGray(_lastBuffer, _bitmap.PixelWidth, _bitmap.PixelHeight);
        _activity = BlockAnalysis.Activity(gray, _bitmap.PixelWidth, _bitmap.PixelHeight, 16, out _actCols, out _actRows);
        _activitySource = _lastBuffer;
        return _activity;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // While scrubbing the surface shows the filmstrip thumbnail: it is instant, whereas a
        // real seek costs a decoder restart. Overlays that describe the exact frame (badge,
        // vectors) are suppressed so they cannot contradict the preview.
        var shown = _preview ?? (ImageSource?)_bitmap;
        if (shown is null)
        {
            var hint = Txt.Make("No media loaded — Ctrl+O to open a file, or drop one here.", 13, HintBrush, dpi);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
            return;
        }

        int srcW = shown is BitmapSource bs ? bs.PixelWidth : 16;
        int srcH = shown is BitmapSource bs2 ? bs2.PixelHeight : 9;

        // uniform fit
        double scale = Math.Min(w / srcW, h / srcH);
        double dw = srcW * scale;
        double dh = srcH * scale;
        double dx = (w - dw) / 2;
        double dy = (h - dh) / 2;

        dc.DrawImage(shown, new Rect(dx, dy, dw, dh));

        // Overlays are clipped to the picture: the legend is wider than a small pane and
        // would otherwise spill over the panel beside it.
        dc.PushClip(new RectangleGeometry(new Rect(dx, dy, dw, dh)));

        if (_preview is not null)
        {
            var tag = Txt.Make("PREVIEW", 12, BannerBrush, dpi, bold: true);
            dc.DrawRectangle(BannerShadow, null, new Rect(dx + 6, dy + dh - tag.Height - 8, tag.Width + 10, tag.Height + 4));
            dc.DrawText(tag, new Point(dx + 11, dy + dh - tag.Height - 6));
            dc.Pop();
            return;
        }

        if (Overlay == OverlayMode.MacroblockGrid)
            DrawMacroblockGrid(dc, dx, dy, dw, dh, scale);
        else if (Overlay == OverlayMode.BlockNoise)
            DrawDots(dc, dx, dy, dw, dh);
        else if (Overlay == OverlayMode.MotionVectors)
            DrawMotionVectors(dc, dx, dy, dw, dh);

        DrawBanner(dc, dx, dy, dpi);
        DrawBadge(dc, dx, dy, dh, dpi);
        DrawPredictionFlags(dc, dx + dw, dy + dh, dpi);
        dc.Pop();
    }

    private void DrawDots(DrawingContext dc, double dx, double dy, double dw, double dh)
    {
        var map = ActivityMap();
        if (map is null || _actCols <= 0 || _actRows <= 0) return;

        double cw = dw / _actCols;
        double ch = dh / _actRows;

        for (int r = 0; r < _actRows; r++)
        {
            for (int c = 0; c < _actCols; c++)
            {
                float v = map[r * _actCols + c];
                if (v < 0.34f) continue;
                double size = 1.0 + v * 1.6;
                dc.DrawRectangle(DotBrush, null,
                    new Rect(dx + c * cw + cw / 2 - size / 2, dy + r * ch + ch / 2 - size / 2, size, size));
            }
        }
    }

    /// <summary>
    /// The macroblock grid: the encoder's 16x16 block boundaries over the picture, with the
    /// blocks whose edges stand out shaded.
    ///
    /// The grid is drawn at the real macroblock pitch — 16 pixels of the *source*, which is
    /// fewer than 16 of the decoded frame whenever the picture is decoded smaller than the file
    /// — and every fourth line is brighter, so the 64-pixel groups read as structure instead of
    /// as a sheet of grey.
    ///
    /// The shading is the part that carries information. A block whose top and left edges are
    /// clearly steeper than its interior is a seam the encoder failed to hide, so the grid says
    /// where the boundaries are and the shading says which of them you can actually see.
    /// </summary>
    private void DrawMacroblockGrid(DrawingContext dc, double dx, double dy, double dw, double dh, double scale)
    {
        int bufW = _bitmap?.PixelWidth ?? 0;
        int bufH = _bitmap?.PixelHeight ?? 0;
        if (bufW <= 0 || bufH <= 0 || _lastBuffer is null) return;

        // one macroblock, in decoded pixels and then in displayed ones
        double bufferPerSource = bufW / (double)Math.Max(1, Media?.Width ?? bufW);
        int cell = Math.Max(2, (int)Math.Round(16 * bufferPerSource));
        double pitch = Math.Max(3.0, cell * scale);

        var pen = new Pen(GridBrush, 0.6);
        var strong = new Pen(GridStrongBrush, 0.6);

        int i = 0;
        for (double x = dx; x <= dx + dw + 0.5; x += pitch, i++)
            dc.DrawLine(i % 4 == 0 ? strong : pen, new Point(x, dy), new Point(x, dy + dh));

        i = 0;
        for (double y = dy; y <= dy + dh + 0.5; y += pitch, i++)
            dc.DrawLine(i % 4 == 0 ? strong : pen, new Point(dx, y), new Point(dx + dw, y));

        var ratios = BlockingMap(cell);
        if (ratios is null || _blockingCols <= 0 || _blockingRows <= 0) return;

        int cols = _blockingCols, rows = _blockingRows;
        double cw = dw / cols, ch = dh / rows;

        // one geometry for every marked block rather than a draw call each: at this density the
        // call overhead would dwarf the fill
        var seams = new StreamGeometry();
        int marked = 0;
        using (var g = seams.Open())
        {
            for (int by = 0; by < rows; by++)
            {
                for (int bx = 0; bx < cols; bx++)
                {
                    if (ratios[by * cols + bx] < SeamRatio) continue;

                    double x0 = dx + bx * cw, y0 = dy + by * ch;
                    g.BeginFigure(new Point(x0, y0), true, true);
                    g.LineTo(new Point(x0 + cw, y0), false, false);
                    g.LineTo(new Point(x0 + cw, y0 + ch), false, false);
                    g.LineTo(new Point(x0, y0 + ch), false, false);
                    marked++;
                }
            }
        }
        seams.Freeze();
        dc.DrawGeometry(SeamBrush, null, seams);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var legend = Txt.Make(
            $"16x16 macroblocks · {marked} of {cols * rows} show a block seam",
            11, BannerBrush, dpi);
        dc.DrawRectangle(BannerShadow, null, new Rect(dx + 4, dy + dh - legend.Height - 6, legend.Width + 10, legend.Height + 3));
        dc.DrawText(legend, new Point(dx + 9, dy + dh - legend.Height - 5));
    }

    /// <summary>Blocking ratios for the frame on screen, computed once per frame.</summary>
    private float[]? BlockingMap(int cell)
    {
        if (_blocking is not null && _blockingCell == cell && ReferenceEquals(_blockingSource, _lastBuffer))
            return _blocking;

        if (_lastBuffer is null || _bitmap is null) return null;

        BlockAnalysis.BlockingRatios(
            _lastBuffer, _bitmap.PixelWidth, _bitmap.PixelHeight, cell,
            ref _blocking, out _blockingCols, out _blockingRows);
        _blockingSource = _lastBuffer;
        _blockingCell = cell;
        return _blocking;
    }

    /// <summary>
    /// Draws the per-block motion field over the picture: an arrow per macroblock pointing
    /// from the block centre toward its match, coloured green for a block predicted from
    /// the previous frame and magenta for one predicted from the next. The arrows are
    /// scaled so that the longest vector in the field spans a fixed fraction of the
    /// displayed frame, which keeps them legible regardless of the search radius.
    /// </summary>
    private void DrawMotionVectors(DrawingContext dc, double dx, double dy, double dw, double dh)
    {
        var field = FrameInfo?.Motion;
        if (field is null || field.Count == 0) return;

        double cellW = dw / field.Cols;
        double cellH = dh / field.Rows;

        // Map the estimator's small-frame pixels onto the displayed picture so the legend can
        // quote the peak in source pixels, which is the unit the graphs use.
        double smallW = 320;
        double sourceScale = Math.Max(1, Media?.Width ?? 320) / smallW;

        double peakSmall = Math.Max(1.0, field.PeakLength);
        double peakSource = peakSmall * sourceScale;

        // Normalise so the longest vector in the field spans most of one cell. With a fine
        // grid an absolute scale would turn ordinary motion into a wall of overlapping arrows.
        double gain = (Math.Min(cellW, cellH) * 0.85) / peakSmall;

        // Pens and arrow-head geometry are rebuilt only when the layout changes; during
        // playback this method runs per frame, so allocating here would churn.
        double lengthPen = Math.Max(1.0, cellW * 0.10);
        if (_vectorKey != (cellW, cellH, lengthPen))
        {
            _vectorKey = (cellW, cellH, lengthPen);
            _fwdPen = new Pen(VectorFwdBrush, lengthPen);
            _bwdPen = new Pen(VectorBwdBrush, lengthPen);
            _stillPen = new Pen(VectorStillBrush, lengthPen);
            _haloPen = new Pen(VectorHaloBrush, lengthPen + 1.4);
            _headCache.Clear();
        }

        for (int r = 0; r < field.Rows; r++)
        {
            for (int c = 0; c < field.Cols; c++)
            {
                int i = r * field.Cols + c;
                double vx = field.Dx[i];
                double vy = field.Dy[i];
                if (vx == 0 && vy == 0) continue;

                double cx = dx + (c + 0.5) * cellW;
                double cy = dy + (r + 0.5) * cellH;

                double ex = cx + vx * gain;
                double ey = cy + vy * gain;

                var pen = field.Reference[i] switch
                {
                    MotionField.RefForward => _fwdPen,
                    MotionField.RefBackward => _bwdPen,
                    _ => _stillPen
                };

                // dark halo underneath keeps the arrows readable over bright footage
                dc.DrawLine(_haloPen, new Point(cx, cy), new Point(ex, ey));
                dc.DrawLine(pen, new Point(cx, cy), new Point(ex, ey));
                DrawArrowHead(dc, pen.Brush, ex, ey, cx, cy);
            }
        }

        // legend: state the colour meaning and the direction convention, because neither is
        // self-evident from the picture alone. Falls back to a short form on a narrow pane
        // rather than being clipped mid-word.
        double dpiLegend = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string full =
            $"{field.Cols}x{field.Rows} blocks · arrow = motion direction · peak {peakSource:0.#} px" +
            $" · green: matched PREVIOUS frame · magenta: matched NEXT frame";
        string brief = $"{field.Cols}x{field.Rows} · green=prev magenta=next · peak {peakSource:0.#} px";

        var legend = Txt.Make(full, 11, BannerBrush, dpiLegend);
        if (legend.Width > dw - 20)
            legend = Txt.Make(brief, 11, BannerBrush, dpiLegend);

        dc.DrawRectangle(BannerShadow, null, new Rect(dx + 4, dy + dh - legend.Height - 6, legend.Width + 10, legend.Height + 3));
        dc.DrawText(legend, new Point(dx + 9, dy + dh - legend.Height - 5));
    }

    /// <summary>
    /// Draws the arrow head at the vector tip. The triangle is built once per quantised
    /// direction and stays frozen, so the rotation is applied through the drawing context
    /// rather than by mutating a frozen geometry (playback redraws this every frame).
    /// </summary>
    private void DrawArrowHead(DrawingContext dc, Brush brush, double ex, double ey, double cx, double cy)
    {
        double ang = Math.Atan2(ey - cy, ex - cx);
        double headLen = Math.Max(1.8, _vectorKey.cw * 0.22);
        int bucket = ((int)Math.Round((ang + Math.PI) / (Math.PI / 24)) % 48 + 48) % 48;

        if (!_headCache.TryGetValue(bucket, out var geometry))
        {
            const double spread = 0.44;
            double half = headLen * Math.Tan(spread);
            geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(headLen, 0), true, true);
                g.LineTo(new Point(0, half), true, false);
                g.LineTo(new Point(0, -half), true, false);
            }
            geometry.Freeze();
            _headCache[bucket] = geometry;
        }

        // the cached triangle points along +X from its origin; put its tip on the vector end
        var xform = new TransformGroup();
        xform.Children.Add(new RotateTransform(ang * 180.0 / Math.PI));
        xform.Children.Add(new TranslateTransform(ex, ey));
        dc.PushTransform(xform);
        dc.DrawGeometry(brush, null, geometry);
        dc.Pop();
    }

    private void DrawBanner(DrawingContext dc, double dx, double dy, double dpi)
    {
        var media = Media;
        var frame = FrameInfo;
        if (media is null) return;

        int dispW = _bitmap!.PixelWidth;
        int dispH = _bitmap.PixelHeight;
        string mv = frame?.MotionMean.HasValue == true
            ? $"MV {frame.MotionVectorCount} (fwd {frame.MotionFwdCount} / bwd {frame.MotionBwdCount})"
            : "MV n/a";

        var text = Txt.Make($"{media.Width}x{media.Height} -> {dispW}x{dispH}   {mv}", 12.5, BannerBrush, dpi);
        dc.DrawRectangle(BannerShadow, null, new Rect(dx + 4, dy + 3, text.Width + 8, text.Height + 2));
        dc.DrawText(text, new Point(dx + 8, dy + 4));
    }

    private void DrawBadge(DrawingContext dc, double dx, double dy, double dh, double dpi)
    {
        var frame = FrameInfo;
        if (frame is null) return;

        var box = frame.Type switch
        {
            PicType.I => IBox,
            PicType.P => PBox,
            PicType.B => BBox,
            _ => UnknownBox
        };

        double size = Math.Max(22, dh * 0.13);
        var rect = new Rect(dx + 6, dy + dh - size - 6, size, size);
        dc.DrawRectangle(box, null, rect);

        var letter = Txt.Make(frame.TypeLetter, size * 0.72, BadgeText, dpi, bold: true);
        dc.DrawText(letter, new Point(
            rect.X + (rect.Width - letter.Width) / 2,
            rect.Y + (rect.Height - letter.Height) / 2));
    }

    private void DrawPredictionFlags(DrawingContext dc, double right, double bottom, double dpi)
    {
        var frame = FrameInfo;
        if (frame is null) return;

        int fwd = frame.MotionFwdCount;
        int bwd = frame.MotionBwdCount;
        if (fwd == 0 && bwd == 0) return;

        string text = $"{Arrow(fwd)}ffwd {Arrow(bwd)}bbwd";
        var ft = Txt.Make(text, 12, BannerBrush, dpi);
        double x = right - ft.Width - 10;
        double y = bottom - ft.Height - 6;
        dc.DrawRectangle(BannerShadow, null, new Rect(x - 4, y - 2, ft.Width + 8, ft.Height + 4));
        dc.DrawText(ft, new Point(x, y));
    }

    private static string Arrow(int count) => count > 0 ? "\u25C0\u25B6 " : "\u25B7 ";
}
