using Microsoft.UI.Dispatching;
using SimpleFile.Core;
using SimpleFile.PluginSdk;

namespace SimpleFile.App;

/// <summary>
/// A plugin's read-only view of the active folder and selection, over <see cref="ExplorerWorkspace"/>.
/// The selection lives in UI controls, so the window snapshots it on its own thread
/// (<see cref="Refresh"/>) and plugins read the snapshot from any thread.
/// </summary>
internal sealed class PluginWorkspaceView : IPluginWorkspace
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<ExplorerWorkspace?> _workspace;
    private readonly Func<IReadOnlyList<string>> _selectedPaths;
    private readonly Action<string> _log;
    private Snapshot _snapshot = new(null, []);

    /// <param name="workspace">Read at call time, since the workspace is created after plugin discovery.</param>
    /// <param name="selectedPaths">Called on the UI thread only.</param>
    public PluginWorkspaceView(
        DispatcherQueue dispatcher,
        Func<ExplorerWorkspace?> workspace,
        Func<IReadOnlyList<string>> selectedPaths,
        Action<string> log)
    {
        _dispatcher = dispatcher;
        _workspace = workspace;
        _selectedPaths = selectedPaths;
        _log = log;
    }

    public string? ActivePath => Volatile.Read(ref _snapshot).ActivePath;

    public IReadOnlyList<string> SelectedPaths => Volatile.Read(ref _snapshot).SelectedPaths;

    /// <summary>Raised on the UI thread when the active folder or the selection changes.</summary>
    public event EventHandler? Changed;

    public Task NavigateAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Always queued, even on the UI thread: a plugin that navigates from ActivateAsync or a provider must never
        // re-enter a workspace sync that is still running (carry-over #82).
        return PluginUiDispatch.EnqueueAsync(_dispatcher, async () =>
        {
            var workspace = _workspace() ?? throw new InvalidOperationException("SumaFile's workspace is not ready.");
            await workspace.NavigateToAsync(path, cancellationToken: ct);
            return true;
        });
    }

    public Task RefreshAsync(CancellationToken ct)
    {
        // Always queued, like NavigateAsync: a plugin that refreshes from a command, a provider or a decoration request
        // must never re-enter a workspace sync that is still running (carry-over #82).
        return PluginUiDispatch.EnqueueAsync(_dispatcher, async () =>
        {
            ct.ThrowIfCancellationRequested();
            var workspace = _workspace() ?? throw new InvalidOperationException("SumaFile's workspace is not ready.");
            await workspace.RefreshAsync(ct);
            return true;
        });
    }

    /// <summary>Takes a new snapshot; call on the UI thread.</summary>
    public void Refresh()
    {
        var active = _workspace()?.Active.Path;
        var next = new Snapshot(string.IsNullOrWhiteSpace(active) ? null : active, [.. _selectedPaths()]);
        var previous = Volatile.Read(ref _snapshot);
        if (string.Equals(previous.ActivePath, next.ActivePath, StringComparison.Ordinal)
            && previous.SelectedPaths.SequenceEqual(next.SelectedPaths, StringComparer.Ordinal))
        {
            return;
        }

        Volatile.Write(ref _snapshot, next);
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                // One plugin's handler must not break the window or the other plugins.
                _log($"[error] IPluginWorkspace.Changed handler threw: {exception}");
            }
        }
    }

    private sealed record Snapshot(string? ActivePath, IReadOnlyList<string> SelectedPaths);
}
