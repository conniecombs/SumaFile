namespace SimpleFile.Core;

/// <summary>
/// Counts who is waiting for each queued request, by its key. A request that reaches the front of its queue
/// with nobody waiting is not worth making: the folder was left, or the row scrolled away.
/// </summary>
public sealed class WantedRequests
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _waiters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One more caller waits for <paramref name="key"/>, until the result is disposed.</summary>
    public IDisposable Want(string key)
    {
        lock (_gate)
        {
            _waiters[key] = _waiters.GetValueOrDefault(key) + 1;
        }

        return new Waiter(this, key);
    }

    /// <summary>True while at least one caller still waits for <paramref name="key"/>.</summary>
    public bool IsWanted(string key)
    {
        lock (_gate)
        {
            return _waiters.ContainsKey(key);
        }
    }

    private void Leave(string key)
    {
        lock (_gate)
        {
            var left = _waiters.GetValueOrDefault(key) - 1;
            if (left > 0)
            {
                _waiters[key] = left;
            }
            else
            {
                _waiters.Remove(key);
            }
        }
    }

    private sealed class Waiter(WantedRequests owner, string key) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                owner.Leave(key);
            }
        }
    }
}
