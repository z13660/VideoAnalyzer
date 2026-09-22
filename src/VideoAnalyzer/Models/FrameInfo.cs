namespace VideoAnalyzer.Models;

/// <summary>Picture type of a coded frame.</summary>
public enum PicType
{
    Other = 0,
    I = 1,
    P = 2,
    B = 3
}

/// <summary>Per-frame record. Populated by ffprobe (fast pass) and optionally enriched by
/// ffmpeg debug passes (QP + motion vectors).</summary>
public sealed class FrameInfo
{
    /// <summary>Index in presentation (time) order — this is what the UI plots.</summary>
    public int Index { get; set; }

    /// <summary>Index in decode order — used to map ffmpeg debug output back onto frames.</summary>
    public int DecodeIndex { get; set; } = -1;

    /// <summary>Presentation time in seconds (best_effort_timestamp_time).</summary>
    public double PtsTime { get; set; }

    /// <summary>Decode time in seconds (pkt_dts_time).</summary>
    public double DtsTime { get; set; }

    public PicType Type { get; set; } = PicType.Other;

    public bool KeyFrame { get; set; }

    /// <summary>Encoded packet size in bytes.</summary>
    public long PacketSize { get; set; }

    /// <summary>Frame duration in milliseconds (container supplied, else 1000/fps).</summary>
    public double DurationMs { get; set; }

    // ---- deep analysis ---------------------------------------------------
    public double? QpAvg { get; set; }
    public double? QpMin { get; set; }
    public double? QpMax { get; set; }

    /// <summary>Mean motion vector length in pixels (both directions).</summary>
    public double? MotionMean { get; set; }

    /// <summary>Number of forward predicted vectors in this frame.</summary>
    public int MotionFwdCount { get; set; }

    /// <summary>Number of backward predicted vectors in this frame.</summary>
    public int MotionBwdCount { get; set; }

    public double? MotionMax { get; set; }
    public int MotionVectorCount { get; set; }

    /// <summary>Full motion vector field for this frame (only populated by the built-in
    /// estimator; decoder driven analysis reports statistics without the field).</summary>
    public MotionField? Motion { get; set; }

    // ---- container flags -------------------------------------------------
    public bool Interlaced { get; set; }
    public bool RepeatPict { get; set; }
    public bool Corrupt { get; set; }
    public bool Discard { get; set; }

    /// <summary>Block-noise detector result (QCTools calls this "brain").</summary>
    public bool Brain { get; set; }

    /// <summary>Distance (in frames) to the previous key frame.</summary>
    public int GopPosition { get; set; }

    /// <summary>pts - dts expressed in frames (the reorder buffer depth).</summary>
    public int ReorderDelta { get; set; }

    /// <summary>Average bitrate of this frame in kbps.</summary>
    public double BitrateKbps { get; set; }

    public string TypeLetter => Type switch
    {
        PicType.I => "I",
        PicType.P => "P",
        PicType.B => "B",
        _ => "?"
    };

    public string TypeLongName => Type switch
    {
        PicType.I => "I (intra)",
        PicType.P => "P (predicted)",
        PicType.B => "B (bi-predicted)",
        _ => "? (unknown)"
    };

    public string SizeText
    {
        get
        {
            double kb = PacketSize / 1024.0;
            return $"{PacketSize} B  ({kb:0.#} KB)  {BitrateKbps:0} kbps";
        }
    }

    public string QpText => QpAvg.HasValue
        ? $"avg {QpAvg.Value:0.#}  min {QpMin ?? 0:0}  max {QpMax ?? 0:0}"
        : "n/a";

    public string MotionText => MotionMean.HasValue
        ? $"{MotionVectorCount} vectors  mean {MotionMean.Value:0.##} px  max {MotionMax ?? 0:0.#} px"
        : "n/a";
}
