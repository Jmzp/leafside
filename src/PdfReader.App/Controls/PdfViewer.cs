using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using PdfReader.App.Services;
using PdfReader.Core.Engine;
using PdfReader.Core.Layout;
using PdfReader.Core.Rendering;
using PdfReader.Core.State;
using PdfReader.Core.Text;
using Windows.ApplicationModel.DataTransfer;
using Launcher = Windows.System.Launcher;
using VirtualKey = Windows.System.VirtualKey;

namespace PdfReader.App.Controls;

/// <summary>
/// Continuous PDF viewer built for smooth scrolling on low-power (ARM) devices:
/// <list type="bullet">
/// <item>Scrolling and zooming are driven by the compositor (<see cref="ScrollView"/> / InteractionTracker), so they
/// never wait for the UI thread or for rendering.</item>
/// <item>Only pages near the viewport are realized; their elements are recycled.</item>
/// <item>Rendering happens on a background thread, most urgent first, and requests for content that left the
/// viewport are cancelled before they run.</item>
/// <item>Each page first shows a cheap thumbnail, then a sharp bitmap (or tiles at high zoom) for the settled zoom.</item>
/// </list>
/// </summary>
public sealed partial class PdfViewer : UserControl
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 10;
    private const double ThumbnailWidthPx = 240;
    private const long ThumbnailBudgetBytes = 160L * 1024 * 1024;

    private readonly ScrollView _scroll;
    private readonly PagesPanel _panel = new();
    private readonly Compositor _compositor;
    private readonly SurfaceFactory _surfaces;
    private readonly DispatcherQueueTimer _zoomSettleTimer;
    private readonly DispatcherQueueTimer _restoreTimeout;
    private readonly Dictionary<int, PageView> _realized = new();
    private readonly Stack<PageView> _pool = new();
    private readonly Dictionary<RequestKey, CancellationTokenSource> _inflight = new();
    private readonly Dictionary<int, Task<CompositionDrawingSurface>> _thumbnailTasks = new();
    private readonly LruCache<int, CompositionDrawingSurface> _thumbnails = new(ThumbnailBudgetBytes);

    private IPdfDocument? _document;
    private DocumentLayout? _layout;
    private RenderScheduler? _scheduler;
    private double _renderZoom = 1;
    private double _reportedZoom = 1;
    private bool _zoomSettling;
    private bool _updateQueued;
    private int _currentPage = -1;
    // View restoration: the ScrollView may drop a ScrollTo issued together with a ZoomTo (or before its extent
    // is known), so the target is re-applied from ViewChanged until the view actually gets there.
    private ViewState? _pendingInitialView;
    private (double Zoom, double X, double Y)? _restoreTarget;
    private int _restoreAttempts;

    private readonly NavigationHistory _history = new();
    private bool _nightMode;
    // Bumped whenever everything must be re-rendered (night mode, GPU device lost) so that renders that were
    // already running when that happened are dropped instead of attached.
    private int _renderGeneration;

    // Selection
    private readonly record struct TextPosition(int Page, int Char);
    private TextPosition? _selectionAnchor, _selectionFocus;
    private readonly Dictionary<int, IReadOnlyList<PageRect>> _selectionRects = new();
    private uint _pressedPointerId;
    private bool _pointerPressed, _dragging;
    private Windows.Foundation.Point _pressPoint;
    private int _hitTestGeneration, _selectionGeneration;

    // Search
    private readonly Dictionary<int, List<SearchMatch>> _matchesByPage = new();
    private readonly List<SearchMatch> _matches = new();
    private SearchMatch? _currentMatch;
    private CancellationTokenSource? _searchCts;

    private readonly record struct RequestKey(int Page, int ScaleKey, int Column, int Row)
    {
        public const int ThumbnailScale = -1;
    }

    public PdfViewer()
    {
        IsTabStop = true;
        _scroll = new ScrollView
        {
            Content = _panel,
            ContentOrientation = ScrollingContentOrientation.Both,
            ZoomMode = ScrollingZoomMode.Enabled,
            MinZoomFactor = MinZoom,
            MaxZoomFactor = MaxZoom,
            HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto,
        };
        _panel.HorizontalAlignment = HorizontalAlignment.Center;
        Content = _scroll;

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _surfaces = SurfaceFactory.Get(_compositor);
        _surfaces.DeviceReplaced += (_, _) => DispatcherQueue.TryEnqueue(InvalidateRendering);

        _zoomSettleTimer = DispatcherQueue.CreateTimer();
        _zoomSettleTimer.Interval = TimeSpan.FromMilliseconds(140);
        _zoomSettleTimer.IsRepeating = false;
        _zoomSettleTimer.Tick += (_, _) => OnZoomSettled();

        _restoreTimeout = DispatcherQueue.CreateTimer();
        _restoreTimeout.Interval = TimeSpan.FromSeconds(1);
        _restoreTimeout.IsRepeating = false;
        _restoreTimeout.Tick += (_, _) => FinishRestore();

        _scroll.ViewChanged += (_, _) => OnViewChanged();
        _scroll.SizeChanged += (_, e) => OnViewportSizeChanged(e.PreviousSize.Width);
        _panel.PointerPressed += OnPanelPointerPressed;
        _panel.PointerMoved += OnPanelPointerMoved;
        _panel.PointerReleased += OnPanelPointerReleased;
        _panel.PointerCaptureLost += (_, _) => _pointerPressed = false;
        _panel.DoubleTapped += OnPanelDoubleTapped;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => { if (XamlRoot is { } root) root.Changed += (_, _) => QueueUpdate(); };
    }

    public IPdfDocument? Document => _document;
    public DocumentLayout? Layout => _layout;
    public int CurrentPage => _currentPage;
    public double ZoomFactor => _scroll.ZoomFactor;
    public int MatchCount => _matches.Count;
    public int CurrentMatchIndex => _currentMatch is null ? -1 : _matches.IndexOf(_currentMatch);
    public bool HasSelection => _selectionAnchor is not null && _selectionFocus is not null;
    /// <summary>True until the initial (or a restored) position has been applied; the view is not meaningful yet.</summary>
    public bool IsRestoringView => _pendingInitialView is not null || _restoreTarget is not null;

    public event EventHandler<int>? CurrentPageChanged;
    public event EventHandler<double>? ZoomChanged;
    /// <summary>Raised when the reading position or zoom changed (never while a position is being restored).</summary>
    public event EventHandler? ViewStateChanged;
    /// <summary>Raised when Back/Forward availability may have changed.</summary>
    public event EventHandler? HistoryChanged;
    /// <summary>Raised when every thumbnail was discarded (e.g. night mode toggled); the sidebar should reload them.</summary>
    public event EventHandler? ThumbnailsInvalidated;

    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;

    /// <summary>Renders pages with inverted colors, for reading in the dark.</summary>
    public bool NightMode
    {
        get => _nightMode;
        set
        {
            if (_nightMode == value) return;
            _nightMode = value;
            foreach (var page in _realized.Values.Concat(_pool)) page.NightMode = value;
            if (_document is not null) InvalidateRendering();
        }
    }
    public event EventHandler? SearchResultsChanged;
    /// <summary>Raised on the UI thread when a page thumbnail becomes available.</summary>
    public event EventHandler<int>? ThumbnailReady;

    // ----------------------------------------------------------------- document lifetime

    public void Open(IPdfDocument document, ViewState? initialView = null)
    {
        Close();
        _document = document;
        _scheduler = new RenderScheduler($"PDF render: {Path.GetFileName(document.FilePath)}");
        _layout = new DocumentLayout(Enumerable.Range(0, document.PageCount).Select(document.GetPageSize).ToList());
        _panel.Layout = _layout;
        _pendingInitialView = initialView ?? new ViewState(0);
        ApplyInitialView();
    }

    public void Close()
    {
        _searchCts?.Cancel();
        foreach (var cts in _inflight.Values) cts.Cancel();
        _inflight.Clear();
        _thumbnailTasks.Clear();
        _scheduler?.Dispose();
        _scheduler = null;
        foreach (var page in _realized.Values) Recycle(page);
        _realized.Clear();
        foreach (var surface in DrainThumbnails()) surface.Dispose();
        ClearSelection();
        _matches.Clear();
        _matchesByPage.Clear();
        _currentMatch = null;
        _document?.Dispose();
        _document = null;
        _layout = null;
        _panel.Layout = null;
        _currentPage = -1;
        _pendingInitialView = null;
        _restoreTarget = null;
        _restoreTimeout.Stop();
        _history.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<CompositionDrawingSurface> DrainThumbnails()
    {
        var surfaces = _thumbnails.Values;
        _thumbnails.Clear();
        return surfaces;
    }

    private void OnViewportSizeChanged(double previousWidth)
    {
        if (_pendingInitialView is not null) ApplyInitialView();
        else if (_layout is not null && previousWidth > 0 && _scroll.ActualWidth > 0)
        {
            // A document shown at "fit width" stays at fit width when the window, sidebar or full screen changes.
            double previousFit = Math.Clamp(previousWidth / _layout.ContentWidth, MinZoom, MaxZoom);
            if (Math.Abs(_scroll.ZoomFactor - previousFit) < 0.005 && Math.Abs(FitWidthZoom() - previousFit) > 0.001)
                FitWidth();
        }
        QueueUpdate();
    }

    private void ApplyInitialView()
    {
        if (_layout is null || _pendingInitialView is not { } view || _scroll.ActualWidth <= 0) return;
        _pendingInitialView = null;
        RestoreView(view);
    }

    /// <summary>The current reading position, or null while there is none yet (no document, or restoring).</summary>
    public ViewState? CaptureViewState()
    {
        if (_layout is null || IsRestoringView || _scroll.ViewportHeight <= 0) return null;
        double zoom = _scroll.ZoomFactor;
        var (page, fraction) = _layout.ToPagePosition(_scroll.VerticalOffset / zoom);
        return new ViewState(page, Math.Round(fraction, 5), Math.Round(zoom, 4), Math.Round(_scroll.HorizontalOffset / zoom, 1));
    }

    /// <summary>Moves to a saved position. A zoom of 0 means "fit width" (capped at 200 %).</summary>
    public void RestoreView(ViewState view)
    {
        if (_layout is null) return;
        double zoom = view.Zoom > 0 ? Math.Clamp(view.Zoom, MinZoom, MaxZoom) : Math.Min(FitWidthZoom(), 2.0);
        double y = _layout.FromPagePosition(view.Page, view.OffsetInPage);
        _restoreTarget = (zoom, Math.Max(0, view.HorizontalOffset * zoom), Math.Max(0, y * zoom));
        _restoreAttempts = 0;
        _restoreTimeout.Stop();
        _restoreTimeout.Start();
        if (Math.Abs(_scroll.ZoomFactor - zoom) > 1e-4)
        {
            _renderZoom = zoom;
            _scroll.ZoomTo((float)zoom, null, new ScrollingZoomOptions(ScrollingAnimationMode.Disabled));
        }
        IssueRestoreScroll();
        CheckRestore();
        QueueUpdate();
    }

    private void IssueRestoreScroll()
    {
        if (_restoreTarget is not { } target) return;
        _restoreAttempts++;
        _scroll.ScrollTo(target.X, target.Y, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }

    private void CheckRestore()
    {
        if (_restoreTarget is not { } target || Math.Abs(_scroll.ZoomFactor - target.Zoom) > 1e-3) return;
        double wantX = Math.Min(target.X, _scroll.ScrollableWidth), wantY = Math.Min(target.Y, _scroll.ScrollableHeight);
        if (Math.Abs(_scroll.VerticalOffset - wantY) <= 1 && Math.Abs(_scroll.HorizontalOffset - wantX) <= 1) FinishRestore();
        else if (_restoreAttempts < 4) IssueRestoreScroll();
        else FinishRestore();
    }

    private void FinishRestore()
    {
        _restoreTimeout.Stop();
        if (_restoreTarget is null) return;
        _restoreTarget = null;
        ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------------- navigation & zoom

    public void GoToPage(int pageIndex, bool animate = false)
    {
        if (_layout is null || pageIndex < 0 || pageIndex >= _layout.PageCount) return;
        RecordJump(new ViewState(pageIndex));
        double zoom = _scroll.ZoomFactor;
        _scroll.ScrollTo(_scroll.HorizontalOffset, Math.Max(0, (_layout.GetPageRect(pageIndex).Y - 8) * zoom),
            new ScrollingScrollOptions(animate ? ScrollingAnimationMode.Auto : ScrollingAnimationMode.Disabled));
    }

    /// <summary>Scrolls so a page-space rectangle is centered in the viewport.</summary>
    public void RevealRect(int pageIndex, PageRect rect)
    {
        if (_layout is null) return;
        var page = _layout.GetPageRect(pageIndex);
        RecordJump(new ViewState(pageIndex, rect.Top * DocumentLayout.PointsToDip / page.Height));
        double zoom = _scroll.ZoomFactor;
        double cx = (page.X + (rect.Left + rect.Right) / 2 * DocumentLayout.PointsToDip) * zoom;
        double cy = (page.Y + (rect.Top + rect.Bottom) / 2 * DocumentLayout.PointsToDip) * zoom;
        _scroll.ScrollTo(Math.Max(0, cx - _scroll.ViewportWidth / 2), Math.Max(0, cy - _scroll.ViewportHeight / 2),
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }

    /// <summary>Remembers where we are before a jump of more than a page, for Back.</summary>
    private void RecordJump(ViewState destination)
    {
        if (CaptureViewState() is not { } current || !current.IsFarFrom(destination)) return;
        _history.Push(current);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoBack() => Navigate(_history.GoBack);
    public void GoForward() => Navigate(_history.GoForward);

    private void Navigate(Func<ViewState, ViewState?> move)
    {
        if (CaptureViewState() is not { } current || move(current) is not { } target) return;
        RestoreView(target);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public double FitWidthZoom() =>
        _layout is null || _scroll.ViewportWidth <= 0 ? 1 : Math.Clamp(_scroll.ViewportWidth / _layout.ContentWidth, MinZoom, MaxZoom);

    public double FitPageZoom()
    {
        if (_layout is null || _scroll.ViewportHeight <= 0) return 1;
        var page = _layout.GetPageRect(Math.Max(0, _currentPage));
        return Math.Clamp(Math.Min(FitWidthZoom(), _scroll.ViewportHeight / (page.Height + 24)), MinZoom, MaxZoom);
    }

    public void SetZoom(double zoom, bool animate = true)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var center = new Vector2((float)(_scroll.ViewportWidth / 2), (float)(_scroll.ViewportHeight / 2));
        _scroll.ZoomTo((float)zoom, center, new ScrollingZoomOptions(animate ? ScrollingAnimationMode.Auto : ScrollingAnimationMode.Disabled));
    }

    public void ZoomIn() => SetZoom(_scroll.ZoomFactor * 1.25);
    public void ZoomOut() => SetZoom(_scroll.ZoomFactor / 1.25);

    public void FitWidth()
    {
        if (CaptureViewState() is { } view) RestoreView(view with { Zoom = FitWidthZoom(), HorizontalOffset = 0 });
    }

    public void FitPage()
    {
        // Show the whole current page, from its top.
        if (_currentPage >= 0) RestoreView(new ViewState(_currentPage, -8 / _layout!.GetPageRect(_currentPage).Height, FitPageZoom()));
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_layout is null) return;
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        double page = _scroll.ViewportHeight * 0.9;
        const double line = 64;
        var animated = new ScrollingScrollOptions(ScrollingAnimationMode.Auto);
        switch (e.Key)
        {
            case VirtualKey.PageDown:
            case VirtualKey.Space: _scroll.ScrollBy(0, page, animated); break;
            case VirtualKey.PageUp: _scroll.ScrollBy(0, -page, animated); break;
            case VirtualKey.Down: _scroll.ScrollBy(0, line, animated); break;
            case VirtualKey.Up: _scroll.ScrollBy(0, -line, animated); break;
            case VirtualKey.Right when !ctrl: _scroll.ScrollBy(line, 0, animated); break;
            case VirtualKey.Left when !ctrl: _scroll.ScrollBy(-line, 0, animated); break;
            case VirtualKey.Home: GoToPage(0); break;
            case VirtualKey.End: GoToPage(_layout.PageCount - 1); break;
            case VirtualKey.C when ctrl: _ = CopySelectionAsync(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ----------------------------------------------------------------- view updates

    private void OnViewChanged()
    {
        double zoom = _scroll.ZoomFactor;
        if (Math.Abs(zoom - _renderZoom) > 1e-4)
        {
            // Zooming: the compositor scales the existing bitmaps; re-render only once the zoom settles.
            _zoomSettling = true;
            _zoomSettleTimer.Stop();
            _zoomSettleTimer.Start();
        }
        if (Math.Abs(zoom - _reportedZoom) > 1e-4)
        {
            _reportedZoom = zoom;
            ZoomChanged?.Invoke(this, zoom);
        }
        UpdateView();
        if (_restoreTarget is not null) CheckRestore();
        else if (_pendingInitialView is null) ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnZoomSettled()
    {
        _zoomSettling = false;
        _renderZoom = _scroll.ZoomFactor;
        UpdateView();
    }

    private void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { _updateQueued = false; UpdateView(); });
    }

    private void InvalidateRendering()
    {
        _renderGeneration++;
        foreach (var cts in _inflight.Values) cts.Cancel();
        _inflight.Clear();
        _thumbnailTasks.Clear();
        foreach (var surface in DrainThumbnails()) surface.Dispose();
        ThumbnailsInvalidated?.Invoke(this, EventArgs.Empty);
        foreach (var page in _realized.Values.ToList())
        {
            int index = page.PageIndex;
            var bounds = page.Bounds;
            page.Assign(index, bounds);
            RefreshHighlights(page);
        }
        QueueUpdate();
    }

    private LayoutRect VisibleContentRect()
    {
        double zoom = _scroll.ZoomFactor;
        double width = _scroll.ViewportWidth / zoom, height = _scroll.ViewportHeight / zoom;
        double x = _scroll.HorizontalOffset / zoom, y = _scroll.VerticalOffset / zoom;
        if (_layout!.ContentWidth * zoom <= _scroll.ViewportWidth)
        {
            x = 0;
            width = _layout.ContentWidth;
        }
        return new LayoutRect(x, y, width, height);
    }

    /// <summary>
    /// Heart of the viewer: realizes/recycles pages around the viewport and reconciles the set of
    /// render requests with what is needed right now (cancelling everything else).
    /// </summary>
    private void UpdateView()
    {
        if (_layout is null || _document is null || _scheduler is null || _scroll.ViewportHeight <= 0) return;

        var visible = VisibleContentRect();
        double centerY = visible.Y + visible.Height / 2;

        // 1. Realize pages within one viewport above and below; recycle the rest.
        var (first, last) = _layout.GetPagesInRange(visible.Y - visible.Height, visible.Bottom + visible.Height);
        foreach (var page in _realized.Values.Where(p => p.PageIndex < first || p.PageIndex > last).ToList())
        {
            _realized.Remove(page.PageIndex);
            Recycle(page);
        }
        for (int i = first; i >= 0 && i <= last; i++)
        {
            if (!_realized.ContainsKey(i)) Realize(i);
        }

        // 2. Current page = the one under the upper third of the viewport.
        int current = _layout.GetPageAt(visible.Y + visible.Height / 3);
        if (current != _currentPage)
        {
            _currentPage = current;
            CurrentPageChanged?.Invoke(this, current);
        }

        // 3. Work out what should be rendering.
        var wanted = new Dictionary<RequestKey, (double Priority, double Scale, Tile Tile)>();
        foreach (var page in _realized.Values)
        {
            if (page.HasThumbnail) continue;
            if (_thumbnails.TryGet(page.PageIndex, out var thumb)) { page.SetThumbnail(thumb); continue; }
            bool isVisible = page.Bounds.Intersects(visible);
            wanted[new RequestKey(page.PageIndex, RequestKey.ThumbnailScale, 0, 0)] =
                ((isVisible ? 0 : 2) + Distance(page.Bounds, centerY), 0, default);
        }

        if (!_zoomSettling)
        {
            double rasterScale = XamlRoot?.RasterizationScale ?? 1;
            double scale = DocumentLayout.PointsToDip * _renderZoom * rasterScale; // pixels per point
            int scaleKey = (int)Math.Round(scale * 1000);
            var sharpZone = visible.Inflate(visible.Width * 0.1, visible.Height * 0.5);

            foreach (var page in _realized.Values)
            {
                if (!page.Bounds.Intersects(sharpZone)) continue;
                var size = _document.GetPageSize(page.PageIndex);
                var (pw, ph) = TilePlanner.ScaledSize(size.Width, size.Height, scale);
                page.BeginLayer(scaleKey);
                double priority = (page.Bounds.Intersects(visible) ? 1 : 3) + Distance(page.Bounds, centerY);

                IReadOnlyList<Tile> tiles;
                if (!TilePlanner.NeedsTiling(pw, ph))
                {
                    tiles = [new Tile(-1, -1, 0, 0, pw, ph)];
                }
                else
                {
                    // Only the tiles under the (slightly enlarged) viewport.
                    var zone = visible.Inflate(visible.Width * 0.15, visible.Height * 0.15);
                    double k = scale / DocumentLayout.PointsToDip; // pixels per DIP
                    tiles = TilePlanner.VisibleTiles(pw, ph,
                        (zone.X - page.Bounds.X) * k, (zone.Y - page.Bounds.Y) * k, zone.Width * k, zone.Height * k,
                        tileSize: 1024);
                }

                bool complete = true;
                for (int t = 0; t < tiles.Count; t++)
                {
                    var tile = tiles[t];
                    if (page.HasTile(scaleKey, tile.Column, tile.Row)) continue;
                    complete = false;
                    wanted[new RequestKey(page.PageIndex, scaleKey, tile.Column, tile.Row)] = (priority + t * 1e-3, scale, tile);
                }
                if (complete) page.DiscardPreviousLayer();
            }
        }

        // 4. Reconcile: cancel stale requests, start new ones.
        foreach (var (key, cts) in _inflight.Where(kv => !wanted.ContainsKey(kv.Key)).ToList())
        {
            cts.Cancel();
            _inflight.Remove(key);
        }
        foreach (var (key, request) in wanted)
        {
            if (_inflight.ContainsKey(key)) continue;
            var cts = new CancellationTokenSource();
            _inflight[key] = cts;
            if (key.ScaleKey == RequestKey.ThumbnailScale) _ = RenderThumbnailForViewAsync(key, request.Priority, cts);
            else _ = RenderTileAsync(key, request.Priority, request.Scale, request.Tile, cts);
        }
    }

    private static double Distance(LayoutRect bounds, double centerY) =>
        Math.Min(0.99, Math.Abs(bounds.Y + bounds.Height / 2 - centerY) / 1e6);

    private void Realize(int pageIndex)
    {
        var page = _pool.Count > 0 ? _pool.Pop() : CreatePageView();
        page.Assign(pageIndex, _layout!.GetPageRect(pageIndex));
        _realized[pageIndex] = page;
        RefreshHighlights(page);
        _panel.InvalidateMeasure();
    }

    private PageView CreatePageView()
    {
        var page = new PageView(_compositor) { NightMode = _nightMode };
        _panel.Children.Add(page);
        return page;
    }

    private void Recycle(PageView page)
    {
        page.Reset();
        _pool.Push(page);
    }

    private async Task RenderTileAsync(RequestKey key, double priority, double scale, Tile tile, CancellationTokenSource cts)
    {
        var document = _document!;
        var surfaces = _surfaces;
        bool invert = _nightMode;
        int generation = _renderGeneration;
        try
        {
            var surface = await _scheduler!.Schedule(
                () => surfaces.CreateSurface(document.Render(key.Page, scale, tile.X, tile.Y, tile.Width, tile.Height), invert),
                priority, cts.Token);

            if (_inflight.TryGetValue(key, out var current) && current == cts) _inflight.Remove(key);
            if (document == _document && generation == _renderGeneration && _realized.TryGetValue(key.Page, out var page) && page.AddTile(key.ScaleKey, tile, scale, surface))
                QueueUpdate(); // may complete the layer, allowing the previous zoom level to be dropped
            else
                surface.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Render failed for page {key.Page}: {ex}");
            if (_inflight.TryGetValue(key, out var current) && current == cts) _inflight.Remove(key);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RenderThumbnailForViewAsync(RequestKey key, double priority, CancellationTokenSource cts)
    {
        try
        {
            await LoadThumbnailAsync(key.Page, priority, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"Thumbnail failed for page {key.Page}: {ex}"); }
        finally
        {
            if (_inflight.TryGetValue(key, out var current) && current == cts) _inflight.Remove(key);
            cts.Dispose();
        }
    }

    // ----------------------------------------------------------------- thumbnails (shared with the sidebar)

    public double GetPageAspectRatio(int pageIndex)
    {
        var size = _document?.GetPageSize(pageIndex) ?? new PageSize(612, 792);
        return size.Height / size.Width;
    }

    /// <summary>Returns a page thumbnail, rendering it at low priority if needed. Null if the document changed.</summary>
    public async Task<CompositionDrawingSurface?> GetThumbnailAsync(int pageIndex, CancellationToken token)
    {
        var document = _document;
        for (int attempt = 0; attempt < 4 && document is not null && document == _document; attempt++)
        {
            if (_thumbnails.TryGet(pageIndex, out var cached)) return cached;
            try
            {
                return await LoadThumbnailAsync(pageIndex, 5 + pageIndex * 1e-6, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // The shared render was cancelled by the main view; ask again with our own token.
            }
        }
        return null;
    }

    private Task<CompositionDrawingSurface> LoadThumbnailAsync(int pageIndex, double priority, CancellationToken token)
    {
        if (_thumbnails.TryGet(pageIndex, out var cached)) return Task.FromResult(cached);
        if (_thumbnailTasks.TryGetValue(pageIndex, out var pending) && !pending.IsCanceled) return pending;

        var document = _document!;
        var surfaces = _surfaces;
        var size = document.GetPageSize(pageIndex);
        double scale = ThumbnailWidthPx / size.Width;
        var (w, h) = TilePlanner.ScaledSize(size.Width, size.Height, scale);
        bool invert = _nightMode;
        var task = _scheduler!.Schedule(() => surfaces.CreateSurface(document.Render(pageIndex, scale, 0, 0, w, h), invert), priority, token);
        _thumbnailTasks[pageIndex] = task;
        _ = CompleteThumbnailAsync(pageIndex, document, _renderGeneration, task, (long)w * h * 4);
        return task;
    }

    private async Task CompleteThumbnailAsync(int pageIndex, IPdfDocument document, int generation, Task<CompositionDrawingSurface> task, long bytes)
    {
        try
        {
            var surface = await task;
            if (document != _document || generation != _renderGeneration) { surface.Dispose(); return; }
            // Evicted thumbnails are not disposed explicitly: a sidebar item may still show one; GC reclaims them.
            _thumbnails.Add(pageIndex, surface, bytes);
            if (_realized.TryGetValue(pageIndex, out var page) && !page.HasThumbnail) page.SetThumbnail(surface);
            ThumbnailReady?.Invoke(this, pageIndex);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { Debug.WriteLine($"Thumbnail failed for page {pageIndex}: {ex}"); }
        finally
        {
            if (_thumbnailTasks.TryGetValue(pageIndex, out var current) && current == task) _thumbnailTasks.Remove(pageIndex);
        }
    }

    // ----------------------------------------------------------------- text selection, links, copy

    private (int Page, double X, double Y) ToPagePoint(Windows.Foundation.Point contentPoint, bool clampToNearest)
    {
        int index = _layout!.GetPageAt(contentPoint.Y);
        var rect = _layout.GetPageRect(index);
        if (!clampToNearest && !(contentPoint.X >= rect.X && contentPoint.X <= rect.Right && contentPoint.Y >= rect.Y && contentPoint.Y <= rect.Bottom))
            return (-1, 0, 0);
        double x = Math.Clamp(contentPoint.X - rect.X, 0, rect.Width) / DocumentLayout.PointsToDip;
        double y = Math.Clamp(contentPoint.Y - rect.Y, 0, rect.Height) / DocumentLayout.PointsToDip;
        return (index, x, y);
    }

    private void OnPanelPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        // Touch pans and zooms; mouse and pen select text.
        if (_layout is null || e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;
        var point = e.GetCurrentPoint(_panel);
        if (point.Properties.IsXButton1Pressed || point.Properties.IsXButton2Pressed)
        {
            // Mouse back/forward buttons.
            if (point.Properties.IsXButton1Pressed) GoBack();
            else GoForward();
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        _pointerPressed = true;
        _dragging = false;
        _pressedPointerId = e.Pointer.PointerId;
        _pressPoint = point.Position;
        _panel.CapturePointer(e.Pointer);
        e.Handled = true;

        var (page, x, y) = ToPagePoint(point.Position, clampToNearest: false);
        ClearSelection();
        if (page >= 0) _ = UpdateSelectionEndAsync(page, x, y, isAnchor: true);
    }

    private void OnPanelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerPressed || e.Pointer.PointerId != _pressedPointerId || _layout is null) return;
        var position = e.GetCurrentPoint(_panel).Position;
        if (!_dragging && Math.Abs(position.X - _pressPoint.X) + Math.Abs(position.Y - _pressPoint.Y) < 4) return;
        _dragging = true;
        var (page, x, y) = ToPagePoint(position, clampToNearest: true);
        _ = UpdateSelectionEndAsync(page, x, y, isAnchor: false);
        e.Handled = true;
    }

    private void OnPanelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerPressed || e.Pointer.PointerId != _pressedPointerId) return;
        _pointerPressed = false;
        _panel.ReleasePointerCapture(e.Pointer);
        if (!_dragging && _layout is not null)
        {
            var (page, x, y) = ToPagePoint(_pressPoint, clampToNearest: false);
            if (page >= 0) _ = FollowLinkAsync(page, x, y);
        }
    }

    /// <summary>Double tap / double click: toggles between fit width and twice that, centered on the point.</summary>
    private void OnPanelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_layout is null) return;
        e.Handled = true;
        double fit = FitWidthZoom();
        double target = Math.Abs(_scroll.ZoomFactor - fit) < 0.02 ? Math.Min(fit * 2, MaxZoom) : fit;
        var point = e.GetPosition(_scroll);
        _scroll.ZoomTo((float)target, new Vector2((float)point.X, (float)point.Y), new ScrollingZoomOptions(ScrollingAnimationMode.Auto));
    }

    /// <summary>
    /// Runs document work off the UI thread (it takes the PDFium lock, which rendering may hold).
    /// Ok is false when the document was closed in the meantime.
    /// </summary>
    private static async Task<(bool Ok, T Value)> RunOnDocumentAsync<T>(Func<T> work)
    {
        try { return (true, await Task.Run(work)); }
        catch (ObjectDisposedException) { return (false, default!); }
    }

    private async Task FollowLinkAsync(int page, double x, double y)
    {
        var document = _document;
        if (document is null) return;
        var (ok, link) = await RunOnDocumentAsync(() => document.GetLinkAt(page, x, y));
        if (!ok || link is null || document != _document) return;
        if (link.Uri is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var target) && (target.Scheme is "http" or "https" or "mailto"))
            await Launcher.LaunchUriAsync(target);
        else if (link.PageIndex >= 0)
            GoToPage(link.PageIndex);
    }

    // Hit-testing takes the PDFium lock, which the render thread may hold, so it never runs on the UI thread.
    private async Task UpdateSelectionEndAsync(int page, double x, double y, bool isAnchor)
    {
        var document = _document;
        if (document is null) return;
        int generation = ++_hitTestGeneration;
        var (ok, index) = await RunOnDocumentAsync(() => document.GetCharIndexAt(page, x, y, isAnchor ? 4 : 12));
        if (!ok || document != _document || (!isAnchor && generation != _hitTestGeneration)) return;
        if (index < 0) return;

        var position = new TextPosition(page, index);
        if (isAnchor)
        {
            _selectionAnchor = position;
            _selectionFocus = null;
        }
        else if (_selectionAnchor is not null)
        {
            _selectionFocus = position;
            await UpdateSelectionRectsAsync();
        }
    }

    private (TextPosition Start, TextPosition End)? OrderedSelection()
    {
        if (_selectionAnchor is not { } a || _selectionFocus is not { } b) return null;
        return (a.Page, a.Char).CompareTo((b.Page, b.Char)) <= 0 ? (a, b) : (b, a);
    }

    private async Task UpdateSelectionRectsAsync()
    {
        var document = _document;
        if (document is null || OrderedSelection() is not var (start, end)) return;
        int generation = ++_selectionGeneration;
        // Only pages currently realized need rectangles; others are computed when realized.
        var pages = _realized.Keys.Where(p => p >= start.Page && p <= end.Page).ToList();
        var (ok, rects) = await RunOnDocumentAsync(() => pages.ToDictionary(p => p, p => SelectionRectsForPage(document, p, start, end)));
        if (!ok || generation != _selectionGeneration || document != _document) return;

        _selectionRects.Clear();
        foreach (var (p, r) in rects) _selectionRects[p] = r;
        foreach (var page in _realized.Values) RefreshHighlights(page);
    }

    private static IReadOnlyList<PageRect> SelectionRectsForPage(IPdfDocument document, int page, TextPosition start, TextPosition end)
    {
        var (from, count) = SelectionRangeForPage(document, page, start, end);
        return count > 0 ? document.GetTextRects(page, from, count) : [];
    }

    private static (int From, int Count) SelectionRangeForPage(IPdfDocument document, int page, TextPosition start, TextPosition end)
    {
        int from = page == start.Page ? start.Char : 0;
        int to = page == end.Page ? end.Char : document.GetCharCount(page) - 1;
        return (from, to - from + 1);
    }

    public void ClearSelection()
    {
        _selectionAnchor = _selectionFocus = null;
        _selectionGeneration++;
        if (_selectionRects.Count == 0) return;
        _selectionRects.Clear();
        foreach (var page in _realized.Values) RefreshHighlights(page);
    }

    public async Task CopySelectionAsync()
    {
        var document = _document;
        if (document is null || OrderedSelection() is not var (start, end)) return;
        var (ok, text) = await RunOnDocumentAsync(() =>
        {
            var parts = new List<string>();
            for (int p = start.Page; p <= end.Page; p++)
            {
                var (from, count) = SelectionRangeForPage(document, p, start, end);
                if (count > 0) parts.Add(document.GetText(p, from, count));
            }
            return string.Join(Environment.NewLine, parts);
        });
        if (!ok || string.IsNullOrEmpty(text)) return;
        var package = new DataPackage();
        package.SetText(text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        Clipboard.SetContent(package);
    }

    private void RefreshHighlights(PageView page)
    {
        int index = page.PageIndex;
        if (index < 0) return;
        if (!_selectionRects.ContainsKey(index) && OrderedSelection() is var (start, end) && index >= start.Page && index <= end.Page)
            _ = UpdateSelectionRectsAsync();
        page.SetHighlights(
            _selectionRects.TryGetValue(index, out var sel) ? sel : [],
            _matchesByPage.TryGetValue(index, out var matches) ? matches : [],
            _currentMatch);
    }

    // ----------------------------------------------------------------- search

    public async Task SearchAsync(string query, bool matchCase = false, bool wholeWord = false)
    {
        _searchCts?.Cancel();
        _matches.Clear();
        _matchesByPage.Clear();
        _currentMatch = null;
        foreach (var page in _realized.Values) RefreshHighlights(page);
        SearchResultsChanged?.Invoke(this, EventArgs.Empty);

        var document = _document;
        if (document is null || string.IsNullOrWhiteSpace(query)) return;

        var cts = _searchCts = new CancellationTokenSource();
        int startPage = Math.Max(0, _currentPage);
        var dispatcher = DispatcherQueue;
        try
        {
            await TextSearchService.SearchAsync(document, query, startPage, matchCase, wholeWord, (page, matches) =>
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    AddMatches(page, matches);
                });
            }, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }

    private void AddMatches(int page, IReadOnlyList<SearchMatch> matches)
    {
        _matchesByPage[page] = matches.ToList();
        _matches.AddRange(matches);
        _matches.Sort((a, b) => (a.PageIndex, a.CharIndex).CompareTo((b.PageIndex, b.CharIndex)));
        if (_currentMatch is null)
        {
            // First results come from the current page onwards: jump to the first one.
            _currentMatch = matches[0];
            RevealRect(_currentMatch.PageIndex, _currentMatch.Rects.FirstOrDefault());
        }
        foreach (var view in _realized.Values) RefreshHighlights(view);
        SearchResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NextMatch() => MoveMatch(+1);
    public void PreviousMatch() => MoveMatch(-1);

    private void MoveMatch(int delta)
    {
        if (_matches.Count == 0) return;
        int index = _currentMatch is null ? 0 : (_matches.IndexOf(_currentMatch) + delta + _matches.Count) % _matches.Count;
        _currentMatch = _matches[index];
        RevealRect(_currentMatch.PageIndex, _currentMatch.Rects.FirstOrDefault());
        foreach (var view in _realized.Values) RefreshHighlights(view);
        SearchResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSearch() => _ = SearchAsync(string.Empty);

    // ----------------------------------------------------------------- benchmark (--bench)

    /// <summary>
    /// Scrolls continuously through the document at a fixed speed, first at the current zoom and then at a
    /// zoom that forces tiling, and reports UI-thread frame intervals. Long frames here mean the UI thread
    /// was blocked (the compositor keeps scrolling regardless, but realization/rendering would lag).
    /// </summary>
    public async Task<string> RunBenchmarkAsync(double pixelsPerFrame = 40, int framesPerPhase = 600)
    {
        var report = new System.Text.StringBuilder();
        foreach (double zoom in new[] { FitWidthZoom(), 3.0 })
        {
            SetZoom(zoom, animate: false);
            GoToPage(0);
            await Task.Delay(800);

            var intervals = new List<double>(framesPerPhase);
            var clock = Stopwatch.StartNew();
            double last = 0;
            int frames = 0;
            var done = new TaskCompletionSource();
            void OnRendering(object? s, object e)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (frames > 0) intervals.Add(now - last);
                last = now;
                _scroll.ScrollBy(0, pixelsPerFrame, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
                if (++frames >= framesPerPhase) done.TrySetResult();
            }
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
            await done.Task;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;

            intervals.Sort();
            double P(double q) => intervals[(int)Math.Min(intervals.Count - 1, q * intervals.Count)];
            report.AppendLine(FormattableString.Invariant($"zoom {zoom:P0}: {frames} frames, reached page {_currentPage + 1}, frame p50 {P(0.5):F1} ms, p95 {P(0.95):F1} ms, p99 {P(0.99):F1} ms, max {intervals[^1]:F1} ms, >33 ms: {intervals.Count(i => i > 33.4)}, pending {_scheduler?.PendingCount}"));
        }
        return report.ToString();
    }
}
