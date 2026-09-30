using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace PdfReader.App.Controls;

/// <summary>Sidebar thumbnail: shows the viewer's shared thumbnail surface for a page in a sprite.</summary>
public sealed class ThumbnailView : Grid
{
    public const double ThumbnailWidth = 120;

    private readonly SpriteVisual _sprite;
    private CancellationTokenSource? _cts;

    public ThumbnailView()
    {
        Width = ThumbnailWidth;
        Background = new SolidColorBrush(Colors.White);
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 0, 0, 0));
        BorderThickness = new Thickness(1);
        var host = new Border();
        Children.Add(host);
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _sprite = compositor.CreateSpriteVisual();
        ElementCompositionPreview.SetElementChildVisual(host, _sprite);
        SizeChanged += (_, e) => _sprite.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
    }

    public async void Show(PdfViewer viewer, int pageIndex)
    {
        Clear();
        Height = ThumbnailWidth * viewer.GetPageAspectRatio(pageIndex);
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var surface = await viewer.GetThumbnailAsync(pageIndex, cts.Token);
            if (surface is null || cts.IsCancellationRequested) return;
            var brush = _sprite.Compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.Fill;
            _sprite.Brush = brush;
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // async void: never let a failed thumbnail (e.g. GPU device lost while closing) take the app down.
            System.Diagnostics.Debug.WriteLine($"Thumbnail {pageIndex} failed: {ex.Message}");
        }
    }

    public void Clear()
    {
        _cts?.Cancel();
        _cts = null;
        _sprite.Brush = null;
    }
}
