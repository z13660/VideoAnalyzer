using System.Collections.Concurrent;
using VideoAnalyzer.Models;

namespace VideoAnalyzer.Services;

/// <summary>
/// Built-in motion field estimator.
///
/// The clip is decoded once to a small greyscale stream; every frame is then matched
/// against its neighbours with a predicted spiral SAD search, in the spirit of a classic
/// encoder ME stage. Each 16x16 block is matched against both the previous and the next
/// frame and the better match wins, which yields the forward / backward split that both
/// the motion graph and the vector overlay show. Vector lengths are reported in source
/// pixels, and the full field is kept per frame so it can be drawn over the picture.
///
/// Frames are matched on the thread pool, so wall-clock time scales with core count.
/// The same pass feeds the block-noise detector behind the "brain" flag.
/// </summary>
public static class MotionEstimator
{
    private const int SmallWidth = 320;

    /// <summary>
    /// Block edge in small-frame pixels. The search cost is proportional to
    /// blocks x candidate-count x block-area, and blocks x block-area is just the frame
    /// area, so halving the block size quadruples the number of vectors without
    /// quadrupling the work — a finer field is nearly free and reads far better in the
    /// overlay. 8 px here is 48 source pixels on a 1920-wide clip.
    /// </summary>
    private const int BlockSize = 8;

    private const int SearchRadius = 8;
    private const double EarlyExitSad = 4.0;
    private const int BatchSize = 48;

    private static readonly (int dx, int dy)[] Offsets = BuildOffsets();

    public static async Task RunAsync(
        AnalysisResult result, Action<double>? progress, CancellationToken ct, DeepAnalysisService.Slice slice = default)
    {
        if (slice.Last < slice.First) slice = DeepAnalysisService.Slice.Of(result, 0, -1);
        var exe = FfmpegLocator.FfmpegPath
                  ?? throw new InvalidOperationException("ffmpeg was not found.");

        var media = result.Media;
        var frames = result.Frames;
        if (frames.Count == 0) return;

        int smallW = SmallWidth;
        int smallH = Math.Max(2, (int)Math.Round(media.Height * (double)smallW / Math.Max(1, media.Width)));
        if (smallH % 2 != 0) smallH++;
        double scale = media.Width / (double)smallW;

        // The slice is a seek plus a duration: the pipe only carries what ffmpeg decided to
        // output, so the first frame on it is the slice's first frame.
        var args = $"-v error -hide_banner {slice.InputArgs}-i \"{media.FilePath}\" {slice.OutputArgs}-an " +
                   $"-vf \"scale={smallW}:{smallH}:flags=fast_bilinear,format=gray\" " +
                   $"-f rawvideo -pix_fmt gray -fps_mode passthrough pipe:1";

        int bufSize = smallW * smallH;
        var results = new List<FrameMotion>(frames.Count);
        var pending = new List<(byte[] cur, byte[]? prev, byte[]? next)>(BatchSize);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Results are copied into the frame table as each batch is matched rather than in one
        // sweep at the end: that is what lets the motion graph fill in while the pass is still
        // running, the way the QP pass does, instead of appearing all at once when it finishes.
        int applied = 0;

        AppLog.Write($"motion: decoding at {smallW}x{smallH} (scale {scale:0.###})");

        await Task.Run(async () =>
        {
            using var p = ProcessRunner.Start(exe, args, ProcessRunner.Analysis);
            _ = p.StandardError.ReadToEndAsync(ct);
            var stream = p.StandardOutput.BaseStream;

            void Flush()
            {
                Match(results, pending, smallW, smallH, scale);
                applied = ApplyResults(results, applied, frames, slice.First);
                progress?.Invoke(Math.Min(1.0, applied / (double)Math.Max(1, slice.Count)));
            }

            // Each decoded frame gets its own buffer. A frame stays alive until the batches
            // that use it as next / current / previous have been matched, which a shared ring
            // of three arrays cannot express: the decoder would overwrite a window while a
            // batch was still reading it. At ~58 KB per frame the transient garbage is
            // negligible and the live set stays bounded by the batch size.
            byte[]? prevPrev = null, prev = null, cur = null;
            int k = 0;
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var slot = new byte[bufSize];
                    int read = 0;
                    while (read < bufSize)
                    {
                        int n = await stream.ReadAsync(slot.AsMemory(read, bufSize - read), ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < bufSize) break;

                    prevPrev = prev;
                    prev = cur;
                    cur = slot;
                    k++;

                    // `prev` now has both neighbours and can be matched
                    if (prevPrev is not null && prev is not null && cur is not null)
                    {
                        pending.Add((prev, prevPrev, cur));
                        if (pending.Count >= BatchSize) Flush();
                    }
                }

                // the tail frame has nothing after it, so match it backwards only
                if (prev is not null && cur is not null)
                    pending.Add((cur, prev, null));
                if (pending.Count > 0) Flush();
            }
            catch (OperationCanceledException)
            {
                ProcessRunner.TryKill(p);
                throw;
            }

            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        AppLog.Write($"motion: {results.Count} samples in {sw.Elapsed.TotalSeconds:0.0}s");

        // A few frames of field statistics: "all vectors zero" is the failure that is hardest
        // to spot from the UI, because a legitimately static clip looks the same.
        for (int i = 0; i < Math.Min(3, results.Count); i++)
        {
            var f = results[i].Field;
            if (f is null) continue;

            int nonZero = 0;
            double peak = 0;
            for (int j = 0; j < f.Count; j++)
            {
                double len = Math.Sqrt((double)f.Dx[j] * f.Dx[j] + (double)f.Dy[j] * f.Dy[j]);
                if (len > 0) nonZero++;
                if (len > peak) peak = len;
            }
            AppLog.Write($"motion: sample {i} (frame {i + 1}) nonzero={nonZero}/{f.Count} " +
                         $"peakSmallPx={peak:0.##} matched={results[i].Matched} " +
                         $"worstSad={results[i].WorstSad:0.##} at({results[i].WorstDx},{results[i].WorstDy})");
        }

        // The decoder emits frames in presentation order. The first trio the matcher can
        // resolve is (0, 1, 2) and it describes frame 1, so results are offset by one
        // against the frame table.
        applied = ApplyResults(results, applied, frames, slice.First);
        progress?.Invoke(1.0);
    }

    /// <summary>
    /// Copies matched results into the frame table from <paramref name="from"/> onwards, and
    /// returns the new watermark. Called after every batch so the graphs have something to show
    /// long before the pass is over.
    /// </summary>
    private static int ApplyResults(List<FrameMotion> results, int from, List<FrameInfo> frames, int sliceStart)
    {
        for (int i = from; i < results.Count; i++)
        {
            int frameIndex = sliceStart + i + 1;
            if (frameIndex >= frames.Count) break;

            var s = results[i];
            var f = frames[frameIndex];

            if (f.Type == PicType.I)
            {
                // an intra frame predicts nothing, so it carries no motion vectors
                f.MotionMean = 0;
                f.MotionMax = 0;
                f.MotionFwdCount = 0;
                f.MotionBwdCount = 0;
                f.MotionVectorCount = 0;
                f.Brain = s.Blockiness > 1.18;
                continue;
            }

            f.MotionMean = s.Mean;
            f.MotionMax = s.Max;
            f.MotionFwdCount = s.Fwd;
            f.MotionBwdCount = s.Bwd;
            f.MotionVectorCount = s.Fwd + s.Bwd;
            f.Motion = s.Field;
            f.Brain = s.Blockiness > 1.18;
        }

        return results.Count;
    }

    /// <summary>Matches the queued frames in parallel, then appends the results in order.</summary>
    private static void Match(
        List<FrameMotion> results,
        List<(byte[] cur, byte[]? prev, byte[]? next)> pending,
        int w, int h, double scale)
    {
        if (pending.Count == 0) return;

        var batch = pending.ToArray();
        pending.Clear();

        var output = new FrameMotion[batch.Length];
        // Leave a core for playback and the UI: the analysis runs concurrently with the
        // video the user is already watching.
        int degree = Math.Max(1, Environment.ProcessorCount - 1);
        var options = new ParallelOptions { MaxDegreeOfParallelism = degree };
        Parallel.For(0, batch.Length, options, i =>
        {
            var (cur, prev, next) = batch[i];
            output[i] = Analyse(cur, prev, next, w, h, scale);
        });

        results.AddRange(output);
    }

    private readonly record struct FrameMotion(double Mean, double Max, int Fwd, int Bwd, double Blockiness, MotionField? Field,
        int Matched, int NonZero, double WorstSad, int WorstDx, int WorstDy);

    private static unsafe FrameMotion Analyse(byte[] cur, byte[]? prev, byte[]? next, int w, int h, double scale)
    {
        int cols = w / BlockSize;
        int rows = h / BlockSize;
        if (cols <= 0 || rows <= 0) return default;

        var field = new MotionField(cols, rows);
        double sum = 0, max = 0;
        int fwd = 0, bwd = 0, counted = 0;
        int matched = 0, nonZero = 0, peakSadX = 0, peakSadY = 0;
        double worstSad = -1;

        fixed (byte* pCur = cur)
        fixed (byte* pPrev = prev)
        fixed (byte* pNext = next)
        {
            int predPrevX = 0, predPrevY = 0;   // predictors carried along the scan order
            int predNextX = 0, predNextY = 0;

            for (int by = 0; by < rows; by++)
            {
                for (int bx = 0; bx < cols; bx++)
                {
                    int x0 = bx * BlockSize;
                    int y0 = by * BlockSize;
                    int cell = by * cols + bx;

                    double bestPrev = double.MaxValue, bestNext = double.MaxValue;
                    int bpx = 0, bpy = 0, bnx = 0, bny = 0;

                    if (pPrev != null)
                        bestPrev = Search(pCur, pPrev, w, h, x0, y0, predPrevX, predPrevY, ref bpx, ref bpy, double.MaxValue);
                    if (pNext != null)
                        bestNext = Search(pCur, pNext, w, h, x0, y0, predNextX, predNextY, ref bnx, ref bny, bestPrev);

                    bool usePrev = bestPrev <= bestNext;
                    double bestSad = usePrev ? bestPrev : bestNext;
                    if (bestSad == double.MaxValue) continue;
                    matched++;

                    int dx = usePrev ? -bpx : bnx;
                    int dy = usePrev ? -bpy : bny;
                    if (usePrev) fwd++; else bwd++;
                    if (dx != 0 || dy != 0) nonZero++;

                    // keep the most textured block, so a diagnostic can tell "nothing moved"
                    // apart from "the matcher never found anything to lock onto"
                    if (bestSad > worstSad)
                    {
                        worstSad = bestSad;
                        peakSadX = dx;
                        peakSadY = dy;
                    }

                    // Two predictors, because the two searches have opposite sign conventions:
                    // matching the previous frame yields the offset toward where the content
                    // came from, matching the next frame toward where it is going.
                    if (pPrev != null) { predPrevX = bpx; predPrevY = bpy; }
                    if (pNext != null) { predNextX = bnx; predNextY = bny; }

                    field.Dx[cell] = (sbyte)Math.Clamp(dx, -128, 127);
                    field.Dy[cell] = (sbyte)Math.Clamp(dy, -128, 127);
                    field.Reference[cell] = usePrev ? MotionField.RefForward : MotionField.RefBackward;

                    double len = Math.Sqrt((double)dx * dx + (double)dy * dy) * scale;
                    sum += len;
                    counted++;
                    if (len > max) max = len;
                }
            }
        }

        double mean = counted > 0 ? sum / counted : 0;
        double blockiness = BlockAnalysis.Blockiness(cur, w, h, Math.Max(2, (int)Math.Round(BlockSize / scale)));
        return new FrameMotion(mean, max, fwd, bwd, blockiness, field, matched, nonZero, worstSad, peakSadX, peakSadY);
    }

    /// <summary>
    /// Predicted spiral SAD search with row-wise early termination.
    /// <paramref name="cutoff"/> is the error already achieved by the other reference
    /// frame: once a candidate provably cannot beat it, the scan abandons it. That single
    /// trick is what keeps a full-length clip inside a few tens of seconds.
    /// </summary>
    private static unsafe double Search(
        byte* cur, byte* refFrame, int w, int h,
        int x0, int y0, int predX, int predY,
        ref int bestDx, ref int bestDy, double cutoff)
    {
        double best = cutoff;
        int limitX = w - BlockSize;
        int limitY = h - BlockSize;
        double lastSad = 0;

        bool Evaluate(int dx, int dy, long hardLimit = long.MaxValue)
        {
            int rx = x0 + dx;
            int ry = y0 + dy;
            if (rx < 0 || ry < 0 || rx > limitX || ry > limitY) return false;

            long limit = best == double.MaxValue
                ? long.MaxValue
                : (long)(best * (BlockSize * BlockSize));
            if (hardLimit < limit) limit = hardLimit;

            long sad = 0;
            byte* a = cur + y0 * w + x0;
            byte* b = refFrame + ry * w + rx;

            for (int row = 0; row < BlockSize; row++)
            {
                byte* pa = a + row * w;
                byte* pb = b + row * w;
                for (int col = 0; col < BlockSize; col++)
                {
                    int d = pa[col] - pb[col];
                    sad += d < 0 ? -d : d;
                }
                if (sad >= limit) return false;   // this candidate can no longer win
            }

            lastSad = sad / (double)(BlockSize * BlockSize);
            return lastSad < best;
        }

        // Zero offset is evaluated first, before the predictor. In a flat region every offset
        // scores the same (SAD 0), so whichever candidate is tried first wins — and if that is
        // the previous block's vector, flat areas inherit it and the field smears sideways.
        // Trying (0,0) first means a block that already matches its counterpart reports no
        // motion, which is the honest answer: nothing in it is measurable.
        //
        // The hard limit is what keeps this cheap: we only care whether (0,0) is *very* good,
        // so a block with real motion abandons the check after a row or two instead of
        // computing the whole 8x8 sum.
        long zeroLimit = (long)(EarlyExitSad * BlockSize * BlockSize);
        if (Evaluate(0, 0, zeroLimit)) { bestDx = 0; bestDy = 0; best = lastSad; }
        if (best < EarlyExitSad) return best;

        if ((predX != 0 || predY != 0) && Evaluate(predX, predY))
        {
            bestDx = predX; bestDy = predY; best = lastSad;
            if (best < EarlyExitSad) return best;
        }

        foreach (var (dx, dy) in Offsets)
        {
            if (dx == 0 && dy == 0) continue;
            if (dx == predX && dy == predY) continue;
            if (Evaluate(dx, dy)) { bestDx = dx; bestDy = dy; best = lastSad; }
            if (best < EarlyExitSad) break;
        }

        return best;
    }

    private static (int dx, int dy)[] BuildOffsets()
    {
        var list = new List<(int dx, int dy, int r2)>();
        for (int dy = -SearchRadius; dy <= SearchRadius; dy++)
            for (int dx = -SearchRadius; dx <= SearchRadius; dx++)
                list.Add((dx, dy, dx * dx + dy * dy));
        list.Sort((a, b) => a.r2.CompareTo(b.r2));
        return list.Select(t => (t.dx, t.dy)).ToArray();
    }
}