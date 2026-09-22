using VideoAnalyzer.Models;

namespace VideoAnalyzer.Services;

/// <summary>
/// A low resolution thumbnail strip covering the whole clip, decoded in one ffmpeg pass.
///
/// Seeking restarts the decoder, which costs a few hundred milliseconds — far too slow to
/// run on every mouse-move while the user drags a playhead. The strip gives an instant
/// picture for any position, so a drag only previews, and the real (frame accurate) seek
/// happens once, on release.
///
/// The strip is sampled at a fixed target count rather than a fixed rate, so a 30 second
/// clip and a 3 hour clip both cost about the same memory.
/// </summary>
public sealed class Filmstrip
{
    private const int TargetWidth = 208;
    private const int TargetCount = 240;

    private byte[][]? _frames;
    private int _width, _height;

    public bool IsReady => _frames is { Length: > 0 };
    public int Width => _width;
    public int Height => _height;
    public int Count => _frames?.Length ?? 0;

    /// <summary>Seconds covered by one thumbnail.</summary>
    public double SecondsPerThumb { get; private set; } = 1;

    public void Clear()
    {
        _frames = null;
        _width = _height = 0;
    }

    public async Task BuildAsync(
        string filePath,
        double durationSeconds,
        int sourceWidth,
        int sourceHeight,
        CancellationToken ct)
    {
        if (durationSeconds <= 0.5) return;

        var exe = FfmpegLocator.FfmpegPath;
        if (exe is null) return;

        double rate = Math.Clamp(TargetCount / durationSeconds, 0.02, 8.0);

        // The height has to be derived here rather than left to ffmpeg's "-2", because every
        // frame is read as a fixed-size block: a thumbnail that comes out even slightly shorter
        // than assumed (a 640x352 source gives 114 rows, not 118) shifts every following read
        // and the whole strip turns into skewed garbage.
        int width = TargetWidth;
        int height = Math.Max(2, (int)Math.Round(width * (double)Math.Max(1, sourceHeight) / Math.Max(1, sourceWidth)));
        if (height % 2 != 0) height++;

        // fps resamples to the strip rate, which is what makes the thumbnail index a function
        // of time; the exact scale keeps the byte layout predictable.
        var args = $"-v error -hide_banner -i \"{filePath}\" -an " +
                   $"-vf \"fps={rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"scale={width}:{height}:flags=fast_bilinear\" " +
                   $"-f rawvideo -pix_fmt bgra pipe:1";

        var frames = new List<byte[]>(TargetCount + 8);
        int stride = width * height * 4;      // BGRA, four bytes per pixel

        await Task.Run(async () =>
        {
            using var p = ProcessRunner.Start(exe, args);
            _ = p.StandardError.ReadToEndAsync(ct);
            var stream = p.StandardOutput.BaseStream;

            try
            {
                while (frames.Count < TargetCount + 8)
                {
                    ct.ThrowIfCancellationRequested();

                    var buf = new byte[stride];
                    int read = 0;
                    while (read < stride)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(read, stride - read), ct).ConfigureAwait(false);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < stride) break;
                    frames.Add(buf);
                }
            }
            catch (OperationCanceledException)
            {
                ProcessRunner.TryKill(p);
                throw;
            }

            ProcessRunner.TryKill(p);
        }, ct).ConfigureAwait(false);

        if (frames.Count == 0) return;

        _frames = frames.ToArray();
        _width = width;
        _height = height;
        SecondsPerThumb = durationSeconds / _frames.Length;
        AppLog.Write($"filmstrip: {_frames.Length} thumbs at {width}x{height}, {SecondsPerThumb:0.###}s each");
    }

    /// <summary>Thumbnail covering <paramref name="seconds"/>, or null when not built.</summary>
    public byte[]? At(double seconds)
    {
        var frames = _frames;
        if (frames is null || frames.Length == 0) return null;

        int index = (int)(seconds / Math.Max(1e-6, SecondsPerThumb));
        return frames[Math.Clamp(index, 0, frames.Length - 1)];
    }
}
