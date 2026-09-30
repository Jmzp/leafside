using System.Text;
using static PdfReader.Core.Engine.Pdfium.PdfiumNative;

namespace PdfReader.Core.Engine.Pdfium;

/// <summary>
/// <see cref="IPdfDocument"/> backed by PDFium. All methods are thread-safe: they serialize on the
/// process-wide PDFium lock, so callers are free to use them from background render threads.
/// </summary>
public sealed unsafe class PdfiumDocument : IPdfDocument
{
    // PDFium's page<->device conversions work in integer device units; converting against a virtual
    // device this many times larger than the page in points keeps sub-point precision.
    private const int Precision = 16;
    private const int MaxCachedPages = 8;
    /// <summary>Extra pixels rendered left of / above a partial region (see <see cref="Render"/>).</summary>
    private const int EdgeMargin = 16;

    private readonly PageSize[] _sizes;
    private readonly LinkedList<CachedPage> _pageCache = new();
    private nint _doc;
    private IReadOnlyList<OutlineItem>? _outline;

    private sealed class CachedPage(int index, nint page)
    {
        public int Index { get; } = index;
        public nint Page { get; } = page;
        public nint TextPage { get; set; }
    }

    private PdfiumDocument(string filePath, nint doc)
    {
        FilePath = filePath;
        _doc = doc;
        PageCount = FPDF_GetPageCount(doc);
        _sizes = new PageSize[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            _sizes[i] = FPDF_GetPageSizeByIndexF(doc, i, out var s) != 0 && s.Width > 0 && s.Height > 0
                ? new PageSize(s.Width, s.Height)
                : new PageSize(612, 792);
        }
    }

    public string FilePath { get; }
    public int PageCount { get; }

    /// <exception cref="PdfPasswordRequiredException">The file is encrypted and the password is missing or wrong.</exception>
    /// <exception cref="IOException">The file could not be opened or is not a valid PDF.</exception>
    public static PdfiumDocument Open(string filePath, string? password = null)
    {
        EnsureInitialized();
        lock (Sync)
        {
            var doc = FPDF_LoadDocument(filePath, password);
            if (doc == 0)
            {
                uint error = FPDF_GetLastError();
                if (error == FPDF_ERR_PASSWORD)
                    throw new PdfPasswordRequiredException("The document is password protected.");
                throw error switch
                {
                    2 => new PdfOpenException(PdfOpenError.FileNotFound, "The file could not be opened."),
                    3 => new PdfOpenException(PdfOpenError.InvalidFormat, "The file is not a valid PDF or is damaged."),
                    5 => new PdfOpenException(PdfOpenError.UnsupportedSecurity, "The document's security scheme is not supported."),
                    _ => new PdfOpenException(PdfOpenError.Unknown, $"The document could not be loaded (error {error})."),
                };
            }
            return new PdfiumDocument(filePath, doc);
        }
    }

    public PageSize GetPageSize(int pageIndex) => _sizes[pageIndex];

    public RenderedBitmap Render(int pageIndex, double scale, int regionX, int regionY, int regionWidth, int regionHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(regionWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(regionHeight, 1);

        var size = _sizes[pageIndex];
        int fullWidth = Math.Max(1, (int)Math.Round(size.Width * scale));
        int fullHeight = Math.Max(1, (int)Math.Round(size.Height * scale));

        // PDFium mis-draws glyphs that straddle the left edge of the target bitmap, which showed up as seams
        // between tiles. Regions that do not start at the page edge are rendered with a margin, then cropped.
        int padLeft = Math.Clamp(regionX, 0, EdgeMargin), padTop = Math.Clamp(regionY, 0, EdgeMargin);
        int renderWidth = regionWidth + padLeft, renderHeight = regionHeight + padTop;
        var rendered = GC.AllocateUninitializedArray<byte>(renderWidth * renderHeight * 4);

        lock (Sync)
        {
            var page = GetPage(pageIndex).Page;
            fixed (byte* p = rendered)
            {
                var bitmap = FPDFBitmap_CreateEx(renderWidth, renderHeight, FPDFBitmap_BGRA, p, renderWidth * 4);
                if (bitmap == 0) throw new InsufficientMemoryException("PDFium could not allocate the bitmap.");
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, renderWidth, renderHeight, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bitmap, page, padLeft - regionX, padTop - regionY, fullWidth, fullHeight, 0, FPDF_ANNOT);
                }
                finally
                {
                    FPDFBitmap_Destroy(bitmap);
                }
            }
        }

        if (padLeft == 0 && padTop == 0) return new RenderedBitmap(rendered, regionWidth, regionHeight);
        var pixels = GC.AllocateUninitializedArray<byte>(regionWidth * regionHeight * 4);
        for (int row = 0; row < regionHeight; row++)
            Buffer.BlockCopy(rendered, ((row + padTop) * renderWidth + padLeft) * 4, pixels, row * regionWidth * 4, regionWidth * 4);
        return new RenderedBitmap(pixels, regionWidth, regionHeight);
    }

    public void RenderToDC(int pageIndex, nint hdc, int x, int y, int width, int height, int rotate)
    {
        lock (Sync)
        {
            var page = GetPage(pageIndex).Page;
            FPDF_RenderPage(hdc, page, x, y, width, height, rotate, FPDF_ANNOT | FPDF_PRINTING);
        }
    }

    public IReadOnlyList<OutlineItem> GetOutline()
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_doc == 0, this);
            return _outline ??= ReadOutline(0, new HashSet<nint>(), 0);
        }
    }

    private List<OutlineItem> ReadOutline(nint parent, HashSet<nint> visited, int depth)
    {
        var items = new List<OutlineItem>();
        if (depth > 32) return items;
        for (var bm = FPDFBookmark_GetFirstChild(_doc, parent); bm != 0; bm = FPDFBookmark_GetNextSibling(_doc, bm))
        {
            if (!visited.Add(bm)) break; // malformed, cyclic outline
            string title = ReadBookmarkTitle(bm);
            int pageIndex = -1;
            var dest = FPDFBookmark_GetDest(_doc, bm);
            if (dest == 0)
            {
                var action = FPDFBookmark_GetAction(bm);
                if (action != 0 && FPDFAction_GetType(action) == PDFACTION_GOTO)
                    dest = FPDFAction_GetDest(_doc, action);
            }
            if (dest != 0) pageIndex = FPDFDest_GetDestPageIndex(_doc, dest);
            items.Add(new OutlineItem(title.Trim(), pageIndex, ReadOutline(bm, visited, depth + 1)));
        }
        return items;
    }

    public LinkTarget? GetLinkAt(int pageIndex, double x, double y)
    {
        lock (Sync)
        {
            var page = GetPage(pageIndex).Page;
            var (px, py) = DisplayToPdf(pageIndex, page, x, y);
            var link = FPDFLink_GetLinkAtPoint(page, px, py);
            if (link == 0) return null;

            var dest = FPDFLink_GetDest(_doc, link);
            if (dest != 0) return new LinkTarget(FPDFDest_GetDestPageIndex(_doc, dest), null);

            var action = FPDFLink_GetAction(link);
            if (action == 0) return null;
            switch (FPDFAction_GetType(action))
            {
                case PDFACTION_GOTO:
                    dest = FPDFAction_GetDest(_doc, action);
                    return dest != 0 ? new LinkTarget(FPDFDest_GetDestPageIndex(_doc, dest), null) : null;
                case PDFACTION_URI:
                    uint len = FPDFAction_GetURIPath(_doc, action, null, 0);
                    if (len <= 1) return null;
                    var buffer = new byte[len];
                    fixed (byte* b = buffer) FPDFAction_GetURIPath(_doc, action, b, len);
                    return new LinkTarget(-1, Encoding.ASCII.GetString(buffer, 0, (int)len - 1));
                default:
                    return null;
            }
        }
    }

    public int GetCharCount(int pageIndex)
    {
        lock (Sync) return FPDFText_CountChars(GetTextPage(pageIndex));
    }

    public int GetCharIndexAt(int pageIndex, double x, double y, double tolerance)
    {
        lock (Sync)
        {
            var cached = GetPage(pageIndex);
            var textPage = GetTextPage(pageIndex);
            var (px, py) = DisplayToPdf(pageIndex, cached.Page, x, y);
            return FPDFText_GetCharIndexAtPos(textPage, px, py, tolerance, tolerance);
        }
    }

    public string GetText(int pageIndex, int start, int count)
    {
        if (count <= 0) return string.Empty;
        lock (Sync)
        {
            var textPage = GetTextPage(pageIndex);
            var buffer = new char[count + 1];
            int written;
            fixed (char* b = buffer) written = FPDFText_GetText(textPage, start, count, b);
            return written <= 1 ? string.Empty : new string(buffer, 0, written - 1);
        }
    }

    public IReadOnlyList<PageRect> GetTextRects(int pageIndex, int start, int count)
    {
        if (count <= 0) return [];
        lock (Sync)
        {
            var page = GetPage(pageIndex).Page;
            return ReadRects(pageIndex, page, GetTextPage(pageIndex), start, count);
        }
    }

    public IReadOnlyList<SearchMatch> Search(int pageIndex, string query, bool matchCase, bool wholeWord)
    {
        if (string.IsNullOrEmpty(query)) return [];
        uint flags = (matchCase ? FPDF_MATCHCASE : 0) | (wholeWord ? FPDF_MATCHWHOLEWORD : 0);
        var matches = new List<SearchMatch>();
        lock (Sync)
        {
            var page = GetPage(pageIndex).Page;
            var textPage = GetTextPage(pageIndex);
            fixed (char* q = query + "\0")
            {
                var handle = FPDFText_FindStart(textPage, q, flags, 0);
                if (handle == 0) return matches;
                try
                {
                    while (FPDFText_FindNext(handle) != 0)
                    {
                        int index = FPDFText_GetSchResultIndex(handle);
                        int count = FPDFText_GetSchCount(handle);
                        matches.Add(new SearchMatch(pageIndex, index, count, ReadRects(pageIndex, page, textPage, index, count)));
                    }
                }
                finally
                {
                    FPDFText_FindClose(handle);
                }
            }
        }
        return matches;
    }

    private List<PageRect> ReadRects(int pageIndex, nint page, nint textPage, int start, int count)
    {
        int n = FPDFText_CountRects(textPage, start, count);
        var rects = new List<PageRect>(Math.Max(n, 0));
        for (int i = 0; i < n; i++)
        {
            if (FPDFText_GetRect(textPage, i, out double l, out double t, out double r, out double b) == 0) continue;
            var (x1, y1) = PdfToDisplay(pageIndex, page, l, t);
            var (x2, y2) = PdfToDisplay(pageIndex, page, r, b);
            rects.Add(new PageRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2)));
        }
        return rects;
    }

    private (double X, double Y) PdfToDisplay(int pageIndex, nint page, double x, double y)
    {
        var (w, h) = VirtualDevice(pageIndex);
        FPDF_PageToDevice(page, 0, 0, w, h, 0, x, y, out int dx, out int dy);
        return ((double)dx / Precision, (double)dy / Precision);
    }

    private (double X, double Y) DisplayToPdf(int pageIndex, nint page, double x, double y)
    {
        var (w, h) = VirtualDevice(pageIndex);
        FPDF_DeviceToPage(page, 0, 0, w, h, 0, (int)Math.Round(x * Precision), (int)Math.Round(y * Precision), out double px, out double py);
        return (px, py);
    }

    private (int Width, int Height) VirtualDevice(int pageIndex)
    {
        var s = _sizes[pageIndex];
        return ((int)Math.Round(s.Width * Precision), (int)Math.Round(s.Height * Precision));
    }

    private static string ReadBookmarkTitle(nint bookmark)
    {
        uint len = FPDFBookmark_GetTitle(bookmark, null, 0);
        if (len <= 2) return string.Empty;
        var buffer = new byte[len];
        fixed (byte* b = buffer) FPDFBookmark_GetTitle(bookmark, b, len);
        return Encoding.Unicode.GetString(buffer, 0, (int)len - 2);
    }

    // --- page handle cache (caller holds Sync) ---

    private CachedPage GetPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_doc == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        for (var node = _pageCache.First; node != null; node = node.Next)
        {
            if (node.Value.Index != pageIndex) continue;
            if (node != _pageCache.First)
            {
                _pageCache.Remove(node);
                _pageCache.AddFirst(node);
            }
            return node.Value;
        }

        var page = FPDF_LoadPage(_doc, pageIndex);
        if (page == 0) throw new InvalidDataException($"Page {pageIndex + 1} could not be loaded.");
        var cached = new CachedPage(pageIndex, page);
        _pageCache.AddFirst(cached);
        while (_pageCache.Count > MaxCachedPages)
        {
            ClosePage(_pageCache.Last!.Value);
            _pageCache.RemoveLast();
        }
        return cached;
    }

    private nint GetTextPage(int pageIndex)
    {
        var cached = GetPage(pageIndex);
        if (cached.TextPage == 0)
        {
            cached.TextPage = FPDFText_LoadPage(cached.Page);
            if (cached.TextPage == 0) throw new InvalidDataException($"The text of page {pageIndex + 1} could not be read.");
        }
        return cached.TextPage;
    }

    private static void ClosePage(CachedPage cached)
    {
        if (cached.TextPage != 0) FPDFText_ClosePage(cached.TextPage);
        FPDF_ClosePage(cached.Page);
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_doc == 0) return;
            foreach (var cached in _pageCache) ClosePage(cached);
            _pageCache.Clear();
            FPDF_CloseDocument(_doc);
            _doc = 0;
        }
    }
}
