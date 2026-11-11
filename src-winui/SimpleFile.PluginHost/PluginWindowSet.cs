namespace SimpleFile.PluginHost;

/// <summary>
/// The bookkeeping behind an <see cref="IPluginWindowHost"/>: the windows a plugin attached, and the seal that
/// <see cref="IPluginWindowHost.CloseAllAsync"/> sets. It is WinUI-free so the seal can be tested; the App's
/// <c>PluginWindowHost</c> keeps one per host and does the closing on the UI thread. Thread-safe: the plugin attaches
/// on the UI thread while the manager seals from a thread-pool thread, and every window is either refused by
/// <see cref="TryTrack"/> or returned by a <see cref="Seal"/> that follows it, never neither.
/// </summary>
public sealed class PluginWindowSet<TWindow> where TWindow : class
{
    private readonly object _gate = new();
    private readonly List<TWindow> _open = [];
    private bool _sealed;

    /// <summary>True once <see cref="Seal"/> ran; it never goes back.</summary>
    public bool IsSealed
    {
        get { lock (_gate) return _sealed; }
    }

    /// <summary>The windows tracked now, in attach order (a snapshot).</summary>
    public IReadOnlyList<TWindow> Open
    {
        get { lock (_gate) return [.. _open]; }
    }

    /// <summary>
    /// Tracks a window the plugin attached. Returns false once the set is sealed: the host is stopping the plugin, so the
    /// caller closes the window at once instead. Tracking a window twice keeps one entry.
    /// </summary>
    public bool TryTrack(TWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_gate)
        {
            if (_sealed) return false;
            if (!_open.Contains(window)) _open.Add(window);
            return true;
        }
    }

    /// <summary>Forgets a window, from its <c>Closed</c> event. Returns false when it was not tracked.</summary>
    public bool Remove(TWindow window)
    {
        lock (_gate) return _open.Remove(window);
    }

    /// <summary>
    /// Seals the set for good and returns the windows still tracked, for the caller to close. They stay tracked until
    /// <see cref="Remove"/>, so a later call returns whatever has not closed yet.
    /// </summary>
    public IReadOnlyList<TWindow> Seal()
    {
        lock (_gate)
        {
            _sealed = true;
            return [.. _open];
        }
    }
}
