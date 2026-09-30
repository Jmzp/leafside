PDF READER for Windows (x64 and ARM64)
https://github.com/Jmzp/winui-pdf-reader

GETTING STARTED
1. Extract the WHOLE zip into a folder (for example Documents\PdfReader).
   Do not run it from inside the zip.
2. Open the folder and double-click PdfReader.exe.
3. If "Windows protected your PC" appears: click "More info" > "Run anyway".
   (It shows up because the app is not code-signed.)
4. Open a PDF with the button, with Ctrl+O, or by dragging it onto the window.

OPEN PDFs FROM FILE EXPLORER
- On first start the app adds itself to "Open with" for .pdf files (for your user only,
  no administrator rights needed).
- Right-click a PDF > Open with > Choose another app > PDF Reader > "Always".
- If you move the folder, start PdfReader.exe once so the registration follows it.
- To remove it: run  PdfReader.exe --unregister

SHORTCUTS
  Ctrl+O open            Ctrl+W close tab        Ctrl+F search        F3 / Shift+F3 next / previous match
  Ctrl+G go to page      Ctrl+P print            Ctrl+0 fit width     Ctrl+ / Ctrl- zoom
  F11 full screen (Esc)  Alt+Left / Alt+Right back / forward
  Double-tap or double-click: zoom in / back to fit width

The app remembers the page, zoom and open tabs (in %LocalAppData%\PdfReader).
The interface is in English, or in Spanish when Windows is in Spanish.

SMOOTHNESS BENCHMARK (optional)
Drag a PDF onto Benchmark.bat. It scrolls automatically for a few seconds (do not touch the
window) and prints frame times. The result is also saved to %LocalAppData%\PdfReader\bench.log

Third-party licenses (PDFium and its dependencies) are in the "licenses" folder.
