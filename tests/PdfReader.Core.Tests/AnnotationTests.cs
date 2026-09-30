using PdfReader.Core.Annotations;
using PdfReader.Core.Engine;
using PdfReader.Core.Engine.Pdfium;

namespace PdfReader.Core.Tests;

/// <summary>Highlights and notes: creating, reading back, editing, undo, and saving them into the file.</summary>
public sealed class AnnotationTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            catch (IOException) { }
        }
    }

    private string Pdf(params string[] pages) => Track(TestPdf.Create(pages));

    private string Track(string path)
    {
        _files.Add(path);
        return path;
    }

    /// <summary>The rectangles of the first <paramref name="count"/> characters of a page, as a text selection gives them.</summary>
    private static IReadOnlyList<PageRect> WordRects(IPdfDocument doc, int page, int count) => doc.GetTextRects(page, 0, count);

    private static AnnotationDraft Highlight(IReadOnlyList<PageRect> areas, AnnotationColor? color = null, string contents = "") =>
        new(AnnotationKind.Highlight, areas, color ?? AnnotationColor.Yellow, contents);

    private static void AssertNear(PageRect expected, PageRect actual, double tolerance = 0.2)
    {
        Assert.InRange(actual.Left, expected.Left - tolerance, expected.Left + tolerance);
        Assert.InRange(actual.Top, expected.Top - tolerance, expected.Top + tolerance);
        Assert.InRange(actual.Right, expected.Right - tolerance, expected.Right + tolerance);
        Assert.InRange(actual.Bottom, expected.Bottom - tolerance, expected.Bottom + tolerance);
    }

    private static void AssertColor(AnnotationColor expected, AnnotationColor? actual)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.Value.R, expected.R - 2, expected.R + 2);
        Assert.InRange(actual.Value.G, expected.G - 2, expected.G + 2);
        Assert.InRange(actual.Value.B, expected.B - 2, expected.B + 2);
    }

    private static void RenderPage(IPdfDocument doc, int page) => doc.Render(page, 0.5, 0, 0, 100, 100);

    [Fact]
    public void Highlight_round_trips_areas_text_color_and_comment()
    {
        using var doc = PdfiumDocument.Open(Pdf("Hello world"));
        var rects = WordRects(doc, 0, 5); // "Hello"
        var added = doc.AddAnnotation(0, Highlight(rects, AnnotationColor.Green, "Important"));

        var read = Assert.Single(doc.GetAnnotations(0));
        Assert.Equal(AnnotationKind.Highlight, read.Kind);
        Assert.Equal(added.Name, read.Name);
        Assert.NotNull(read.Name);
        Assert.Equal("Important", read.Contents);
        Assert.Equal("Hello", read.Text);
        Assert.Equal(rects.Count, read.Areas.Count);
        for (int i = 0; i < rects.Count; i++) AssertNear(rects[i], read.Areas[i]);
        AssertColor(AnnotationColor.Green, read.Color);
        Assert.NotNull(read.Modified);
    }

    [Fact]
    public void Color_is_still_known_after_the_appearance_is_generated()
    {
        // PDFium generates the appearance stream on the first render and then refuses to report /C.
        using var doc = PdfiumDocument.Open(Pdf("Hello world"));
        doc.AddAnnotation(0, Highlight(WordRects(doc, 0, 5), AnnotationColor.Blue));
        RenderPage(doc, 0);
        AssertColor(AnnotationColor.Blue, Assert.Single(doc.GetAnnotations(0)).Color);
    }

    [Fact]
    public void Highlight_is_drawn_on_the_page()
    {
        using var doc = PdfiumDocument.Open(Pdf("Hello world"));
        var rect = WordRects(doc, 0, 5)[0];
        double scale = 1;
        int x = (int)((rect.Left + rect.Right) / 2 * scale), y = (int)(rect.Top * scale + 2);
        byte[] Pixel() => doc.Render(0, scale, x, y, 1, 1).Pixels;

        var before = Pixel();
        doc.AddAnnotation(0, Highlight([rect], AnnotationColor.Blue));
        var after = Pixel();
        // BGRA: the white background near the top of the glyphs turns blue-ish (red drops).
        Assert.Equal(255, before[2]);
        Assert.True(after[2] < 200, $"red channel {after[2]}");
    }

    [Fact]
    public void Note_is_placed_at_the_point_and_kept_inside_the_page()
    {
        using var doc = PdfiumDocument.Open(Pdf("Hello"));
        var note = doc.AddAnnotation(0, new AnnotationDraft(AnnotationKind.Note, [new PageRect(100, 200, 100, 200)], null, "Remember this"));
        AssertNear(new PageRect(100, 200, 120, 220), note.Bounds);
        Assert.Equal("Remember this", note.Contents);
        AssertColor(AnnotationColor.Yellow, note.Color);

        var corner = doc.AddAnnotation(0, new AnnotationDraft(AnnotationKind.Note, [new PageRect(610, 790, 610, 790)], null));
        AssertNear(new PageRect(592, 772, 612, 792), corner.Bounds);
        Assert.Equal(2, doc.GetAnnotations(0).Count);
    }

    [Fact]
    public void Areas_round_trip_on_rotated_pages()
    {
        var path = Track(TestPdf.Create(["Rotated text"], rotate: 90));
        using var doc = PdfiumDocument.Open(path);
        var rects = WordRects(doc, 0, 7);
        var read = doc.AddAnnotation(0, Highlight(rects));
        for (int i = 0; i < rects.Count; i++) AssertNear(rects[i], read.Areas[i]);
        Assert.Equal("Rotated", read.Text);
    }

    [Fact]
    public void Updating_the_comment_keeps_the_annotation_and_a_new_color_recreates_it()
    {
        using var doc = PdfiumDocument.Open(Pdf("Hello world"));
        var added = doc.AddAnnotation(0, Highlight(WordRects(doc, 0, 5)));
        RenderPage(doc, 0); // appearance generated

        var commented = doc.UpdateAnnotation(added, added.ToDraft() with { Contents = "Why?" });
        Assert.Equal(added.Index, commented.Index);
        Assert.Equal("Why?", Assert.Single(doc.GetAnnotations(0)).Contents);

        var recolored = doc.UpdateAnnotation(commented, commented.ToDraft() with { Color = AnnotationColor.Pink });
        var read = Assert.Single(doc.GetAnnotations(0));
        Assert.Equal(added.Name, read.Name);
        Assert.Equal("Why?", read.Contents);
        AssertColor(AnnotationColor.Pink, read.Color);
        Assert.Equal(recolored.Index, read.Index);
    }

    [Fact]
    public void Existing_annotations_from_other_programs_can_be_edited_and_removed()
    {
        // A highlight without /NM, and a link: the link is not listed and must survive.
        var path = Track(TestPdf.Create(["Hello world", "Second page"],
            firstPageAnnotation: "<< /Type /Annot /Subtype /Highlight /Rect [70 680 150 710] /C [1 1 0] " +
                                 "/QuadPoints [70 710 150 710 70 680 150 680] /Contents (From Acrobat) >>"));
        using var doc = PdfiumDocument.Open(path);
        var existing = Assert.Single(doc.GetAnnotations(0));
        Assert.Null(existing.Name);
        Assert.Equal("From Acrobat", existing.Contents);

        var edited = doc.UpdateAnnotation(existing, existing.ToDraft() with { Contents = "Edited" });
        Assert.NotNull(edited.Name); // named on first edit, so it can be found again after other changes
        doc.RemoveAnnotation(edited);
        Assert.Empty(doc.GetAnnotations(0));
        Assert.NotNull(doc.GetLinkAt(0, 50, 750)); // the link at the bottom-left is still there
    }

    [Fact]
    public void Removing_an_annotation_that_is_gone_throws()
    {
        using var doc = PdfiumDocument.Open(Pdf("Hello world"));
        var added = doc.AddAnnotation(0, Highlight(WordRects(doc, 0, 5)));
        doc.RemoveAnnotation(added);
        Assert.Throws<InvalidOperationException>(() => doc.RemoveAnnotation(added));
    }

    [Fact]
    public void Saves_in_place_incrementally_and_keeps_working()
    {
        var path = Pdf("First page", "Second page", "Third page");
        var original = File.ReadAllBytes(path);
        var created = File.GetCreationTimeUtc(path);
        using (var doc = PdfiumDocument.Open(path))
        {
            doc.AddAnnotation(0, Highlight(WordRects(doc, 0, 5), AnnotationColor.Green, "Saved note"));
            doc.Save();

            // Incremental: the original bytes are untouched, the changes are appended.
            var saved = File.ReadAllBytes(path);
            Assert.True(saved.Length > original.Length);
            Assert.Equal(original, saved[..original.Length]);
            Assert.Equal(created, File.GetCreationTimeUtc(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, $"~{Path.GetFileNameWithoutExtension(path)}.*"));

            // The document keeps reading pages it had not loaded yet (from the new file).
            Assert.Equal("Third page", doc.GetText(2, 0, 10));
            RenderPage(doc, 2);

            // Saving again (with more changes) works too.
            doc.AddAnnotation(1, new AnnotationDraft(AnnotationKind.Note, [new PageRect(50, 50, 50, 50)], null, "Second"));
            doc.Save();
        }

        using var reopened = PdfiumDocument.Open(path);
        var highlight = Assert.Single(reopened.GetAnnotations(0));
        Assert.Equal("Saved note", highlight.Contents);
        Assert.Equal("First", highlight.Text);
        AssertColor(AnnotationColor.Green, highlight.Color);
        Assert.Equal("Second", Assert.Single(reopened.GetAnnotations(1)).Contents);
        Assert.Equal("Second page", reopened.GetText(1, 0, 11));
    }

    [Fact]
    public void Save_as_writes_a_new_file_and_leaves_the_original_alone()
    {
        var path = Pdf("Hello world");
        var original = File.ReadAllBytes(path);
        var copy = Track(Path.Combine(Path.GetTempPath(), $"pdfreader-copy-{Guid.NewGuid():N}.pdf"));
        using (var doc = PdfiumDocument.Open(path))
        {
            doc.AddAnnotation(0, Highlight(WordRects(doc, 0, 5)));
            doc.Save(copy);
            Assert.Equal(copy, doc.FilePath);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        using var reopened = PdfiumDocument.Open(copy);
        Assert.Single(reopened.GetAnnotations(0));
    }

    [Fact]
    public void Failed_saves_leave_the_file_untouched()
    {
        var path = Pdf("Hello world");
        var original = File.ReadAllBytes(path);
        using var doc = PdfiumDocument.Open(path);
        doc.AddAnnotation(0, Highlight(WordRects(doc, 0, 5)));

        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.Throws<UnauthorizedAccessException>(() => doc.Save());
        File.SetAttributes(path, FileAttributes.Normal);

        // Open in another program that does not share it.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => doc.Save());

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, $"~{Path.GetFileNameWithoutExtension(path)}.*"));
        Assert.Equal("Hello", doc.GetText(0, 0, 5)); // still readable
        doc.Save(); // and saves once the file is free
        using var reopened = PdfiumDocument.Open(path);
        Assert.Single(reopened.GetAnnotations(0));
    }

    [Fact]
    public void Editor_undoes_redoes_and_tracks_unsaved_changes()
    {
        using var doc = PdfiumDocument.Open(Pdf("Hello world"));
        var editor = new AnnotationEditor(doc);
        Assert.False(editor.IsDirty);

        var added = editor.Add(0, Highlight(WordRects(doc, 0, 5)));
        Assert.True(editor.IsDirty);
        editor.Update(added, added.ToDraft() with { Contents = "Comment" });
        RenderPage(doc, 0);

        Assert.Equal(0, editor.Undo()); // the comment
        Assert.Equal("", Assert.Single(doc.GetAnnotations(0)).Contents);
        Assert.Equal(0, editor.Undo()); // the highlight
        Assert.Empty(doc.GetAnnotations(0));
        Assert.False(editor.IsDirty);
        Assert.Null(editor.Undo());

        editor.Redo();
        editor.Redo();
        Assert.Equal("Comment", Assert.Single(doc.GetAnnotations(0)).Contents);
        Assert.True(editor.IsDirty);
        Assert.False(editor.CanRedo);

        editor.Save();
        Assert.False(editor.IsDirty);
        var current = Assert.Single(doc.GetAnnotations(0));
        editor.Remove(current);
        Assert.True(editor.IsDirty);
        editor.Undo(); // restores it, with its comment and color
        Assert.False(editor.IsDirty);
        var restored = Assert.Single(doc.GetAnnotations(0));
        Assert.Equal("Comment", restored.Contents);
        AssertColor(AnnotationColor.Yellow, restored.Color);
    }

    [Fact]
    public void A_new_change_after_undo_clears_redo()
    {
        using var doc = PdfiumDocument.Open(Pdf("Hello world"));
        var editor = new AnnotationEditor(doc);
        editor.Add(0, Highlight(WordRects(doc, 0, 5)));
        editor.Undo();
        Assert.True(editor.CanRedo);
        editor.Add(0, Highlight(WordRects(doc, 0, 3)));
        Assert.False(editor.CanRedo);
    }

    [Theory]
    [InlineData("D:20260930143015+02'00'", 2026, 9, 30, 14, 30, 15, 120)]
    [InlineData("D:20260930143015-05'30", 2026, 9, 30, 14, 30, 15, -330)]
    [InlineData("D:20260930143015Z", 2026, 9, 30, 14, 30, 15, 0)]
    [InlineData("D:2026", 2026, 1, 1, 0, 0, 0, 0)]
    public void Pdf_dates_parse(string text, int y, int mo, int d, int h, int mi, int s, int offsetMinutes)
    {
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.FromMinutes(offsetMinutes)), PdfDate.Parse(text));
    }

    [Fact]
    public void Pdf_dates_round_trip()
    {
        var now = new DateTimeOffset(2026, 9, 30, 14, 30, 15, TimeSpan.FromHours(-5));
        Assert.Equal("D:20260930143015-05'00'", PdfDate.Format(now));
        Assert.Equal(now, PdfDate.Parse(PdfDate.Format(now)));
        Assert.Null(PdfDate.Parse("yesterday"));
        Assert.Null(PdfDate.Parse("D:20261340"));
    }

    [Fact]
    public void Selection_rectangles_merge_into_one_per_line()
    {
        var merged = HighlightGeometry.MergeLines(
        [
            new PageRect(10, 100, 40, 112),  // "Hello"
            new PageRect(44, 99, 80, 113),   // "world", slightly taller
            new PageRect(10, 120, 60, 132),  // next line
            new PageRect(300, 100, 340, 112), // same height, other column
            new PageRect(5, 5, 5, 20),       // empty
        ]);
        Assert.Equal(
        [
            new PageRect(10, 99, 80, 113),
            new PageRect(300, 100, 340, 112),
            new PageRect(10, 120, 60, 132),
        ], merged);
    }

    [Fact]
    public void Saving_writes_only_the_edited_pages_however_much_was_viewed()
    {
        // PDFium's incremental save rewrites every object the document loaded; viewing loads them all.
        long Growth(bool viewEverything)
        {
            var path = Pdf(Enumerable.Range(1, 40).Select(i => $"Page number {i} with some text").ToArray());
            long before = new FileInfo(path).Length;
            using (var doc = PdfiumDocument.Open(path))
            {
                if (viewEverything)
                    for (int p = 0; p < doc.PageCount; p++)
                    {
                        RenderPage(doc, p);
                        doc.GetText(p, 0, 5);
                    }
                doc.AddAnnotation(3, Highlight(WordRects(doc, 3, 4)));
                doc.Save();
            }
            return new FileInfo(path).Length - before;
        }
        long quiet = Growth(viewEverything: false), viewed = Growth(viewEverything: true);
        Assert.InRange(viewed, 1, quiet + 64);
    }

    [Fact]
    public void Saving_keeps_other_programs_annotations_and_applies_edits_and_deletions()
    {
        var path = Track(TestPdf.Create(["Hello world", "Second page"],
            firstPageAnnotation: "<< /Type /Annot /Subtype /Highlight /Rect [70 680 150 710] /C [0 1 0] " +
                                 "/QuadPoints [70 710 150 710 70 680 150 680] /Contents (From Acrobat) >>"));
        using (var doc = PdfiumDocument.Open(path))
        {
            var existing = Assert.Single(doc.GetAnnotations(0));
            doc.UpdateAnnotation(existing, existing.ToDraft() with { Contents = "Edited here" });
            var mine = doc.AddAnnotation(0, Highlight(WordRects(doc, 0, 5), AnnotationColor.Pink, "Mine"));
            var gone = doc.AddAnnotation(1, new AnnotationDraft(AnnotationKind.Note, [new PageRect(50, 50, 50, 50)], null, "Temporary"));
            doc.RemoveAnnotation(gone);
            doc.Save();

            // Edit again after the first save: a comment change and a new color.
            var reread = doc.GetAnnotations(0).Single(a => a.Name == mine.Name);
            doc.UpdateAnnotation(reread, reread.ToDraft() with { Contents = "Mine, v2", Color = AnnotationColor.Blue });
            doc.Save();
        }
        using var reopened = PdfiumDocument.Open(path);
        var annotations = reopened.GetAnnotations(0);
        Assert.Equal(2, annotations.Count);
        Assert.Contains(annotations, a => a.Contents == "Edited here");
        var saved = Assert.Single(annotations, a => a.Contents == "Mine, v2");
        AssertColor(AnnotationColor.Blue, saved.Color);
        Assert.Equal("Hello", saved.Text);
        Assert.Empty(reopened.GetAnnotations(1));
        Assert.NotNull(reopened.GetLinkAt(0, 50, 750));
    }
}
