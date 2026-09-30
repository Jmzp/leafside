# AGENTS.md

Guidance for AI coding agents and contributors working in this repository.

## What this is

A native Windows PDF reader (WinUI 3, C#, .NET 10) for **smooth scrolling on ARM64 and x64**, built on PDFium.
It is unpackaged (no MSIX), self-contained, and published as a zip per architecture.

## Commands

```powershell
dotnet test tests/PdfReader.Core.Tests                        # all tests (~75, a few seconds)
dotnet build src/PdfReader.App -c Debug -p:Platform=x64       # app; -p:Platform=ARM64 for Arm
.\build\publish.ps1                                           # release zips for both into artifacts\
src\PdfReader.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\PdfReader.exe [file.pdf]
PdfReader.exe --bench file.pdf                                # scroll benchmark -> %LocalAppData%\PdfReader\bench.log
python tests/assets/make_sample.py                            # heavy 300-page tests/assets/sample.pdf (git-ignored)
```

- Always pass `-p:Platform=x64` or `-p:Platform=ARM64` when building the app; it selects the RID and the
  matching `pdfium.dll`.
- The test project is x64 only: it loads `src/native/pdfium/win-x64/bin/pdfium.dll`.
- `PDFREADER_LANGUAGE=en-US` (or `es`) forces the UI language.

## Architecture

- `src/PdfReader.Core` is UI-independent and fully unit-tested:
  - `Engine/`: `IPdfDocument` and the PDFium implementation.
  - `Layout/`: page positions in DIPs at 100 % zoom.
  - `Rendering/`: `RenderScheduler`, `LruCache`, `TilePlanner`, `PixelOps`, `PrintLayout`.
  - `Text/`: background search.
  - `State/`: `AppStateStore`, `ViewState`, `NavigationHistory`.
  - `CommandLine`.
- `src/PdfReader.App` is the WinUI 3 app:
  - `Controls/PdfViewer.cs` is the heart. It hosts a `ScrollView` over a `PagesPanel`. `UpdateView()`
    realizes and recycles `PageView`s around the viewport and reconciles render requests: it cancels
    stale ones and schedules new ones by priority.
  - `PageView` holds composition visuals: a thumbnail, the sharp layer (one bitmap or tiles) and a
    XAML overlay for selection and search highlights.
  - `DocumentView` is one tab: toolbar, sidebar and viewer. `MainWindow` owns the tabs (the tab strip
    only) and a `DocumentHost` grid holding every `DocumentView`; hidden tabs stay loaded but are
    collapsed and disabled.
  - `Program.cs` is a custom `Main` for single instance. It redirects activations with
    `AppInstance.RedirectActivationToAsync` and sets the MRT language.

## Invariants: do not break these

1. **The UI thread never calls PDFium.** Every `IPdfDocument` call takes a global lock (`PdfiumNative.Sync`;
   PDFium is not thread-safe), which the render thread may hold. Use the render scheduler, or
   `Task.Run` / `RunOnDocumentAsync` in `PdfViewer`.
2. **Rendering goes through `RenderScheduler`**, with a priority (lower value runs first) and a
   cancellation token. Drop results whose document, layer or `_renderGeneration` no longer match.
3. **GPU upload happens on the render thread** (`SurfaceFactory.CreateSurface`). The UI thread only
   attaches surfaces to sprites.
4. **Coordinates:**
   - page space is PDF points with a top-left origin (`PageRect`);
   - layout space is DIPs at zoom 1 (`DocumentLayout.PointsToDip = 96/72`);
   - render scale is pixels per point (`PointsToDip * zoom * rasterizationScale`).
5. **Partial renders get a left/top margin** (`PdfiumDocument.EdgeMargin`). PDFium mis-draws small
   glyphs that straddle the left edge of a bitmap, which showed up as seams between tiles.
   `Tiles_reassemble_the_full_page` guards this.
6. **View restoration:** a `ScrollTo` issued together with a `ZoomTo` can be dropped, so
   `PdfViewer.RestoreView` re-applies the target from `ViewChanged` until it sticks. Nothing is
   persisted while `IsRestoringView` is true.
7. **Keep `--bench` smooth.** On the x64 dev machine: p99 about 10 ms and 0 frames over 33 ms at 125 %
   and 300 %. Re-run the benchmark after touching `PdfViewer`, `PageView`, the scheduler or rendering.
   Benchmark runs must not write state (`AppState.PersistenceEnabled = false`).

## Conventions

- **Language:** code, comments, commit messages and docs are in English. UI text lives in
  `src/PdfReader.App/Strings/{en-US,es}/Resources.resw`:
  - XAML elements get an `x:Uid`, with keys like `Uid.Text` or
    `Uid.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`;
  - code uses `Loc.Get("Key")` or `Loc.Format("Key", args)`;
  - add every key to both files. `LocalizationTests` fails on missing or extra keys, mismatched
    placeholders, unknown `x:Uid`s, and Spanish literals left in code.
- **Accessibility:** icon-only buttons need an accessible name (`AutomationProperties.Name` via `x:Uid`).
- **Style:** match the surrounding code. File-scoped namespaces, nullable enabled, comments explain *why*.
  `Directory.Build.props` holds shared settings and the version.
- **Tests:** add Core tests for new logic in `tests/PdfReader.Core.Tests`. `TestPdf.Create(...)` writes
  small PDFs with text, an outline and a link. The WinUI app has no automated UI tests. Verify UI
  changes by running the app. UI Automation (element names come from `x:Uid` resources) and window
  screenshots work well for that.

## Gotchas

- **Unpackaged publish omits `PdfReader.pri` and `*.xbf`.** The `CopyXamlResourcesToPublishDir` target
  in the csproj copies them. Without them the app crashes at startup with a XAML parse error.
  `build/publish.ps1` checks they are present.
- **MRT language:** unpackaged apps must set `ApplicationLanguages.PrimaryLanguageOverride`, or every
  resource (WinUI's own included) resolves to `en-US`.
- **Printing** uses `PrintDlgEx` plus `FPDF_RenderPage` on the printer HDC. On Windows 11,
  `PrintDlgEx` shows the modern dialog in a separate `PrintDialog.exe` window hosted by
  ApplicationFrameHost. It is modal but pumps messages, so guard against re-entrancy (`_printBusy`).
- **Registry:** the app registers itself under `HKCU\Software\Classes` at startup (only when the exe
  path changes). Running dev builds does this too; `PdfReader.exe --unregister` cleans up.
- **User state:** `%LocalAppData%\PdfReader\state.json`. Back it up before manual tests that open
  files, and restore it afterwards.
