using ImmichSplash.Core.Layout;

namespace ImmichSplash.Core.Tests;

public class JustifiedLayoutTests
{
    const double Gap = 4;

    [Fact]
    public void Every_full_row_ends_at_the_same_edge_and_keeps_every_photo()
    {
        var ratios = Enumerable.Range(0, 60).Select(i => (i % 5) switch { 0 => 1.0, 1 => 1.5, 2 => 0.75, 3 => 0.56, _ => 1.78 }).ToList();

        var rows = JustifiedLayout.Compute(ratios, 900, 160, Gap);

        Assert.Equal(60, rows.Sum(r => r.Widths.Length));
        Assert.Equal(Enumerable.Range(0, 60), rows.SelectMany(r => Enumerable.Range(r.Start, r.Widths.Length)));
        foreach (var row in rows[..^1])
            Assert.Equal(900, row.Widths.Sum() + Gap * (row.Widths.Length - 1));
    }

    [Fact]
    public void Tiles_keep_their_photos_shape()
    {
        var rows = JustifiedLayout.Compute([1.0, 1.0, 1.0, 1.5, 0.5, 2.0, 1.0, 1.0], 800, 200, Gap);

        var first = rows[0];
        // A wide photo gets a wider tile than a square one; a tall one a narrower tile.
        var all = rows.SelectMany(r => r.Widths.Select(w => (w, r.Height))).ToList();
        Assert.True(all[3].w / all[3].Height > all[0].w / all[0].Height);
        Assert.True(all[4].w / all[4].Height < all[0].w / all[0].Height);
        Assert.True(first.Height > 100 && first.Height < 300);
    }

    [Fact]
    public void The_last_row_is_not_stretched()
    {
        var rows = JustifiedLayout.Compute([1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0], 1000, 200, Gap);

        var last = rows[^1];
        Assert.Equal(200, last.Height);
        Assert.True(last.Widths.Sum() + Gap * (last.Widths.Length - 1) < 1000);
    }

    [Fact]
    public void Extreme_shapes_and_bad_numbers_cant_break_it()
    {
        var rows = JustifiedLayout.Compute([5.0, 0.05, 0, double.NaN, -1, 1.0], 600, 150, Gap);

        Assert.Equal(6, rows.Sum(r => r.Widths.Length));
        Assert.All(rows.SelectMany(r => r.Widths), w => Assert.True(w >= 24 && w < 600));
    }

    [Fact]
    public void A_single_photo_or_no_photos()
    {
        Assert.Empty(JustifiedLayout.Compute([], 800, 200, Gap));
        var one = Assert.Single(JustifiedLayout.Compute([1.5], 800, 200, Gap));
        Assert.Equal(200, one.Height);
        Assert.Equal(300, Assert.Single(one.Widths));
    }
}
