using PdfReader.Core.Rendering;

namespace PdfReader.Core.Tests;

public sealed class PrintLayoutTests
{
    [Fact]
    public void Portrait_page_fills_portrait_paper()
    {
        // Letter page (612 x 792 pt) on a 4800 x 6000 printable area: height-limited? 4800/612 = 7.84, 6000/792 = 7.58.
        var p = PrintLayout.Fit(612, 792, 4800, 6000);
        Assert.Equal(0, p.Rotate);
        Assert.Equal(6000, p.Height);
        Assert.Equal(4636, p.Width);
        Assert.Equal((4800 - 4636) / 2, p.X);
        Assert.Equal(0, p.Y);
    }

    [Fact]
    public void Landscape_page_is_turned_on_portrait_paper()
    {
        var p = PrintLayout.Fit(842, 595, 4800, 6000);
        Assert.Equal(1, p.Rotate);
        Assert.True(p.Height > p.Width); // occupies the sheet in its rotated orientation
        Assert.Equal(6000, p.Height); // 595 x 842 turned: height-limited
    }

    [Fact]
    public void Square_pages_are_not_rotated()
    {
        Assert.Equal(0, PrintLayout.Fit(500, 500, 4800, 6000).Rotate);
    }

    [Fact]
    public void Ranges_expand_to_zero_based_pages()
    {
        Assert.Equal([0, 1, 2, 9], PrintLayout.ExpandRanges([(1, 3), (10, 10)], 20));
        Assert.Equal([18, 19], PrintLayout.ExpandRanges([(19, 99)], 20)); // clamped to the document
        Assert.Equal([4, 5], PrintLayout.ExpandRanges([(6, 5)], 20));    // reversed range
    }
}
