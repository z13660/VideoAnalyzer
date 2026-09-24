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
    /// <summary>
    /// The part of a file a deep pass should look at: a range of the frame table, plus the
    /// ffmpeg arguments that make a decoder produce exactly that part.
    ///
    /// The two offsets are not the same thing, which is the whole reason this type exists. A
    /// <c>-debug qp</c> dump is written by the decoder as it works, so it also covers the key
    /// frame it had to decode on the way in — hence <see cref="DecodeOffset"/>, taken from the
    /// key frame and paired with a skip of everything before the slice. A rawvideo pipe only
    /// carries what ffmpeg decided to output, which starts at the slice itself — hence the plain
    /// presentation index used by the motion pass.
    /// </summary>
    public readonly record struct Slice(
        int First, int Last, int DecodeOffset, int FirstDecode, double From, double To)
    {
        public static readonly Slice Whole = new(0, int.MaxValue, 0, 0, 0, -1);

        public int Count => Math.Max(0, Last - First + 1);
        public bool Ranged => From > 0.01 || (To > 0 && To < double.MaxValue);
        public double Length => Math.Max(0, To - From);

        /// <summary>Input-side arguments: the seek, placed before <c>-i</c>.</summary>
        public string InputArgs => Ranged
            ? $"-ss {From.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} "
            : string.Empty;

        /// <summary>Output-side arguments: the duration, placed after <c>-i</c>.</summary>
        public string OutputArgs => Ranged
            ? $"-t {Length.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} "
            : string.Empty;

        public IEnumerable<FrameInfo> Frames(AnalysisResult result)
        {
            for (int i = First; i <= Last && i < result.Frames.Count; i++)
                yield return result.Frames[i];
        }

        public static Slice Of(AnalysisResult result, double from, double to)
        {
            int last = result.FrameCount - 1;
            if (last < 0) return new Slice(0, -1, 0, 0, 0, -1);

            double end = to > 0 ? Math.Min(to, result.DurationSeconds) : result.DurationSeconds;
            double start = Math.Clamp(from, 0, Math.Max(0, end - 0.04));
            if (end - start < 0.05) end = Math.Min(result.DurationSeconds, start + 0.05);

            int first = Math.Clamp(result.IndexAt(start), 0, last);
            int lastIndex = Math.Clamp(result.IndexAt(end), first, last);

            // the decoder starts at the key frame at or before the slice, so that is the index
            // its own numbering has to be shifted by
            int key = first;
            while (key > 0 && !result.Frames[key].KeyFrame) key--;
            int decodeOffset = Math.Max(0, result.Frames[key].DecodeIndex);
            int firstDecode = Math.Max(0, result.Frames[first].DecodeIndex);

            return new Slice(first, lastIndex, decodeOffset, firstDecode, start, end);
        }

        public override string ToString()
            => Ranged ? $"{From:0.###}-{To:0.###}s frames {First}-{Last}" : "whole file";
    }

    private static readonly Regex NewFrameRx = new(@"New frame, type:\s*(\w)", RegexOptions.Compiled);
    private static readonly Regex IntRx = new(@"-?\d+", RegexOptions.Compiled);
    private static readonly Regex MvRx = new(@"mv[^\d-]*(-?\d+)[^\d-]+(-?\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int UpdateEvery = 96;   // frames between UI refreshes

    // ------------------------------------------------------------------ QP + type

    /// <summary>
    /// Picture types and QP. <paramref name="fromSeconds"/> / <paramref name="toSeconds"/> limit
    /// the decode to a slice of the file — a two hour recording can be examined a minute at a
    /// time — and the parsed frames are mapped back onto the whole frame table, so the graphs
    /// show the analysed slice in its real place and leave the rest empty.
    /// </summary>
    public static async Task RunTypeAndQpPassAsync(
        AnalysisResult result,
        IProgress<DeepProgress>? progress,
        Action onUpdated,
        CancellationToken ct,
        double fromSeconds = 0,
        double toSeconds = -1)
    {
        var file = result.Media.FilePath;
        var slice = Slice.Of(result, fromSeconds, toSeconds);
        bool qpAvailable = await ProbeAsync(file, "-debug qp", "New frame, type:", ct);
        AppLog.Write($"deep: qpNative={qpAvailable} range={slice}");

        if (qpAvailable)
        {
            try
            {
                await RunQpPassAsync(result, progress, onUpdated, ct, slice);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Write("deep: qp pass failed", ex);
            }
        }

        // Types can still be missing (frames near the end, or no dump at all).
        InferMissingTypes(result, slice);

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
            }, ct, ProcessRunner.Analysis).ConfigureAwait(false);

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
            }, ct, ProcessRunner.Analysis).ConfigureAwait(false);

            return found;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static async Task RunQpPassAsync(
        AnalysisResult result,
        IProgress<DeepProgress>? progress,
        Action onUpdated,
        CancellationToken ct,
        Slice slice)
    {
        var exe = FfmpegLocator.FfmpegPath!;
        var args = $"-v debug -hide_banner -debug qp {slice.InputArgs}-i \"{result.Media.FilePath}\" " +
                   $"{slice.OutputArgs}-an -f null -";

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
        double total = Math.Max(1, slice.Count);

        await ProcessRunner.RunStreamingAsync(exe, args, line =>
        {
            var m = NewFrameRx.Match(line);
            if (m.Success)
            {
                decodeIndex++;

                // The decoder was handed a seek, so it starts at the key frame before the
                // slice and re-numbers from there. Shift its numbering back onto the file's
                // decode indices, and ignore the pre-roll it decoded on the way in.
                int global = decodeIndex + slice.DecodeOffset;
                if (global < slice.FirstDecode) return;

                types[global] = ParseType(m.Groups[1].Value);
                values[global] = new List<int>(64);
                openFrame[PointerOf(line)] = global;

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
        }, ct, ProcessRunner.Analysis).ConfigureAwait(false);

        Apply(result, byDecodeIndex, values, types);
        FillQpGaps(result, slice);

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
    private static void FillQpGaps(AnalysisResult result, Slice slice)
    {
        double? last = null;
        foreach (var f in slice.Frames(result))
        {
            if (f.QpAvg.HasValue) { last = f.QpAvg; continue; }
            if (last is null) continue;

            f.QpAvg = last;
            f.QpMin = last;
            f.QpMax = last;
        }

        // a leading run with nothing before it takes the first known value
        last = null;
        for (int i = slice.Last; i >= slice.First; i--)
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
    private static void InferMissingTypes(AnalysisResult result, Slice slice)
    {
        var frames = result.Frames;
        int last = slice.First - 1;
        for (int i = slice.First; i <= slice.Last && i < frames.Count; i++)
        {
            if (frames[i].Type != PicType.Other) { last = i; continue; }
            if (frames[i].KeyFrame) { frames[i].Type = PicType.I; continue; }
            if (last >= 0) frames[i].Type = frames[last].Type;
        }

        // a leading run with no known type inherits from the first known one
        int first = -1;
        for (int i = slice.First; i <= slice.Last && i < frames.Count; i++)
            if (frames[i].Type != PicType.Other) { first = i; break; }
        if (first > slice.First)
            for (int i = slice.First; i < first; i++)
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
        CancellationToken ct,
        double fromSeconds = 0,
        double toSeconds = -1)
    {
        var slice = Slice.Of(result, fromSeconds, toSeconds);
        bool mvAvailable = await ProbeMotionVectorsAsync(result.Media.FilePath, ct);
        AppLog.Write($"deep: mvNative={mvAvailable} range={slice}");

        if (mvAvailable)
        {
            try
            {
                await RunMvPassAsync(result, progress, ct, slice);
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
        }, ct, slice);

        result.MotionEstimated = true;
        result.ComputeAggregates();
        result.MarkUpdated();
        onUpdated();
        AppLog.Write($"deep: motion done withValues={result.Frames.Count(f => f.MotionMean.HasValue)}");
    }

    private static async Task RunMvPassAsync(
        AnalysisResult result,
        IProgress<DeepProgress>? progress,
        CancellationToken ct,
        Slice slice)
    {
        var exe = FfmpegLocator.FfmpegPath!;
        var args = $"-v debug -hide_banner -debug mv {slice.InputArgs}-i \"{result.Media.FilePath}\" " +
                   $"{slice.OutputArgs}-an -f null -";

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
        }, ct, ProcessRunner.Analysis).ConfigureAwait(false);

        if (count > 0) perFrame.Add((sum / count, max, fwd, bwd));

        // perFrame[0] is the key frame the decoder had to start from, so the file's decode
        // indices are shifted by that much before they index into it
        foreach (var f in result.Frames)
        {
            int local = f.DecodeIndex - slice.DecodeOffset;
            if (f.DecodeIndex >= 0 && local >= 0 && local < perFrame.Count)
            {
                var (mean, mx, fw, bw) = perFrame[local];
                f.MotionMean = mean; f.MotionMax = mx;
                f.MotionFwdCount = fw; f.MotionBwdCount = bw;
                f.MotionVectorCount = fw + bw;
            }
        }
    }
}
