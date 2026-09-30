namespace PdfReader.Core.Engine;

/// <summary>The annotation kinds the reader lists and edits. Other kinds are rendered but left alone.</summary>
public enum AnnotationKind
{
    /// <summary>A text highlight (/Highlight) with optional comment.</summary>
    Highlight,
    /// <summary>A sticky note (/Text) placed at a point.</summary>
    Note,
}

public readonly record struct AnnotationColor(byte R, byte G, byte B)
{
    public static AnnotationColor Yellow { get; } = new(255, 226, 52);
    public static AnnotationColor Green { get; } = new(120, 220, 110);
    public static AnnotationColor Blue { get; } = new(110, 185, 255);
    public static AnnotationColor Pink { get; } = new(255, 130, 190);

    /// <summary>The colors offered by the UI, the first being the default.</summary>
    public static IReadOnlyList<AnnotationColor> Palette { get; } = [Yellow, Green, Blue, Pink];
}

/// <summary>What an annotation should look like: used to create, re-create (undo) and update annotations.</summary>
/// <param name="Areas">
/// Highlight: one rectangle per line of text, in page space. Note: a single rectangle, the icon.
/// </param>
/// <param name="Color">Null keeps the current color when updating, and means yellow when creating.</param>
/// <param name="Name">The annotation's unique name (/NM); generated when null.</param>
public sealed record AnnotationDraft(
    AnnotationKind Kind,
    IReadOnlyList<PageRect> Areas,
    AnnotationColor? Color,
    string Contents = "",
    string? Name = null);

/// <summary>An annotation read from a page.</summary>
/// <param name="Index">Position in the page's annotation array when it was read (it shifts after removals).</param>
/// <param name="Name">The annotation's /NM, when it has one. Annotations the reader creates or edits always do.</param>
/// <param name="Bounds">The annotation rectangle, in page space.</param>
/// <param name="Color">Null when it cannot be determined.</param>
/// <param name="Text">For highlights, the text under the highlighted areas.</param>
public sealed record PdfAnnotation(
    int PageIndex,
    int Index,
    AnnotationKind Kind,
    string? Name,
    PageRect Bounds,
    IReadOnlyList<PageRect> Areas,
    AnnotationColor? Color,
    string Contents,
    string Text,
    DateTimeOffset? Modified)
{
    public AnnotationDraft ToDraft() => new(Kind, Areas, Color, Contents, Name);
}
