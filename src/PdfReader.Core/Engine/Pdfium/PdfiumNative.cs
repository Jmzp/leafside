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

    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const int FPDFANNOT_COLORTYPE_Color = 0;
    public const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;
    public const uint FPDF_INCREMENTAL = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_SIZEF { public float Width, Height; }

    /// <summary>A rectangle in PDF space (y up): <c>Top</c> is greater than <c>Bottom</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF { public float Left, Top, Right, Bottom; }

    /// <summary>Quad points: top-left, top-right, bottom-left, bottom-right (in reading direction).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FS_QUADPOINTSF { public float X1, Y1, X2, Y2, X3, Y3, X4, Y4; }

    /// <summary>Custom file reading. <c>unsigned long</c> is 32-bit on Windows, hence the 4 GB limit.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public uint FileLen;
        public delegate* unmanaged<void*, uint, byte*, uint, int> GetBlock;
        public void* Param;
    }

    /// <summary>Custom file writing (FPDF_SaveAsCopy). PDFium passes this struct back to the callback.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int Version;
        public delegate* unmanaged<FPDF_FILEWRITE*, void*, uint, int> WriteBlock;
    }

    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadDocument(string filePath, string? password);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadCustomDocument(FPDF_FILEACCESS* fileAccess, string? password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetSecurityHandlerRevision(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_SaveAsCopy(nint doc, FPDF_FILEWRITE* fileWrite, uint flags);
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
    [LibraryImport(Lib)] public static partial int FPDFText_GetBoundedText(nint textPage, double left, double top, double right, double bottom, char* buffer, int bufLen);

    // Annotations
    [LibraryImport(Lib)] public static partial nint FPDFPage_CreateAnnot(nint page, int subtype);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotCount(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_GetAnnot(nint page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotIndex(nint page, nint annot);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFPage_RemoveAnnot(nint page, int index);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetSubtype(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetColor(nint annot, int type, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetColor(nint annot, int type, out uint r, out uint g, out uint b, out uint a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendAttachmentPoints(nint annot, in FS_QUADPOINTSF quad);
    [LibraryImport(Lib)] public static partial nuint FPDFAnnot_CountAttachmentPoints(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetAttachmentPoints(nint annot, nuint index, out FS_QUADPOINTSF quad);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(nint annot, in FS_RECTF rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetRect(nint annot, out FS_RECTF rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetFlags(nint annot, int flags);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDFAnnot_SetStringValue(nint annot, string key, char* value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint FPDFAnnot_GetStringValue(nint annot, string key, char* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetAP(nint annot, int appearanceMode, char* buffer, uint bufLen);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDFAnnot_GetLinkedAnnot(nint annot, string key);

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
