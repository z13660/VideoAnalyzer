using VideoAnalyzer.Models;

namespace VideoAnalyzer.Services;

public sealed record AnalysisProgress(string Stage, double Fraction);

/// <summary>
/// Orchestrates the analysis of one file as a progressive pipeline.
///
/// Stages run in order of usefulness rather than completeness, and each one publishes its
/// results as soon as it has them:
///
///   1. probe        (~0.3 s)  container and stream metadata      -> info panel, playback
///   2. packets      (~0.5 s)  size / pts / dts / key flags        -> bitrate, GOP, reorder
///   3. type + QP    (~10 s)   picture types and real QP           -> frame type, QP
///   4. motion       (~17 s)   block-matched motion field          -> motion graph, overlay
///
/// Stages 3 and 4 run at the same time: they read the file through separate decoders and touch
/// different fields of each frame record, so the deep pass costs the slower of the two rather
/// than their sum. The only shared field is the picture type, which stage 3 has usually written
/// long before stage 4 applies its results.
///
/// The caller is told the moment playback can start (<see cref="FramesReady"/>) and again
/// on every batch that changes the frame table (<see cref="Updated"/>), so the window is
/// interactive and the graphs fill in while the rest of the work continues.
/// </summary>
public sealed class AnalysisPipeline
{
    /// <summary>Raised once the frame table exists and the transport can be used.</summary>
    public event Action<AnalysisResult>? FramesReady;

    /// <summary>Raised when the frame table changed and the views should refresh.</summary>
    public event Action? Updated;

    public AnalysisResult? Result { get; private set; }

    public async Task RunAsync(
        string filePath,
        bool motionAnalysis,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct)
    {
        AppLog.Write($"analyse: start {filePath} motion={motionAnalysis}");

        // ---- 1. probe -------------------------------------------------------
        progress?.Report(new AnalysisProgress("Reading container metadata…", 0));
        var media = await MediaProbeService.ProbeAsync(filePath, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var result = new AnalysisResult { Media = media };
        Result = result;
        AppLog.Write($"analyse: probe ok {media.Width}x{media.Height} {media.FrameRateText} " +
                     $"frames={media.NbFrames} dur={media.DurationSeconds:0.###}");

        // The transport can already open the file; the graphs simply have no data yet.
        FramesReady?.Invoke(result);

        // ---- 2. packets -----------------------------------------------------
        progress?.Report(new AnalysisProgress("Scanning packet table…", 0.02));
        var frames = await PacketScanService.ScanAsync(
            media.FilePath,
            media.FrameRate,
            media.NbFrames,
            new Progress<double>(p => progress?.Report(new AnalysisProgress("Scanning packet table…", 0.02 + p * 0.18))),
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        result.Frames.AddRange(frames);
        result.ComputeAggregates();

        if (frames.Count > 0)
        {
            media.NbFrames = frames.Count;
            if (media.DurationSeconds <= 0) media.DurationSeconds = result.DurationSeconds;
        }

        AppLog.Write($"analyse: packets produced {frames.Count} frames, maxGop={result.MaxGop} " +
                     $"maxBitrate={result.MaxBitrateKbps:0} reorder=[{result.MinReorder},{result.MaxReorder}]");

        result.MarkUpdated();
        Updated?.Invoke();
        FramesReady?.Invoke(result);   // the transport now has exact frame timings

        if (frames.Count == 0)
        {
            progress?.Report(new AnalysisProgress("Ready", 1));
            return;
        }

        // ---- 3 + 4. picture types + QP, and motion, side by side -------------
        // The two passes are independent — one parses the decoder's QP dump, the other
        // block-matches the decoded picture — and each takes about as long as the other. Run
        // together they finish in the time of the slower one instead of the sum, which is what
        // gets the graphs complete shortly after the file is opened.
        var gate = new object();
        double qpDone = 0, motionDone = 0;
        string deepStage = motionAnalysis ? "Analysing QP and motion…" : "Analysing QP…";

        void ReportDeep()
        {
            double fraction;
            lock (gate) fraction = Math.Max(qpDone, motionDone);
            progress?.Report(new AnalysisProgress(deepStage, 0.20 + fraction * 0.80));
        }

        var qpTask = DeepAnalysisService.RunTypeAndQpPassAsync(
            result,
            new Progress<DeepProgress>(p => { lock (gate) qpDone = p.Fraction; ReportDeep(); }),
            () => Updated?.Invoke(),
            ct);

        var motionTask = Task.CompletedTask;
        if (motionAnalysis)
        {
            motionTask = DeepAnalysisService.RunMotionPassAsync(
                result,
                new Progress<DeepProgress>(p => { lock (gate) motionDone = p.Fraction; ReportDeep(); }),
                () => Updated?.Invoke(),
                ct);
        }

        await Task.WhenAll(qpTask, motionTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (motionAnalysis) result.HasDeepAnalysis = true;

        result.ComputeAggregates();
        result.MarkUpdated();
        Updated?.Invoke();
        progress?.Report(new AnalysisProgress("Ready", 1));
    }
}
