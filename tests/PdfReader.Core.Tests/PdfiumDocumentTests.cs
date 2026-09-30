using PdfReader.Core.Engine.Pdfium;
using PdfReader.Core.Text;

namespace PdfReader.Core.Tests;

public sealed class PdfiumDocumentTests : IDisposable
{
    private readonly string _path = TestPdf.Create(["Hola mundo", "Segunda pagina", "Mundo final"]);

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Opens_and_reports_pages()
    {
        using var doc = PdfiumDocument.Open(_path);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(612, doc.GetPageSize(0).Width, 1);
        Assert.Equal(792, doc.GetPageSize(0).Height, 1);
    }

    [Fact]
    public void Rotated_page_swaps_size()
    {
        string path = TestPdf.Create(["Rotada"], rotate: 90);
        try
        {
            using var doc = PdfiumDocument.Open(path);
            Assert.Equal(792, doc.GetPageSize(0).Width, 1);
            Assert.Equal(612, doc.GetPageSize(0).Height, 1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Invalid_file_throws_io_exception()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "no es un pdf");
        try { Assert.Throws<IOException>(() => PdfiumDocument.Open(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Renders_region_with_text_pixels()
    {
        using var doc = PdfiumDocument.Open(_path);
        var full = doc.Render(0, 1.0, 0, 0, 612, 792);
        Assert.Equal(612 * 792 * 4, full.Pixels.Length);
        Assert.Contains(full.Pixels, b => b < 128); // some dark text pixels

        // A tile far from the text must be pure white.
        var tile = doc.Render(0, 2.0, 1000, 1400, 200, 100);
        Assert.Equal(200, tile.Width);
        Assert.All(tile.Pixels, b => Assert.Equal(255, b));
    }

    [Fact]
    public void Extracts_text_and_hit_tests_characters()
    {
        using var doc = PdfiumDocument.Open(_path);
        int count = doc.GetCharCount(0);
        Assert.Equal("Hola mundo", doc.GetText(0, 0, count).Trim());

        var rects = doc.GetTextRects(0, 0, 4);
        Assert.NotEmpty(rects);
        var r = rects[0];
        // Text baseline is 100 pt from the top; with top-left origin the glyphs sit just above y = 100.
        Assert.InRange(r.Left, 70, 76);
        Assert.InRange(r.Bottom, 90, 106);

        int index = doc.GetCharIndexAt(0, r.Left + 2, (r.Top + r.Bottom) / 2, 2);
        Assert.Equal(0, index);
    }

    [Fact]
    public async Task Searches_case_insensitively_across_document()
    {
        using var doc = PdfiumDocument.Open(_path);
        var hits = new List<int>();
        int total = await TextSearchService.SearchAsync(doc, "mundo", 1, false, false,
            (page, matches) => { lock (hits) hits.Add(page); }, CancellationToken.None);

        Assert.Equal(2, total);
        Assert.Equal([2, 0], hits); // starts at page 1 and wraps around
        var match = doc.Search(0, "mundo", false, false).Single();
        Assert.Equal(5, match.CharIndex);
        Assert.NotEmpty(match.Rects);
        Assert.Empty(doc.Search(0, "MUNDO", true, false));
    }

    [Fact]
    public void Reads_outline_and_links()
    {
        using var doc = PdfiumDocument.Open(_path);
        var outline = doc.GetOutline();
        Assert.Equal(["Capitulo 1", "Capitulo 2", "Capitulo 3"], outline.Select(o => o.Title));
        Assert.Equal([0, 1, 2], outline.Select(o => o.PageIndex));

        // Link rect is [0 0 100 100] in PDF space = bottom-left corner of the page.
        var link = doc.GetLinkAt(0, 50, 792 - 50);
        Assert.NotNull(link);
        Assert.Equal(2, link.PageIndex);
        Assert.Null(doc.GetLinkAt(0, 300, 300));
    }
}
