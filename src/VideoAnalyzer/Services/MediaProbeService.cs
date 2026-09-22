using System.Globalization;
using System.IO;
using System.Text.Json;
using VideoAnalyzer.Models;

namespace VideoAnalyzer.Services;

/// <summary>Reads container / stream metadata with <c>ffprobe -show_format -show_streams</c>.</summary>
public static class MediaProbeService
{
    public static async Task<MediaInfo> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        var exe = FfmpegLocator.FfprobePath
                  ?? throw new InvalidOperationException("ffprobe was not found. Configure the ffmpeg tool folder first.");

        var args = $"-v error -hide_banner -print_format json -show_format -show_streams \"{filePath}\"";

        var json = await Task.Run(() =>
        {
            using var p = ProcessRunner.Start(exe, args);
            var outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            return outp;
        }, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new MediaInfo
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath)
        };

        try { info.FileSizeBytes = new FileInfo(filePath).Length; } catch { }

        if (root.TryGetProperty("format", out var fmt))
        {
            info.FormatName = Str(fmt, "format_name", "unknown");
            info.FormatLongName = Str(fmt, "format_long_name", "unknown");
            info.ContainerBitRate = Long(fmt, "bit_rate");
            info.DurationSeconds = Dbl(fmt, "duration");
            if (info.FileSizeBytes == 0) info.FileSizeBytes = Long(fmt, "size");
        }

        JsonElement? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type", "");
                if (type == "video" && video is null) video = s;
                else if (type == "audio" && audio is null) audio = s;
            }
        }

        if (video is { } v)
        {
            info.VideoCodec = Str(v, "codec_name", "unknown");
            info.VideoProfile = Str(v, "profile", "unknown");
            info.VideoLevel = Str(v, "level", "");
            info.CodecTag = Str(v, "codec_tag_string", "");
            info.Width = (int)Long(v, "width");
            info.Height = (int)Long(v, "height");
            info.PixFmt = Str(v, "pix_fmt", "unknown");
            info.ColorRange = Str(v, "color_range", "unknown");
            info.ColorSpace = Str(v, "color_space", "unknown");
            info.ColorPrimaries = Str(v, "color_primaries", "unknown");
            info.ColorTransfer = Str(v, "color_transfer", "unknown");
            info.FieldOrder = Str(v, "field_order", "unknown");
            info.NbFrames = Long(v, "nb_frames");
            info.VideoBitRate = Long(v, "bit_rate");

            var (sn, sd, _) = ParseRatio(Str(v, "sample_aspect_ratio", "1:1"));
            info.SarNum = sn == 0 ? 1 : sn;
            info.SarDen = sd == 0 ? 1 : sd;

            var avg = ParseRatio(Str(v, "avg_frame_rate", "0/0"));
            var raw = ParseRatio(Str(v, "r_frame_rate", "0/0"));
            info.FrameRate = avg.ratio > 0 ? avg.ratio : raw.ratio;

            if (info.DurationSeconds <= 0) info.DurationSeconds = Dbl(v, "duration");
        }

        if (audio is { } a)
        {
            info.HasAudio = true;
            info.AudioCodec = Str(a, "codec_name", "unknown");
            info.SampleRate = (int)Long(a, "sample_rate");
            info.Channels = (int)Long(a, "channels");
            info.SampleFormat = Str(a, "sample_fmt", "unknown");
            info.ChannelLayout = Str(a, "channel_layout", "unknown");
            info.AudioBitRate = Long(a, "bit_rate");
        }

        if (info.FrameRate <= 0) info.FrameRate = 25;
        if (info.DurationSeconds <= 0 && info.NbFrames > 0)
            info.DurationSeconds = info.NbFrames / info.FrameRate;

        return info;
    }

    private static string Str(JsonElement e, string name, string fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l2) ? l2 : 0,
            _ => 0
        };
    }

    private static double Dbl(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0,
            _ => 0
        };
    }

    private static (int num, int den, double ratio) ParseRatio(string text)
    {
        var parts = text.Split('/');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var n) && int.TryParse(parts[1], out var d) && d != 0)
            return (n, d, (double)n / d);
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var single))
            return (0, 0, single);
        return (0, 0, 0);
    }
}
