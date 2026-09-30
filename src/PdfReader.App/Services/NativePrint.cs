using System.Runtime.InteropServices;

namespace PdfReader.App.Services;

/// <summary>Win32 print dialog (comdlg32) and GDI print job functions.</summary>
internal static partial class NativePrint
{
    public const uint PD_NOSELECTION = 0x4;
    public const uint PD_PAGENUMS = 0x2;
    public const uint PD_RETURNDC = 0x100;
    public const uint PD_USEDEVMODECOPIESANDCOLLATE = 0x40000;
    public const uint PD_CURRENTPAGE = 0x400000;
    public const uint START_PAGE_GENERAL = 0xFFFFFFFF;
    public const uint PD_RESULT_PRINT = 1;

    public const int HORZRES = 8;
    public const int VERTRES = 10;

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTDLGEX
    {
        public uint lStructSize;
        public nint hwndOwner;
        public nint hDevMode;
        public nint hDevNames;
        public nint hDC;
        public uint Flags;
        public uint Flags2;
        public uint ExclusionFlags;
        public uint nPageRanges;
        public uint nMaxPageRanges;
        public nint lpPageRanges;
        public uint nMinPage;
        public uint nMaxPage;
        public uint nCopies;
        public nint hInstance;
        public nint lpPrintTemplateName;
        public nint lpCallback;
        public uint nPropertyPages;
        public nint lphPropertyPages;
        public uint nStartPage;
        public uint dwResultAction;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTPAGERANGE
    {
        public uint nFromPage;
        public uint nToPage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DOCINFOW
    {
        public int cbSize;
        public nint lpszDocName;
        public nint lpszOutput;
        public nint lpszDatatype;
        public uint fwType;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "PrintDlgExW")]
    public static partial int PrintDlgEx(ref PRINTDLGEX dialog);

    [LibraryImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true)]
    public static partial int StartDoc(nint hdc, in DOCINFOW info);

    [LibraryImport("gdi32.dll", SetLastError = true)] public static partial int StartPage(nint hdc);
    [LibraryImport("gdi32.dll", SetLastError = true)] public static partial int EndPage(nint hdc);
    [LibraryImport("gdi32.dll", SetLastError = true)] public static partial int EndDoc(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial int AbortDoc(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial int GetDeviceCaps(nint hdc, int index);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("kernel32.dll")] public static partial nint GlobalFree(nint handle);
}
