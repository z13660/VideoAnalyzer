using System.Globalization;
using System.Text.RegularExpressions;
using VideoAnalyzer.Models;

namespace VideoAnalyzer.Services;

public sealed record DeepProgress(string Stage, double Fraction);

/// <summary>
/// The decoder passes that run after the packet scan.
///
/// Two jobs, deliberately separated so the cheap and the expensive one can be scheduled
/// independently:
///
///  * <see cref="RunTypeAndQpPassAsync"/> decodes the stream with <c>-debug qp</c>. That
///    single pass yields both the picture type of every frame (packets do not carry it)
///    and the real per-macroblock quantiser. Mainline ffmpeg emits the QP dump for the
///    MPEG-1/2/4 family and for H.264, which covers the common cases; when it is missing
///    the value falls back to a bits-per-pixel estimate that the UI flags as "(est.)".
///
///  * <see cref="RunMotionPassAsync"/> is the expensive one and is optional.
///
/// Both report progress as they go and call <paramref name="onUpdated"/> in batches, so
/// the graphs fill in while the file is still being analysed.
/// </summary>
public static class DeepAnalysisService
{
    private static readonly Regex NewFrameRx = new(@"New frame, type:\s*(\w)", RegexOptions.Compiled);
    private static readonly Regex IntRx = new(@"-?\d+", RegexOptions.Compiled);
    private static readonly Regex MvRx = new(@"mv[^\d-]*(-?\d+)[^\d-]+(-?\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int UpdateEvery = 96;   // frames between UI refreshes

    // ------------------------------------------------------------------ QP + type

    public static async Task RunTypeAndQpPassAsync(
        AnalysisResult result,
        IProgress<DeepProgress>? progress,
        Action onUpdated,
        CancellationToken ct)
    {
        var file = result.Media.FilePath;
        bool qpAvailable = await ProbeAsync(file, "-debug qp", "New frame, type:", ct);
        AppLog.Write($"deep: qpNative={qpAvailable}");

        if (qpAvailable)
        {
            try
            {
                await RunQpPassAsync(result, progress, onUpdated, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Write("deep: qp pass failed", ex);
            }
        }

        // Types can still be missing (frames near the end, or no dump at all).
        InferMissingTypes(result);

        if (result.Frames.All(f => !f.QpAvg.HasValue))
        {
            EstimateQp(result);
            result.QpEstimated = true;
        }

        result.ComputeAggregates();
        result.MarkUpdated();
        onUpdated();
        AppLog.Write($"deep: qp done native={!result.QpEstimated} withValues={result.Frames.Count(f => f.QpAvg.HasValue)}");
    }

    private static async Task<bool> ProbeAsync(string file, string debugFlag, string needle, CancellationToken ct)
    {
        try
        {
            var exe = FfmpegLocator.FfmpegPath!;
            var args = $"-v debug -hide_banner {debugFlag} -i \"{file}\" -t 1 -an -f null -";
            bool found = false;

            await ProcessRunner.RunStreamingAsync(exe, args, line =>
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase)) found = true;
            }, ct).ConfigureAwait(false);

            return found;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Motion vector debug output needs its own probe: asking ffmpeg for <c>-debug mv</c> on
    /// H.264 fails with "Undefined constant or missing '(' in 'mv'", and a plain substring
    /// match on "mv" happily matches that error text. Only an actual vector pair counts.
    /// </summary>
    private static async Task<bool> ProbeMotionVectorsAsync(string file, CancellationToken ct)
    {
        try
        {
            var exe = FfmpegLocator.FfmpegPath!;
            var args = $"-v debug -hide_banner -debug mv -i \"{file}\" -t 1 -an -f null -";
            bool found = false;

            await ProcessRunner.RunStreamingAsync(exe, args, line =>
            {
                if (MvRx.IsMatch(line)) found = true;
            }, ct).ConfigureAwait(false);

            return found;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static async Task RunQpPassAsync(
        AnalysisResult result,
        IProgress<DeepProgress>? progress,
        Action onUpdated,
        CancellationToken ct)
    {
        var exe = FfmpegLocator.FfmpegPath!;
        var args = $"-v debug -hide_banner -debug qp -i \"{result.Media.FilePath}\" -an -f null -";

        // decode order -> frame, resolved once instead of searched per sample
        var byDecodeIndex = new Dictionary<int, FrameInfo>(result.FrameCount);
        foreach (var f in result.Frames)
            if (f.DecodeIndex >= 0) byDecodeIndex[f.DecodeIndex] = f;

        // ffmpeg decodes with several frame threads and their QP dumps are interleaved on
        // stderr, so a run of lines between two "New frame" markers can belong to different
        // frames. Grouping by the decoder instance in the line prefix is the only reliable
        // way to attribute values: each thread emits its own frame's rows in order.
        var openFrame = new Dictionary<IntPtr, int>();
        var values = new Dictionary<int, List<int>>();
        var types = new Dictionary<int, PicType>();
        int decodeIndex = -1;
        int sinceUpdate = 0;
        double total = Math.Max(1, result.FrameCount);

        await ProcessRunner.RunStreamingAsync(exe, args, line =>
        {
            var m = NewFrameRx.Match(line);
            if (m.Success)
            {
                decodeIndex++;
                types[decodeIndex] = ParseType(m.Groups[1].Value);
                values[decodeIndex] = new List<int>(64);
                openFrame[PointerOf(line)] = decodeIndex;

                if (++sinceUpdate >= UpdateEvery)
                {
                    sinceUpdate = 0;
                    Apply(result, byDecodeIndex, values, types);
                    result.ComputeAggregates();
                    result.MarkUpdated();
                    onUpdated();
                    progress?.Report(new DeepProgress("Extracting picture types and QP…",
                        Math.Min(1.0, decodeIndex / total)));
                }
                return;
            }

            var body = BodyOf(line);
            if (body.Length == 0) return;
            if (!openFrame.TryGetValue(PointerOf(line), out var index)) return;
            if (!values.TryGetValue(index, out var list)) return;

            // the first line of a dump is the column header: block x coordinates, which are
            // far larger than any quantiser value
            foreach (Match n in IntRx.Matches(body))
            {
                if (!int.TryParse(n.Value, out var v)) continue;
                if (v > 63) return;
            }

            foreach (Match n in IntRx.Matches(body))
                if (int.TryParse(n.Value, out var v) && v is > 0 and <= 63) list.Add(v);
        }, ct).ConfigureAwait(false);

        Apply(result, byDecodeIndex, values, types);
        FillQpGaps(result);

        result.ComputeAggregates();
        result.MarkUpdated();
        onUpdated();
        progress?.Report(new DeepProgress("Extracting picture types and QP…", 1));
    }

    /// <summary>Decoder instance from a <c>[h264 @ 000001b5...]</c> line prefix.</summary>
    private static IntPtr PointerOf(string line)
    {
        int at = line.IndexOf("@ ", StringComparison.Ordinal);
        if (at < 0) return IntPtr.Zero;

        int close = line.IndexOf(']', at + 2);
        if (close < 0) return IntPtr.Zero;

        var hex = line.AsSpan(at + 2, close - at - 2).Trim();
        return long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? (IntPtr)v
            : IntPtr.Zero;
    }

    /// <summary>Everything after the <c>[component @ address]</c> prefix, which contains numbers that are not QP values.</summary>
    private static string BodyOf(string line)
    {
        int close = line.IndexOf(']');
        return close >= 0 && close + 1 < line.Length ? line[(close + 1)..] : string.Empty;
    }

    private static void Apply(
        AnalysisResult result,
        Dictionary<int, FrameInfo> byDecodeIndex,
        Dictionary<int, List<int>> values,
        Dictionary<int, PicType> types)
    {
        foreach (var (index, list) in values)
        {
            if (!byDecodeIndex.TryGetValue(index, out var frame)) continue;

            if (types.TryGetValue(index, out var type) && type != PicType.Other)
                frame.Type = type;

            if (list.Count == 0) continue;

            double sum = 0;
            int min = int.MaxValue, max = 0;
            foreach (var v in list) { sum += v; if (v < min) min = v; if (v > max) max = v; }

            frame.QpAvg = sum / list.Count;
            frame.QpMin = min;
            frame.QpMax = max;
        }
    }

    /// <summary>
    /// Frames whose dump was all zeros (a P or B frame with no coded residual) end up without a
    /// quantiser. Carrying the previous value forward keeps the graph continuous, and the values
    /// on either side of such a frame differ by very little in practice.
    /// </summary>
    private static void FillQpGaps(AnalysisResult result)
    {
        double? last = null;
        foreach (var f in result.Frames)
        {
            if (f.QpAvg.HasValue) { last = f.QpAvg; continue; }
            if (last is null) continue;

            f.QpAvg = last;
            f.QpMin = last;
            f.QpMax = last;
        }

        // a leading run with nothing before it takes the first known value
        last = null;
        for (int i = result.Frames.Count - 1; i >= 0; i--)
        {
            var f = result.Frames[i];
            if (f.QpAvg.HasValue) { last = f.QpAvg; continue; }
            if (last is null) continue;
            f.QpAvg = last;
            f.QpMin = last;
            f.QpMax = last;
        }
    }

    private static PicType ParseType(string s) => s.Length == 0 ? PicType.Other : char.ToUpperInvariant(s[0]) switch
    {
        'I' => PicType.I,
        'P' => PicType.P,
        'B' => PicType.B,
        _ => PicType.Other
    };

    /// <summary>
    /// The QP dump can stop short of the last few frames. Those keep whatever the packet
    /// pass left, so the type sequence is completed by copying the nearest known type —
    /// types run in a stable pattern inside a GOP, which makes this a safe repair.
    /// </summary>
    private static void InferMissingTypes(AnalysisResult result)
    {
        var frames = result.Frames;
        int last = -1;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Type != PicType.Other) { last = i; continue; }
            if (frames[i].KeyFrame) { frames[i].Type = PicType.I; continue; }
            if (last >= 0) frames[i].Type = frames[last].Type;
        }

        // a leading run with no known type inherits from the first known one
        int first = frames.FindIndex(f => f.Type != PicType.Other);
        if (first > 0)
            for (int i = 0; i < first; i++)
                frames[i].Type = frames[first].Type;
    }

    /// <summary>
    /// Bits-per-pixel driven quantiser estimate. Encoder rate control keeps bits per frame
    /// roughly proportional to 1/QP^1.6, so inverting that gives a usable trend line even
    /// though the absolute value is approximate.
    /// </summary>
    private static void EstimateQp(AnalysisResult result)
    {
        var m = result.Media;
        double px = Math.Max(1, m.Width * (double)m.Height);
        if (result.Frames.Count == 0) return;

        foreach (var f in result.Frames)
        {
            double bpp = f.PacketSize * 8.0 / px;
            double qp = 34.0 / Math.Pow(Math.Max(bpp, 1e-5) * 260.0, 1 / 1.6);
            qp = Math.Clamp(qp, 1, 51);
            if (f.Type == PicType.I) qp *= 0.75;
            else if (f.Type == PicType.B) qp *= 1.15;

            f.QpAvg = Math.Clamp(qp, 1, 51);
            f.QpMin = Math.Max(0, f.QpAvg.Value - (f.Type == PicType.I ? 12 : 8));
            f.QpMax = Math.Min(51, f.QpAvg.Value + (f.Type == PicType.B ? 14 : 10));
        }
    }

    // ------------------------------------------------------------------- motion

    public static async Task RunMotionPassAsync(
        AnalysisResult result,
        IProgress<DeepProgress>? progress,
        Action onUpdated,
        CancellationToken ct)
    {
        bool mvAvailable = await ProbeMotionVectorsAsync(result.Media.FilePath, ct);
        AppLog.Write($"deep: mvNative={mvAvailable}");

        if (mvAvailable)
        {
            try
            {
                await RunMvPassAsync(result, progress, ct);
                if (result.Frames.Any(f => f.MotionMean.HasValue))
                {
                    result.ComputeAggregates();
                    result.MarkUpdated();
                    onUpdated();
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLog.Write("deep: mv pass failed", ex); }
        }

        await MotionEstimator.RunAsync(result, p =>
        {
            progress?.Report(new DeepProgress("Estimating motion field…", p));
            // The estimator publishes batch by batch, so the aggregates have to be refreshed
            // with it — the motion graph scales to MaxMotion, and a stale maximum would peg
            // every bar at full height while the pass is running.
            result.ComputeAggregates();
            result.MarkUpdated();
            onUpdated();
        }, ct);

        result.MotionEstimated = true;
        result.ComputeAggregates();
        result.MarkUpdated();
        onUpdated();
        AppLog.Write($"deep: motion done withValues={result.Frames.Count(f => f.MotionMean.HasValue)}");
    }

    private static async Task RunMvPassAsync(
        AnalysisResult result,
        IProgress<DeepProgress>? progress,
        CancellationToken ct)
    {
        var exe = FfmpegLocator.FfmpegPath!;
        var args = $"-v debug -hide_banner -debug mv -i \"{result.Media.FilePath}\" -an -f null -";

        var perFrame = new List<(double mean, double max, int fwd, int bwd)>();
        double sum = 0, max = 0;
        int fwd = 0, bwd = 0, count = 0;
        double total = Math.Max(1, result.FrameCount);

        await ProcessRunner.RunStreamingAsync(exe, args, line =>
        {
            if (NewFrameRx.IsMatch(line))
            {
                if (count > 0) perFrame.Add((sum / count, max, fwd, bwd));
                sum = 0; max = 0; fwd = 0; bwd = 0; count = 0;
                if (perFrame.Count % 32 == 0)
                    progress?.Report(new DeepProgress("Extracting motion vectors…", Math.Min(0.99, perFrame.Count / total)));
                return;
            }

            foreach (Match mv in MvRx.Matches(line))
            {
                if (!double.TryParse(mv.Groups[1].Value, out var x)) continue;
                if (!double.TryParse(mv.Groups[2].Value, out var y)) continue;
                var len = Math.Sqrt(x * x + y * y);
                sum += len; count++;
                if (len > max) max = len;
                if (x >= 0) fwd++; else bwd++;
            }
        }, ct).ConfigureAwait(false);

        if (count > 0) perFrame.Add((sum / count, max, fwd, bwd));

        foreach (var f in result.Frames)
        {
            if (f.DecodeIndex >= 0 && f.DecodeIndex < perFrame.Count)
            {
                var (mean, mx, fw, bw) = perFrame[f.DecodeIndex];
                f.MotionMean = mean; f.MotionMax = mx;
                f.MotionFwdCount = fw; f.MotionBwdCount = bw;
                f.MotionVectorCount = fw + bw;
            }
        }
    }
}
