# Lector PDF (Windows, ARM64 + x64)

Lector de PDF nativo para Windows pensado para que el scroll y el zoom sean fluidos en dispositivos ARM
(Surface con Snapdragon) sin emulación.

## Por qué es fluido

- **Scroll/zoom en el compositor**: `ScrollView` de WinUI 3 (InteractionTracker). El desplazamiento nunca espera al hilo UI.
- **Virtualización**: solo existen elementos para las páginas cercanas al viewport; se reciclan.
- **Render en segundo plano con prioridades y cancelación** (`RenderScheduler`): lo visible primero; lo que sale de pantalla se cancela antes de ejecutarse.
- **Calidad progresiva**: miniatura inmediata → página nítida al zoom actual → **tiles** de 1024 px a zoom alto (solo los visibles). Durante un pinch se escala el bitmap existente y se re-renderiza al soltar.
- **Subida a GPU fuera del hilo UI** (Win2D + `CompositionDrawingSurface`).
- **Motor PDFium** nativo ARM64/x64 (binarios de [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries), `chromium/8076`).

## Funciones

Pestañas, miniaturas, índice (marcadores), búsqueda (Ctrl+F, F3 / Mayús+F3), selección de texto con ratón/lápiz y copiar (Ctrl+C),
enlaces internos y web, ir a página (Ctrl+G), zoom (pinch, Ctrl+rueda, Ctrl+/Ctrl-, Ctrl+0 ajustar al ancho), PDFs con contraseña,
arrastrar y soltar, recuerda la última página de cada archivo.

## Estructura

```
src/PdfReader.Core     Motor (PDFium P/Invoke), layout, scheduler, caché LRU, planificador de tiles, búsqueda
src/PdfReader.App      App WinUI 3 (PdfViewer, PageView, DocumentView, MainWindow)
src/native/pdfium      pdfium.dll para win-x64 y win-arm64
tests/                 Pruebas xUnit del núcleo; tests/assets/make_sample.py genera un PDF pesado de 300 páginas
```

## Compilar y ejecutar

Requiere .NET 10 SDK.

```bash
dotnet test tests/PdfReader.Core.Tests
dotnet build src/PdfReader.App -c Release -p:Platform=x64
dotnet build src/PdfReader.App -c Release -p:Platform=ARM64
```

Publicar una carpeta autocontenida para la Surface (copiar y ejecutar `PdfReader.exe`):

```bash
dotnet publish src/PdfReader.App -c Release -p:Platform=ARM64 -o publish/arm64
```

## Medir la fluidez

```bash
PdfReader.exe --bench "ruta\al\archivo.pdf"
```

Hace scroll continuo a zoom "ajustar al ancho" y a 300 % (tiles) y escribe los tiempos de frame del hilo UI en
`%LocalAppData%\PdfReader\bench.log`. Los errores inesperados se registran en `%LocalAppData%\PdfReader\error.log`.
