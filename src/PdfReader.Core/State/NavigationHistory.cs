namespace PdfReader.Core.State;

/// <summary>Back/forward stack of reading positions, like a browser's.</summary>
public sealed class NavigationHistory
{
    public const int Capacity = 50;

    private readonly List<ViewState> _back = [];
    private readonly List<ViewState> _forward = [];

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Records the position being left by a jump. Clears the forward history.</summary>
    public void Push(ViewState leaving)
    {
        _back.Add(leaving);
        if (_back.Count > Capacity) _back.RemoveAt(0);
        _forward.Clear();
    }

    /// <summary>Returns the position to go back to, remembering <paramref name="current"/> for Forward.</summary>
    public ViewState? GoBack(ViewState current) => Move(_back, _forward, current);

    public ViewState? GoForward(ViewState current) => Move(_forward, _back, current);

    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }

    private static ViewState? Move(List<ViewState> from, List<ViewState> to, ViewState current)
    {
        if (from.Count == 0) return null;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(current);
        return target;
    }
}
