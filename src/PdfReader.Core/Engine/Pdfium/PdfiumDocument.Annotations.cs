using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static PdfReader.Core.Engine.Pdfium.PdfiumNative;

namespace PdfReader.Core.Engine.Pdfium;

// Annotations (highlights and notes) and saving.
public sealed unsafe partial class PdfiumDocument
{
    private const double NoteSize = 20; // points, the usual sticky-note icon size

    // Pages whose annotations changed since the last save: only they are written (see Save).
    private readonly HashSet<int> _editedPages = new();

    public bool CanEditAnnotations { get; }

    public IReadOnlyList<PdfAnnotation> GetAnnotations(int pageIndex)
    {
        lock (Sync)
        {
            var page = GetPage(pageIndex).Page;
            var result = new List<PdfAnnotation>();
            int count = FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                var annot = FPDFPage_GetAnnot(page, i);
                if (annot == 0) continue;
                try
                {
                    if (ReadAnnotation(pageIndex, page, annot, i) is { } annotation) result.Add(annotation);
                }
                finally
                {
                    FPDFPage_CloseAnnot(annot);
                }
            }
            return result;
        }
    }

    public PdfAnnotation AddAnnotation(int pageIndex, AnnotationDraft draft)
    {
        if (draft.Areas.Count == 0) throw new ArgumentException("An annotation needs at least one area.", nameof(draft));
        lock (Sync)
        {
            ThrowIfReadOnly();
            var page = GetPage(pageIndex).Page;
            _editedPages.Add(pageIndex);
            return CreateAnnotation(pageIndex, page, draft);
        }
    }

    public PdfAnnotation UpdateAnnotation(PdfAnnotation annotation, AnnotationDraft draft)
    {
        lock (Sync)
        {
            ThrowIfReadOnly();
            var page = GetPage(annotation.PageIndex).Page;
            int index = FindAnnotation(page, annotation);
            if (index < 0) throw new InvalidOperationException("The annotation no longer exists.");
            _editedPages.Add(annotation.PageIndex);

            bool sameLook = draft.Kind == annotation.Kind && (draft.Color is null || draft.Color == annotation.Color) && SameAreas(draft.Areas, annotation.Areas);
            if (!sameLook)
            {
                // The appearance stream PDFium generated cannot be recolored or reshaped: re-create the annotation.
                RemoveAt(page, index);
                return CreateAnnotation(annotation.PageIndex, page, draft with { Name = draft.Name ?? annotation.Name, Color = draft.Color ?? annotation.Color });
            }

            var annot = FPDFPage_GetAnnot(page, index);
            try
            {
                SetString(annot, "Contents", draft.Contents);
                SetString(annot, "M", PdfDate.Format(DateTimeOffset.Now));
                if (annotation.Name is null) SetString(annot, "NM", draft.Name ?? NewName());
                return ReadAnnotation(annotation.PageIndex, page, annot, index)!;
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
        }
    }

    public void RemoveAnnotation(PdfAnnotation annotation)
    {
        lock (Sync)
        {
            ThrowIfReadOnly();
            var page = GetPage(annotation.PageIndex).Page;
            int index = FindAnnotation(page, annotation);
            if (index < 0) throw new InvalidOperationException("The annotation no longer exists.");
            _editedPages.Add(annotation.PageIndex);
            RemoveAt(page, index);
        }
    }

    /// <summary>
    /// Saves the document, with its changes, to <paramref name="path"/> (by default its own file), which it
    /// then reads from.
    /// </summary>
    /// <remarks>
    /// The save is incremental: the new file is the original bytes plus an appended update, which keeps
    /// everything else (including signatures) untouched. It is written next to the target and then swapped
    /// in, so a failure never leaves a half-written file behind. Because the new file starts with the
    /// original bytes, PDFium, which keeps reading lazily, can continue reading from it.
    /// <para>
    /// PDFium's incremental save rewrites every object the document has loaded, and viewing loads nearly all of
    /// them (saving the viewed document once doubled a 2.5 MB file). So the update is written from a second,
    /// freshly loaded copy of the file, onto which the annotations of the edited pages are copied: the update
    /// only holds those pages and their annotations.
    /// </para>
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The target (or its folder) is read-only.</exception>
    /// <exception cref="IOException">The file could not be written or replaced (e.g. open in another program).</exception>
    public void Save(string? path = null)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_doc == 0, this);
            if (_source is null) throw new IOException("Files over 4 GB cannot be saved.");
            string target = Path.GetFullPath(path ?? FilePath);
            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                throw new UnauthorizedAccessException($"'{target}' is read-only.");

            string directory = Path.GetDirectoryName(target)!;
            string temp = Path.Combine(directory, $"~{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.tmp");
            string current = FilePath;
            bool detached = false;
            try
            {
                // The file as it is now (with earlier saves), not as it was when this document opened it.
                using (var freshSource = PdfFileSource.Open(current))
                {
                    var fresh = FPDF_LoadCustomDocument(freshSource.Access, null);
                    if (fresh == 0) throw new IOException("The document could not be reloaded for saving.");
                    try
                    {
                        foreach (int pageIndex in _editedPages) CopyAnnotations(pageIndex, fresh);
                        WriteCopy(fresh, temp);
                    }
                    finally
                    {
                        FPDF_CloseDocument(fresh);
                    }
                    using var written = PdfFileSource.OpenHandle(temp);
                    // It must start with the whole current file (which starts with the bytes this document reads).
                    if (!freshSource.IsPrefixOf(written)) throw new IOException("The document could not be saved incrementally.");
                }
                // Nothing reads the file while Sync is held, so it can be closed for the swap (Windows will not
                // replace a file that is open). Replace keeps the original's creation time, attributes and permissions.
                _source.SwitchTo(null);
                detached = true;
                if (File.Exists(target)) File.Replace(temp, target, null, ignoreMetadataErrors: true);
                else File.Move(temp, target);
                _source.SwitchTo(PdfFileSource.OpenHandle(target));
                detached = false;
                FilePath = target;
                _editedPages.Clear();
            }
            catch
            {
                if (detached)
                {
                    // The swap failed: keep reading the original, which is still in place.
                    try { _source.SwitchTo(PdfFileSource.OpenHandle(current)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    private static void WriteCopy(nint doc, string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        var handle = GCHandle.Alloc(stream);
        try
        {
            var writer = new FileWriter { Base = { Version = 1, WriteBlock = &WriteBlock }, Stream = GCHandle.ToIntPtr(handle) };
            if (FPDF_SaveAsCopy(doc, &writer.Base, FPDF_INCREMENTAL) == 0)
                throw new IOException("PDFium could not save the document.");
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWriter
    {
        public FPDF_FILEWRITE Base; // first, so PDFium's pointer to it is a pointer to the whole struct
        public nint Stream;
    }

    [UnmanagedCallersOnly]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, uint size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(((FileWriter*)self)->Stream).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, (int)size));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    // --- copying a page's annotations to the document being saved (caller holds Sync)

    /// <summary>A highlight or note as stored in the file (PDF coordinates), for comparing two documents.</summary>
    private sealed record StoredAnnotation(
        nint Handle, int Subtype, string Name, FS_RECTF Rect, IReadOnlyList<FS_QUADPOINTSF> Quads,
        AnnotationColor? Color, string Contents, string Modified);

    private static List<StoredAnnotation> ReadStored(nint page)
    {
        var result = new List<StoredAnnotation>();
        int count = FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            var annot = FPDFPage_GetAnnot(page, i);
            if (annot == 0) continue;
            int subtype = FPDFAnnot_GetSubtype(annot);
            if ((subtype != FPDF_ANNOT_HIGHLIGHT && subtype != FPDF_ANNOT_TEXT) || FPDFAnnot_GetRect(annot, out var rect) == 0)
            {
                FPDFPage_CloseAnnot(annot);
                continue;
            }
            var quads = new List<FS_QUADPOINTSF>();
            nuint n = subtype == FPDF_ANNOT_HIGHLIGHT ? FPDFAnnot_CountAttachmentPoints(annot) : 0;
            for (nuint q = 0; q < n; q++)
                if (FPDFAnnot_GetAttachmentPoints(annot, q, out var quad) != 0) quads.Add(quad);
            result.Add(new StoredAnnotation(annot, subtype, GetString(annot, "NM"), rect, quads, ReadColor(annot),
                GetString(annot, "Contents"), GetString(annot, "M")));
        }
        return result;
    }

    /// <summary>
    /// Makes the highlights and notes of a page in <paramref name="target"/> (the document being saved) match
    /// this document: removed ones are removed, changed comments are updated in place (keeping the appearance
    /// other programs gave them), and new or recolored ones are created. Other annotations are not touched.
    /// </summary>
    private void CopyAnnotations(int pageIndex, nint target)
    {
        var livePage = GetPage(pageIndex).Page;
        var targetPage = FPDF_LoadPage(target, pageIndex);
        if (targetPage == 0) throw new IOException($"Page {pageIndex + 1} could not be loaded for saving.");
        var live = ReadStored(livePage);
        var saved = ReadStored(targetPage);
        try
        {
            var matched = new bool[live.Count];
            var create = new List<StoredAnnotation>();
            foreach (var old in saved)
            {
                int j = MatchStored(old, live, matched);
                if (j < 0)
                {
                    RemoveAt(targetPage, FPDFPage_GetAnnotIndex(targetPage, old.Handle));
                    continue;
                }
                matched[j] = true;
                var now = live[j];
                bool sameLook = SameQuads(old.Quads, now.Quads) && NearRect(old.Rect, now.Rect) &&
                                (now.Color is null || old.Color is null || now.Color == old.Color);
                if (!sameLook)
                {
                    RemoveAt(targetPage, FPDFPage_GetAnnotIndex(targetPage, old.Handle));
                    create.Add(now);
                    continue;
                }
                if (old.Contents != now.Contents) SetString(old.Handle, "Contents", now.Contents);
                if (old.Modified != now.Modified && now.Modified.Length > 0) SetString(old.Handle, "M", now.Modified);
                if (old.Name != now.Name && now.Name.Length > 0) SetString(old.Handle, "NM", now.Name);
            }
            for (int j = 0; j < live.Count; j++)
                if (!matched[j]) create.Add(live[j]);

            foreach (var a in create)
            {
                var annot = FPDFPage_CreateAnnot(targetPage, a.Subtype);
                if (annot == 0) throw new IOException("PDFium could not copy an annotation.");
                try
                {
                    var color = a.Color ?? AnnotationColor.Yellow;
                    FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255);
                    foreach (var quad in a.Quads) FPDFAnnot_AppendAttachmentPoints(annot, quad);
                    FPDFAnnot_SetRect(annot, a.Rect);
                    SetString(annot, "NM", a.Name.Length > 0 ? a.Name : NewName());
                    SetString(annot, "Contents", a.Contents);
                    if (a.Modified.Length > 0) SetString(annot, "M", a.Modified);
                    FPDFAnnot_SetFlags(annot, a.Subtype == FPDF_ANNOT_HIGHLIGHT ? FPDF_ANNOT_FLAG_PRINT : 0);
                }
                finally
                {
                    FPDFPage_CloseAnnot(annot);
                }
            }
        }
        finally
        {
            foreach (var a in live.Concat(saved)) FPDFPage_CloseAnnot(a.Handle);
            FPDF_ClosePage(targetPage);
        }
    }

    /// <summary>The live annotation a stored one corresponds to: same name, or (unnamed in the file) same kind and place.</summary>
    private static int MatchStored(StoredAnnotation old, List<StoredAnnotation> live, bool[] matched)
    {
        for (int j = 0; j < live.Count; j++)
        {
            if (matched[j] || live[j].Subtype != old.Subtype) continue;
            if (old.Name.Length > 0 ? live[j].Name == old.Name : NearRect(live[j].Rect, old.Rect)) return j;
        }
        return -1;
    }

    private static bool NearRect(FS_RECTF a, FS_RECTF b) =>
        Math.Abs(a.Left - b.Left) < 0.5 && Math.Abs(a.Top - b.Top) < 0.5 && Math.Abs(a.Right - b.Right) < 0.5 && Math.Abs(a.Bottom - b.Bottom) < 0.5;

    private static bool SameQuads(IReadOnlyList<FS_QUADPOINTSF> a, IReadOnlyList<FS_QUADPOINTSF> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            Math.Abs(p.First.X1 - p.Second.X1) < 0.5 && Math.Abs(p.First.Y1 - p.Second.Y1) < 0.5 &&
            Math.Abs(p.First.X4 - p.Second.X4) < 0.5 && Math.Abs(p.First.Y4 - p.Second.Y4) < 0.5);

    // --- helpers (caller holds Sync)

    private void ThrowIfReadOnly()
    {
        ObjectDisposedException.ThrowIf(_doc == 0, this);
        if (!CanEditAnnotations) throw new InvalidOperationException("Encrypted documents cannot be annotated.");
    }

    private static int Subtype(AnnotationKind kind) => kind == AnnotationKind.Highlight ? FPDF_ANNOT_HIGHLIGHT : FPDF_ANNOT_TEXT;

    private static string NewName() => $"pdfreader-{Guid.NewGuid():N}";

    private PdfAnnotation CreateAnnotation(int pageIndex, nint page, AnnotationDraft draft)
    {
        var annot = FPDFPage_CreateAnnot(page, Subtype(draft.Kind));
        if (annot == 0) throw new InvalidOperationException("PDFium could not create the annotation.");
        try
        {
            // Color first: once PDFium has generated the appearance (on the next render) it cannot change.
            var color = draft.Color ?? AnnotationColor.Yellow;
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255);
            var areas = draft.Kind == AnnotationKind.Note ? [NoteArea(pageIndex, draft.Areas[0])] : draft.Areas;
            double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
            foreach (var area in areas)
            {
                var (x1, y1) = DisplayToPdf(pageIndex, page, area.Left, area.Top);
                var (x2, y2) = DisplayToPdf(pageIndex, page, area.Right, area.Top);
                var (x3, y3) = DisplayToPdf(pageIndex, page, area.Left, area.Bottom);
                var (x4, y4) = DisplayToPdf(pageIndex, page, area.Right, area.Bottom);
                if (draft.Kind == AnnotationKind.Highlight)
                {
                    var quad = new FS_QUADPOINTSF
                    {
                        X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2,
                        X3 = (float)x3, Y3 = (float)y3, X4 = (float)x4, Y4 = (float)y4,
                    };
                    FPDFAnnot_AppendAttachmentPoints(annot, quad);
                }
                left = Math.Min(left, Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)));
                right = Math.Max(right, Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)));
                bottom = Math.Min(bottom, Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)));
                top = Math.Max(top, Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)));
            }
            FPDFAnnot_SetRect(annot, new FS_RECTF { Left = (float)left, Top = (float)top, Right = (float)right, Bottom = (float)bottom });
            SetString(annot, "NM", draft.Name ?? NewName());
            SetString(annot, "Contents", draft.Contents);
            SetString(annot, "M", PdfDate.Format(DateTimeOffset.Now));
            // Highlights print, like in other readers; note icons do not.
            FPDFAnnot_SetFlags(annot, draft.Kind == AnnotationKind.Highlight ? FPDF_ANNOT_FLAG_PRINT : 0);
            return ReadAnnotation(pageIndex, page, annot, FPDFPage_GetAnnotIndex(page, annot))!;
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>A note's icon: <see cref="NoteSize"/> points from the given top-left, kept inside the page.</summary>
    private PageRect NoteArea(int pageIndex, PageRect requested)
    {
        var size = _sizes[pageIndex];
        double x = Math.Clamp(requested.Left, 0, Math.Max(0, size.Width - NoteSize));
        double y = Math.Clamp(requested.Top, 0, Math.Max(0, size.Height - NoteSize));
        return new PageRect(x, y, x + NoteSize, y + NoteSize);
    }

    private void RemoveAt(nint page, int index)
    {
        // A note may have a linked pop-up annotation (added by other readers); remove it too.
        int popupIndex = -1;
        var annot = FPDFPage_GetAnnot(page, index);
        if (annot != 0)
        {
            var popup = FPDFAnnot_GetLinkedAnnot(annot, "Popup");
            if (popup != 0)
            {
                popupIndex = FPDFPage_GetAnnotIndex(page, popup);
                FPDFPage_CloseAnnot(popup);
            }
            FPDFPage_CloseAnnot(annot);
        }
        if (FPDFPage_RemoveAnnot(page, index) == 0) throw new InvalidOperationException("PDFium could not remove the annotation.");
        if (popupIndex >= 0) FPDFPage_RemoveAnnot(page, popupIndex > index ? popupIndex - 1 : popupIndex);
    }

    /// <summary>Index of the annotation on the page now: by name, else where it was read if it still matches.</summary>
    private int FindAnnotation(nint page, PdfAnnotation target)
    {
        int count = FPDFPage_GetAnnotCount(page);
        int subtype = Subtype(target.Kind);
        IEnumerable<int> candidates = target.Name is not null
            ? Enumerable.Range(0, count)
            : new[] { target.Index }.Concat(Enumerable.Range(0, count).Where(i => i != target.Index)).Where(i => i >= 0 && i < count);
        foreach (int i in candidates)
        {
            var annot = FPDFPage_GetAnnot(page, i);
            if (annot == 0) continue;
            try
            {
                if (FPDFAnnot_GetSubtype(annot) != subtype) continue;
                string name = GetString(annot, "NM");
                if (target.Name is not null)
                {
                    if (name == target.Name) return i;
                }
                else if (name.Length == 0 && FPDFAnnot_GetRect(annot, out var r) != 0 &&
                         Near(PdfRectToDisplay(target.PageIndex, page, r), target.Bounds))
                {
                    return i;
                }
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
        }
        return -1;
    }

    private PdfAnnotation? ReadAnnotation(int pageIndex, nint page, nint annot, int index)
    {
        var kind = FPDFAnnot_GetSubtype(annot) switch
        {
            FPDF_ANNOT_HIGHLIGHT => AnnotationKind.Highlight,
            FPDF_ANNOT_TEXT => AnnotationKind.Note,
            _ => (AnnotationKind?)null,
        };
        if (kind is null || FPDFAnnot_GetRect(annot, out var rect) == 0) return null;
        var bounds = PdfRectToDisplay(pageIndex, page, rect);

        var areas = new List<PageRect>();
        string text = string.Empty;
        if (kind == AnnotationKind.Highlight)
        {
            nuint quads = FPDFAnnot_CountAttachmentPoints(annot);
            var parts = new List<string>();
            for (nuint q = 0; q < quads; q++)
            {
                if (FPDFAnnot_GetAttachmentPoints(annot, q, out var p) == 0) continue;
                double l = Math.Min(Math.Min(p.X1, p.X2), Math.Min(p.X3, p.X4)), r = Math.Max(Math.Max(p.X1, p.X2), Math.Max(p.X3, p.X4));
                double b = Math.Min(Math.Min(p.Y1, p.Y2), Math.Min(p.Y3, p.Y4)), t = Math.Max(Math.Max(p.Y1, p.Y2), Math.Max(p.Y3, p.Y4));
                areas.Add(PdfRectToDisplay(pageIndex, page, new FS_RECTF { Left = (float)l, Top = (float)t, Right = (float)r, Bottom = (float)b }));
                parts.Add(BoundedText(pageIndex, l, t, r, b));
            }
            if (areas.Count == 0) areas.Add(bounds);
            text = string.Join(" ", parts.Where(p => p.Length > 0));
        }
        else
        {
            areas.Add(bounds);
        }

        string name = GetString(annot, "NM");
        return new PdfAnnotation(pageIndex, index, kind.Value, name.Length > 0 ? name : null, bounds, areas,
            ReadColor(annot), GetString(annot, "Contents"), text, PdfDate.Parse(GetString(annot, "M")));
    }

    private string BoundedText(int pageIndex, double left, double top, double right, double bottom)
    {
        var textPage = GetTextPage(pageIndex);
        int count = FPDFText_GetBoundedText(textPage, left, top, right, bottom, null, 0);
        if (count <= 0) return string.Empty;
        var buffer = new char[count + 1];
        int written;
        fixed (char* b = buffer) written = FPDFText_GetBoundedText(textPage, left, top, right, bottom, b, buffer.Length);
        return new string(buffer, 0, Math.Clamp(written, 0, count)).Replace("\r\n", " ").Replace('\n', ' ').Trim();
    }

    [GeneratedRegex(@"(?<![\w.])(\d*\.?\d+)\s+(\d*\.?\d+)\s+(\d*\.?\d+)\s+rg\b")]
    private static partial Regex FillColorRegex();

    /// <summary>
    /// The annotation color (/C). PDFium refuses to read it once an appearance stream exists (always the case
    /// after the first render, or for files from other readers), so it then falls back to the stream's fill color.
    /// </summary>
    private static AnnotationColor? ReadColor(nint annot)
    {
        if (FPDFAnnot_GetColor(annot, FPDFANNOT_COLORTYPE_Color, out uint r, out uint g, out uint b, out _) != 0)
            return new AnnotationColor((byte)r, (byte)g, (byte)b);

        uint len = FPDFAnnot_GetAP(annot, FPDF_ANNOT_APPEARANCEMODE_NORMAL, null, 0);
        if (len <= 2) return null;
        var buffer = new char[len / 2];
        fixed (char* p = buffer) FPDFAnnot_GetAP(annot, FPDF_ANNOT_APPEARANCEMODE_NORMAL, p, len);
        var match = FillColorRegex().Match(new string(buffer, 0, buffer.Length - 1));
        if (!match.Success) return null;
        static byte Component(Group g) => (byte)Math.Clamp(Math.Round(double.Parse(g.Value, CultureInfo.InvariantCulture) * 255), 0, 255);
        return new AnnotationColor(Component(match.Groups[1]), Component(match.Groups[2]), Component(match.Groups[3]));
    }

    private PageRect PdfRectToDisplay(int pageIndex, nint page, FS_RECTF r)
    {
        var (x1, y1) = PdfToDisplay(pageIndex, page, r.Left, r.Top);
        var (x2, y2) = PdfToDisplay(pageIndex, page, r.Right, r.Bottom);
        return new PageRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    private static bool Near(PageRect a, PageRect b, double tolerance = 0.5) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Right - b.Right) <= tolerance && Math.Abs(a.Bottom - b.Bottom) <= tolerance;

    private static bool SameAreas(IReadOnlyList<PageRect> a, IReadOnlyList<PageRect> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => Near(pair.First, pair.Second));

    private static string GetString(nint annot, string key)
    {
        uint bytes = FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (bytes <= 2) return string.Empty;
        var buffer = new char[bytes / 2];
        fixed (char* b = buffer) FPDFAnnot_GetStringValue(annot, key, b, bytes);
        return new string(buffer, 0, buffer.Length - 1);
    }

    private static void SetString(nint annot, string key, string value)
    {
        fixed (char* v = value + "\0") FPDFAnnot_SetStringValue(annot, key, v);
    }
}

/// <summary>PDF date strings: <c>D:YYYYMMDDHHmmSS+HH'mm'</c>.</summary>
public static partial class PdfDate
{
    public static string Format(DateTimeOffset value)
    {
        var offset = value.Offset;
        string zone = offset == TimeSpan.Zero ? "Z" : $"{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):D2}'{Math.Abs(offset.Minutes):D2}'";
        return "D:" + value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + zone;
    }

    [GeneratedRegex(@"^(?:D:)?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?(?:([Zz])|([+-])(\d{2})'?(\d{2})?'?)?")]
    private static partial Regex DateRegex();

    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || DateRegex().Match(text) is not { Success: true } m) return null;
        int Part(int group, int fallback) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : fallback;
        var offset = TimeSpan.Zero;
        if (m.Groups[8].Success)
        {
            offset = new TimeSpan(Part(9, 0), Part(10, 0), 0);
            if (m.Groups[8].Value == "-") offset = -offset;
        }
        try
        {
            return new DateTimeOffset(Part(1, 1), Part(2, 1), Part(3, 1), Part(4, 0), Part(5, 0), Part(6, 0), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
