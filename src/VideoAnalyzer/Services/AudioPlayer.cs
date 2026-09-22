using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VideoAnalyzer.Services;

/// <summary>
/// Plays the file's audio track alongside the self-drawn picture.
///
/// The picture is decoded through an ffmpeg pipe rather than by a media engine — that is what
/// makes frame stepping and overlays possible — so the sound is decoded the same way: ffmpeg
/// emits raw 16-bit PCM and it is pushed to the sound card with <c>waveOut</c>.
///
/// <see cref="System.Windows.Media.MediaPlayer"/> was tried first and rejected: it buffers
/// before it emits anything, so the first Play() after opening a file starts roughly 1.7 s
/// late, and its reported Position cannot tell a decoder that is behind from a genuine output
/// delay. Owning the pipeline instead means the position is derived from the number of samples
/// the device has actually played, which is exact, and the volume is applied to the samples in
/// software so it cannot disturb the system mixer.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * 2;      // signed 16-bit
    private const int BufferFrames = 4096;               // ~85 ms of audio per buffer
    private const int BufferCount = 8;                   // ~680 ms of look-ahead

    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const uint WHDR_DONE = 0x00000001;
    private const uint TIME_SAMPLES = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MMTIME
    {
        public uint wType;
        public uint sample;
        public uint pad;      // the union is 8 bytes (smpte), so the structure is 12
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(out IntPtr handle, uint device, ref WAVEFORMATEX format,
                                          IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetPosition(IntPtr handle, ref MMTIME time, uint size);

    private sealed class Slot
    {
        public readonly byte[] Data = new byte[BufferFrames * BytesPerFrame];
        public IntPtr Header;
        public GCHandle Pin;
        public bool Queued;
    }

    private readonly object _gate = new();
    private readonly Slot[] _slots = new Slot[BufferCount];

    private string? _path;
    private IntPtr _device = IntPtr.Zero;
    private Thread? _feeder;
    private Process? _decoder;
    private CancellationTokenSource? _feedCts;
    private double _startSeconds;
    private long _framesPlayedAtStart;
    private volatile bool _playing;
    private double _volume = 0.8;
    private bool _muted;
    private bool _positionErrLogged;
    private volatile bool _silent;

    public bool HasAudio { get; private set; }

    /// <summary>True once the output device opened successfully.</summary>
    public bool IsLoaded => _device != IntPtr.Zero;

    public bool SourceHasNoAudio => !HasAudio;

    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 1);
    }

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    private float Gain => _muted ? 0f : (float)_volume;

    /// <summary>
    /// Position derived from the samples the device has actually played, so it reflects what is
    /// coming out of the speakers rather than what the decoder has buffered.
    /// </summary>
    public double PositionSeconds
    {
        get
        {
            if (_device == IntPtr.Zero) return _startSeconds;

            var t = new MMTIME { wType = TIME_SAMPLES };
            int rc = waveOutGetPosition(_device, ref t, (uint)Marshal.SizeOf<MMTIME>());
            if (rc != 0)
            {
                if (!_positionErrLogged) { _positionErrLogged = true; AppLog.Write($"audio: waveOutGetPosition failed, code {rc}"); }
                return _startSeconds;
            }

            long played = _framesPlayedAtStart + t.sample;
            return played / (double)SampleRate;
        }
    }

    public void Load(string filePath, bool hasAudio)
    {
        Stop();
        _path = filePath;
        HasAudio = hasAudio;
        if (!hasAudio) return;

        if (OpenDevice())
        {
            AppLog.Write($"audio: waveOut device open, {SampleRate} Hz stereo");
            StartKeepAlive();          // so the first Play() does not pay the device start-up cost
        }
    }

    private bool OpenDevice()
    {
        if (_device != IntPtr.Zero) return true;

        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1,                       // PCM
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * BytesPerFrame,
            nBlockAlign = BytesPerFrame,
            wBitsPerSample = 16,
            cbSize = 0
        };

        int rc = waveOutOpen(out _device, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0)
        {
            _device = IntPtr.Zero;
            AppLog.Write($"audio: waveOutOpen failed, code {rc}");
            return false;
        }

        int size = Marshal.SizeOf<WAVEHDR>();
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = new Slot();
            slot.Pin = GCHandle.Alloc(slot.Data, GCHandleType.Pinned);
            slot.Header = Marshal.AllocHGlobal(size);

            var hdr = new WAVEHDR { lpData = slot.Pin.AddrOfPinnedObject(), dwBufferLength = (uint)slot.Data.Length };
            Marshal.StructureToPtr(hdr, slot.Header, false);
            int prc = waveOutPrepareHeader(_device, slot.Header, (uint)size);
            if (prc != 0) AppLog.Write($"audio: prepare header {i} failed, code {prc}");

            _slots[i] = slot;
        }

        return true;
    }

    /// <summary>Starts playback at <paramref name="seconds"/>.</summary>
    public void Play(double seconds)
    {
        if (_path is null || !HasAudio) return;
        if (!OpenDevice()) return;

        StopFeeder();
        waveOutReset(_device);          // drops anything still queued
        ReleaseQueuedSlots();

        _startSeconds = seconds;
        _framesPlayedAtStart = (long)Math.Round(seconds * SampleRate);
        _playing = true;
        StartFeeder(silent: false);

        AppLog.Write($"audio: play from {seconds:0.###}s");
    }

    public void Pause()
    {
        if (_device == IntPtr.Zero) return;

        AppLog.Write($"audio: pause at {PositionSeconds:0.###}s");
        _playing = false;
        StopFeeder();
        waveOutReset(_device);
        ReleaseQueuedSlots();

        // Keep the device hot; see StartKeepAlive.
        StartKeepAlive();
    }

    /// <summary>
    /// Keeps the output device running with silence.
    ///
    /// Opening an audio stream and getting the first samples out costs about 1.5 s on this
    /// machine, but only the first time — while the device stays open, later starts are within
    /// ~0.2 s. Rather than freeze the picture for that second and a half on the first press of
    /// play, the device is opened and fed silence as soon as a file is loaded, so every real
    /// playback starts immediately.
    /// </summary>
    public void StartKeepAlive()
    {
        if (_device == IntPtr.Zero || !HasAudio) return;
        if (_feeder is not null) return;

        StartFeeder(silent: true);
    }

    private void StartFeeder(bool silent)
    {
        _silent = silent;
        _feedCts = new CancellationTokenSource();
        var ct = _feedCts.Token;
        _feeder = new Thread(() => Feed(ct)) { IsBackground = true, Name = "audio-feed" };
        _feeder.Start();
    }

    /// <summary>Moves the audio to a new picture position, restarting the feed if playing.</summary>
    public void Seek(double seconds)
    {
        if (_device == IntPtr.Zero || !HasAudio) return;

        if (_playing) Play(seconds);
        else
        {
            _startSeconds = seconds;
            _framesPlayedAtStart = (long)Math.Round(seconds * SampleRate);
        }
    }

    public void Stop()
    {
        _playing = false;
        StopFeeder();

        if (_device == IntPtr.Zero) return;

        waveOutReset(_device);
        ReleaseQueuedSlots();

        int size = Marshal.SizeOf<WAVEHDR>();
        foreach (var slot in _slots)
        {
            if (slot is null) continue;
            waveOutUnprepareHeader(_device, slot.Header, (uint)size);
            Marshal.FreeHGlobal(slot.Header);
            if (slot.Pin.IsAllocated) slot.Pin.Free();
        }

        waveOutClose(_device);
        _device = IntPtr.Zero;
        Array.Clear(_slots, 0, _slots.Length);
    }

    private void StopFeeder()
    {
        try { _feedCts?.Cancel(); } catch { /* already gone */ }
        try { _decoder?.Kill(true); } catch { /* already gone */ }
        try { _feeder?.Join(400); } catch { /* best effort */ }

        _feedCts?.Dispose();
        _feedCts = null;
        _feeder = null;
        _decoder = null;
    }

    private void ReleaseQueuedSlots()
    {
        foreach (var slot in _slots)
            if (slot is not null) slot.Queued = false;
    }

    /// <summary>
    /// Decodes PCM with ffmpeg and pushes it to the device, keeping the queue topped up and
    /// waiting for a buffer to come back before reusing it.
    /// </summary>
    private void Feed(CancellationToken ct)
    {
        try
        {
            int hdrSize = Marshal.SizeOf<WAVEHDR>();
            int written = 0;
            Stream? pcm = null;
            Process? decoder = null;

            if (!_silent)
            {
                var exe = FfmpegLocator.FfmpegPath;
                if (exe is null || _path is null) { AppLog.Write("audio: feeder aborted, no ffmpeg or path"); return; }

                var args = $"-v error -hide_banner -ss {_startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                           $"-i \"{_path}\" -vn -f s16le -ac {Channels} -ar {SampleRate} pipe:1";

                decoder = ProcessRunner.Start(exe, args);
                _decoder = decoder;
                // drain stderr: if the pipe filled up, ffmpeg would block and the feed would stall
                _ = decoder.StandardError.ReadToEndAsync();
                pcm = decoder.StandardOutput.BaseStream;
            }

            while (!ct.IsCancellationRequested)
            {
                Slot? free = null;
                while (free is null)
                {
                    if (ct.IsCancellationRequested) return;

                    foreach (var slot in _slots)
                    {
                        if (slot is null) continue;

                        // A slot that was never queued is free; one that was queued is free
                        // again once the device has finished with it.
                        if (!slot.Queued) { free = slot; break; }

                        var hdr = Marshal.PtrToStructure<WAVEHDR>(slot.Header);
                        if ((hdr.dwFlags & WHDR_DONE) != 0) { slot.Queued = false; free = slot; break; }
                    }

                    if (free is null) Thread.Sleep(4);
                }

                int read;
                if (_silent)
                {
                    Array.Clear(free.Data);          // silence keeps the device warm
                    read = free.Data.Length;
                }
                else
                {
                    read = 0;
                    while (read < free.Data.Length)
                    {
                        int n = pcm!.Read(free.Data, read, free.Data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    if (read <= 0) { AppLog.Write("audio: stream ended"); break; }
                    if (read % BytesPerFrame != 0) read -= read % BytesPerFrame;

                    // gain is read per buffer so a volume change takes effect within ~85 ms
                    float gain = Gain;
                    if (gain < 1f) ApplyGain(free.Data, read, gain);
                }

                // Reuse the prepared header: clear WHDR_DONE but keep WHDR_PREPARED, which
                // waveOutPrepareHeader set. Zeroing dwFlags instead makes the device reject the
                // buffer with WAVERR_UNPREPARED (34).
                var queued = Marshal.PtrToStructure<WAVEHDR>(free.Header);
                queued.lpData = free.Pin.AddrOfPinnedObject();
                queued.dwBufferLength = (uint)read;
                queued.dwFlags &= ~WHDR_DONE;
                Marshal.StructureToPtr(queued, free.Header, false);

                int wrc = waveOutWrite(_device, free.Header, (uint)hdrSize);
                if (wrc != 0) { AppLog.Write($"audio: waveOutWrite failed, code {wrc}"); break; }
                free.Queued = true;

                if (++written == 1 && !_silent) AppLog.Write("audio: playback started");
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) AppLog.Write("audio: feed failed", ex);
        }
    }

    /// <summary>Scales 16-bit samples in place; the system mixer is left alone.</summary>
    private static void ApplyGain(byte[] data, int count, float gain)
    {
        for (int i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(data[i] | (data[i + 1] << 8));
            int scaled = (int)(s * gain);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            data[i] = (byte)(scaled & 0xFF);
            data[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    public void Dispose() => Stop();
}
