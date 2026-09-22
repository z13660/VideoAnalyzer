using System.Collections.ObjectModel;

namespace VideoAnalyzer.Models;

/// <summary>Aggregated statistics for one picture type.</summary>
public sealed class TypeStats
{
    public PicType Type { get; init; }
    public int Count { get; set; }
    public long Bytes { get; set; }
    public int TotalFrames { get; set; }

    public double Percent => TotalFrames > 0 ? Count * 100.0 / TotalFrames : 0;
    public double BytePercent => TotalBytes > 0 ? Bytes * 100.0 / TotalBytes : 0;
    public long TotalBytes { get; set; }

    public string CountText => $"{Count} frames";
    public string PercentText => $"{Percent:0.0}%";
    public string SizeText => $"{Bytes / 1024.0 / 1024.0:0.00} MB";
    public string BytePercentText => $"{BytePercent:0.0}%";
}

/// <summary>Everything the UI needs about one analysed file.</summary>
public sealed class AnalysisResult
{
    public MediaInfo Media { get; init; } = new();

    /// <summary>Frames in presentation order. Filled progressively: the packet pass creates
    /// them, later passes enrich each record in place.</summary>
    public List<FrameInfo> Frames { get; } = new();

    /// <summary>
    /// Bumped whenever the frame table changes. The graphs watch this instead of being
    /// invalidated on every individual field write, which keeps a streaming analysis from
    /// forcing a redraw per frame.
    /// </summary>
    public int Version { get; private set; }

    public void MarkUpdated() => Version++;

    /// <summary>True once the packet pass has produced a usable frame table.</summary>
    public bool HasFrames => Frames.Count > 0;

    /// <summary>True when the QP / motion deep pass has been executed.</summary>
    public bool HasDeepAnalysis { get; set; }

    /// <summary>QP values come from the bits-per-pixel estimator rather than the decoder.</summary>
    public bool QpEstimated { get; set; }

    /// <summary>Motion field comes from the built-in block matcher rather than the decoder.</summary>
    public bool MotionEstimated { get; set; }

    // ---- aggregate scales used by the graphs ------------------------------
    public double MaxBitrateKbps { get; private set; }
    public double AvgBitrateKbps { get; private set; }
    public double MinBitrateKbps { get; private set; }
    public double MaxQp { get; private set; } = 51;
    public double MinQp { get; private set; }
    public double MaxMotion { get; private set; } = 1;
    public int MaxGop { get; private set; } = 1;
    public int MaxReorder { get; private set; } = 1;
    public int MinReorder { get; private set; }

    public int FrameCount => Frames.Count;

    public double DurationSeconds => Frames.Count > 0
        ? Frames[^1].PtsTime + Frames[^1].DurationMs / 1000.0
        : Media.DurationSeconds;

    /// <summary>
    /// Presentation time of the last frame. The timeline axis ends here rather than at the end
    /// of the last frame's duration, so that the last frame sits exactly at 100% of the axis
    /// and the playhead can actually reach the right edge.
    /// </summary>
    public double LastFrameTime => Frames.Count > 0 ? Frames[^1].PtsTime : Media.DurationSeconds;

    /// <summary>
    /// Mean time between consecutive frames. The graphs use this to put each frame at its time
    /// position rather than spreading the visible frames evenly across the plot.
    /// </summary>
    public double AverageFrameInterval
    {
        get
        {
            if (Frames.Count > 1)
            {
                double span = Frames[^1].PtsTime - Frames[0].PtsTime;
                if (span > 0) return span / (Frames.Count - 1);
            }
            return Media.FrameRate > 0 ? 1.0 / Media.FrameRate : 0.04;
        }
    }

    public TypeStats IStats { get; private set; } = new() { Type = PicType.I };
    public TypeStats PStats { get; private set; } = new() { Type = PicType.P };
    public TypeStats BStats { get; private set; } = new() { Type = PicType.B };
    public TypeStats OtherStats { get; private set; } = new() { Type = PicType.Other };

    public IReadOnlyList<TypeStats> AllStats => new[] { IStats, PStats, BStats };

    public double KeyFramePercent => FrameCount > 0 ? IStats.Count * 100.0 / FrameCount : 0;

    public void ComputeAggregates()
    {
        if (Frames.Count == 0) return;

        int i = 0, p = 0, b = 0, o = 0;
        long iB = 0, pB = 0, bB = 0, oB = 0;
        long totalBytes = 0;

        double sumBitrate = 0;
        MaxBitrateKbps = 0;
        MinBitrateKbps = double.MaxValue;
        MaxQp = 0;
        MinQp = double.MaxValue;
        MaxMotion = 0;
        MaxGop = 0;
        MaxReorder = 0;
        MinReorder = 0;

        foreach (var f in Frames)
        {
            totalBytes += f.PacketSize;
            sumBitrate += f.BitrateKbps;
            if (f.BitrateKbps > MaxBitrateKbps) MaxBitrateKbps = f.BitrateKbps;
            if (f.BitrateKbps < MinBitrateKbps) MinBitrateKbps = f.BitrateKbps;

            switch (f.Type)
            {
                case PicType.I: i++; iB += f.PacketSize; break;
                case PicType.P: p++; pB += f.PacketSize; break;
                case PicType.B: b++; bB += f.PacketSize; break;
                default: o++; oB += f.PacketSize; break;
            }

            if (f.QpAvg.HasValue)
            {
                if (f.QpAvg.Value > MaxQp) MaxQp = f.QpAvg.Value;
                if (f.QpAvg.Value < MinQp) MinQp = f.QpAvg.Value;
            }

            if (f.MotionMean.HasValue && f.MotionMean.Value > MaxMotion)
                MaxMotion = f.MotionMean.Value;

            if (f.GopPosition > MaxGop) MaxGop = f.GopPosition;
            if (f.ReorderDelta > MaxReorder) MaxReorder = f.ReorderDelta;
            if (f.ReorderDelta < MinReorder) MinReorder = f.ReorderDelta;
        }

        AvgBitrateKbps = sumBitrate / Frames.Count;
        if (MinBitrateKbps == double.MaxValue) MinBitrateKbps = 0;
        if (MinQp == double.MaxValue) MinQp = 0;
        if (MaxQp <= 0) MaxQp = 51;

        IStats = new TypeStats { Type = PicType.I, Count = i, Bytes = iB, TotalFrames = Frames.Count, TotalBytes = totalBytes };
        PStats = new TypeStats { Type = PicType.P, Count = p, Bytes = pB, TotalFrames = Frames.Count, TotalBytes = totalBytes };
        BStats = new TypeStats { Type = PicType.B, Count = b, Bytes = bB, TotalFrames = Frames.Count, TotalBytes = totalBytes };
        OtherStats = new TypeStats { Type = PicType.Other, Count = o, Bytes = oB, TotalFrames = Frames.Count, TotalBytes = totalBytes };

        MaxMotion = Math.Max(MaxMotion, 1);
        MaxGop = Math.Max(MaxGop, 1);
    }

    /// <summary>Binary search for the frame that should be on screen at <paramref name="seconds"/>.</summary>
    public FrameInfo? FrameAt(double seconds)
    {
        if (Frames.Count == 0) return null;
        int lo = 0, hi = Frames.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Frames[mid].PtsTime <= seconds) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return Frames[best];
    }

    public int IndexAt(double seconds) => FrameAt(seconds)?.Index ?? 0;
}
