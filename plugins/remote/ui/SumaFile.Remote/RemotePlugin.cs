using Microsoft.UI.Dispatching;
using SimpleFile.PluginSdk;
using SumaFile.Remote.Model;
using SumaFile.Remote.Views;

namespace SumaFile.Remote;

/// <summary>
/// The entry type of <c>com.sumafile.remote</c>. It owns one backend client and at most one manager window, which the
/// <c>ftp-sftp-manager</c> command, the Connections sidebar rows and the settings page all open. The host calls
/// <see cref="ActivateAsync"/>, <see cref="ExecuteAsync"/> and the providers on the UI thread, and
/// <see cref="DeactivateAsync"/> on the thread pool after it has closed the plugin's windows.
/// </summary>
public sealed class RemotePlugin : IPlugin, ICommandHandler, ISidebarSectionProvider, ISettingsPageProvider
{
    /// <summary>The manager command as the host exposes it.</summary>
    public const string ManagerCommandId = "plugin:com.sumafile.remote:ftp-sftp-manager";

    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly SingleWindowSlot<RemoteManagerWindow> _manager = new(window => !window.IsClosed);
    private IPluginHost? _host;
    private RemoteBackendClient? _backend;

    public Task ActivateAsync(IPluginHost host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _backend = new RemoteBackendClient(host);
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.Equals(context.CommandId, ManagerCommandId, StringComparison.Ordinal))
        {
            OpenManager(null);
        }
        else
        {
            _host?.Logger.Warn("Unknown command " + context.CommandId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the one manager window on the UI thread: activates the open one, or creates, attaches and shows a new
    /// one. A <paramref name="profileId"/> (a sidebar row) selects that profile unless a session is open. It runs
    /// inside WinUI event handlers (the settings page button, the sidebar rows), where an escaping exception would end
    /// the whole App, so it never throws: a failure is logged and shown, and a window that failed to attach is closed
    /// and not kept.
    /// </summary>
    internal void OpenManager(string? profileId) =>
        _manager.TryShow(
            reuse: open =>
            {
                open.RequestProfileSelection(profileId);
                open.Activate();
            },
            create: () =>
            {
                var (host, backend) = Services();
                var viewModel = new RemoteManagerViewModel(backend, new LocalFolderSource());
                viewModel.RequestProfileSelection(profileId);
                var window = new RemoteManagerWindow(host, viewModel);
                window.Closed += (_, _) => _manager.Forget(window);
                return window;
            },
            show: window => window.AttachAndShow(),
            abandon: window =>
            {
                if (!window.IsClosed)
                {
                    window.Close();
                }
            },
            report: ReportOpenFailure);

    private void ReportOpenFailure(Exception exception)
    {
        if (_host is not { } host)
        {
            return;
        }

        host.Logger.Error("The FTP/SFTP manager could not open.", exception);
        _ = ShowOpenFailureAsync(host, exception.Message);
    }

    private static async Task ShowOpenFailureAsync(IPluginHost host, string message)
    {
        try
        {
            await host.Dialogs.ShowMessageAsync("FTP/SFTP manager", "The FTP/SFTP manager could not open: " + message, CancellationToken.None);
        }
        catch (Exception exception)
        {
            host.Logger.Warn("The FTP/SFTP manager error could not be shown: " + exception.Message);
        }
    }

    public object CreateSidebarSection(PluginSidebarSectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RemoteSidebarSection(this, Services().Backend).Root;
    }

    public object CreateSettingsPage(PluginSettingsPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RemoteSettingsPage(this).Root;
    }

    /// <summary>Keeps a backend event subscription so <see cref="DeactivateAsync"/> can end it.</summary>
    internal IDisposable TrackSubscription(IDisposable subscription)
    {
        var tracked = new TrackedSubscription(this, subscription);
        lock (_gate)
        {
            _subscriptions.Add(tracked);
        }

        return tracked;
    }

    public async Task DeactivateAsync()
    {
        // The host closes attached windows first; close a straggler on its own thread, never from this one.
        if (_manager.Take() is { IsClosed: false } window)
        {
            await CloseOnUiThreadAsync(window).ConfigureAwait(false);
        }

        IDisposable[] subscriptions;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    private static Task CloseOnUiThreadAsync(RemoteManagerWindow window)
    {
        DispatcherQueue queue;
        try
        {
            queue = window.DispatcherQueue;
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }

        if (queue is null)
        {
            return Task.CompletedTask;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    if (!window.IsClosed)
                    {
                        window.Close();
                    }
                }
                catch (Exception)
                {
                    // A window the dispatcher already tore down is closed.
                }
                finally
                {
                    closed.TrySetResult();
                }
            }))
        {
            // The dispatcher is shutting down and the window goes with it.
            return Task.CompletedTask;
        }

        return closed.Task;
    }

    private (IPluginHost Host, RemoteBackendClient Backend) Services() =>
        _host is { } host && _backend is { } backend
            ? (host, backend)
            : throw new InvalidOperationException("The FTP/SFTP plugin is not active.");

    private void Forget(TrackedSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    /// <summary>A subscription that leaves the plugin's list when it is disposed, and disposes its inner one once.</summary>
    private sealed class TrackedSubscription(RemotePlugin owner, IDisposable inner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Forget(this);
                inner.Dispose();
            }
        }
    }
}
