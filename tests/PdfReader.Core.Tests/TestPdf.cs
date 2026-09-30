using System.Text;

namespace PdfReader.Core.Tests;

/// <summary>
/// Writes small, valid PDFs for tests: one line of Helvetica text per page, an outline and an internal link.
/// <paramref name="firstPageAnnotation"/> is the dictionary of an extra annotation on the first page
/// (as another program would have written it).
/// </summary>
internal static class TestPdf
{
    public static string Create(IReadOnlyList<string> pageTexts, double width = 612, double height = 792, int rotate = 0,
        string? firstPageAnnotation = null)
    {
        int n = pageTexts.Count;
        // Object numbers: 1 catalog, 2 pages, 3 font, 4 outlines, then per page: page, content, outline item.
        int PageObj(int i) => 5 + i * 3;
        int ContentObj(int i) => 6 + i * 3;
        int OutlineObj(int i) => 7 + i * 3;
        int linkObj = 5 + n * 3;
        int extraObj = linkObj + 1;

        var objects = new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            [2] = $"<< /Type /Pages /Count {n} /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{PageObj(i)} 0 R"))}] >>",
            [3] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            [4] = $"<< /Type /Outlines /First {OutlineObj(0)} 0 R /Last {OutlineObj(n - 1)} 0 R /Count {n} >>",
        };
        for (int i = 0; i < n; i++)
        {
            var pageAnnots = new List<string>();
            if (i == 0 && n > 1) pageAnnots.Add($"{linkObj} 0 R");
            if (i == 0 && firstPageAnnotation is not null) pageAnnots.Add($"{extraObj} 0 R");
            string annots = pageAnnots.Count > 0 ? $" /Annots [{string.Join(" ", pageAnnots)}]" : "";
            objects[PageObj(i)] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Rotate {rotate} " +
                                  $"/Resources << /Font << /F1 3 0 R >> >> /Contents {ContentObj(i)} 0 R{annots} >>";
            string stream = $"BT /F1 24 Tf 72 {height - 100} Td ({pageTexts[i]}) Tj ET";
            objects[ContentObj(i)] = $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream";
            string prev = i > 0 ? $" /Prev {OutlineObj(i - 1)} 0 R" : "";
            string next = i < n - 1 ? $" /Next {OutlineObj(i + 1)} 0 R" : "";
            objects[OutlineObj(i)] = $"<< /Title (Chapter {i + 1}) /Parent 4 0 R{prev}{next} /Dest [{PageObj(i)} 0 R /Fit] >>";
        }
        if (n > 1)
            objects[linkObj] = $"<< /Type /Annot /Subtype /Link /Rect [0 0 100 100] /Border [0 0 0] /Dest [{PageObj(n - 1)} 0 R /Fit] >>";
        else if (firstPageAnnotation is not null)
            objects[linkObj] = "<< >>"; // keeps object numbers contiguous
        if (firstPageAnnotation is not null) objects[extraObj] = firstPageAnnotation;

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (num, body) in objects)
        {
            offsets[num] = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"{num} 0 obj\n{body}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(sb.ToString());
        int size = objects.Keys.Max() + 1;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        string path = Path.Combine(Path.GetTempPath(), $"pdfreader-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }
}
