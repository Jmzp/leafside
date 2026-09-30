namespace PdfReader.Core.State;

/// <summary>
/// A reading position that survives zoom and window-size changes.
/// </summary>
/// <param name="Page">Page at the top edge of the viewport.</param>
/// <param name="OffsetInPage">How far down that page the top edge is, as a fraction of the page height.</param>
/// <param name="Zoom">Zoom factor, or 0 when unknown (the viewer then fits the page width).</param>
/// <param name="HorizontalOffset">Left edge of the viewport in unzoomed content DIPs.</param>
public sealed record ViewState(int Page, double OffsetInPage = 0, double Zoom = 0, double HorizontalOffset = 0)
{
    /// <summary>True when the two positions are more than one page apart (worth a history entry).</summary>
    public bool IsFarFrom(ViewState other) =>
        Math.Abs(Page + OffsetInPage - (other.Page + other.OffsetInPage)) > 1;
}
