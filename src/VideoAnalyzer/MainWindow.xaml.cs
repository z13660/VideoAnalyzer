using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoAnalyzer.Controls;
using VideoAnalyzer.Models;
using VideoAnalyzer.Services;
using VideoAnalyzer.ViewModels;

namespace VideoAnalyzer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly VideoPlaybackEngine _engine = new();
    private readonly Filmstrip _filmstrip = new();
    private readonly AudioPlayer _audio = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();

    private AnalysisResult? _result;
    private CancellationTokenSource? _analysisCts;
    private byte[]? _currentBuffer;

    private int _decodeW = 640;
    private int _decodeH = 360;
    private int _displayedIndex = -1;
    private int _targetIndex;
    private bool _playing;
    private int _playAnchorIndex;
    private int _refreshQueued;
    private int _syncTicks;
    private int _syncLogTicks;
    private bool _audioOffsetWarned;
    private bool _scrubbing;

    /// <summary>Set when a drag paused playback, so the transport can be handed back on release.</summary>
    private bool _resumeAfterScrub;

    /// <summary>
    /// Set when the picture ran out of frames. Playing again then means playing from the top,
    /// which the frame index alone cannot express when the decoder stops short of the last
    /// entry in the packet table.
    /// </summary>
    private bool _playbackEnded;

    /// <summary>Frames that came back from the on-disk cache for the file currently open.</summary>
    private int _restoredFromCache;

    private bool _videoFullScreen;
    private WindowState _savedState;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;
    private Rect _savedBounds;
    private Thickness _savedRootMargin;
    private Thickness _savedFrameMargin;
    private CornerRadius _savedFrameCorners;
    private Thickness _savedFrameBorder;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        FfmpegLocator.RefreshVersion();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += OnTick;
        _timer.Start();

        _engine.Failed += (_, msg) => _vm.StatusText = "decode error: " + msg;

        Surface.SizeChanged += (_, _) => ResizeDecoder();

        // A streaming analysis bumps this whenever the frame table grew, so the graphs drop
        // their cached layer and redraw with the new data.
        _vm.DataChanged += InvalidateGraphCaches;

        // Volume and mute live in the view model (they are bound to the toolbar); the audio
        // engine is told about them here rather than being bound directly.
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.Volume): _audio.Volume = _vm.Volume; break;
                case nameof(MainViewModel.Muted): _audio.Muted = _vm.Muted; break;
            }
        };

        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _analysisCts?.Cancel();
            _engine.Dispose();
            _audio.Dispose();

            // The token only stops the read loops; nothing here waits for them to reach their
            // own kill, and ffmpeg is a child process — it does not die with the window.
            ProcessRunner.KillAll();
        };
    }

    /// <summary>
    /// Shrinks the window to the desktop work area on first show. The default size assumes a
    /// large display; on a smaller or scaled screen the bottom rows (status bar, and part of
    /// the info panel) would otherwise sit underneath the taskbar.
    /// </summary>
    private void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        double maxW = Math.Max(MinWidth, wa.Width - 16);
        double maxH = Math.Max(MinHeight, wa.Height - 16);

        double w = Math.Min(Width, maxW);
        double h = Math.Min(Height, maxH);

        AppLog.Write($"window: work area {wa.Width:0}x{wa.Height:0}, requested {Width:0}x{Height:0} -> {w:0}x{h:0}");

        if (w >= Width && h >= Height) return;

        Width = w;
        Height = h;
        Left = Math.Max(wa.Left, wa.Left + (wa.Width - w) / 2);
        Top = Math.Max(wa.Top, wa.Top + (wa.Height - h) / 2);
    }

    private void InvalidateGraphCaches()
    {
        foreach (var child in GraphStack.Children)
            if (child is FrameGraph g) g.InvalidateCache();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();

        if (!FfmpegLocator.IsAvailable)
        {
            var answer = MessageBox.Show(
                "ffmpeg / ffprobe were not found.\n\n" +
                "This analyser needs them to read frame data and decode video.\n" +
                "Expected location: <app>\\tools\\ffmpeg\\bin, or anywhere on PATH.\n\n" +
                "Locate ffmpeg.exe now?",
                "VideoAnalyzer — ffmpeg required",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes) LocateFfmpeg();
        }

        _vm.StatusText = FfmpegLocator.IsAvailable
            ? "Ready. Ctrl+O to open a video."
            : "ffmpeg not found — video analysis is unavailable.";

        var startup = App.StartupFile;
        if (startup is not null && FfmpegLocator.IsAvailable)
            await OpenAsync(startup);
    }

    // ===================================================================== file

    private void LocateFfmpeg()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Locate ffmpeg.exe",
            Filter = "ffmpeg executable|ffmpeg.exe|All files|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            FfmpegLocator.Override(dlg.FileName);
            FfmpegLocator.RefreshVersion();
            _vm.RaiseFfmpegText();
            _vm.StatusText = "ffmpeg: " + FfmpegLocator.VersionText;
        }
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (!FfmpegLocator.IsAvailable) { LocateFfmpeg(); if (!FfmpegLocator.IsAvailable) return; }

        var dlg = new OpenFileDialog
        {
            Title = "Open video",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.mpg;*.mpeg;*.ts;*.m2ts;*.wmv;*.flv;*.mxf|All files|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            await OpenAsync(dlg.FileName);
    }

    /// <param name="useCache">
    /// False for an explicit 重新分析: the cache exists to avoid decoding the same file twice by
    /// accident, not to refuse to do what the button says.
    /// </param>
    private async Task OpenAsync(string path, bool useCache = true)
    {
        if (_analysisCts is { IsCancellationRequested: false })
        {
            AppLog.Write("open: cancelling the previous analysis");
            _analysisCts.Cancel();
        }

        // Deterministic: the old file's decoders go now, rather than when their read loops
        // happen to notice the token.
        ProcessRunner.KillAll(ProcessRunner.Analysis);
        SetPlaying(false);
        _engine.Stop();
        ReleaseCurrentBuffer();
        Surface.ClearFrame();
        _vm.Clear();
        _result = null;
        _displayedIndex = -1;
        _targetIndex = 0;
        _filmstrip.Clear();

        _vm.RangeAnchor = double.NaN;
        _vm.ClearRange();
        _restoredFromCache = 0;

        var cts = new CancellationTokenSource();
        _analysisCts = cts;

        _vm.IsBusy = true;
        _vm.Progress = 0;
        _vm.ProgressStage = "starting…";
        _vm.StatusText = $"Opening {Path.GetFileName(path)} …";

        var pipeline = new AnalysisPipeline();
        bool transportStarted = false;
        bool filmstripStarted = false;

        // The pipeline runs its stages on background threads (the per-line parsing must not
        // sit on the dispatcher), so everything that touches bound state is marshalled back.
        pipeline.FramesReady += result => Dispatcher.Invoke(() =>
        {
            _result = result;
            _vm.SetResult(result);
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, 1000.0 / Math.Max(1, result.Media.FrameRate)));

            if (!transportStarted)
            {
                transportStarted = true;
                ResizeDecoder(force: true);
                RestartEngine(0, 0);
                _vm.StatusText = $"{result.Media.FileName}  ·  playing while analysing…";
                AppLog.Write($"open: transport started {_decodeW}x{_decodeH} (frame table {result.FrameCount})");

                // The audio engine is independent of the picture decoder, so it can be
                // opened as soon as the probe has told us whether there is anything to play.
                _audio.Volume = _vm.Volume;
                _audio.Muted = _vm.Muted;
                _audio.Load(result.Media.FilePath, result.Media.HasAudio);
                _vm.AudioReady = _audio.IsLoaded;
            }

            // Once the packet table exists the duration is known, so the scrub thumbnails
            // can be built in the background without blocking anything.
            if (!filmstripStarted && result.Frames.Count > 0)
            {
                filmstripStarted = true;
                _ = BuildFilmstripAsync(result.DurationSeconds, cts.Token);
            }
        });

        // Throttled: a streaming pass can publish hundreds of updates a second.
        pipeline.Updated += QueueViewRefresh;

        // Re-opening a file that has been analysed before costs nothing: the deep columns come
        // back from the cache and the decoders stay closed.
        if (useCache)
        {
            pipeline.DeepAnalysisAlreadyDone = result =>
            {
                int restored = AnalysisCache.TryLoad(result);
                if (restored == 0) return false;

                _restoredFromCache = restored;
                return true;
            };
        }

        try
        {
            var progress = new Progress<AnalysisProgress>(p =>
            {
                _vm.Progress = p.Fraction;
                _vm.ProgressStage = p.Stage;
            });

            await pipeline.RunAsync(path, _vm.MotionAnalysisEnabled, progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            var result = pipeline.Result;
            if (result is not null)
            {
                var mode = result.HasDeepAnalysis
                    ? (result.QpEstimated || result.MotionEstimated ? "QP/motion partly estimated" : "decoder native data")
                    : "motion pass off";
                string cacheNote = _restoredFromCache > 0
                    ? $"  ·  {_restoredFromCache} frames from cache"
                    : string.Empty;
                _vm.StatusText = $"{result.Media.FileName}  ·  {result.FrameCount} frames  ·  " +
                                 $"{result.Media.DurationText}  ·  {result.Media.FrameRateText}  ·  {mode}{cacheNote}";
            }
        }
        catch (OperationCanceledException)
        {
            var kept = _result?.Frames.Count(f => f.QpAvg.HasValue || f.MotionMean.HasValue) ?? 0;
            _vm.StatusText = $"analysis cancelled  ·  {kept} frames of QP / motion kept";
            AppLog.Write($"open: cancelled, {kept} frames kept");
            SaveCache();
        }
        catch (Exception ex)
        {
            _vm.StatusText = "analysis failed: " + ex.Message;
            AppLog.Write("open: failed", ex);
            MessageBox.Show(ex.ToString(), "Analysis failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _vm.IsBusy = false;
            _vm.Progress = 0;
            _vm.ProgressStage = string.Empty;
            SaveCache();
            ReleaseAnalysisToken(cts);
        }
    }

    /// <summary>
    /// Releases the token source of a finished run.
    ///
    /// The guard that keeps two passes from running at once reads this field, so leaving a
    /// completed one in place makes every later pass look like it is already running — which is
    /// exactly how a second range analysis came to be silently refused.
    /// </summary>
    private void ReleaseAnalysisToken(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_analysisCts, cts)) return;      // a newer run owns it now
        _analysisCts = null;
        try { cts.Dispose(); } catch { }
    }

    /// <summary>
    /// Writes whatever has been analysed so far. Called when a pass ends, including a cancelled
    /// one, so work already done survives both a cancel and a restart of the program.
    /// </summary>
    private void SaveCache()
    {
        var result = _result;
        if (result is null || result.FrameCount == 0) return;
        if (!result.Frames.Any(f => f.QpAvg.HasValue || f.MotionMean.HasValue)) return;
        AnalysisCache.Save(result);
    }

    /// <summary>
    /// Coalesces the streaming updates into at most ~8 UI refreshes a second. Each refresh
    /// invalidates the graph caches, so redrawing per frame would waste the cores the
    /// analysis itself needs.
    /// </summary>
    private void QueueViewRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            _vm.NotifyDataChanged();
        });
    }

    /// <summary>Builds the scrub-preview thumbnails without blocking the pipeline.</summary>
    private async Task BuildFilmstripAsync(double durationSeconds, CancellationToken ct)
    {
        try
        {
            var media = _result?.Media;
            if (media is null || string.IsNullOrEmpty(media.FilePath)) return;

            await _filmstrip.BuildAsync(media.FilePath, durationSeconds, media.Width, media.Height, ct)
                .ConfigureAwait(false);
            if (!_filmstrip.IsReady) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (!_scrubbing) _vm.StatusText += "  ·  scrub preview ready";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("filmstrip: failed", ex); }
    }

    private async void OnReanalyseClick(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;

        // the marked range is a setting, not a one-shot: re-analysing honours it
        if (_vm.HasRange)
        {
            await AnalyseRangeAsync(_vm.RangeFrom, _vm.RangeTo);
            return;
        }

        await OpenAsync(_result.Media.FilePath, useCache: false);
    }

    /// <summary>
    /// Right-clicking the ruler marks a range: the first click sets the start, the second sets
    /// the end and analyses that stretch. Another right-click starts a new range, and whatever
    /// was analysed before stays in the frame table, so ranges can be walked through one after
    /// another.
    ///
    /// The step is read from the state rather than counted, so a range set through the dialog
    /// behaves the same way.
    /// </summary>
    private async void OnTimelineRangeRequested(object? sender, double seconds)
    {
        var result = _result;
        if (result is null) return;

        if (!double.IsNaN(_vm.RangeAnchor))
        {
            double from = Math.Min(_vm.RangeAnchor, seconds);
            double to = Math.Max(_vm.RangeAnchor, seconds);
            _vm.RangeAnchor = double.NaN;

            if (to - from < 0.1)
            {
                _vm.StatusText = "that range is too short — right-click twice to mark it again";
                return;
            }

            await AnalyseRangeAsync(from, to);
            return;
        }

        // A range that is half marked, or one that has already been analysed, is simply
        // replaced. Its results stay in the frame table, so marking one range after another is
        // the normal way to work; dropping the range altogether is a job for the dialog.
        _vm.ClearRange();
        _vm.RangeAnchor = seconds;
        _vm.StatusText = $"range starts at {TimelineStrip.FormatClock(seconds, true)}" +
                         "  ·  right-click again for the end";
        AppLog.Write($"range: start marked at {seconds:0.###}s");
    }

    /// <summary>
    /// Re-runs the deep passes over the time range the strip is showing. The packet table, the
    /// transport and the view are left alone, and results outside the range are kept: on a long
    /// clip this is the difference between waiting for the whole file and waiting for the minute
    /// you actually care about.
    /// </summary>
    private async void OnAnalyseRangeClick(object sender, RoutedEventArgs e)
    {
        var result = _result;
        if (result is null)
        {
            _vm.StatusText = "open a video first";
            return;
        }
        if (_analysisCts is { IsCancellationRequested: false })
        {
            _vm.StatusText = "an analysis is already running";
            return;
        }

        // the dialog opens on the visible range, so the common case is still one click
        var dialog = new AnalysisRangeWindow(result.DurationSeconds, _vm.ViewStart, _vm.ViewEnd)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;

        await AnalyseRangeAsync(dialog.RangeFrom, dialog.RangeTo);
    }

    /// <summary>Runs the deep passes over a range and keeps that range as the current setting.</summary>
    private async Task AnalyseRangeAsync(double from, double to)
    {
        var result = _result;
        if (result is null) return;

        if (_analysisCts is { IsCancellationRequested: false })
        {
            _vm.StatusText = "an analysis is already running";
            return;
        }

        _vm.RangeAnchor = double.NaN;
        _vm.SetRange(from, to);

        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        _vm.IsBusy = true;
        _vm.Progress = 0;
        _vm.ProgressStage = $"analysing {TimelineStrip.FormatClock(from)} – {TimelineStrip.FormatClock(to)}…";
        AppLog.Write($"range: deep pass over {from:0.###}-{to:0.###}s");

        try
        {
            var progress = new Progress<AnalysisProgress>(p =>
            {
                _vm.Progress = p.Fraction;
                _vm.ProgressStage = p.Stage;
            });

            var pipeline = new AnalysisPipeline();
            pipeline.Updated += QueueViewRefresh;
            await pipeline.RunDeepAsync(result, _vm.MotionAnalysisEnabled, progress, cts.Token, from, to);

            if (cts.IsCancellationRequested) return;
            int done = result.Frames.Count(f => f.QpAvg.HasValue || f.MotionMean.HasValue);
            AppLog.Write($"range: done {from:0.###}-{to:0.###}s, {done} frames of QP / motion in the clip");
            _vm.StatusText = $"analysed {TimelineStrip.FormatClock(from)} – {TimelineStrip.FormatClock(to)}" +
                             $"  ·  {done} frames of QP / motion in the clip";
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("range: cancelled");
        }
        catch (Exception ex)
        {
            _vm.StatusText = "range analysis failed: " + ex.Message;
            AppLog.Write("range: failed", ex);
        }
        finally
        {
            _vm.IsBusy = false;
            _vm.Progress = 0;
            _vm.ProgressStage = string.Empty;
            SaveCache();
            ReleaseAnalysisToken(cts);
        }
    }

    /// <summary>
    /// Stops the deep passes and keeps everything they produced so far. The frame table, the
    /// bitrate / GOP / reorder data and whatever QP and motion rows were already filled in stay
    /// usable — only the remaining work is dropped.
    /// </summary>
    private void OnCancelAnalysisClick(object sender, RoutedEventArgs e)
    {
        if (_analysisCts is not { IsCancellationRequested: false }) return;

        _analysisCts.Cancel();
        int killed = ProcessRunner.KillAll(ProcessRunner.Analysis);
        AppLog.Write($"open: cancel requested, {killed} decoder(s) killed");
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!FfmpegLocator.IsAvailable) { LocateFfmpeg(); if (!FfmpegLocator.IsAvailable) return; }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            await OpenAsync(files[0]);
    }

    // ============================================================== full screen

    /// <summary>
    /// Double-clicking the picture hands the window over to the video: the toolbar, the info
    /// panel, the graphs, the timeline and the status bar all collapse, and the window itself
    /// goes borderless and maximised so the picture fills the screen. Double-clicking again, or
    /// Escape, puts everything back exactly as it was.
    /// </summary>
    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        ToggleVideoFullScreen();
        e.Handled = true;
    }

    private void ToggleVideoFullScreen()
    {
        if (_videoFullScreen) ExitVideoFullScreen();
        else EnterVideoFullScreen();
    }

    private void EnterVideoFullScreen()
    {
        if (_videoFullScreen) return;
        _videoFullScreen = true;

        _savedState = WindowState;
        _savedStyle = WindowStyle;
        _savedResize = ResizeMode;
        _savedBounds = RestoreBounds;
        _savedRootMargin = Root.Margin;
        _savedFrameMargin = VideoFrame.Margin;
        _savedFrameCorners = VideoFrame.CornerRadius;
        _savedFrameBorder = VideoFrame.BorderThickness;

        Toolbar.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        GraphPanel.Visibility = Visibility.Collapsed;
        TimelinePanel.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;

        // Collapsing the panel is not enough: the column it sat in keeps its share of the width,
        // and its MinWidth would hold the picture back even at zero.
        InfoColumn.Width = new GridLength(0);
        InfoColumn.MinWidth = 0;
        VideoColumn.MinWidth = 0;

        Root.Margin = new Thickness(0);
        VideoFrame.Margin = new Thickness(0);
        VideoFrame.CornerRadius = new CornerRadius(0);
        VideoFrame.BorderThickness = new Thickness(0);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        AppLog.Write("video: full screen on");
    }

    private void ExitVideoFullScreen()
    {
        if (!_videoFullScreen) return;
        _videoFullScreen = false;

        // Back to a normal window first: restyling a maximised borderless window leaves it
        // sized for the old frame.
        WindowState = WindowState.Normal;
        WindowStyle = _savedStyle;
        ResizeMode = _savedResize;
        Left = _savedBounds.Left;
        Top = _savedBounds.Top;
        Width = _savedBounds.Width;
        Height = _savedBounds.Height;
        if (_savedState == WindowState.Maximized) WindowState = WindowState.Maximized;

        Toolbar.Visibility = Visibility.Visible;
        InfoPanel.Visibility = Visibility.Visible;
        GraphPanel.Visibility = Visibility.Visible;
        TimelinePanel.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;

        InfoColumn.Width = new GridLength(1, GridUnitType.Star);
        InfoColumn.MinWidth = 330;
        VideoColumn.MinWidth = 300;

        Root.Margin = _savedRootMargin;
        VideoFrame.Margin = _savedFrameMargin;
        VideoFrame.CornerRadius = _savedFrameCorners;
        VideoFrame.BorderThickness = _savedFrameBorder;

        AppLog.Write("video: full screen off");
    }

    // ================================================================ playback

    private void ReleaseCurrentBuffer()
    {
        if (_currentBuffer is null) return;
        _engine.ReturnBuffer(_currentBuffer);
        _currentBuffer = null;
    }

    private void RestartEngine(int index, double seconds)
    {
        var result = _result;
        if (result is null) return;

        _engine.Start(result.Media.FilePath, _decodeW, _decodeH, seconds, index);
        _displayedIndex = index - 1;
        _targetIndex = index;

        if (index >= 0 && index < result.FrameCount)
        {
            _vm.CurrentFrame = result.Frames[index];
            _vm.Position = result.Frames[index].PtsTime;
        }
    }

    private void ResizeDecoder(bool force = false)
    {
        var result = _result;
        if (result is null) return;

        double availW = Surface.ActualWidth;
        double availH = Surface.ActualHeight;
        if (availW < 40 || availH < 40) return;

        var m = result.Media;
        double scale = Math.Min(availW / Math.Max(1, m.Width), availH / Math.Max(1, m.Height));
        scale = Math.Min(scale, 1.0);
        if (m.Width * scale > 1280) scale = 1280.0 / m.Width;

        int w = Math.Max(2, (int)(m.Width * scale));
        int h = Math.Max(2, (int)(m.Height * scale));
        w -= w % 2;
        h -= h % 2;

        if (!force && w == _decodeW && h == _decodeH) return;

        _decodeW = w;
        _decodeH = h;

        int resumeIndex = Math.Max(0, _displayedIndex);
        double resumeTime = _result is not null && resumeIndex < _result.FrameCount
            ? _result.Frames[resumeIndex].PtsTime
            : 0;
        RestartEngine(resumeIndex, resumeTime);
    }

    /// <summary>
    /// True when the transport is sitting at the end of the clip.
    ///
    /// Three signals, because no single one covers every route to the end: the picture running
    /// out of frames (the decoder can stop short of the packet table), the frame index reaching
    /// the last entry, and the position landing on the last frame — which is what a seek to the
    /// end does, without playback ever having advanced the index.
    /// </summary>
    private bool AtEndOfClip()
    {
        var r = _result;
        if (r is null || r.FrameCount == 0) return false;
        if (_playbackEnded || _displayedIndex >= r.FrameCount - 1) return true;

        double halfFrame = 0.5 / Math.Max(1, r.Media.FrameRate);
        return _vm.Position >= r.LastFrameTime - halfFrame;
    }

    private void SetPlaying(bool play)
    {
        _playing = play;
        _vm.IsPlaying = play;

        if (play)
        {
            // Pressing play on a finished clip means "play it again from the top"; playing from
            // the last frame would stop again on the next tick.
            if (AtEndOfClip()) SeekTo(0);

            _playbackEnded = false;
            _playAnchorIndex = Math.Max(0, _displayedIndex);
            _clock.Restart();
            _targetIndex = _playAnchorIndex;
            _audioOffsetWarned = false;

            // The picture and the sound are decoded by different engines, so the audio is
            // re-anchored on the frame the picture is about to start from.
            var result = _result;
            double from = result is not null && _playAnchorIndex < result.FrameCount
                ? result.Frames[_playAnchorIndex].PtsTime
                : _vm.Position;
            _audio.Play(from);
        }
        else
        {
            _clock.Stop();
            _audio.Pause();

            var r = _result;
            double shown = r is not null && _displayedIndex >= 0 && _displayedIndex < r.FrameCount
                ? r.Frames[_displayedIndex].PtsTime
                : 0;
            AppLog.Write($"playback: stopped after {_clock.Elapsed.TotalSeconds:0.###}s wall clock, picture at {shown:0.###}s");
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var result = _result;
        if (result is null || result.FrameCount == 0) return;

        if (_playing)
        {
            int expected = _playAnchorIndex + (int)(_clock.Elapsed.TotalSeconds * Math.Max(1, result.Media.FrameRate));
            if (expected > _targetIndex)
                _targetIndex = Math.Min(result.FrameCount - 1, expected);

            if (++_syncTicks >= 25)
            {
                _syncTicks = 0;
                double shown = _displayedIndex >= 0 && _displayedIndex < result.FrameCount
                    ? result.Frames[_displayedIndex].PtsTime
                    : 0;
                double audio = _audio.IsLoaded ? _audio.PositionSeconds : shown;
                double offset = shown - audio;

                // Log sparsely: enough to diagnose drift, not enough to bury the pipeline
                // messages. A one-off warning the first time the offset is audible is the
                // part that actually matters.
                if (Math.Abs(offset) > 0.5 && !_audioOffsetWarned)
                {
                    _audioOffsetWarned = true;
                    AppLog.Write($"audio: A/V offset {offset:+0.###;-0.###}s");
                }
                else if (++_syncLogTicks >= 250)
                {
                    _syncLogTicks = 0;
                    AppLog.Write($"sync: wall {_clock.Elapsed.TotalSeconds:0.00}s  picture {shown:0.00}s  " +
                                 $"audio {audio:0.00}s  offset {offset:+0.00;-0.00;0.00}s");
                }
            }
        }

        while (_displayedIndex < _targetIndex && _engine.TryGetFrame(out var buffer) && buffer is not null)
        {
            _displayedIndex++;
            ShowFrame(buffer, _displayedIndex);
        }

        // The decoder can run out of frames before the packet table does, so "the clip is over"
        // is taken from the engine — end of stream, queue drained — and not only from the frame
        // index. Relying on the index alone left the transport stuck in "playing" on the last
        // decoded frame, with no way to play the clip again.
        bool finished = _displayedIndex >= result.FrameCount - 1 || _engine.Ended;

        if (_playing && finished)
        {
            if (_vm.Loop)
            {
                SeekTo(0);
                _playAnchorIndex = 0;
                _clock.Restart();
            }
            else
            {
                _playbackEnded = true;
                AppLog.Write($"playback: end of clip at frame {_displayedIndex}/{result.FrameCount - 1} " +
                             $"(decoder eof={_engine.Ended})");
                SetPlaying(false);
            }
        }
    }

    private void ShowFrame(byte[] buffer, int index)
    {
        Surface.ClearPreview();
        Surface.PushFrame(buffer, _decodeW, _decodeH);

        if (_currentBuffer is not null && !ReferenceEquals(_currentBuffer, buffer))
            _engine.ReturnBuffer(_currentBuffer);
        _currentBuffer = buffer;

        var result = _result;
        if (result is null || index < 0 || index >= result.FrameCount) return;

        var frame = result.Frames[index];
        _vm.CurrentFrame = frame;
        _vm.Position = frame.PtsTime;
        _vm.EnsureVisible(frame.PtsTime);
    }

    private void SeekTo(double seconds)
    {
        var result = _result;
        if (result is null || result.FrameCount == 0)
        {
            _vm.Position = seconds;
            return;
        }

        int index = Math.Clamp(result.IndexAt(seconds), 0, result.FrameCount - 1);
        var frame = result.Frames[index];
        AppLog.Write($"seek: {seconds:0.###}s -> frame {index} @ {frame.PtsTime:0.###}s");

        _playbackEnded = false;      // the transport left the end of the clip
        _engine.Start(result.Media.FilePath, _decodeW, _decodeH, frame.PtsTime, index);
        _displayedIndex = index - 1;
        _targetIndex = index;
        _vm.CurrentFrame = frame;
        _vm.Position = frame.PtsTime;
        _vm.EnsureVisible(frame.PtsTime);
        _audio.Seek(frame.PtsTime);

        if (_playing)
        {
            _playAnchorIndex = index;
            _clock.Restart();
        }
    }

    // ================================================================ scrubbing

    /// <summary>
    /// Drag feedback: no decoder restart, just the filmstrip thumbnail for the position
    /// under the cursor. This is what keeps a drag smooth — a real seek per mouse-move
    /// would queue up dozens of decoder restarts.
    /// </summary>
    private void OnScrubPreview(object? sender, double seconds)
    {
        if (!_scrubbing)
        {
            // A drag takes the transport over: the picture is driven by the thumbnail preview
            // instead, and whatever was playing resumes once the drag is released.
            _resumeAfterScrub = _playing;
            if (_playing) SetPlaying(false);
        }

        _scrubbing = true;
        seconds = ClampToClip(seconds);
        _vm.Position = seconds;

        var thumb = _filmstrip.At(seconds);
        AppLog.Write($"scrub: preview {seconds:0.###}s thumb={(thumb is null ? "none" : "ok")}");
        if (thumb is not null)
            Surface.ShowPreview(thumb, _filmstrip.Width, _filmstrip.Height);

        _vm.EnsureVisible(seconds);
    }

    private void OnScrubCommit(object? sender, double seconds)
    {
        bool resume = _resumeAfterScrub || _playing;
        _resumeAfterScrub = false;
        _scrubbing = false;
        seconds = ClampToClip(seconds);

        AppLog.Write($"scrub: commit {seconds:0.###}s resume={resume}");
        SeekTo(seconds);

        // SeekTo re-anchors the clock when the transport is already running; it only needs
        // starting again when a drag paused it.
        if (resume && !_playing) SetPlaying(true);
    }

    /// <summary>
    /// The strip is wider than the clip while the playhead is pinned — it shows time before the
    /// first frame and after the last — so a drag can land outside the file. Seeking is clamped
    /// to the real range instead of following the cursor into the empty part.
    /// </summary>
    private double ClampToClip(double seconds)
    {
        var result = _result;
        if (result is null || result.FrameCount == 0) return Math.Max(0, seconds);
        return Math.Clamp(seconds, 0, result.LastFrameTime);
    }

    private void OnZoomRequested(object? sender, (double seconds, double factor) value)
        => _vm.ZoomAt(value.seconds, value.factor);

    private void OnToggleLoopClick(object sender, RoutedEventArgs e) => _vm.Loop = !_vm.Loop;

    private void OnToggleMuteClick(object sender, RoutedEventArgs e) => _vm.Muted = !_vm.Muted;

    private void OnTogglePinClick(object sender, RoutedEventArgs e) => _vm.PinPlayhead = !_vm.PinPlayhead;

    private void OnToggleMotionClick(object sender, RoutedEventArgs e)
        => _vm.MotionAnalysisEnabled = !_vm.MotionAnalysisEnabled;

    private void OnZoomInClick(object sender, RoutedEventArgs e) => _vm.ZoomAt(_vm.Position, 0.6);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => _vm.ZoomAt(_vm.Position, 1 / 0.6);

    private void OnResetZoomClick(object sender, RoutedEventArgs e) => _vm.ResetZoom();

    private void OnGoToStartClick(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        SeekTo(0);
    }

    private void OnGoToEndClick(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        SeekTo(Math.Max(0, _result.DurationSeconds - 1.0 / Math.Max(1, _result.Media.FrameRate)));
    }

    /// <summary>
    /// Jumps to the neighbouring key frame. With a GOP of a few dozen frames this is the
    /// quickest way to move around a long clip while keeping the seek cheap.
    /// </summary>
    private void OnPrevKeyFrameClick(object sender, RoutedEventArgs e)
    {
        var result = _result;
        if (result is null || result.FrameCount == 0) return;

        SetPlaying(false);
        int from = Math.Max(0, _displayedIndex);
        for (int i = from - 1; i >= 0; i--)
        {
            if (!result.Frames[i].KeyFrame) continue;
            SeekTo(result.Frames[i].PtsTime);
            return;
        }
        SeekTo(0);
    }

    private void OnNextKeyFrameClick(object sender, RoutedEventArgs e)
    {
        var result = _result;
        if (result is null || result.FrameCount == 0) return;

        SetPlaying(false);
        int from = Math.Max(0, _displayedIndex);
        for (int i = from + 1; i < result.FrameCount; i++)
        {
            if (!result.Frames[i].KeyFrame) continue;
            SeekTo(result.Frames[i].PtsTime);
            return;
        }
    }

    // ================================================================== input

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        SetPlaying(!_playing);
    }

    private void OnStepBackClick(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        SetPlaying(false);
        int index = Math.Max(0, _displayedIndex - 1);
        SeekTo(_result.Frames[index].PtsTime);
    }

    private void OnStepForwardClick(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        SetPlaying(false);
        int index = Math.Min(_result.FrameCount - 1, Math.Max(0, _displayedIndex) + 1);
        _targetIndex = index;
    }

    private void OnCycleOverlayClick(object sender, RoutedEventArgs e)
        => _vm.Overlay = _vm.Overlay switch
        {
            OverlayMode.None => OverlayMode.MotionVectors,
            OverlayMode.MotionVectors => OverlayMode.BlockNoise,
            OverlayMode.BlockNoise => OverlayMode.MacroblockGrid,
            _ => OverlayMode.None
        };

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape when _videoFullScreen:
                ExitVideoFullScreen();
                e.Handled = true;
                break;
            case Key.Space:
                if (_result is not null) SetPlaying(!_playing);
                e.Handled = true;
                break;
            case Key.Left when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                OnPrevKeyFrameClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Right when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                OnNextKeyFrameClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Left:
                OnStepBackClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Right:
                OnStepForwardClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.L when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                _vm.Loop = !_vm.Loop;
                e.Handled = true;
                break;
            case Key.M when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                _vm.Muted = !_vm.Muted;
                e.Handled = true;
                break;
            case Key.Up:
                _vm.Volume += 0.05;
                e.Handled = true;
                break;
            case Key.Down:
                _vm.Volume -= 0.05;
                e.Handled = true;
                break;
            case Key.O when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                OnOpenClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.D0 when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
            case Key.NumPad0 when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                _vm.ResetZoom();
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                _vm.ZoomAt(_vm.Position, 0.6);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                _vm.ZoomAt(_vm.Position, 1 / 0.6);
                e.Handled = true;
                break;
            case Key.Home when _result is not null:
                SeekTo(0);
                e.Handled = true;
                break;
            case Key.End when _result is not null:
                SeekTo(_result.DurationSeconds);
                e.Handled = true;
                break;
        }
    }
}
