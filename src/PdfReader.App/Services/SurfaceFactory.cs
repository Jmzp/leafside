using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using PdfReader.Core.Engine;
using PdfReader.Core.Rendering;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace PdfReader.App.Services;

/// <summary>
/// Turns engine bitmaps into GPU composition surfaces. Upload happens on the calling (render) thread,
/// so the UI thread only attaches ready-made surfaces to sprites.
/// </summary>
public sealed class SurfaceFactory
{
    private static SurfaceFactory? _instance;

    private readonly CanvasDevice _device;
    private readonly CompositionGraphicsDevice _graphicsDevice;

    private SurfaceFactory(Compositor compositor)
    {
        Compositor = compositor;
        _device = CanvasDevice.GetSharedDevice();
        _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
        _graphicsDevice.RenderingDeviceReplaced += (_, _) => DeviceReplaced?.Invoke(this, EventArgs.Empty);
    }

    public static SurfaceFactory Get(Compositor compositor) => _instance ??= new SurfaceFactory(compositor);

    public Compositor Compositor { get; }

    /// <summary>Raised when the GPU device was lost and recreated; every surface must be rendered again.</summary>
    public event EventHandler? DeviceReplaced;

    /// <param name="invert">Night mode: invert the colors first (cheap, and still off the UI thread).</param>
    public CompositionDrawingSurface CreateSurface(RenderedBitmap bitmap, bool invert = false)
    {
        if (invert) PixelOps.InvertBgr(bitmap.Pixels);
        var surface = _graphicsDevice.CreateDrawingSurface(
            new Size(bitmap.Width, bitmap.Height), Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        using var canvasBitmap = CanvasBitmap.CreateFromBytes(
            _device, bitmap.Pixels, bitmap.Width, bitmap.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using (var session = CanvasComposition.CreateDrawingSession(surface))
        {
            session.DrawImage(canvasBitmap);
        }
        return surface;
    }
}
