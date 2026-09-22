using System.Collections.Concurrent;
using System.Diagnostics;

namespace VideoAnalyzer.Services;

/// <summary>
/// Frame-accurate playback engine.
///
/// ffmpeg decodes the clip into a raw BGRA pipe; a reader thread fills a small pool of
/// reusable buffers and the UI pulls them on a timer. This gives exact frame stepping and
/// lets WPF draw overlays (motion vectors, block-noise dots, the picture-type badge) on
/// top of the picture, which a MediaElement would not allow.
///
/// Seeking restarts ffmpeg with an input <c>-ss</c>: ffmpeg seeks to the preceding key
/// frame and then discards up to the requested timestamp, so it stays fast even in long
/// files.
/// </summary>
public sealed class VideoPlaybackEngine : IDisposable
{
    private const int ReadyCapacity = 24;
    private const int PoolSize = 32;

    private readonly object _gate = new();
    private Process? _proc;
    private Thread? _reader;
    private BlockingCollection<byte[]>? _free;
    private BlockingCollection<byte[]>? _ready;
    private CancellationTokenSource? _cts;
    private volatile bool _eof;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int StartIndex { get; private set; }
    public bool IsRunning => _reader is { IsAlive: true };

    /// <summary>
    /// True once the decoder has delivered everything it has: the read loop hit the end of the
    /// stream and the queue has drained.
    ///
    /// The caller cannot work this out from the frame index alone. A container's packet table
    /// can list more frames than the decoder emits — a truncated tail, a packet that carries no
    /// picture — so the last frame shown may be a couple short of the last frame in the table,
    /// and "the index reached the end" never becomes true.
    /// </summary>
    public bool Ended => _eof && (_ready is null || _ready.Count == 0);

    public int Stride => Width * Height * 4;

    public event EventHandler<string>? Failed;

    public void Start(string file, int width, int height, double startSeconds, int startIndex)
    {
        Stop();

        Width = Math.Max(2, width - width % 2);
        Height = Math.Max(2, height - height % 2);
        StartIndex = Math.Max(0, startIndex);
        _eof = false;

        var cts = new CancellationTokenSource();
        var free = new BlockingCollection<byte[]>(PoolSize);
        var ready = new BlockingCollection<byte[]>(ReadyCapacity);

        int stride = Stride;
        for (int i = 0; i < PoolSize; i++) free.Add(new byte[stride]);

        var exe = FfmpegLocator.FfmpegPath;
        if (exe is null)
        {
            Failed?.Invoke(this, "ffmpeg was not found.");
            return;
        }

        var args = $"-v error -hide_banner -ss {startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                   $"-i \"{file}\" -an -vf \"scale={Width}:{Height}:flags=bilinear\" " +
                   $"-f rawvideo -pix_fmt bgra -fps_mode passthrough pipe:1";

        var proc = ProcessRunner.Start(exe, args);

        lock (_gate)
        {
            _cts = cts;
            _proc = proc;
            _free = free;
            _ready = ready;

            _reader = new Thread(() => ReadLoop(proc, cts.Token, free, ready, stride))
            {
                IsBackground = true,
                Name = "video-decode"
            };
            _reader.Start();
        }
    }

    private void ReadLoop(
        Process proc,
        CancellationToken ct,
        BlockingCollection<byte[]> free,
        BlockingCollection<byte[]> ready,
        int stride)
    {
        try
        {
            using var stream = proc.StandardOutput.BaseStream;
            _ = proc.StandardError.ReadToEndAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                if (!free.TryTake(out var buffer, 200, ct)) continue;

                int read = 0;
                try
                {
                    while (read < stride)
                    {
                        int n = stream.Read(buffer, read, stride - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { break; }

                if (read < stride)
                {
                    try { free.Add(buffer, ct); } catch { }

                    // Only the live reader may report the end of the stream. A reader that was
                    // stopped exits through this same path — its process is killed and the read
                    // returns nothing — and claiming the new stream had finished would stop the
                    // transport the moment a seek restarted it.
                    if (!ct.IsCancellationRequested) _eof = true;
                    break;
                }

                try { ready.Add(buffer, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex.Message);
        }
    }

    /// <summary>Pulls the next decoded frame. The caller owns the buffer and must hand it
    /// back with <see cref="ReturnBuffer"/>.</summary>
    public bool TryGetFrame(out byte[]? buffer)
    {
        buffer = null;
        var ready = _ready;
        if (ready is null || ready.IsAddingCompleted) return false;
        return ready.TryTake(out buffer);
    }

    public void ReturnBuffer(byte[] buffer)
    {
        var free = _free;
        if (free is null || buffer.Length != Stride) return;
        if (free.IsAddingCompleted) return;
        try { free.TryAdd(buffer); } catch { }
    }

    public void Stop()
    {
        Process? proc;
        Thread? reader;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            proc = _proc; reader = _reader; cts = _cts;
            _proc = null; _reader = null; _cts = null;
            _ready?.CompleteAdding();
            _free?.CompleteAdding();
        }

        try { cts?.Cancel(); } catch { }
        if (proc is not null) ProcessRunner.TryKill(proc);
        try { reader?.Join(1500); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
