using PdfReader.Core.Engine;

namespace PdfReader.Core.Annotations;

public static class HighlightGeometry
{
    /// <summary>
    /// Turns the rectangles of selected text (one per run of characters, with gaps between words and ragged
    /// heights) into one rectangle per line, which is what a highlight should cover. Runs far apart on the same
    /// line (e.g. two columns) stay separate.
    /// </summary>
    public static IReadOnlyList<PageRect> MergeLines(IReadOnlyList<PageRect> rects)
    {
        var lines = new List<List<PageRect>>();
        foreach (var rect in rects.Where(r => r.Width > 0 && r.Height > 0).OrderBy(r => r.Top).ThenBy(r => r.Left))
        {
            var line = lines.FirstOrDefault(l => SameLine(Union(l), rect));
            if (line is null) lines.Add([rect]);
            else line.Add(rect);
        }

        var result = new List<PageRect>();
        foreach (var line in lines)
        {
            var current = (PageRect?)null;
            foreach (var rect in line.OrderBy(r => r.Left))
            {
                if (current is { } c && rect.Left - c.Right <= Math.Max(c.Height, rect.Height))
                {
                    current = Union([c, rect]);
                }
                else
                {
                    if (current is { } done) result.Add(done);
                    current = rect;
                }
            }
            if (current is { } last) result.Add(last);
        }
        return result.OrderBy(r => r.Top).ThenBy(r => r.Left).ToList();
    }

    private static bool SameLine(PageRect line, PageRect rect)
    {
        double overlap = Math.Min(line.Bottom, rect.Bottom) - Math.Max(line.Top, rect.Top);
        return overlap >= 0.5 * Math.Min(line.Height, rect.Height);
    }

    private static PageRect Union(IEnumerable<PageRect> rects)
    {
        var list = rects.ToList();
        return new PageRect(list.Min(r => r.Left), list.Min(r => r.Top), list.Max(r => r.Right), list.Max(r => r.Bottom));
    }
}
