# Third-party notices

This project redistributes or depends on the following components.

| Component | Use | License |
| --- | --- | --- |
| [PDFium](https://pdfium.googlesource.com/pdfium/) (chromium/8076) | PDF rendering, text, search, printing | BSD-3-Clause / Apache-2.0 |
| [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) by Benoit Blanchon | Prebuilt `pdfium.dll` for win-x64 and win-arm64 in `src/native/pdfium` | MIT |
| PDFium dependencies (FreeType, ICU, libjpeg-turbo, libpng, zlib, OpenJPEG, Little CMS, AGG, Abseil, fast_float, simdutf, LLVM libc) | Statically linked into `pdfium.dll` | See `src/native/pdfium/<arch>/licenses/` |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | UI framework | MIT |
| [Win2D](https://github.com/microsoft/Win2D) | GPU upload of rendered pages | MIT |
| [xUnit](https://xunit.net/) | Tests | Apache-2.0 |

The full license texts of PDFium and its dependencies ship with every release in the `licenses/pdfium` folder,
and are kept in this repository under `src/native/pdfium/win-x64/licenses` and `src/native/pdfium/win-arm64/licenses`.
