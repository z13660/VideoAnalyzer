namespace VideoAnalyzer.Services;

/// <summary>
/// Small pixel-domain helpers shared by the deep analysis pass and the video overlay:
/// per-block activity (used for the dotted block-noise overlay) and a blockiness metric
/// (used for the "brain" flag).
/// </summary>
public static class BlockAnalysis
{
    /// <summary>
    /// Per-block blocking ratio, into a caller-owned map: the mean gradient across each block's
    /// top and left edges over the mean gradient inside it.
    ///
    /// A block whose edges are clearly stronger than its interior is the seam an encoder leaves
    /// when it fails to hide a block boundary, which is what the macroblock grid is there to
    /// find. Ratios are left unnormalised so the caller can pick its own threshold.
    /// </summary>
    public static void BlockingRatios(
        byte[] bgra, int w, int h, int cell, ref float[]? map, out int cols, out int rows)
    {
        cell = Math.Max(2, cell);
        cols = Math.Max(1, w / cell);
        rows = Math.Max(1, h / cell);

        int needed = cols * rows;
        if (map is null || map.Length != needed) map = new float[needed];
        if (bgra.Length < w * h * 4) return;

        for (int by = 0; by < rows; by++)
        {
            int y0 = by * cell;
            int y1 = Math.Min(h - 1, y0 + cell);

            for (int bx = 0; bx < cols; bx++)
            {
                int x0 = bx * cell;
                int x1 = Math.Min(w - 1, x0 + cell);

                long edge = 0, inner = 0;
                int ne = 0, ni = 0;

                for (int y = y0; y < y1; y++)
                {
                    int row = y * w;
                    bool onTopEdge = y == y0;

                    for (int x = x0; x < x1; x++)
                    {
                        int i = (row + x) * 4;
                        int g = Math.Abs(Luma(bgra, i + 4) - Luma(bgra, i))
                              + Math.Abs(Luma(bgra, (row + w + x) * 4) - Luma(bgra, i));

                        if (onTopEdge || x == x0) { edge += g; ne += 2; }
                        else { inner += g; ni += 2; }
                    }
                }

                // a block with no interior detail at all says nothing either way
                map[by * cols + bx] = ne > 0 && ni > 0 && inner > 0
                    ? (float)((edge / (double)ne) / (inner / (double)ni))
                    : 0f;
            }
        }
    }

    /// <summary>BT.601 luma of one BGRA pixel, integer math.</summary>
    private static int Luma(byte[] bgra, int offset)
        => (bgra[offset + 2] * 77 + bgra[offset + 1] * 150 + bgra[offset] * 29) >> 8;

    /// <summary>
    /// Per-block activity map: mean absolute gradient inside each block.
    /// Values are normalised to 0..1 so they can drive the overlay opacity directly.
    /// </summary>
    public static float[] Activity(byte[] gray, int w, int h, int blockSize, out int cols, out int rows)
    {
        cols = Math.Max(1, w / Math.Max(2, blockSize));
        rows = Math.Max(1, h / Math.Max(2, blockSize));
        var map = new float[cols * rows];
        if (gray.Length < w * h) return map;

        float peak = 0;
        for (int by = 0; by < rows; by++)
        {
            int y0 = by * blockSize;
            int y1 = Math.Min(h - 1, y0 + blockSize);
            for (int bx = 0; bx < cols; bx++)
            {
                int x0 = bx * blockSize;
                int x1 = Math.Min(w - 1, x0 + blockSize);

                long sum = 0;
                int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int rowOff = y * w;
                    for (int x = x0; x < x1; x++)
                    {
                        int gx = Math.Abs(gray[rowOff + x + 1] - gray[rowOff + x]);
                        int gy = Math.Abs(gray[rowOff + w + x] - gray[rowOff + x]);
                        sum += gx + gy;
                        n += 2;
                    }
                }

                float v = n > 0 ? (float)(sum / (double)n) : 0;
                map[by * cols + bx] = v;
                if (v > peak) peak = v;
            }
        }

        if (peak > 0)
            for (int i = 0; i < map.Length; i++) map[i] /= peak;

        return map;
    }

    /// <summary>
    /// Blockiness ratio: mean gradient across block boundaries divided by the mean
    /// gradient inside blocks. Compression blocking pushes this above 1.
    /// </summary>
    public static double Blockiness(byte[] gray, int w, int h, int blockSize)
    {
        if (blockSize < 2 || gray.Length < w * h) return 1.0;

        long boundary = 0, interior = 0;
        int nb = 0, ni = 0;

        for (int y = 0; y < h; y++)
        {
            int off = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int g = Math.Abs(gray[off + x] - gray[off + x - 1]);
                if (x % blockSize == 0) { boundary += g; nb++; }
                else { interior += g; ni++; }
            }
        }

        if (nb == 0 || ni == 0) return 1.0;
        double b = boundary / (double)nb;
        double i = interior / (double)ni;
        if (i <= 0.0001) return 1.0;
        return b / i;
    }

    /// <summary>Converts a BGRA buffer to a greyscale buffer (BT.601 luma, integer math).</summary>
    public static byte[] ToGray(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];
        int n = Math.Min(gray.Length, bgra.Length / 4);
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            // BGRA layout
            int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
            gray[i] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
        }
        return gray;
    }
}
