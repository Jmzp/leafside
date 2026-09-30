using PdfReader.Core.Engine;

namespace PdfReader.Core.Layout;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Intersects(LayoutRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public LayoutRect Inflate(double dx, double dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
}

/// <summary>
/// Continuous single-column layout of every page, in device-independent pixels at 100 % zoom
/// (1 pt = 96/72 DIP). Zoom is applied on top of this by the scroll host, so the layout never changes
/// while the user zooms.
/// </summary>
public sealed class DocumentLayout
{
    public const double PointsToDip = 96.0 / 72.0;

    private readonly LayoutRect[] _rects;

    public DocumentLayout(IReadOnlyList<PageSize> pageSizes, double pageGap = 12, double padding = 16)
    {
        _rects = new LayoutRect[pageSizes.Count];
        double maxWidth = 0;
        foreach (var s in pageSizes) maxWidth = Math.Max(maxWidth, s.Width * PointsToDip);

        ContentWidth = maxWidth + 2 * padding;
        double y = padding;
        for (int i = 0; i < pageSizes.Count; i++)
        {
            double w = pageSizes[i].Width * PointsToDip;
            double h = pageSizes[i].Height * PointsToDip;
            _rects[i] = new LayoutRect(padding + (maxWidth - w) / 2, y, w, h);
            y += h + (i == pageSizes.Count - 1 ? 0 : pageGap);
        }
        ContentHeight = y + padding;
        MaxPageWidth = maxWidth;
    }

    public int PageCount => _rects.Length;
    public double ContentWidth { get; }
    public double ContentHeight { get; }
    public double MaxPageWidth { get; }

    public LayoutRect GetPageRect(int pageIndex) => _rects[pageIndex];

    /// <summary>Inclusive range of pages intersecting the vertical band [top, bottom], or (-1, -1) if none.</summary>
    public (int First, int Last) GetPagesInRange(double top, double bottom)
    {
        if (_rects.Length == 0 || bottom < top) return (-1, -1);
        int first = FirstPageEndingAfter(top);
        if (first >= _rects.Length || _rects[first].Y > bottom) return (-1, -1);
        int last = first;
        while (last + 1 < _rects.Length && _rects[last + 1].Y <= bottom) last++;
        return (first, last);
    }

    /// <summary>The page containing or closest to a vertical position.</summary>
    public int GetPageAt(double y)
    {
        if (_rects.Length == 0) return -1;
        int index = FirstPageEndingAfter(y);
        return Math.Min(index, _rects.Length - 1);
    }

    private int FirstPageEndingAfter(double y)
    {
        int lo = 0, hi = _rects.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_rects[mid].Bottom < y) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
