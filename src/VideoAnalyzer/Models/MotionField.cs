namespace VideoAnalyzer.Models;

/// <summary>
/// Per-frame motion vector field, on the estimator's block grid.
///
/// Every vector points the way its block's content is travelling: matching the previous
/// frame yields the offset toward where the content came from, so the estimator negates it,
/// while matching the next frame already points where it is going. Without that
/// normalisation the two reference directions would mean opposite things on screen.
///
/// Values are stored as signed bytes because the search window is small and a whole
/// clip is kept in memory: 880 blocks per frame at 320x180 costs about 2.6 KB per frame.
/// <see cref="Reference"/> says which neighbour each block matched against, which is what
/// lets the overlay colour forward and backward predictions differently.
/// </summary>
public sealed class MotionField
{
    public const byte RefForward = 1;   // matched the previous frame
    public const byte RefBackward = 2;  // matched the next frame

    public MotionField(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        Dx = new sbyte[cols * rows];
        Dy = new sbyte[cols * rows];
        Reference = new byte[cols * rows];
    }

    public int Cols { get; }
    public int Rows { get; }

    /// <summary>Horizontal offset toward the match, in small-frame pixels.</summary>
    public sbyte[] Dx { get; }

    /// <summary>Vertical offset toward the match, in small-frame pixels.</summary>
    public sbyte[] Dy { get; }

    public byte[] Reference { get; }

    public int Count => Dx.Length;

    /// <summary>Longest vector in the field, in small-frame pixels.</summary>
    public double PeakLength
    {
        get
        {
            double peak = 0;
            for (int i = 0; i < Dx.Length; i++)
            {
                double len = Math.Sqrt((double)Dx[i] * Dx[i] + (double)Dy[i] * Dy[i]);
                if (len > peak) peak = len;
            }
            return peak;
        }
    }
}