using System.Runtime.InteropServices;
using PdfReader.Core.Engine;
using PdfReader.Core.Engine.Pdfium;
using PdfReader.Core.Layout;
using PdfReader.Core.Rendering;
using PdfReader.Core.State;
using PdfReader.Core.Text;

namespace PdfReader.Core.Tests;

/// <summary>Edge cases and failure modes of the engine and the rendering/state helpers.</summary>
public sealed partial class RobustnessTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private string Pdf(params string[] pages)
    {
        var path = TestPdf.Create(pages);
        _files.Add(path);
        return path;
    }

    // ------------------------------------------------------------------ engine

    [Theory]
    [InlineData("Cañón y acción")]
    [InlineData("日本語")]
    [InlineData("emoji 😀")]
    public void Opens_files_with_non_ascii_paths(string folderName)
    {
        var dir = Directory.CreateTempSubdirectory("pdfreader-").FullName;
        var path = Path.Combine(dir, folderName, "documento ñ.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Pdf("Hello"), path);
        try
        {
            using var doc = PdfiumDocument.Open(path);
            Assert.Equal(1, doc.PageCount);
            Assert.Equal(path, doc.FilePath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_throws_io_exception()
    {
        var ex = Assert.ThrowsAny<IOException>(() => PdfiumDocument.Open(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf")));
        Assert.IsType<PdfOpenException>(ex);
        Assert.Equal(PdfOpenError.FileNotFound, ((PdfOpenException)ex).Error);
    }

    [Fact]
    public void Garbage_file_reports_invalid_format()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfreader-bad-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "%PDF-1.7 this is not really a pdf");
        _files.Add(path);
        var ex = Assert.Throws<PdfOpenException>(() => PdfiumDocument.Open(path));
        Assert.Equal(PdfOpenError.InvalidFormat, ex.Error);
    }

    [Fact]
    public void Every_call_after_dispose_throws_object_disposed()
    {
        var doc = PdfiumDocument.Open(Pdf("Hello world", "Second"));
        doc.Dispose();
        doc.Dispose(); // idempotent
        Assert.Throws<ObjectDisposedException>(() => doc.Render(0, 1, 0, 0, 10, 10));
        Assert.Throws<ObjectDisposedException>(() => doc.GetText(0, 0, 1));
        Assert.Throws<ObjectDisposedException>(() => doc.GetCharCount(0));
        Assert.Throws<ObjectDisposedException>(() => doc.GetLinkAt(0, 1, 1));
        Assert.Throws<ObjectDisposedException>(() => doc.Search(0, "Hello", false, false));
        Assert.Throws<ObjectDisposedException>(() => doc.GetOutline());
        Assert.Equal(2, doc.PageCount); // cached metadata stays readable
    }

    [Fact]
    public void Out_of_range_pages_are_rejected()
    {
        using var doc = PdfiumDocument.Open(Pdf("One"));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Render(1, 1, 0, 0, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetCharCount(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Render(0, 1, 0, 0, 0, 10));
    }

    [Fact]
    public async Task Concurrent_renders_are_serialized_and_identical()
    {
        using var doc = PdfiumDocument.Open(Pdf("Parallel rendering", "Page two", "Page three"));
        var reference = doc.Render(0, 1, 0, 0, 612, 792).Pixels;
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            doc.GetText(i % 3, 0, 4); // interleave other API calls that share the page cache
            return doc.Render(0, 1, 0, 0, 612, 792).Pixels;
        })));
        Assert.All(results, pixels => Assert.Equal(reference, pixels));
    }

    [Theory]
    [InlineData(256, 1.5)] // tile edges cross the text vertically
    [InlineData(128, 1.5)] // ... and horizontally
    [InlineData(256, 3)]
    [InlineData(256, 4)]
    [InlineData(512, 6)]   // high zoom: glyphs far wider than the edge margin
    [InlineData(1024, 10)]
    public void Tiles_reassemble_the_full_page(int tileSize, double scale)
    {
        using var doc = PdfiumDocument.Open(Pdf("Tiles must line up exactly"));
        var (w, h) = TilePlanner.ScaledSize(612, 792, scale);
        var full = doc.Render(0, scale, 0, 0, w, h).Pixels;
        var assembled = new byte[full.Length];
        foreach (var tile in TilePlanner.VisibleTiles(w, h, 0, 0, w, h, tileSize))
        {
            var part = doc.Render(0, scale, tile.X, tile.Y, tile.Width, tile.Height).Pixels;
            for (int row = 0; row < tile.Height; row++)
                Array.Copy(part, row * tile.Width * 4, assembled, ((tile.Y + row) * w + tile.X) * 4, tile.Width * 4);
        }
        // Anti-aliasing may differ by a level at tile edges; nothing more.
        var diffs = new List<(int X, int Y, int D)>();
        for (int i = 0; i < full.Length; i++)
            if (Math.Abs(full[i] - assembled[i]) > 2) diffs.Add(((i / 4) % w, (i / 4) / w, Math.Abs(full[i] - assembled[i])));
        Assert.True(diffs.Count == 0, $"{diffs.Count} bytes differ; e.g. " + string.Join(" ", diffs.Take(12)) +
            $" | xs: {string.Join(",", diffs.Select(d => d.X).Distinct().Order().Take(20))} | ys: {string.Join(",", diffs.Select(d => d.Y).Distinct().Order().Take(20))}");
    }

    [Fact]
    public void Renders_onto_a_gdi_device_context()
    {
        using var doc = PdfiumDocument.Open(Pdf("Printed text"));
        const int w = 306, h = 396;
        var dc = CreateCompatibleDC(0);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
        try
        {
            SelectObject(dc, bitmap);
            PatBlt(dc, 0, 0, w, h, WHITENESS);
            var place = PrintLayout.Fit(612, 792, w, h);
            doc.RenderToDC(0, dc, place.X, place.Y, place.Width, place.Height, place.Rotate);
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            int dark = 0;
            for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] < 128) dark++;
            Assert.InRange(dark, 20, w * h / 10); // some text, not a black page
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [Fact]
    public void Whole_word_and_case_options_filter_matches()
    {
        using var doc = PdfiumDocument.Open(Pdf("Reader reader readers"));
        Assert.Equal(3, doc.Search(0, "reader", matchCase: false, wholeWord: false).Count);
        Assert.Equal(2, doc.Search(0, "reader", matchCase: true, wholeWord: false).Count);
        Assert.Equal(2, doc.Search(0, "reader", matchCase: false, wholeWord: true).Count);
        Assert.Empty(doc.Search(0, "absent", false, false));
        var match = doc.Search(0, "readers", false, false).Single();
        Assert.NotEmpty(match.Rects);
        Assert.All(match.Rects, r => Assert.True(r.Width > 0 && r.Height > 0));
    }

    // ------------------------------------------------------------------ search service

    [Fact]
    public async Task Search_starts_at_the_current_page_and_wraps_around()
    {
        using var doc = PdfiumDocument.Open(Pdf("needle", "hay", "needle", "needle"));
        var pages = new List<int>();
        int total = await TextSearchService.SearchAsync(doc, "needle", 2, false, false, (p, _) => pages.Add(p), CancellationToken.None);
        Assert.Equal(3, total);
        Assert.Equal([2, 3, 0], pages);
    }

    [Fact]
    public async Task Search_can_be_cancelled()
    {
        using var doc = PdfiumDocument.Open(Pdf(Enumerable.Repeat("needle", 50).ToArray()));
        using var cts = new CancellationTokenSource();
        int seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TextSearchService.SearchAsync(doc, "needle", 0, false, false, (_, _) => { if (++seen == 3) cts.Cancel(); }, cts.Token));
        Assert.InRange(seen, 3, 4);
    }

    // ------------------------------------------------------------------ scheduler & cache

    [Fact]
    public async Task Scheduler_propagates_exceptions_and_runs_off_the_caller_thread()
    {
        using var scheduler = new RenderScheduler("test");
        int caller = Environment.CurrentManagedThreadId;
        int worker = await scheduler.Schedule(() => Environment.CurrentManagedThreadId, 0);
        Assert.NotEqual(caller, worker);
        await Assert.ThrowsAsync<InvalidDataException>(() => scheduler.Schedule<int>(() => throw new InvalidDataException(), 0));
        Assert.Equal(7, await scheduler.Schedule(() => 7, 0)); // still alive after a failed job
    }

    [Fact]
    public async Task Scheduler_cancels_pre_cancelled_and_pending_jobs_on_dispose()
    {
        var scheduler = new RenderScheduler("test");
        var cancelled = scheduler.Schedule(() => 1, 0, new CancellationToken(canceled: true));
        Assert.True(cancelled.IsCanceled);

        var started = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        var running = scheduler.Schedule(() => { started.Set(); gate.Wait(TimeSpan.FromSeconds(5)); return 1; }, 0);
        var pending = scheduler.Schedule(() => 2, 1);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var disposing = Task.Run(scheduler.Dispose);
        // Let the running job finish only once shutdown has been requested, so "pending" can never run.
        Assert.True(SpinWait.SpinUntil(() => scheduler.IsShuttingDown, TimeSpan.FromSeconds(5)));
        gate.Set();
        await disposing;
        Assert.Equal(1, await running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Scheduler_survives_a_job_that_outlives_dispose()
    {
        // A render still running when the timeout expires must not crash the render thread afterwards.
        var scheduler = new RenderScheduler("test") { ShutdownTimeout = TimeSpan.FromMilliseconds(50) };
        var started = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        var running = scheduler.Schedule(() => { started.Set(); gate.Wait(TimeSpan.FromSeconds(5)); return 1; }, 0);
        var pending = scheduler.Schedule(() => 2, 1);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        scheduler.Dispose(); // returns while the job is still running
        gate.Set();
        Assert.Equal(1, await running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Lru_cache_replaces_keys_and_rejects_oversized_entries()
    {
        var evicted = new List<(string, int)>();
        var cache = new LruCache<string, int>(100, (k, v) => evicted.Add((k, v)));
        cache.Add("a", 1, 30);
        cache.Add("a", 2, 50); // replacing releases the old value
        Assert.Equal([("a", 1)], evicted);
        Assert.Equal(50, cache.TotalCost);
        Assert.True(cache.TryGet("a", out var value) && value == 2);

        cache.Add("huge", 3, 500); // larger than the whole budget: evicted straight away
        Assert.False(cache.Contains("huge"));
        Assert.Equal(0, cache.TotalCost);

        cache.Add("x", 4, 10);
        cache.Add("y", 5, 10);
        Assert.Equal([5, 4], cache.Values); // most recent first
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TotalCost);
    }

    // ------------------------------------------------------------------ layout & tiles

    [Fact]
    public void Empty_document_layout_is_safe()
    {
        var layout = new DocumentLayout([]);
        Assert.Equal(-1, layout.GetPageAt(100));
        Assert.Equal((-1, -1), layout.GetPagesInRange(0, 1000));
        Assert.Equal((0, 0.0), layout.ToPagePosition(50));
        Assert.Equal(0, layout.FromPagePosition(3, 0.5));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1000, 1000)]
    [InlineData(1023, 4097)]
    [InlineData(5000, 3000)]
    public void Tiles_cover_the_page_exactly_once(int width, int height)
    {
        var tiles = TilePlanner.VisibleTiles(width, height, 0, 0, width, height, tileSize: 512);
        Assert.Equal((long)width * height, tiles.Sum(t => (long)t.Width * t.Height));
        Assert.All(tiles, t => Assert.True(t.X + t.Width <= width && t.Y + t.Height <= height && t.Width > 0 && t.Height > 0));
        Assert.Equal(tiles.Count, tiles.Select(t => (t.Column, t.Row)).Distinct().Count());
    }

    [Fact]
    public void Tiling_threshold()
    {
        Assert.False(TilePlanner.NeedsTiling(2048, 4096)); // exactly 8 MP
        Assert.True(TilePlanner.NeedsTiling(2048, 4097));
        Assert.Equal((1, 1), TilePlanner.ScaledSize(0.1, 0.1, 1)); // never zero-sized
    }

    // ------------------------------------------------------------------ state

    [Fact]
    public void Save_reports_failure_instead_of_throwing()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"pdfreader-file-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file where a directory should be");
        _files.Add(blocker);
        var store = new AppStateStore(Path.Combine(blocker, "state.json"));
        store.Touch(Pdf("x"));
        Assert.False(store.Save());
    }

    [Fact]
    public void Touch_keeps_the_latest_path_casing_for_display()
    {
        var dir = Path.GetTempPath();
        var store = new AppStateStore(Path.Combine(dir, $"state-{Guid.NewGuid():N}.json"));
        store.Touch(Path.Combine(dir, "REPORT.PDF"));
        store.Touch(Path.Combine(dir, "Report.pdf"));
        Assert.Equal("Report.pdf", Path.GetFileName(store.Recent(1).Single().Path));
    }

    // ------------------------------------------------------------------ GDI helpers for the device-context test

    private const uint WHITENESS = 0x00FF0062;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PatBlt(nint hdc, int x, int y, int w, int h, uint rop);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint hdc);
}
