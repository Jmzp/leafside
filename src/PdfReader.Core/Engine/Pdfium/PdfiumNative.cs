using System.Runtime.InteropServices;

namespace PdfReader.Core.Engine.Pdfium;

/// <summary>
/// P/Invoke bindings for the subset of the PDFium C API used by the reader.
/// PDFium is not thread-safe: every call must happen while holding <see cref="Sync"/>.
/// </summary>
internal static unsafe partial class PdfiumNative
{
    private const string Lib = "pdfium";

    public static readonly Lock Sync = new();

    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;
            FPDF_InitLibrary();
            _initialized = true;
        }
    }

    public const int FPDF_ERR_PASSWORD = 4;
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_PRINTING = 0x800;
    public const int FPDFBitmap_BGRA = 4;
    public const uint FPDF_MATCHCASE = 0x1;
    public const uint FPDF_MATCHWHOLEWORD = 0x2;
    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_URI = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_SIZEF { public float Width, Height; }

    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadDocument(string filePath, string? password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndexF(nint doc, int index, out FS_SIZEF size);

    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint doc, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    /// <summary>Windows only: draws a page onto a GDI device context (vector output, used for printing).</summary>
    [LibraryImport(Lib)] public static partial void FPDF_RenderPage(nint dc, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial int FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);
    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);

    // Text
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double xTolerance, double yTolerance);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(nint textPage, int startIndex, int count, char* result);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);
    [LibraryImport(Lib)] public static partial int FPDFText_GetRect(nint textPage, int rectIndex, out double left, out double top, out double right, out double bottom);
    [LibraryImport(Lib)] public static partial nint FPDFText_FindStart(nint textPage, char* findWhat, uint flags, int startIndex);
    [LibraryImport(Lib)] public static partial int FPDFText_FindNext(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(nint handle);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(nint handle);

    // Bookmarks, destinations, links
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetFirstChild(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetNextSibling(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetDest(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetAction(nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetType(nint action);
    [LibraryImport(Lib)] public static partial nint FPDFAction_GetDest(nint doc, nint action);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetURIPath(nint doc, nint action, void* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(nint doc, nint dest);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetLinkAtPoint(nint page, double x, double y);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetDest(nint doc, nint link);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetAction(nint link);
}
