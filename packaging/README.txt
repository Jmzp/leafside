LEAFSIDE, a fast PDF reader for Windows (x64 and ARM64)
https://github.com/Jmzp/winui-pdf-reader

Tip: the .msi installer from the download page is easier (Start menu entry, uninstall from
Settings > Apps). This zip is the portable version.

GETTING STARTED
1. Extract the WHOLE zip into a folder (for example Documents\LeafSide).
   Do not run it from inside the zip.
2. Open the folder and double-click LeafSide.exe.
3. If "Windows protected your PC" appears: click "More info" > "Run anyway".
   (It shows up because the app is not code-signed.)
4. Open a PDF with the button, with Ctrl+O, or by dragging it onto the window.

OPEN PDFs FROM FILE EXPLORER
- On first start the app adds itself to "Open with" for .pdf files (for your user only,
  no administrator rights needed).
- Right-click a PDF > Open with > Choose another app > LeafSide > "Always".
- If you move the folder, start LeafSide.exe once so the registration follows it.
- To remove it: run  LeafSide.exe --unregister

HIGHLIGHTS AND NOTES
- Select text and click the highlighter (the arrow next to it picks the color), or right-click the selection.
- Add a note with the note button (then click on the page) or by right-clicking a page.
- Click a highlight or note to comment on it, recolor it or delete it. The "Notes" tab lists them all.
- They are saved INTO the PDF (Ctrl+S), so other readers (Edge, Acrobat, phones) show them too.
  The app asks before closing if there are unsaved changes. Ctrl+Z / Ctrl+Y undo / redo.

SHORTCUTS
  Ctrl+O open            Ctrl+W close tab        Ctrl+F search        F3 / Shift+F3 next / previous match
  Ctrl+G go to page      Ctrl+P print            Ctrl+0 fit width     Ctrl+ / Ctrl- zoom
  Ctrl+S save notes      Ctrl+Shift+S save as    Ctrl+Z / Ctrl+Y undo / redo
  F11 full screen (Esc)  Alt+Left / Alt+Right back / forward
  Double-tap or double-click: zoom in / back to fit width

The app remembers the page, zoom and open tabs (in %LocalAppData%\PdfReader).
The interface is in English, or in Spanish when Windows is in Spanish.
Once a day it asks GitHub whether a newer version exists (no personal data is sent). Turn it off on
the start page: "Check for updates automatically".

SMOOTHNESS BENCHMARK (optional)
Drag a PDF onto Benchmark.bat. It scrolls automatically for a few seconds (do not touch the
window) and prints frame times. The result is also saved to %LocalAppData%\PdfReader\bench.log

LeafSide is free software under the GNU GPL v3 (LICENSE.txt); the source code is at the address above.
Third-party licenses: THIRD-PARTY-NOTICES.md and the "licenses" folder (PDFium and its dependencies).
