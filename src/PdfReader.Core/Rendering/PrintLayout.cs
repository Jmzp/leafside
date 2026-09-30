namespace PdfReader.Core.Rendering;

/// <summary>Where a page goes on the printable area: device rectangle plus quarter turns clockwise.</summary>
public readonly record struct PrintPlacement(int X, int Y, int Width, int Height, int Rotate);

public static class PrintLayout
{
    /// <summary>
    /// Scales a page (any unit) to fit the printable area (device pixels), centered, keeping its aspect ratio.
    /// Landscape pages on portrait paper (and vice versa) are turned a quarter so they use the whole sheet.
    /// </summary>
    public static PrintPlacement Fit(double pageWidth, double pageHeight, int areaWidth, int areaHeight)
    {
        bool rotate = pageWidth != pageHeight && areaWidth != areaHeight && (pageWidth > pageHeight) != (areaWidth > areaHeight);
        double w = rotate ? pageHeight : pageWidth, h = rotate ? pageWidth : pageHeight;
        double scale = Math.Min(areaWidth / w, areaHeight / h);
        int width = Math.Max(1, (int)Math.Round(w * scale)), height = Math.Max(1, (int)Math.Round(h * scale));
        return new PrintPlacement((areaWidth - width) / 2, (areaHeight - height) / 2, width, height, rotate ? 1 : 0);
    }

    /// <summary>1-based inclusive ranges (as a print dialog returns them) to 0-based page indices, in order.</summary>
    public static IReadOnlyList<int> ExpandRanges(IEnumerable<(int From, int To)> ranges, int pageCount)
    {
        var pages = new List<int>();
        foreach (var (from, to) in ranges)
        {
            int first = Math.Max(1, Math.Min(from, to)), last = Math.Min(pageCount, Math.Max(from, to));
            for (int p = first; p <= last; p++) pages.Add(p - 1);
        }
        return pages;
    }
}
