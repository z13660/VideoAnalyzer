using System.IO;
using VideoAnalyzer.Models;

namespace VideoAnalyzer.Services;

/// <summary>
/// Keeps the deep analysis of recently opened files in memory, so switching back to one does not
/// decode it again.
///
/// Nothing is written to disk — this is a convenience for the session, not a store. Only the
/// per-frame columns are held: picture type, QP and the motion scalars. The motion vector field
/// is deliberately left out, because it is ~2.6 KB per frame and would mean hundreds of megabytes
/// held for a picture the pass can redraw on demand.
///
/// Entries are keyed by the file's identity (path, size, modification time) and by the number of
/// frames in the packet table, so a re-encode, a re-mux or a different frame table misses the
/// cache instead of applying stale numbers to the wrong frames.
/// </summary>
public static class AnalysisCache
{
    /// <summary>How many files to hold. Two or three covers A/B comparison, not a library.</summary>
    private const int Capacity = 3;

    private readonly record struct Row(
        byte Type, bool Brain, float QpAvg, float QpMin, float QpMax,
        float MotionMean, float MotionMax, ushort Fwd, ushort Bwd);

    private sealed record Entry(string Key, int FrameCount, Row[] Rows);

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();      // most recently used first

    /// <summary>Identity of the file plus the frame table it was analysed against.</summary>
    private static string? KeyFor(MediaInfo media, int frameCount)
    {
        try
        {
            var info = new FileInfo(media.FilePath);
            if (!info.Exists) return null;
            return $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{frameCount}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies whatever is held for this file into the frame table. Returns how many frames came
    /// back, 0 when there is nothing usable.
    /// </summary>
    public static int TryLoad(AnalysisResult result)
    {
        string? key = KeyFor(result.Media, result.FrameCount);
        if (key is null) return 0;

        Entry? entry;
        lock (Gate)
        {
            entry = Entries.FirstOrDefault(e => e.Key == key);
            if (entry is not null)
            {
                Entries.Remove(entry);
                Entries.Insert(0, entry);          // touched, so it survives the next eviction
            }
        }

        if (entry is null || entry.FrameCount != result.FrameCount) return 0;

        var frames = result.Frames;
        int restored = 0;

        for (int i = 0; i < frames.Count && i < entry.Rows.Length; i++)
        {
            var row = entry.Rows[i];
            var f = frames[i];
            bool touched = false;

            if (row.Type != (byte)PicType.Other) { f.Type = (PicType)row.Type; touched = true; }
            if (!float.IsNaN(row.QpAvg))
            {
                f.QpAvg = row.QpAvg;
                f.QpMin = row.QpMin;
                f.QpMax = row.QpMax;
                touched = true;
            }
            if (!float.IsNaN(row.MotionMean))
            {
                f.MotionMean = row.MotionMean;
                f.MotionMax = row.MotionMax;
                f.MotionFwdCount = row.Fwd;
                f.MotionBwdCount = row.Bwd;
                f.MotionVectorCount = row.Fwd + row.Bwd;
                touched = true;
            }
            if (row.Brain) { f.Brain = true; touched = true; }

            if (touched) restored++;
        }

        if (restored == 0) return 0;

        result.HasDeepAnalysis = true;
        result.ComputeAggregates();
        result.MarkUpdated();
        AppLog.Write($"cache: restored {restored}/{frames.Count} frames from memory");
        return restored;
    }

    /// <summary>Stores the current deep columns for this file.</summary>
    public static void Save(AnalysisResult result)
    {
        string? key = KeyFor(result.Media, result.FrameCount);
        if (key is null) return;

        var frames = result.Frames;
        if (frames.Count == 0) return;

        var rows = new Row[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            rows[i] = new Row(
                (byte)f.Type,
                f.Brain,
                f.QpAvg.HasValue ? (float)f.QpAvg.Value : float.NaN,
                f.QpMin.HasValue ? (float)f.QpMin.Value : float.NaN,
                f.QpMax.HasValue ? (float)f.QpMax.Value : float.NaN,
                f.MotionMean.HasValue ? (float)f.MotionMean.Value : float.NaN,
                f.MotionMax.HasValue ? (float)f.MotionMax.Value : float.NaN,
                (ushort)Math.Clamp(f.MotionFwdCount, 0, ushort.MaxValue),
                (ushort)Math.Clamp(f.MotionBwdCount, 0, ushort.MaxValue));
        }

        int held;
        lock (Gate)
        {
            Entries.RemoveAll(e => e.Key == key);
            Entries.Insert(0, new Entry(key, frames.Count, rows));
            while (Entries.Count > Capacity) Entries.RemoveAt(Entries.Count - 1);
            held = Entries.Count;
        }

        AppLog.Write($"cache: held {frames.Count} frames in memory ({held} file(s))");
    }
}
