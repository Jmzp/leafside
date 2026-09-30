using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfReader.Core.Layout;
using Windows.Foundation;

namespace PdfReader.App.Controls;

/// <summary>
/// The scrollable content: as large as the whole document at 100 % zoom, but it only hosts the
/// handful of <see cref="PageView"/>s the viewer realized. Each child arranges itself at its layout bounds.
/// </summary>
public sealed class PagesPanel : Panel
{
    private DocumentLayout? _layout;

    public PagesPanel()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }

    public DocumentLayout? Layout
    {
        get => _layout;
        set { _layout = value; InvalidateMeasure(); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            if (child is PageView { PageIndex: >= 0 } page)
                child.Measure(new Size(page.Bounds.Width, page.Bounds.Height));
        }
        return _layout is null ? new Size(0, 0) : new Size(_layout.ContentWidth, _layout.ContentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (child is PageView { PageIndex: >= 0 } page)
            {
                var b = page.Bounds;
                child.Arrange(new Rect(b.X, b.Y, b.Width, b.Height));
            }
        }
        return finalSize;
    }
}
