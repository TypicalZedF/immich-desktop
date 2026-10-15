namespace ImmichSplash.Core.Layout;

/// <summary>One row of a photo grid: which photos (from <see cref="Start"/>), how wide each tile is, and how tall the row is.</summary>
public sealed record GridRow(int Start, double[] Widths, double Height);

/// <summary>
/// Lays photos out in rows of equal height, like the Immich web app: every tile has its photo's shape
/// (square, wide, tall), and each row is stretched to fill the width. The last row keeps the normal
/// height, left-aligned, instead of being stretched.
/// </summary>
public static class JustifiedLayout
{
    /// <summary>Extreme shapes (panoramas, very tall screenshots) are cropped to at most this wide or narrow.</summary>
    public const double MinRatio = 0.5, MaxRatio = 2.2;

    /// <param name="ratios">Width divided by height of each photo.</param>
    /// <param name="width">The width of a row.</param>
    /// <param name="target">The height rows aim for.</param>
    /// <param name="gap">Space between tiles (and between rows, added by the caller).</param>
    public static List<GridRow> Compute(IReadOnlyList<double> ratios, double width, double target, double gap)
    {
        var rows = new List<GridRow>();
        if (ratios.Count == 0 || width <= 0 || target <= 0)
            return rows;

        var start = 0;
        var sum = 0.0;
        for (var i = 0; i < ratios.Count; i++)
        {
            sum += Clamp(ratios[i]);
            var count = i - start + 1;
            if (sum * target + gap * (count - 1) < width)
                continue; // room for more

            // Close the row with this photo, or without it if that keeps the row closer to the usual height.
            var heightWith = (width - gap * (count - 1)) / sum;
            if (count > 1)
            {
                var sumWithout = sum - Clamp(ratios[i]);
                var heightWithout = (width - gap * (count - 2)) / sumWithout;
                if (Math.Abs(heightWithout - target) < Math.Abs(heightWith - target))
                {
                    rows.Add(Fill(ratios, start, count - 1, width, gap, heightWithout));
                    start = i;
                    sum = Clamp(ratios[i]);
                    continue;
                }
            }
            rows.Add(Fill(ratios, start, count, width, gap, heightWith));
            start = i + 1;
            sum = 0;
        }

        if (start < ratios.Count)
        {
            // The last row: normal height, not stretched.
            var count = ratios.Count - start;
            var widths = new double[count];
            for (var k = 0; k < count; k++)
                widths[k] = Math.Max(24, Math.Floor(Clamp(ratios[start + k]) * target));
            rows.Add(new GridRow(start, widths, Math.Round(target)));
        }
        return rows;
    }

    static GridRow Fill(IReadOnlyList<double> ratios, int start, int count, double width, double gap, double height)
    {
        height = Math.Round(height);
        var widths = new double[count];
        double used = 0;
        for (var k = 0; k < count; k++)
        {
            widths[k] = Math.Max(24, Math.Floor(Clamp(ratios[start + k]) * height));
            used += widths[k];
        }
        // Whole pixels, and the last tile takes what's left so every row ends at the same edge.
        var rest = width - gap * (count - 1) - (used - widths[count - 1]);
        widths[count - 1] = Math.Max(24, Math.Floor(rest));
        return new GridRow(start, widths, height);
    }

    static double Clamp(double ratio) => double.IsFinite(ratio) && ratio > 0 ? Math.Clamp(ratio, MinRatio, MaxRatio) : 1;
}
