using PdfReader.Core.Engine;
using PdfReader.Core.Layout;
using PdfReader.Core.Rendering;

namespace PdfReader.Core.Tests;

public sealed class RenderingTests
{
    [Fact]
    public void Layout_stacks_and_centers_pages()
    {
        var layout = new DocumentLayout([new PageSize(72, 72), new PageSize(144, 72)], pageGap: 10, padding: 5);
        Assert.Equal(192 + 10, layout.ContentWidth);
        var first = layout.GetPageRect(0);
        Assert.Equal(new LayoutRect(5 + 48, 5, 96, 96), first);
        Assert.Equal(5 + 96 + 10, layout.GetPageRect(1).Y);
        Assert.Equal(5 + 96 + 10 + 96 + 5, layout.ContentHeight);
    }

    [Fact]
    public void Layout_finds_visible_pages()
    {
        var layout = new DocumentLayout(Enumerable.Repeat(new PageSize(72, 72), 100).ToList(), pageGap: 4, padding: 0);
        // Each page is 96 DIP tall with a 4 DIP gap: page i starts at 100 * i.
        Assert.Equal((0, 0), layout.GetPagesInRange(0, 50));
        Assert.Equal((2, 4), layout.GetPagesInRange(250, 410));
        Assert.Equal((-1, -1), layout.GetPagesInRange(20_000, 30_000));
        Assert.Equal(3, layout.GetPageAt(350));
        Assert.Equal(4, layout.GetPageAt(398)); // in the gap → next page
        Assert.Equal(99, layout.GetPageAt(1e9));
    }

    [Fact]
    public void Tile_planner_only_returns_visible_tiles_center_first()
    {
        var tiles = TilePlanner.VisibleTiles(2000, 3000, 600, 600, 500, 500, tileSize: 512);
        Assert.Equal(4, tiles.Count);
        Assert.All(tiles, t => Assert.InRange(t.Column, 1, 2));
        Assert.Equal((1, 1), (tiles[0].Column, tiles[0].Row)); // closest to the center of the visible region

        var edge = TilePlanner.VisibleTiles(1000, 1000, 900, 900, 500, 500, tileSize: 512).Single();
        Assert.Equal((512, 512, 488, 488), (edge.X, edge.Y, edge.Width, edge.Height));
        Assert.Empty(TilePlanner.VisibleTiles(1000, 1000, 2000, 0, 100, 100));
    }

    [Fact]
    public void Lru_cache_evicts_least_recently_used_by_cost()
    {
        var evicted = new List<string>();
        var cache = new LruCache<string, int>(100, (k, _) => evicted.Add(k));
        cache.Add("a", 1, 40);
        cache.Add("b", 2, 40);
        Assert.True(cache.TryGet("a", out _)); // "b" is now the oldest
        cache.Add("c", 3, 40);
        Assert.Equal(["b"], evicted);
        Assert.Equal(80, cache.TotalCost);

        cache.RemoveWhere(k => k == "a");
        Assert.Equal(["b", "a"], evicted);
    }

    [Fact]
    public async Task Scheduler_runs_by_priority_and_skips_cancelled_jobs()
    {
        using var scheduler = new RenderScheduler();
        var gate = new ManualResetEventSlim();
        var order = new List<int>();
        var blocker = scheduler.Schedule(() => { gate.Wait(); return 0; }, 0);

        using var cts = new CancellationTokenSource();
        var low = scheduler.Schedule(() => { order.Add(3); return 3; }, 3);
        var cancelled = scheduler.Schedule(() => { order.Add(99); return 99; }, 1, cts.Token);
        var high = scheduler.Schedule(() => { order.Add(2); return 2; }, 2);
        cts.Cancel();
        gate.Set();

        await Task.WhenAll(blocker, low, high);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal([2, 3], order);
    }
}
