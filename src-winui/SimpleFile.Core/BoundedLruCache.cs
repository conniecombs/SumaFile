namespace SimpleFile.Core;

/// <summary>A thread-safe cache bounded by both entry count and estimated resident bytes.</summary>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(TKey Key, TValue Value, long Bytes);
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _recency = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _bytes;

    public BoundedLruCache(int maxEntries, long maxBytes, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _entries = new(comparer);
    }

    public int Count { get { lock (_gate) return _entries.Count; } }
    public long EstimatedBytes { get { lock (_gate) return _bytes; } }

    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                value = default!;
                return false;
            }
            _recency.Remove(node);
            _recency.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value, long estimatedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(estimatedBytes);
        lock (_gate)
        {
            if (_entries.Remove(key, out var previous))
            {
                _recency.Remove(previous);
                _bytes -= previous.Value.Bytes;
            }
            // A single oversized image must not evict every useful cached image.
            if (estimatedBytes > _maxBytes) return;
            while (_entries.Count >= _maxEntries || _bytes > _maxBytes - estimatedBytes)
            {
                var oldest = _recency.Last!;
                _entries.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Bytes;
                _recency.RemoveLast();
            }
            var node = _recency.AddFirst(new Entry(key, value, estimatedBytes));
            _entries.Add(key, node);
            _bytes += estimatedBytes;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
            _bytes = 0;
        }
    }
}
