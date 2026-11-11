namespace SimpleFile.Core;

/// <summary>
/// Queues the loads of plugin-provided bodies (sidebar sections now, panels later), so plugin code never runs inline
/// inside the host method that asked for it, such as a layout pass or a workspace sync (carry-over #82). A key is
/// queued at most once while it waits; keys carry their slot (<c>sidebar:</c>, <c>panel:</c>), so two slots never drop
/// each other's load.
/// </summary>
/// <param name="enqueue">Runs a callback later on the UI thread, for example at low dispatcher priority.</param>
public sealed class PluginBodyLoadQueue(Action<Action> enqueue)
{
    private readonly object _lock = new();
    // The ticket of each waiting key; a queued callback whose ticket is no longer the key's does nothing.
    private readonly Dictionary<string, long> _pending = new(StringComparer.Ordinal);
    private long _tickets;

    /// <summary>
    /// Queues <paramref name="load"/> under <paramref name="key"/>. False, and nothing queued, when that key is already
    /// waiting. Never runs <paramref name="load"/> inline; the queued callback clears the pending mark before it starts it.
    /// </summary>
    public bool Request(string key, Func<Task> load)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);
        long ticket;
        lock (_lock)
        {
            if (_pending.ContainsKey(key))
            {
                return false;
            }

            ticket = ++_tickets;
            _pending[key] = ticket;
        }

        enqueue(() => Run(key, ticket, load));
        return true;
    }

    /// <summary>Drops the waiting load of <paramref name="key"/>, if any; its queued callback then does nothing.</summary>
    public void Forget(string key)
    {
        lock (_lock)
        {
            _pending.Remove(key);
        }
    }

    public bool IsPending(string key)
    {
        lock (_lock)
        {
            return _pending.ContainsKey(key);
        }
    }

    private void Run(string key, long ticket, Func<Task> load)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(key, out var current) || current != ticket)
            {
                return;
            }

            _pending.Remove(key);
        }

        Task task;
        try
        {
            task = load();
        }
        catch (Exception exception)
        {
            // The load reports its own failures; one that throws before its first await must not escape into the
            // dispatcher.
            task = Task.FromException(exception);
        }

        _ = Observe(task);

        static async Task Observe(Task running)
        {
            try { await running.ConfigureAwait(false); } catch { }
        }
    }
}
