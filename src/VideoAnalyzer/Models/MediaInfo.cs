namespace VideoAnalyzer.Models;

/// <summary>
/// Container / stream level information produced by <c>ffprobe -show_format -show_streams</c>.
/// </summary>
public sealed class MediaInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    // ---- container -------------------------------------------------------
    public string FormatName { get; set; } = "unknown";
    public string FormatLongName { get; set; } = "unknown";
    public long ContainerBitRate { get; set; }

    // ---- video stream ----------------------------------------------------
    public string VideoCodec { get; set; } = "unknown";
    public string VideoProfile { get; set; } = "unknown";
    public string VideoLevel { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string PixFmt { get; set; } = "unknown";
    public int SarNum { get; set; } = 1;
    public int SarDen { get; set; } = 1;
    public double FrameRate { get; set; }
    public long NbFrames { get; set; }
    public double DurationSeconds { get; set; }
    public long VideoBitRate { get; set; }
    public string ColorRange { get; set; } = "unknown";
    public string ColorSpace { get; set; } = "unknown";
    public string ColorPrimaries { get; set; } = "unknown";
    public string ColorTransfer { get; set; } = "unknown";
    public string FieldOrder { get; set; } = "unknown";
    public string CodecTag { get; set; } = string.Empty;

    // ---- audio stream ----------------------------------------------------
    public bool HasAudio { get; set; }
    public string AudioCodec { get; set; } = "unknown";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string SampleFormat { get; set; } = "unknown";
    public string ChannelLayout { get; set; } = "unknown";
    public long AudioBitRate { get; set; }

    // ---- derived ---------------------------------------------------------
    public double Duration => DurationSeconds;

    /// <summary>Duration formatted the ffmpeg way: mm:ss.mmm (or hh:mm:ss.mmm).</summary>
    public string DurationText
    {
        get
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}"
                : $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
        }
    }

    public string VideoLine
    {
        get
        {
            var level = string.IsNullOrEmpty(VideoLevel) ? string.Empty : " L" + VideoLevel;
            return $"{VideoCodec} ({VideoProfile}{level})";
        }
    }

    public string FrameRateText => FrameRate > 0 ? FrameRate.ToString("0.###") + " fps" : "unknown fps";

    public string SizeText => $"{Width}x{Height}";

    public string AudioLine
    {
        get
        {
            if (!HasAudio) return "none";
            var ch = Channels == 1 ? "1 channel" : $"{Channels} channels";
            return $"{AudioCodec} {SampleRate} Hz {ch} {SampleFormat}";
        }
    }
}
