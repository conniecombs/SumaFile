namespace SumaFile.Remote.Model;

/// <summary>
/// Holds the plugin's one window of a kind (the FTP/SFTP manager). Its entry points (a command, a settings page
/// button, sidebar rows) run inside WinUI event handlers, where an escaping exception ends the whole host App, so
/// <see cref="TryShow"/> never throws: it reports the failure instead. A window is kept only after it has shown and
/// is still open; one that failed to show is abandoned (closed) and never reused. WinUI-free so it can be tested.
/// </summary>
public sealed class SingleWindowSlot<TWindow>(Func<TWindow, bool> isOpen) where TWindow : class
{
    private readonly object _gate = new();
    private readonly Func<TWindow, bool> _isOpen = isOpen ?? throw new ArgumentNullException(nameof(isOpen));
    private TWindow? _window;

    /// <summary>The kept window while it is open, otherwise null.</summary>
    public TWindow? Open
    {
        get
        {
            lock (_gate)
            {
                return _window is { } window && _isOpen(window) ? window : null;
            }
        }
    }

    /// <summary>
    /// Brings up the window: <paramref name="reuse"/> the open one, or <paramref name="create"/> a new one and
    /// <paramref name="show"/> it. When showing fails, the new window is passed to <paramref name="abandon"/> and is
    /// not kept. Any failure goes to <paramref name="report"/>; nothing is thrown.
    /// </summary>
    /// <returns>True when a window was reused or shown.</returns>
    public bool TryShow(
        Action<TWindow> reuse,
        Func<TWindow> create,
        Action<TWindow> show,
        Action<TWindow> abandon,
        Action<Exception> report)
    {
        ArgumentNullException.ThrowIfNull(reuse);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(abandon);
        ArgumentNullException.ThrowIfNull(report);

        TWindow? window = null;
        try
        {
            if (Open is { } open)
            {
                reuse(open);
                return true;
            }

            window = create();
            show(window);
        }
        catch (Exception exception)
        {
            if (window is not null)
            {
                Abandon(window, abandon);
            }

            Report(exception, report);
            return false;
        }

        lock (_gate)
        {
            // A host that is stopping the plugin closes the window while attaching it; such a window is not kept.
            _window = _isOpen(window) ? window : null;
        }

        return true;
    }

    /// <summary>Drops <paramref name="window"/> when it is the kept one (its Closed handler calls this).</summary>
    public void Forget(TWindow window)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        }
    }

    /// <summary>Empties the slot and returns what it held, open or not (plugin deactivation).</summary>
    public TWindow? Take()
    {
        lock (_gate)
        {
            var window = _window;
            _window = null;
            return window;
        }
    }

    private static void Abandon(TWindow window, Action<TWindow> abandon)
    {
        try
        {
            abandon(window);
        }
        catch (Exception)
        {
            // The window never showed; the original failure is the one to report.
        }
    }

    private static void Report(Exception exception, Action<Exception> report)
    {
        try
        {
            report(exception);
        }
        catch (Exception)
        {
            // Reporting must not turn a handled failure into an unhandled one.
        }
    }
}
