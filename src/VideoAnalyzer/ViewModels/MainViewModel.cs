using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using VideoAnalyzer.Controls;
using VideoAnalyzer.Models;
using VideoAnalyzer.Services;

namespace VideoAnalyzer.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class InfoRow
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public Brush? ValueBrush { get; init; }
}

public sealed class TypeRow
{
    public string Letter { get; init; } = string.Empty;
    public Brush Swatch { get; init; } = Brushes.Gray;
    public string CountText { get; init; } = string.Empty;
    public string PercentText { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string BytePercentText { get; init; } = string.Empty;
}

/// <summary>Everything the analyser window binds to.</summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly Brush IBrush = Freeze(Color.FromRgb(0xF2, 0x2B, 0x2B));
    private static readonly Brush PBrush = Freeze(Color.FromRgb(0x4C, 0x8B, 0xE8));
    private static readonly Brush BBrush = Freeze(Color.FromRgb(0x35, 0xC7, 0x59));
    private static readonly Brush ValueBrush = Freeze(Color.FromRgb(0xE4, 0xE7, 0xEA));
    private static readonly Brush WarnBrush = Freeze(Color.FromRgb(0xE8, 0xC0, 0x50));

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private AnalysisResult? _result;
    private FrameInfo? _currentFrame;
    private string _statusText = "Ready.";
    private string _fileTitle = "no file loaded";
    private bool _isBusy;
    private bool _isPlaying;
    private bool _motionAnalysis = true;
    private bool _loop;
    private bool _pinPlayhead = true;
    private double _volume = 0.8;
    private bool _muted;
    private bool _audioReady;
    private double _zoomLevel;
    private bool _syncingZoom;
    private bool _viewFitted;
    private double _rangeFrom;
    private double _rangeTo = -1;
    private double _analysedFrom;
    private double _analysedTo = -1;
    private IReadOnlyList<AnalysedRun> _analysedRuns = Array.Empty<AnalysedRun>();
    private double _rangeAnchor = double.NaN;
    private double _viewStart;
    private double _viewEnd = 1;
    private double _progress;
    private string _progressStage = string.Empty;
    private double _position;
    private OverlayMode _overlay = OverlayMode.MotionVectors;

    public ObservableCollection<InfoRow> MediaRows { get; } = new();
    public ObservableCollection<InfoRow> FrameRows { get; } = new();
    public ObservableCollection<TypeRow> TypeRows { get; } = new();

    public AnalysisResult? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public FrameInfo? CurrentFrame
    {
        get => _currentFrame;
        set
        {
            if (!Set(ref _currentFrame, value)) return;
            RebuildFrameRows();
        }
    }

    public MediaInfo? Media => _result?.Media;

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string FileTitle { get => _fileTitle; set => Set(ref _fileTitle, value); }
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_isBusy;
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (Set(ref _isPlaying, value)) Raise(nameof(PlayButtonText));
        }
    }

    /// <summary>Transport button caption, so the primary action always states what it does.</summary>
    public string PlayButtonText => _isPlaying ? "\u23F8  暂停" : "\u25B6  播放";

    /// <summary>Enables the block-matching motion pass (the expensive stage).</summary>
    public bool MotionAnalysisEnabled { get => _motionAnalysis; set => Set(ref _motionAnalysis, value); }

    /// <summary>Restart from the beginning when playback reaches the end.</summary>
    public bool Loop { get => _loop; set => Set(ref _loop, value); }

    /// <summary>
    /// Pins the playhead to the centre of the view and scrolls the graphs underneath it, the
    /// way an audio editor behaves: the playhead never moves, the data travels past it. The
    /// view is allowed to reach past both ends of the clip, which is what keeps the playhead
    /// exactly centred on the first and the last frame as well.
    /// </summary>
    public bool PinPlayhead { get => _pinPlayhead; set { if (Set(ref _pinPlayhead, value)) ReapplyView(); } }

    /// <summary>Audio level, 0..1. Bound to the toolbar slider.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(value, 0, 1))) return;
            if (_volume > 0 && _muted) { _muted = false; Raise(nameof(Muted)); }
            Raise(nameof(VolumeText));
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            Raise(nameof(VolumeText));
        }
    }

    public string VolumeText => _muted ? "静音" : $"{_volume * 100:0}%";

    /// <summary>Set once the audio engine has the file, so the UI can report it.</summary>
    public bool AudioReady { get => _audioReady; set => Set(ref _audioReady, value); }

    /// <summary>Whether the loaded file has an audio stream; drives the audio controls' enabled state.</summary>
    public bool HasAudio => _result?.Media.HasAudio == true;

    public string AudioHint => HasAudio ? "音量 (↑ / ↓)" : "此文件没有音轨";

    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string ProgressStage { get => _progressStage; set => Set(ref _progressStage, value); }
    public double Position { get => _position; set => Set(ref _position, value); }

    public OverlayMode Overlay
    {
        get => _overlay;
        set
        {
            if (Set(ref _overlay, value)) Raise(nameof(OverlayLabel));
        }
    }

    public string OverlayLabel => _overlay switch
    {
        OverlayMode.None => "叠加层: 关闭",
        OverlayMode.BlockNoise => "叠加层: 块噪声",
        OverlayMode.MotionVectors => "叠加层: 运动矢量",
        _ => "叠加层: 宏块网格"
    };

    public string FfmpegText => FfmpegLocator.IsAvailable
        ? FfmpegLocator.VersionText.Replace(" Copyright (c) 2000-2026 the FFmpeg developers", string.Empty)
        : "ffmpeg not found";

    public void RaiseFfmpegText() => Raise(nameof(FfmpegText));

    public string KeyFrameText
    {
        get
        {
            var r = _result;
            if (r is null) return string.Empty;
            return $"key frames {r.IStats.Count}  ({r.KeyFramePercent:0.0}%)   " +
                   $"est. GOP {r.MaxGop}   deep pass: {(r.HasDeepAnalysis ? (r.QpEstimated || r.MotionEstimated ? "partial" : "native") : "off")}";
        }
    }

    /// <summary>Section heading for the picture-type table, carrying the summary on the
    /// same line so the panel does not need a separate row for it.</summary>
    public string TypeSummary
    {
        get
        {
            var r = _result;
            if (r is null) return "PICTURE TYPES";

            var mode = !r.HasDeepAnalysis
                ? "motion off"
                : r.QpEstimated || r.MotionEstimated ? "partly estimated" : "decoder native";

            return $"PICTURE TYPES   key {r.IStats.Count} ({r.KeyFramePercent:0.0}%)   " +
                   $"GOP {r.MaxGop}   {mode}";
        }
    }

    /// <summary>
    /// The stretch of the clip the deep passes cover. 0 / -1 means the whole file. The ruler and
    /// the QP / motion rows grey out everything outside it: those rows have nothing in them, and
    /// an empty row is easy to mistake for a flat one.
    /// </summary>
    public double RangeFrom
    {
        get => _rangeFrom;
        private set { if (Set(ref _rangeFrom, value)) Raise(nameof(HasRange)); }
    }

    public double RangeTo
    {
        get => _rangeTo;
        private set { if (Set(ref _rangeTo, value)) Raise(nameof(HasRange)); }
    }

    public bool HasRange => _rangeTo > _rangeFrom + 0.05;

    /// <summary>
    /// The stretch that actually has QP or motion data, read off the frame table.
    ///
    /// This is deliberately not the range setting: the setting says what the next pass will
    /// cover, this says what has been measured. The two part company as soon as a pass is
    /// cancelled or a range is cleared, and it is this one that decides what is greyed out —
    /// greying by the setting would clear the shading while the rows were still empty.
    /// </summary>
    public double AnalysedFrom
    {
        get => _analysedFrom;
        private set { if (Set(ref _analysedFrom, value)) Raise(nameof(HasAnalysedSpan)); }
    }

    public double AnalysedTo
    {
        get => _analysedTo;
        private set { if (Set(ref _analysedTo, value)) Raise(nameof(HasAnalysedSpan)); }
    }

    public bool HasAnalysedSpan => _analysedTo > _analysedFrom;

    /// <summary>
    /// The stretches that have data, one entry per contiguous run of frames. Analysing several
    /// ranges leaves gaps between them, and the shading has to follow the runs rather than the
    /// span or it would paint those gaps as measured.
    /// </summary>
    public IReadOnlyList<AnalysedRun> AnalysedRuns
    {
        get => _analysedRuns;
        private set { _analysedRuns = value; Raise(); }
    }

    /// <summary>Recomputes <see cref="AnalysedFrom"/> / <see cref="AnalysedTo"/> from the data.</summary>
    private void RefreshAnalysedSpan()
    {
        var r = _result;
        var frames = r?.Frames;
        if (frames is null || frames.Count == 0)
        {
            AnalysedFrom = 0;
            AnalysedTo = -1;
            AnalysedRuns = Array.Empty<AnalysedRun>();
            return;
        }

        double step = frames.Count > 1 ? Math.Max(0.001, frames[1].PtsTime - frames[0].PtsTime) : 0.04;
        var runs = new List<AnalysedRun>();
        int start = -1;

        for (int i = 0; i <= frames.Count; i++)
        {
            bool on = i < frames.Count
                      && (frames[i].QpAvg.HasValue || frames[i].MotionMean.HasValue);

            if (on && start < 0) start = i;
            else if (!on && start >= 0)
            {
                runs.Add(new AnalysedRun(frames[start].PtsTime, frames[i - 1].PtsTime + step));
                start = -1;
            }
        }

        AnalysedRuns = runs;
        AnalysedFrom = runs.Count > 0 ? runs[0].From : 0;
        AnalysedTo = runs.Count > 0 ? runs[^1].To : -1;
    }

    /// <summary>Start of a range that has been marked but not finished; NaN when there is none.</summary>
    public double RangeAnchor
    {
        get => _rangeAnchor;
        set => Set(ref _rangeAnchor, value);
    }

    public void SetRange(double from, double to)
    {
        RangeFrom = from;
        RangeTo = to;
    }

    public void ClearRange()
    {
        RangeFrom = 0;
        RangeTo = -1;
    }

    public double ViewStart { get => _viewStart; private set => Set(ref _viewStart, value); }
    public double ViewEnd { get => _viewEnd; private set => Set(ref _viewEnd, value); }

    /// <summary>Full span of the loaded clip, measured to the last frame's presentation time.</summary>
    public double TotalSeconds => _result is null ? 1 : Math.Max(0.04, _result.LastFrameTime);

    /// <summary>
    /// True when the view shows a meaningful sub-range. The 2% tolerance matters: the first
    /// view is set from the probe's duration, which is a frame longer than the last frame's
    /// presentation time, so a freshly loaded clip would otherwise report itself as "1% zoomed"
    /// instead of showing the whole thing.
    /// </summary>
    public bool IsZoomed => (_viewEnd - _viewStart) < TotalSeconds * 0.98;

    public string ZoomText => IsZoomed
        ? $"{TimelineStrip.FormatClock(Math.Max(0, _viewStart))} – {TimelineStrip.FormatClock(Math.Min(TotalSeconds, _viewEnd))}" +
          $"   {(1 - (_viewEnd - _viewStart) / TotalSeconds) * 100:0}%"
        : "全程";


    /// <summary>
    /// Zooms the shared time view around a point. <paramref name="factor"/> below 1 zooms in.
    /// </summary>
    public void ZoomAt(double seconds, double factor)
    {
        double total = TotalSeconds;
        double span = _viewEnd - _viewStart;
        double newSpan = Math.Clamp(span * factor, Math.Min(0.25, total), total);

        if (Math.Abs(newSpan - total) < 1e-6)
        {
            ApplyView(0, total);
            return;
        }

        // keep the anchor point at the same relative position inside the view
        double rel = span > 0 ? (seconds - _viewStart) / span : 0.5;
        rel = Math.Clamp(rel, 0, 1);
        double start = seconds - rel * newSpan;
        ApplyView(start, start + newSpan);
    }

    /// <summary>Scrolls the view so that <paramref name="seconds"/> stays visible.</summary>
    public void EnsureVisible(double seconds)
    {
        double total = TotalSeconds;
        double span = _viewEnd - _viewStart;

        if (_pinPlayhead)
        {
            // The playhead is the centre of the view, so every position change is a scroll.
            ApplyView(seconds - span / 2, seconds + span / 2);
            return;
        }

        if (span >= total - 1e-6) return;         // nothing to scroll

        double margin = span * 0.12;
        if (seconds >= _viewStart + margin && seconds <= _viewEnd - margin) return;

        // page the view forward/back so the playhead sits near the left edge
        double start = seconds - span * 0.15;
        ApplyView(start, start + span);
    }

    /// <summary>Re-applies the current view after a mode or zoom change.</summary>
    private void ReapplyView()
    {
        double total = TotalSeconds;
        double span = _viewEnd - _viewStart;

        if (_pinPlayhead) { ApplyView(_position - span / 2, _position + span / 2); return; }
        if (span >= total - 1e-6) { ApplyView(0, total); return; }
        ApplyView(_viewStart, _viewEnd);
    }

    public void ResetZoom() => ApplyView(0, TotalSeconds);

    /// <summary>
    /// Zoom as a 0..1 control, 0 showing the whole clip and 1 the deepest zoom. This is what
    /// the toolbar slider drives; wheel and button zoom feed the value back the other way.
    /// </summary>
    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _zoomLevel) < 1e-6) return;

            _zoomLevel = value;
            Raise(nameof(ZoomLevel));
            if (_syncingZoom) return;

            // anchor on the playhead so zooming in keeps the frame of interest on screen
            double total = TotalSeconds;
            double minSpan = Math.Min(0.25, total);
            double span = total - (total - minSpan) * value;
            double start = _position - span / 2;
            ApplyView(start, start + span);
        }
    }

    private void ApplyView(double start, double end)
    {
        double total = TotalSeconds;
        double span = Math.Clamp(end - start, Math.Min(0.25, total), total);

        if (_pinPlayhead)
        {
            // Pinned: the start follows from the playhead rather than from the range the caller
            // asked for, so the playhead sits exactly in the middle of the strip at every
            // position. The view is deliberately allowed to run past both ends of the clip —
            // that empty time is what buys the centring on the first and last frame, and the
            // data travelling under a stationary playhead is the point of the mode.
            start = _position - span / 2;
        }
        else
        {
            start = Math.Clamp(start, 0, Math.Max(0, total - span));
        }

        ViewStart = start;
        ViewEnd = start + span;
        Raise(nameof(IsZoomed));
        Raise(nameof(ZoomText));

        // keep the slider in step with wheel / button zoom
        double minSpan = Math.Min(0.25, total);
        double level = total > minSpan ? 1 - (span - minSpan) / (total - minSpan) : 0;
        if (Math.Abs(level - _zoomLevel) > 1e-4)
        {
            _syncingZoom = true;
            _zoomLevel = level;
            Raise(nameof(ZoomLevel));
            _syncingZoom = false;
        }
    }

    public double FrameRate => _result?.Media.FrameRate ?? 25;
    public int FrameCount => _result?.FrameCount ?? 0;

    public void SetResult(AnalysisResult result)
    {
        bool firstTime = !ReferenceEquals(_result, result);
        Result = result;
        FileTitle = result.Media.FileName;
        RebuildMediaRows();
        RebuildTypeRows();
        RebuildFrameRows();

        // The first view is set from the probe's duration, which is a frame longer than the
        // last frame's time. Refit once the frame table exists so the whole clip fills the
        // strip exactly, with no sliver of empty time at either end.
        if (!_viewFitted && result.Frames.Count > 0)
        {
            _viewFitted = true;
            ApplyView(0, TotalSeconds);
        }
        else if (firstTime)
        {
            ApplyView(0, TotalSeconds);
        }

        Raise(nameof(FrameRate));
        Raise(nameof(FrameCount));
        Raise(nameof(Media));
        Raise(nameof(KeyFrameText));
        Raise(nameof(TypeSummary));
        Raise(nameof(TotalSeconds));
        Raise(nameof(IsZoomed));
        Raise(nameof(ZoomText));
        Raise(nameof(HasAudio));
        Raise(nameof(AudioHint));
    }

    /// <summary>
    /// Called when the analysis pipeline has added data. Rebuilds the rows and, crucially,
    /// invalidates the graph caches so a streaming pass shows up on screen.
    /// </summary>
    public void NotifyDataChanged()
    {
        if (_result is null) return;

        RebuildTypeRows();
        RebuildFrameRows();
        RefreshAnalysedSpan();
        Raise(nameof(KeyFrameText));
        Raise(nameof(TypeSummary));
        DataChanged?.Invoke();
    }

    /// <summary>Raised when the graphs should drop their cached layer and redraw.</summary>
    public event Action? DataChanged;

    public void Clear()
    {
        Result = null;
        CurrentFrame = null;
        MediaRows.Clear();
        FrameRows.Clear();
        TypeRows.Clear();
        FileTitle = "no file loaded";
        _viewFitted = false;
        ApplyView(0, 1);
        DataChanged?.Invoke();
    }

    // ------------------------------------------------------------------ rows

    private void RebuildMediaRows()
    {
        MediaRows.Clear();
        var m = _result?.Media;
        if (m is null) return;

        var dar = ComputeDar(m);
        var rangeText = m.ColorRange switch
        {
            "tv" => "tv",
            "pc" => "pc",
            _ => m.ColorRange
        };
        var scan = m.FieldOrder == "progressive" ? "progressive" : m.FieldOrder;

        // Kept to one line per row: the panel is sized to fit without scrolling, so a value
        // that wraps would push the sections below it out of view.
        Add(MediaRows, "FILE", m.FileName);
        Add(MediaRows, "FORMAT", FormatLine(m));
        Add(MediaRows, "VIDEO", $"{m.VideoLine}   SAR {m.SarNum}:{m.SarDen}  DAR {dar}");
        Add(MediaRows, "SCAN", $"{m.Width}x{m.Height}  @ {m.FrameRateText}  {scan}");
        Add(MediaRows, "COLOR", $"{m.PixFmt} / {m.ColorSpace} / {rangeText}");
        Add(MediaRows, "LENGTH", $"{m.DurationText}  {m.NbFrames} frames  avg {AvgKbps():0} kbps");
        Add(MediaRows, "AUDIO", m.AudioLine);
        Add(MediaRows, "SIZE", $"{m.FileSizeBytes / 1024.0 / 1024.0:0.00} MB");
    }

    private double AvgKbps()
    {
        var r = _result;
        if (r is null) return 0;
        if (r.AvgBitrateKbps > 0) return r.AvgBitrateKbps;
        return r.Media.VideoBitRate / 1000.0;
    }

    private static string ComputeDar(MediaInfo m)
    {
        if (m.Width <= 0 || m.Height <= 0) return "?";
        double ar = m.Width * (double)m.SarNum / (m.Height * (double)m.SarDen);
        foreach (var (w, h) in new[] { (16, 9), (4, 3), (3, 2), (1, 1), (21, 9), (2, 1) })
            if (Math.Abs(ar - (double)w / h) < 0.02) return $"{w}:{h}";
        return $"{ar:0.00}";
    }

    private static string ShortFormat(string longName)
    {
        int comma = longName.IndexOf(',');
        var head = comma > 0 ? longName[..comma] : longName;
        return head.Trim();
    }

    /// <summary>
    /// Container line for the info panel.
    ///
    /// ffprobe's format_name is the demuxer's alias list — "mov,mp4,m4a,3gp,3g2,mj2" is every
    /// extension that one demuxer accepts, not what this file is — so it is the readable long
    /// name plus the file's own extension that goes on screen. The extension is only appended
    /// when the long name does not already mention it.
    /// </summary>
    private static string FormatLine(MediaInfo m)
    {
        var name = ShortFormat(m.FormatLongName);
        if (name.Length == 0 || name == "unknown") name = m.FormatName;

        var ext = Path.GetExtension(m.FileName).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0 || name.Contains(ext, StringComparison.OrdinalIgnoreCase)) return name;

        return $"{name}  ·  {ext}";
    }

    private void RebuildTypeRows()
    {
        TypeRows.Clear();
        var r = _result;
        if (r is null) return;

        foreach (var s in r.AllStats)
        {
            if (s.Count == 0) continue;
            TypeRows.Add(new TypeRow
            {
                Letter = s.Type switch { PicType.I => "I", PicType.P => "P", PicType.B => "B", _ => "?" },
                Swatch = s.Type switch { PicType.I => IBrush, PicType.P => PBrush, PicType.B => BBrush, _ => Brushes.Gray },
                CountText = s.CountText,
                PercentText = s.PercentText,
                SizeText = s.SizeText,
                BytePercentText = s.BytePercentText
            });
        }
    }

    private void RebuildFrameRows()
    {
        FrameRows.Clear();
        var r = _result;
        var f = _currentFrame;
        if (r is null || f is null) return;

        var m = r.Media;
        var qpSuffix = r.QpEstimated ? " (est.)" : string.Empty;
        var motion = f.MotionMean.HasValue
            ? $"mean {f.MotionMean:0.##} px  max {f.MotionMax ?? 0:0.#} px  ·  {f.MotionVectorCount} vec (fwd {f.MotionFwdCount} / bwd {f.MotionBwdCount})"
            : "not analysed";

        Add(FrameRows, "FRAME", $"{f.Index} / {r.FrameCount}   {TimelineStrip.FormatClock(f.PtsTime, true)}   dur {f.DurationMs:0.00} ms");
        Add(FrameRows, "TYPE", f.TypeLongName, TypeBrush(f.Type));
        Add(FrameRows, "SIZE", $"{f.SizeText}   {f.BitrateKbps:0} kbps");
        Add(FrameRows, "QP", f.QpText + qpSuffix, r.QpEstimated ? WarnBrush : null);
        Add(FrameRows, "MOTION", motion, r.MotionEstimated ? WarnBrush : null);
        Add(FrameRows, "GOP", $"+{f.GopPosition} since keyframe   reorder {(f.ReorderDelta >= 0 ? "+" : "")}{f.ReorderDelta}");
        Add(FrameRows, "FLAGS", BuildFlags(f, m));
    }

    private static string BuildFlags(FrameInfo f, MediaInfo m)
    {
        var parts = new List<string>();
        if (m.FieldOrder != "unknown") parts.Add(m.FieldOrder);
        if (f.Interlaced) parts.Add("interlaced");
        if (f.RepeatPict) parts.Add("repeat");
        if (f.KeyFrame) parts.Add("key");
        if (f.Brain) parts.Add("blocky");
        if (f.Corrupt) parts.Add("corrupt");
        if (f.Discard) parts.Add("discard");
        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    private static Brush TypeBrush(PicType t) => t switch
    {
        PicType.I => IBrush,
        PicType.P => PBrush,
        PicType.B => BBrush,
        _ => ValueBrush
    };

    private static void Add(ObservableCollection<InfoRow> rows, string label, string value, Brush? brush = null)
        => rows.Add(new InfoRow { Label = label, Value = value, ValueBrush = brush ?? ValueBrush });
}
