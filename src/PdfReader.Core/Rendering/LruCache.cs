namespace PdfReader.Core.Rendering;

/// <summary>
/// Least-recently-used cache bounded by a total cost (typically bytes of pixel memory).
/// Evicted values are handed to the eviction callback so GPU/native resources can be released.
/// Not thread-safe; owned by a single (UI) thread.
/// </summary>
public sealed class LruCache<TKey, TValue>(long capacity, Action<TKey, TValue>? onEvicted = null) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new();

    private sealed record Entry(TKey Key, TValue Value, long Cost);

    public long Capacity { get; set; } = capacity;
    public long TotalCost { get; private set; }
    public int Count => _map.Count;
    public IReadOnlyList<TValue> Values => _order.Select(e => e.Value).ToList();

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public bool Contains(TKey key) => _map.ContainsKey(key);

    public void Add(TKey key, TValue value, long cost)
    {
        Remove(key);
        var node = _order.AddFirst(new Entry(key, value, cost));
        _map[key] = node;
        TotalCost += cost;
        Trim(Capacity);
    }

    public bool Remove(TKey key)
    {
        if (!_map.Remove(key, out var node)) return false;
        _order.Remove(node);
        TotalCost -= node.Value.Cost;
        onEvicted?.Invoke(key, node.Value.Value);
        return true;
    }

    /// <summary>Removes every entry matching the predicate (e.g. all tiles of a stale zoom level).</summary>
    public void RemoveWhere(Func<TKey, bool> predicate)
    {
        foreach (var key in _map.Keys.Where(predicate).ToList()) Remove(key);
    }

    /// <summary>Evicts least-recently-used entries until the total cost fits in <paramref name="budget"/>.</summary>
    public void Trim(long budget)
    {
        while (TotalCost > budget && _order.Last is { } last)
            Remove(last.Value.Key);
    }

    public void Clear() => Trim(-1);
}
