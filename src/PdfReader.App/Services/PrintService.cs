using System.ComponentModel;
using System.Runtime.InteropServices;
using PdfReader.Core.Engine;
using PdfReader.Core.Rendering;
using static PdfReader.App.Services.NativePrint;

namespace PdfReader.App.Services;

/// <summary>A print job the user confirmed: the printer's device context and the pages to print.</summary>
public sealed class PrintJob(nint hdc, IReadOnlyList<int> pages) : IDisposable
{
    public nint Hdc { get; private set; } = hdc;
    public IReadOnlyList<int> Pages { get; } = pages;

    public void Dispose()
    {
        if (Hdc != 0) DeleteDC(Hdc);
        Hdc = 0;
    }
}

/// <summary>
/// Printing through PDFium straight onto the printer's GDI device context: pages go out as vectors
/// (sharp text, small spool files) and only one page is processed at a time, whatever the document size.
/// </summary>
public static class PrintService
{
    private const int MaxRanges = 32;

    /// <summary>Shows the Windows print dialog (printer, pages, copies). Returns null if cancelled. UI thread.</summary>
    public static PrintJob? ShowDialog(nint ownerWindow, int pageCount, int currentPage)
    {
        var ranges = Marshal.AllocHGlobal(Marshal.SizeOf<PRINTPAGERANGE>() * MaxRanges);
        var dialog = new PRINTDLGEX
        {
            lStructSize = (uint)Marshal.SizeOf<PRINTDLGEX>(),
            hwndOwner = ownerWindow,
            Flags = PD_RETURNDC | PD_NOSELECTION | PD_USEDEVMODECOPIESANDCOLLATE,
            nPageRanges = 1,
            nMaxPageRanges = MaxRanges,
            lpPageRanges = ranges,
            nMinPage = 1,
            nMaxPage = (uint)pageCount,
            nCopies = 1,
            nStartPage = START_PAGE_GENERAL,
        };
        try
        {
            Marshal.StructureToPtr(new PRINTPAGERANGE { nFromPage = 1, nToPage = (uint)pageCount }, ranges, false);
            int hr = PrintDlgEx(ref dialog);
            if (hr != 0) throw new COMException(Loc.Get("PrintDialogFailed"), hr);
            if (dialog.dwResultAction != PD_RESULT_PRINT || dialog.hDC == 0)
            {
                if (dialog.hDC != 0) DeleteDC(dialog.hDC);
                return null;
            }

            IReadOnlyList<int> pages;
            if ((dialog.Flags & PD_CURRENTPAGE) != 0) pages = [currentPage];
            else if ((dialog.Flags & PD_PAGENUMS) != 0)
            {
                var list = new List<(int, int)>();
                for (int i = 0; i < dialog.nPageRanges; i++)
                {
                    var r = Marshal.PtrToStructure<PRINTPAGERANGE>(ranges + i * Marshal.SizeOf<PRINTPAGERANGE>());
                    list.Add(((int)r.nFromPage, (int)r.nToPage));
                }
                pages = PrintLayout.ExpandRanges(list, pageCount);
            }
            else pages = Enumerable.Range(0, pageCount).ToList();
            return new PrintJob(dialog.hDC, pages);
        }
        finally
        {
            Marshal.FreeHGlobal(ranges);
            if (dialog.hDevMode != 0) GlobalFree(dialog.hDevMode);
            if (dialog.hDevNames != 0) GlobalFree(dialog.hDevNames);
        }
    }

    /// <summary>
    /// Sends the pages to the printer. Runs on a background thread; each page takes the PDFium lock only
    /// while it is drawn, so the viewer keeps rendering in between. Reports the number of pages done.
    /// </summary>
    /// <param name="outputFile">Optional: print to this file instead (e.g. with "Microsoft Print to PDF").</param>
    public static void Print(IPdfDocument document, string documentName, PrintJob job, IProgress<int> progress,
        CancellationToken token, string? outputFile = null)
    {
        var name = Marshal.StringToHGlobalUni(documentName);
        var output = outputFile is null ? 0 : Marshal.StringToHGlobalUni(outputFile);
        try
        {
            var info = new DOCINFOW { cbSize = Marshal.SizeOf<DOCINFOW>(), lpszDocName = name, lpszOutput = output };
            if (StartDoc(job.Hdc, info) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Loc.Get("PrintRejected"));
            int width = GetDeviceCaps(job.Hdc, HORZRES), height = GetDeviceCaps(job.Hdc, VERTRES);
            for (int i = 0; i < job.Pages.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    AbortDoc(job.Hdc);
                    token.ThrowIfCancellationRequested();
                }
                int page = job.Pages[i];
                var size = document.GetPageSize(page);
                var place = PrintLayout.Fit(size.Width, size.Height, width, height);
                if (StartPage(job.Hdc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Loc.Get("PrintError"));
                document.RenderToDC(page, job.Hdc, place.X, place.Y, place.Width, place.Height, place.Rotate);
                if (EndPage(job.Hdc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Loc.Get("PrintError"));
                progress.Report(i + 1);
            }
            if (EndDoc(job.Hdc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Loc.Get("PrintError"));
        }
        catch (Win32Exception)
        {
            AbortDoc(job.Hdc);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            if (output != 0) Marshal.FreeHGlobal(output);
        }
    }
}
