using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SimpleFile.PluginHost;
using SimpleFile.PluginSdk;

namespace SimpleFile.App;

/// <summary>
/// One plugin runtime's windows (<see cref="IPluginWindowHost"/>). <c>Attach</c> gives a plugin's code-built
/// <see cref="Window"/> the host's window chrome (<see cref="SecondaryWindowChrome.Apply"/>: icon, Mica, DPI-aware
/// size, the app theme) and tracks it until it closes. The manager seals the host when it stops the plugin
/// (<see cref="CloseAllAsync"/>), and from then on <c>Attach</c> closes the window it is handed at once. A sealed host is
/// never reused: <c>MainWindow.WindowsFor</c> creates a new one for every runtime.
/// </summary>
internal sealed class PluginWindowHost : IPluginWindowHost
{
    private readonly DispatcherQueue _queue;
    private readonly PluginWindowSet<Window> _windows = new();

    public PluginWindowHost(string pluginId, DispatcherQueue queue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(queue);
        PluginId = pluginId;
        _queue = queue;
    }

    public string PluginId { get; }

    /// <summary>The plugin windows open now, in attach order (the UI smoke reads it).</summary>
    public IReadOnlyList<Window> OpenWindows => _windows.Open;

    public bool IsSealed => _windows.IsSealed;

    /// <summary>Runs on the UI thread, where the plugin created the window.</summary>
    public void Attach(object window, PluginWindowOptions options)
    {
        if (window is not Window pluginWindow)
        {
            throw new ArgumentException("A plugin window must be a Microsoft.UI.Xaml.Window.", nameof(window));
        }

        ArgumentNullException.ThrowIfNull(options);
        if (!_windows.TryTrack(pluginWindow))
        {
            // Sealed: the host is stopping this plugin, so the window never opens past its DeactivateAsync.
            TryClose(pluginWindow);
            return;
        }

        pluginWindow.Closed += (_, _) =>
        {
            // A window the user closes (title-bar X, Alt+F4) or the plugin closes itself never goes through CloseEach,
            // and WinUI does not complete a dialog's ShowAsync when its window closes: hide its dialogs here too, so the
            // app-wide DialogHost gate is released. After CloseEach this finds nothing left to hide.
            DialogHost.HideDialogsOn(XamlRootOf(pluginWindow));
            _windows.Remove(pluginWindow);
        };
        SecondaryWindowChrome.Apply(pluginWindow, options.Title, options.Width, options.Height);
    }

    /// <summary>
    /// Tracks a window the host itself built for this plugin, such as a detached panel's <see cref="PluginPanelWindow"/>,
    /// in the same set and under the same seal as <see cref="Attach"/>, without applying chrome (the window applied its
    /// own). Teardown and the main window's close therefore close it with the plugin's windows. A sealed host closes the
    /// window at once. Runs on the UI thread.
    /// </summary>
    internal void TrackHostWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!_windows.TryTrack(window))
        {
            // Sealed: the plugin is stopping, so its panel window never outlives the runtime.
            TryClose(window);
            return;
        }

        window.Closed += (_, _) =>
        {
            // As in Attach: a dialog the plugin opened on the panel window's XamlRoot holds the app-wide gate.
            DialogHost.HideDialogsOn(XamlRootOf(window));
            _windows.Remove(window);
        };
    }

    /// <summary>
    /// Seals the host on the calling thread (so the seal holds even when the dispatcher is gone), then closes the
    /// windows still open on the UI thread. Completes at once when none is open, or when the dispatcher is shutting
    /// down and the windows go with it. The manager's <c>WindowCloseTimeout</c> bounds the rest.
    /// </summary>
    public Task CloseAllAsync()
    {
        var windows = _windows.Seal();
        if (windows.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (_queue.HasThreadAccess)
        {
            CloseEach(windows);
            return Task.CompletedTask;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(() =>
            {
                try
                {
                    CloseEach(windows);
                }
                finally
                {
                    closed.TrySetResult();
                }
            }))
        {
            return Task.CompletedTask;
        }

        return closed.Task;
    }

    /// <summary>
    /// Seals the host and closes every tracked window now, on the calling UI thread. The main window's <c>OnClosed</c>
    /// calls it for every plugin, so plugin windows close with the main window; the later teardown finds nothing to close.
    /// </summary>
    internal void CloseAllNow()
    {
        var windows = _windows.Seal();
        CloseEach(windows);
    }

    private void CloseEach(IReadOnlyList<Window> windows)
    {
        foreach (var window in windows)
        {
            // A window that closed in the meantime has left the set: skip it.
            if (_windows.Open.Contains(window))
            {
                // A dialog open on the window holds the app-wide DialogHost gate, and WinUI may never complete its
                // ShowAsync once the window is gone: hide it first, which releases the gate either way.
                DialogHost.HideDialogsOn(XamlRootOf(window));
                TryClose(window);
            }
        }
    }

    private static XamlRoot? XamlRootOf(Window window)
    {
        try
        {
            return window.Content?.XamlRoot;
        }
        catch (Exception)
        {
            // Its content is already gone, and with it any dialog on it.
            return null;
        }
    }

    private static void TryClose(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // Already closed, or its thread is gone: either way it is not open.
        }
    }
}
