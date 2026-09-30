# PDF Reader for Windows

[![CI](https://github.com/Jmzp/winui-pdf-reader/actions/workflows/ci.yml/badge.svg)](https://github.com/Jmzp/winui-pdf-reader/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Jmzp/winui-pdf-reader)](https://github.com/Jmzp/winui-pdf-reader/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A native Windows PDF reader built for **smooth scrolling and zooming**, including on **Windows on Arm**
devices such as Surface laptops and tablets with Snapdragon chips. It runs as native ARM64 code (no x64
emulation), and scrolling never waits for page rendering.

![PDF Reader showing a document with the thumbnail sidebar](docs/images/reader.png)

## Download

Get the latest zip from **[Releases](https://github.com/Jmzp/winui-pdf-reader/releases/latest)**:

| Device | File |
| --- | --- |
| Windows on Arm (Surface with Snapdragon, other ARM64 PCs) | `PdfReader-<version>-win-arm64.zip` |
| Intel / AMD PCs | `PdfReader-<version>-win-x64.zip` |

Extract the **whole** zip and run `PdfReader.exe`. You don't need to install anything, not even the .NET
runtime. The app isn't code-signed yet, so SmartScreen may ask you to confirm: click
*More info → Run anyway*.

Requires Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.

## Features

- **Smooth reading:** continuous scrolling with inertia; pinch, Ctrl+wheel and touchpad zoom (10 %–1000 %).
  Pages stay sharp at any zoom.
- **Tabs, thumbnails and table of contents** (PDF outline), internal and web links.
- **Search** with highlighted matches, and **text selection** with mouse or pen. Ctrl+C copies the selection.
- **Remembers where you were:** the page, the exact position within it and the zoom of every file. The
  tabs of your last session reopen on start, and the start page lists recent files.
- **Back / Forward** after following a link, the outline or a search result.
- **Full screen**, **night mode** (inverted colors) and **double-tap zoom** on touch screens.
- **Printing** through the Windows print dialog. Pages are sent as vectors, one at a time.
- **File Explorer integration:** appears in *Open with* for `.pdf` (per user, no admin rights needed).
  Opening another PDF adds a tab to the window that is already open.
- Password-protected PDFs, drag and drop.
- **English and Spanish UI**, following the Windows display language.

![Night mode](docs/images/night-mode.png)

### Keyboard shortcuts

| Keys | Action |
| --- | --- |
| Ctrl+O / Ctrl+W | Open file / close tab |
| Ctrl+F, F3 / Shift+F3 | Search, next / previous match |
| Ctrl+G | Go to page |
| Ctrl+ / Ctrl- / Ctrl+0 | Zoom in / out / fit width |
| Alt+← / Alt+→ (or mouse back/forward buttons) | Back / forward |
| Ctrl+P | Print |
| F11, Esc | Full screen, exit full screen |
| Space / PgUp / PgDn / Home / End | Scroll by screen, go to first / last page |

## How it stays smooth

Many PDF readers stutter on ARM devices for two reasons: they run emulated (x64 code translated to
ARM64), and they render pages on the same thread that handles scrolling. This reader avoids both:

1. **The compositor scrolls, not the UI thread.** WinUI's `ScrollView` (InteractionTracker) moves the
   content at display refresh rate even while the app is busy.
2. **Only pages near the viewport exist.** Page elements are virtualized and recycled.
3. **Rendering happens in the background, most urgent first.** A dedicated render thread uses a priority
   queue. Visible pages come before prefetch, and pages that scroll out of view are cancelled before
   they're rendered.
4. **Progressive quality.** Each page first shows a cheap thumbnail, then a sharp bitmap for the
   current zoom. Above ~8 megapixels it switches to 1024 px **tiles**, and only the visible ones are
   rendered. During a pinch the existing bitmaps are scaled, and they are re-rendered once the zoom
   settles.
5. **GPU upload off the UI thread.** Bitmaps become `CompositionDrawingSurface`s (Win2D) on the render
   thread, so the UI thread only attaches finished surfaces.
6. **Native PDFium for x64 and ARM64** via P/Invoke.

Measured with the built-in benchmark (a 300-page document with dense vector art, continuous scrolling,
x64 release build):

| Zoom | Frame p50 | Frame p99 | Frames over 33 ms |
| --- | --- | --- | --- |
| 125 % (whole-page bitmaps) | 5.5 ms | 9.6 ms | 0 |
| 300 % (tiles) | 5.6 ms | 8.6 ms | 0 |

Run it yourself: drag a PDF onto `Benchmark.bat` in the release folder, or run
`PdfReader.exe --bench file.pdf`. The result is written to `%LocalAppData%\PdfReader\bench.log`.

## Building from source

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio
is optional; everything builds with the `dotnet` CLI. The Windows App SDK, Win2D and PDFium come from
NuGet or are included in the repository.

```powershell
dotnet test tests/PdfReader.Core.Tests                      # unit tests
dotnet build src/PdfReader.App -c Release -p:Platform=x64   # or -p:Platform=ARM64
.\build\publish.ps1                                         # self-contained zips for both, in artifacts\
```

To run a debug build:
`src\PdfReader.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\PdfReader.exe [file.pdf]`.

`tests/assets/make_sample.py` (Python 3) generates the heavy 300-page `sample.pdf` used for manual and
benchmark testing.

### Project layout

```
src/PdfReader.Core     UI-independent engine: PDFium P/Invoke, page layout, render scheduler, LRU cache,
                       tile planner, search, reading state, navigation history, print layout
src/PdfReader.App      WinUI 3 app: PdfViewer (virtualized pages), PageView, DocumentView, MainWindow,
                       services (state, printing, file association, localization)
src/native/pdfium      pdfium.dll for win-x64 and win-arm64 (with licenses)
tests/                 xUnit tests for Core, engine and localization resources
packaging/             Files shipped next to the exe (README.txt, LEEME.txt, Benchmark.bat)
build/publish.ps1      Release packaging
```

### Localization

UI strings live in `src/PdfReader.App/Strings/<language>/Resources.resw`. `en-US` is the default and
`es` is Spanish. XAML uses `x:Uid`, and code uses `Loc.Get` / `Loc.Format`. To add a language, copy the
`en-US` folder and translate it. `LocalizationTests` checks that every language has the same keys and
placeholders. Set `PDFREADER_LANGUAGE=en-US` (or `es`) to force a language when testing.

## Data and privacy

The app makes no network requests. It stores:

- its state (reading positions, recent files, open tabs, settings) in `%LocalAppData%\PdfReader\state.json`;
- the *Open with* registration under `HKCU\Software\Classes` (`PdfReader.exe --unregister` removes it).

## Limitations

- No annotations, form filling or editing. The goal is a fast, focused reader.
- The print dialog shows no preview.
- Releases are not code-signed yet.

## License

[MIT](LICENSE). Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
