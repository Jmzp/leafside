using PdfReader.Core.Engine;

namespace PdfReader.Core.Annotations;

/// <summary>
/// Edits a document's annotations with undo/redo, and tracks whether there are unsaved changes.
/// Every method calls into the document (PDFium), so callers run them off the UI thread; the editor
/// serializes them itself.
/// </summary>
public sealed class AnnotationEditor(IPdfDocument document)
{
    private readonly Lock _gate = new();
    private readonly Stack<Change> _undo = new();
    private readonly Stack<Change> _redo = new();
    private long _nextId = 1;
    private long _savedId; // id of the change on top of the undo stack when last saved (0: none)

    /// <summary>One edit: the annotation before (null: it did not exist) and after (null: removed).</summary>
    private sealed record Change(long Id, PdfAnnotation? Before, PdfAnnotation? After)
    {
        public int Page => (After ?? Before)!.PageIndex;
    }

    public IPdfDocument Document { get; } = document;

    public bool IsDirty { get { lock (_gate) return TopId != _savedId; } }
    public bool CanUndo { get { lock (_gate) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count > 0; } }

    private long TopId => _undo.TryPeek(out var top) ? top.Id : 0;

    public PdfAnnotation Add(int pageIndex, AnnotationDraft draft)
    {
        lock (_gate)
        {
            var added = Document.AddAnnotation(pageIndex, draft);
            Record(null, added);
            return added;
        }
    }

    public PdfAnnotation Update(PdfAnnotation annotation, AnnotationDraft draft)
    {
        lock (_gate)
        {
            var updated = Document.UpdateAnnotation(annotation, draft);
            Record(annotation, updated);
            return updated;
        }
    }

    public void Remove(PdfAnnotation annotation)
    {
        lock (_gate)
        {
            Document.RemoveAnnotation(annotation);
            Record(annotation, null);
        }
    }

    /// <summary>Reverts the last change. Returns the page it affected, or null when there was nothing to undo.</summary>
    public int? Undo()
    {
        lock (_gate)
        {
            if (!_undo.TryPop(out var change)) return null;
            // The annotation may be re-created with a new index: keep the snapshot that now exists, for Redo.
            var restored = Apply(change.After, change.Before);
            _redo.Push(change with { Before = restored });
            return change.Page;
        }
    }

    /// <summary>Re-applies the last undone change. Returns the page it affected, or null.</summary>
    public int? Redo()
    {
        lock (_gate)
        {
            if (!_redo.TryPop(out var change)) return null;
            var redone = Apply(change.Before, change.After);
            _undo.Push(change with { After = redone });
            return change.Page;
        }
    }

    /// <summary>Saves the document (to its own file by default) and marks the current state as saved.</summary>
    public void Save(string? path = null)
    {
        lock (_gate)
        {
            Document.Save(path);
            _savedId = TopId;
        }
    }

    private void Record(PdfAnnotation? before, PdfAnnotation? after)
    {
        _undo.Push(new Change(_nextId++, before, after));
        _redo.Clear();
    }

    /// <summary>Moves an annotation from state <paramref name="from"/> to <paramref name="to"/>; returns the new state.</summary>
    private PdfAnnotation? Apply(PdfAnnotation? from, PdfAnnotation? to)
    {
        if (to is null)
        {
            Document.RemoveAnnotation(from!);
            return null;
        }
        return from is null
            ? Document.AddAnnotation(to.PageIndex, to.ToDraft())
            : Document.UpdateAnnotation(from, to.ToDraft());
    }
}
