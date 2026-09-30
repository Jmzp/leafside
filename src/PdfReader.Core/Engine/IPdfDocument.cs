namespace PdfReader.Core.Engine;

/// <summary>Size of a page in PDF points (1/72 inch), with the page rotation already applied.</summary>
public readonly record struct PageSize(double Width, double Height);

/// <summary>Rectangle in page space: points, origin at the top-left of the displayed page, y down.</summary>
public readonly record struct PageRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>A BGRA, premultiplied, top-down bitmap produced by the engine.</summary>
public sealed class RenderedBitmap(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public long ByteCount => (long)Width * Height * 4;
}

public sealed record OutlineItem(string Title, int PageIndex, IReadOnlyList<OutlineItem> Children);

/// <summary>Where a link points: an internal page or an external URI.</summary>
public sealed record LinkTarget(int PageIndex, string? Uri);

public sealed record SearchMatch(int PageIndex, int CharIndex, int CharCount, IReadOnlyList<PageRect> Rects);

public sealed class PdfPasswordRequiredException(string message) : Exception(message);

/// <summary>Why a document could not be opened (the UI turns this into a localized message).</summary>
public enum PdfOpenError
{
    Unknown,
    /// <summary>The file does not exist or could not be read.</summary>
    FileNotFound,
    /// <summary>Not a PDF, or damaged beyond repair.</summary>
    InvalidFormat,
    /// <summary>Encrypted with a security handler PDFium does not support.</summary>
    UnsupportedSecurity,
}

public sealed class PdfOpenException(PdfOpenError error, string message) : IOException(message)
{
    public PdfOpenError Error { get; } = error;
}

public interface IPdfDocument : IDisposable
{
    string FilePath { get; }
    int PageCount { get; }
    PageSize GetPageSize(int pageIndex);

    /// <summary>
    /// Renders a region of a page. <paramref name="scale"/> is pixels per point; the region is expressed in
    /// pixels of the fully scaled page, which makes the same call usable for whole pages and for tiles.
    /// </summary>
    RenderedBitmap Render(int pageIndex, double scale, int regionX, int regionY, int regionWidth, int regionHeight);

    /// <summary>
    /// Draws a page onto a Windows GDI device context (a printer), as vectors where possible, into the device
    /// rectangle (x, y, width, height). <paramref name="rotate"/> is in quarter turns clockwise (0-3).
    /// </summary>
    void RenderToDC(int pageIndex, nint hdc, int x, int y, int width, int height, int rotate);

    IReadOnlyList<OutlineItem> GetOutline();
    LinkTarget? GetLinkAt(int pageIndex, double x, double y);

    int GetCharCount(int pageIndex);
    /// <summary>Index of the character at a page-space point, or -1.</summary>
    int GetCharIndexAt(int pageIndex, double x, double y, double tolerance);
    string GetText(int pageIndex, int start, int count);
    IReadOnlyList<PageRect> GetTextRects(int pageIndex, int start, int count);
    IReadOnlyList<SearchMatch> Search(int pageIndex, string query, bool matchCase, bool wholeWord);

    /// <summary>False for encrypted documents: their annotations are shown but cannot be changed.</summary>
    bool CanEditAnnotations { get; }
    /// <summary>The page's highlights and notes, in page order (other annotation kinds are left out).</summary>
    IReadOnlyList<PdfAnnotation> GetAnnotations(int pageIndex);
    PdfAnnotation AddAnnotation(int pageIndex, AnnotationDraft draft);
    /// <summary>Changes an annotation; the result may be a re-created annotation (e.g. for a new color).</summary>
    PdfAnnotation UpdateAnnotation(PdfAnnotation annotation, AnnotationDraft draft);
    void RemoveAnnotation(PdfAnnotation annotation);

    /// <summary>
    /// Writes the document with its changes to <paramref name="path"/> (its own file by default), safely:
    /// a failure leaves the target untouched. Afterwards <see cref="FilePath"/> is the saved file.
    /// </summary>
    void Save(string? path = null);
}
