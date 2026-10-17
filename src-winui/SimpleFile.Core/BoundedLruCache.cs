namespace SimpleFile.Core;

public readonly record struct BoundedLruCacheStats(
    int Count,
    int MaxEntries,
    long EstimatedBytes,
    long MaxBytes,
    long Hits,
    long Misses,
    long Evictions,
    long Bypasses);

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
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _bypasses;

    public BoundedLruCache(int maxEntries, long maxBytes, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _entries = new(comparer);
    }

    public int Count { get { lock (_gate) return _entries.Count; } }
    public int MaxEntries => _maxEntries;
    public long EstimatedBytes { get { lock (_gate) return _bytes; } }
    public long MaxBytes => _maxBytes;
    public long Hits { get { lock (_gate) return _hits; } }
    public long Misses { get { lock (_gate) return _misses; } }
    public long Evictions { get { lock (_gate) return _evictions; } }
    public long Bypasses { get { lock (_gate) return _bypasses; } }

    public BoundedLruCacheStats SnapshotStats()
    {
        lock (_gate)
        {
            return new BoundedLruCacheStats(
                _entries.Count,
                _maxEntries,
                _bytes,
                _maxBytes,
                _hits,
                _misses,
                _evictions,
                _bypasses);
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                _misses++;
                value = default!;
                return false;
            }
            _hits++;
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
            // A single oversized item must not evict every useful cached entry.
            if (estimatedBytes > _maxBytes)
            {
                _bypasses++;
                return;
            }
            while (_entries.Count >= _maxEntries || _bytes > _maxBytes - estimatedBytes)
            {
                var oldest = _recency.Last!;
                _entries.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Bytes;
                _recency.RemoveLast();
                _evictions++;
            }
            var node = _recency.AddFirst(new Entry(key, value, estimatedBytes));
            _entries.Add(key, node);
            _bytes += estimatedBytes;
        }
    }

    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_entries.Remove(key, out var node))
            {
                return false;
            }
            _recency.Remove(node);
            _bytes -= node.Value.Bytes;
            return true;
        }
    }

    public int RemoveWhere(Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            var removed = 0;
            var current = _recency.First;
            while (current is not null)
            {
                var next = current.Next;
                if (predicate(current.Value.Key))
                {
                    _entries.Remove(current.Value.Key);
                    _bytes -= current.Value.Bytes;
                    _recency.Remove(current);
                    removed++;
                }
                current = next;
            }
            return removed;
        }
    }

    public int TrimToBytes(long targetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetBytes);
        lock (_gate)
        {
            var removed = 0;
            while (_recency.Last is { } oldest && _bytes > targetBytes)
            {
                _entries.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Bytes;
                _recency.RemoveLast();
                _evictions++;
                removed++;
            }
            return removed;
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

