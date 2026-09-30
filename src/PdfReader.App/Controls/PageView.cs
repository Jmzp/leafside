using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PdfReader.Core.Engine;
using PdfReader.Core.Layout;
using PdfReader.Core.Rendering;

namespace PdfReader.App.Controls;

/// <summary>
/// One realized page. Its pixels live entirely in composition visuals:
/// a low-resolution thumbnail stretched over the page (always present, so scrolling never shows
/// blank pages), and on top a sharp layer at the current zoom (one bitmap or a set of tiles).
/// While a new zoom level renders, the previous sharp layer stays underneath it.
/// Text selection and search hits are drawn by a lightweight XAML overlay above the visuals.
/// </summary>
public sealed class PageView : Grid
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly SpriteVisual _base;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private Layer? _layer;
    private Layer? _previousLayer;

    private static readonly Brush SelectionBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 120, 215));
    private static readonly Brush MatchBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 214, 0));
    private static readonly Brush CurrentMatchBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 255, 120, 0));

    private sealed class Layer(ContainerVisual visual, int scaleKey)
    {
        public ContainerVisual Visual { get; } = visual;
        public int ScaleKey { get; } = scaleKey;
        public Dictionary<(int Column, int Row), CompositionDrawingSurface> Tiles { get; } = new();

        public void Dispose()
        {
            Visual.Children.RemoveAll();
            Visual.Dispose();
            foreach (var surface in Tiles.Values) surface.Dispose();
            Tiles.Clear();
        }
    }

    public PageView(Compositor compositor)
    {
        _compositor = compositor;
        Background = new SolidColorBrush(Colors.White);
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0));
        BorderThickness = new Thickness(0.5);

        var host = new Border();
        Children.Add(host);
        Children.Add(_overlay);

        _root = compositor.CreateContainerVisual();
        _base = compositor.CreateSpriteVisual();
        _root.Children.InsertAtBottom(_base);
        ElementCompositionPreview.SetElementChildVisual(host, _root);
    }

    public int PageIndex { get; private set; } = -1;
    public LayoutRect Bounds { get; private set; }
    public bool HasThumbnail => _base.Brush is not null;
    public int? LayerScaleKey => _layer?.ScaleKey;

    public void Assign(int pageIndex, LayoutRect bounds)
    {
        Reset();
        PageIndex = pageIndex;
        Bounds = bounds;
        var size = new Vector2((float)bounds.Width, (float)bounds.Height);
        _root.Size = size;
        _base.Size = size;
        Visibility = Visibility.Visible;
    }

    public void Reset()
    {
        PageIndex = -1;
        _base.Brush = null;
        _layer?.Dispose();
        _previousLayer?.Dispose();
        _layer = _previousLayer = null;
        _overlay.Children.Clear();
        Visibility = Visibility.Collapsed;
    }

    public void SetThumbnail(CompositionDrawingSurface surface)
    {
        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        _base.Brush = brush;
    }

    /// <summary>Starts a new sharp layer for a new zoom level, keeping the old one visible below it.</summary>
    public void BeginLayer(int scaleKey)
    {
        if (_layer?.ScaleKey == scaleKey) return;
        _previousLayer?.Dispose();
        _previousLayer = _layer;
        var visual = _compositor.CreateContainerVisual();
        _root.Children.InsertAtTop(visual);
        _layer = new Layer(visual, scaleKey);
    }

    public bool HasTile(int scaleKey, int column, int row) =>
        _layer?.ScaleKey == scaleKey && _layer.Tiles.ContainsKey((column, row));

    /// <summary>
    /// Adds a rendered tile (or the whole page when column/row are -1). Returns false if the tile belongs
    /// to a stale layer; the caller then owns (and disposes) the surface.
    /// </summary>
    public bool AddTile(int scaleKey, Tile tile, double scale, CompositionDrawingSurface surface)
    {
        if (_layer is null || _layer.ScaleKey != scaleKey || _layer.Tiles.ContainsKey((tile.Column, tile.Row))) return false;

        double dipPerPixel = DocumentLayout.PointsToDip / scale;
        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        var sprite = _compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        if (tile.Column < 0)
        {
            sprite.Size = _root.Size;
        }
        else
        {
            sprite.Offset = new Vector3((float)(tile.X * dipPerPixel), (float)(tile.Y * dipPerPixel), 0);
            sprite.Size = new Vector2((float)(tile.Width * dipPerPixel), (float)(tile.Height * dipPerPixel));
        }
        _layer.Visual.Children.InsertAtTop(sprite);
        _layer.Tiles[(tile.Column, tile.Row)] = surface;
        return true;
    }

    /// <summary>Drops the previous zoom level once the current one fully covers what is visible.</summary>
    public void DiscardPreviousLayer()
    {
        _previousLayer?.Dispose();
        _previousLayer = null;
    }

    public void SetHighlights(IReadOnlyList<PageRect> selection, IReadOnlyList<SearchMatch> matches, SearchMatch? current)
    {
        _overlay.Children.Clear();
        foreach (var match in matches)
            foreach (var r in match.Rects) AddRect(r, ReferenceEquals(match, current) ? CurrentMatchBrush : MatchBrush);
        foreach (var r in selection) AddRect(r, SelectionBrush);
    }

    private void AddRect(PageRect r, Brush brush)
    {
        const double k = DocumentLayout.PointsToDip;
        var rect = new Rectangle { Width = Math.Max(1, r.Width * k), Height = Math.Max(1, r.Height * k), Fill = brush };
        Canvas.SetLeft(rect, r.Left * k);
        Canvas.SetTop(rect, r.Top * k);
        _overlay.Children.Add(rect);
    }
}
