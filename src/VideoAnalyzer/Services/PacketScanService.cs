using System.Globalization;
using VideoAnalyzer.Models;

namespace VideoAnalyzer.Services;

/// <summary>
/// Fast pass: reads the container's packet table instead of decoding frames.
///
/// <c>ffprobe -show_packets</c> reports each packet's size, presentation / decode
/// timestamps and flags without decoding anything, which on a 60 s 1080p clip takes about
/// half a second rather than the ~22 s a full <c>-show_frames</c> decode costs. That is
/// what lets the window open, play and draw its first graphs almost immediately.
///
/// Picture type is the one thing packets do not carry, so key frames are marked here and
/// the remaining types are filled in by the decoder pass that also produces QP.
///
/// Packet level DTS is the meaningful one: for MP4 the frame level <c>pkt_dts_time</c> is
/// reported equal to PTS, which collapses the reorder graph into a flat line. The packet
/// table carries the real decode timestamps, so the reorder depth is computed from those.
/// </summary>
public static class PacketScanService
{
    private const string Entries = "packet=pts_time,dts_time,size,flags";

    public static async Task<List<FrameInfo>> ScanAsync(
        string filePath,
        double fps,
        long expectedFrames,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var exe = FfmpegLocator.FfprobePath
                  ?? throw new InvalidOperationException("ffprobe was not found.");

        var args = $"-v error -hide_banner -select_streams v:0 -show_entries {Entries} " +
                   $"-of compact=p=0 -i \"{filePath}\"";

        AppLog.Write($"packets: ffprobe {args}");

        var frames = new List<FrameInfo>(
            expectedFrames > 0 && expectedFrames < 4_000_000 ? (int)expectedFrames : 8192);

        double fallbackDuration = fps > 0 ? 1.0 / fps : 0.04;

        await Task.Run(async () =>
        {
            using var p = ProcessRunner.Start(exe, args);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

            int decodeIndex = 0;
            string? firstLine = null;
            string? lastLine = null;

            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0 || !line.Contains('=')) continue;
                firstLine ??= line;
                lastLine = line;

                var f = Parse(line, decodeIndex);
                if (f is null) continue;

                f.DurationMs = fallbackDuration * 1000.0;
                frames.Add(f);
                decodeIndex++;

                if (progress is not null && expectedFrames > 0 && decodeIndex % 512 == 0)
                    progress.Report(Math.Min(1.0, decodeIndex / (double)expectedFrames));
            }

            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var err = await stderrTask.ConfigureAwait(false);
            AppLog.Write($"packets: exit={p.ExitCode} packets={frames.Count} expected={expectedFrames}");
            AppLog.Write($"packets: first=[{firstLine}] last=[{lastLine}]");
            if (!string.IsNullOrWhiteSpace(err)) AppLog.Write($"packets: stderr=[{err.Trim()}]");
        }, ct).ConfigureAwait(false);

        progress?.Report(1.0);
        if (frames.Count == 0) return frames;

        // ---- presentation order -------------------------------------------
        // The packet table is in decode order; the graphs and the transport need
        // presentation order, so sort while keeping the decode position for the passes
        // that report in decode order.
        frames.Sort(static (a, b) => a.PtsTime.CompareTo(b.PtsTime));
        for (int i = 0; i < frames.Count; i++) frames[i].Index = i;

        // ---- derived per-frame values --------------------------------------
        int sinceKey = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];

            if (i + 1 < frames.Count)
            {
                double delta = frames[i + 1].PtsTime - f.PtsTime;
                if (delta > 0 && delta < 1.0) f.DurationMs = delta * 1000.0;
            }

            double seconds = f.DurationMs / 1000.0;
            if (seconds <= 0) seconds = fallbackDuration;
            f.BitrateKbps = f.PacketSize * 8.0 / seconds / 1000.0;

            if (f.KeyFrame)
            {
                sinceKey = 0;
                f.Type = PicType.I;          // a key frame is an intra frame
            }
            f.GopPosition = sinceKey++;

            f.ReorderDelta = (int)Math.Round((f.PtsTime - f.DtsTime) * (fps > 0 ? fps : 25));
        }

        return frames;
    }

    private static FrameInfo? Parse(string line, int decodeIndex)
    {
        var f = new FrameInfo { DecodeIndex = decodeIndex };
        bool sawSize = false;

        foreach (var token in line.Split('|'))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var key = token.AsSpan(0, eq);
            var val = token.AsSpan(eq + 1).Trim();

            if (key.SequenceEqual("size")) { f.PacketSize = ParseLong(val); sawSize = true; }
            else if (key.SequenceEqual("pts_time")) f.PtsTime = ParseDouble(val);
            else if (key.SequenceEqual("dts_time")) f.DtsTime = ParseDouble(val);
            else if (key.SequenceEqual("flags"))
            {
                // K = key frame, D = discardable, C = corrupt
                f.KeyFrame = val.Contains('K');
                f.Discard = val.Contains('D');
                f.Corrupt = val.Contains('C');
            }
        }

        if (!sawSize) return null;
        if (f.PtsTime < 0) f.PtsTime = 0;
        return f;
    }

    private static long ParseLong(ReadOnlySpan<char> v)
        => long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0;

    private static double ParseDouble(ReadOnlySpan<char> v)
        => double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
