using Microsoft.UI.Xaml;
using SimpleFile.PluginSdk;
using SumaFile.Git.Model;
using SumaFile.Git.Views;

namespace SumaFile.Git;

/// <summary>
/// The entry type of <c>com.sumafile.git</c>. It owns one backend client, one workbench view model and controller,
/// and the row decoration provider. The host calls <see cref="ActivateAsync"/>, <see cref="ExecuteAsync"/> and
/// <see cref="CreatePanel"/> on the UI thread, <see cref="GetDecorationsAsync"/> on the thread pool, and
/// <see cref="DeactivateAsync"/> on the thread pool after it has hidden the plugin's panel and stopped its backend.
/// </summary>
/// <remarks>
/// The host shows and toggles the workbench for <c>git-panel</c> itself (plugin.json <c>panels[].command</c>); the
/// controller runs the other nine commands, and <c>git-diff-selected</c> shows the workbench through
/// <see cref="IPluginHost.Panels"/> before it previews the diff.
/// </remarks>
public sealed class GitPlugin : IPlugin, ICommandHandler, IPanelProvider, IRowDecorationProvider
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private IPluginHost? _host;
    private GitWorkbenchViewModel? _model;
    private GitWorkbenchController? _controller;
    private GitDecorationProvider? _decorations;
    private WeakReference<FrameworkElement>? _shownPanel;
    private EventHandler? _workspaceChanged;

    public Task ActivateAsync(IPluginHost host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        var backend = new GitBackendClient(host);
        _model = new GitWorkbenchViewModel();
        _controller = new GitWorkbenchController(backend, host, _model, new GitPrompts(host, ShownPanelXamlRoot));
        _decorations = new GitDecorationProvider(backend, host.Settings, host.Panels, host.Logger);
        var controller = _controller;
        _workspaceChanged = (_, _) => controller.OnWorkspaceChanged();
        host.Workspace.Changed += _workspaceChanged;
        return Task.CompletedTask;
    }

    public async Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_controller is not { } controller || _host is not { } host)
        {
            return;
        }

        if (string.Equals(context.CommandId, GitCommandIds.Panel, StringComparison.Ordinal))
        {
            // The host normally shows the panel for its command; this covers a host that routes it here.
            try
            {
                await host.Panels.ShowAsync(GitWorkbenchController.WorkbenchPanelId, ct);
            }
            catch (Exception exception)
            {
                host.Logger.Warn("The Git workbench could not be shown: " + exception.Message);
            }

            return;
        }

        await controller.ExecuteAsync(context, ct);
    }

    /// <summary>A new workbench body per presentation; it shows the view model's state at once and refreshes silently.</summary>
    public object CreatePanel(PluginPanelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (controller, model) = Services();
        var panel = new GitWorkbenchPanel(controller, model, request.Detached);
        _shownPanel = new WeakReference<FrameworkElement>(panel.Root);
        _ = controller.RefreshAsync(silent: true, CancellationToken.None);
        return panel.Root;
    }

    public Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct) =>
        _decorations is { } decorations ? decorations.GetAsync(request, ct) : Task.FromResult(PluginFolderDecorations.Empty);

    public async Task DeactivateAsync()
    {
        if (_host is { } host && _workspaceChanged is { } handler)
        {
            host.Workspace.Changed -= handler;
            _workspaceChanged = null;
        }

        if (_controller is { } controller)
        {
            await controller.StopAsync(StopTimeout).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The XamlRoot the dialogs open on: the shown workbench element's, docked or in its window; null, which means
    /// the main window, when no workbench is shown.
    /// </summary>
    private XamlRoot? ShownPanelXamlRoot()
    {
        if (_host is not { } host
            || !host.Panels.IsVisible(GitWorkbenchController.WorkbenchPanelId)
            || _shownPanel is null
            || !_shownPanel.TryGetTarget(out var element))
        {
            return null;
        }

        return element.IsLoaded ? element.XamlRoot : null;
    }

    private (GitWorkbenchController Controller, GitWorkbenchViewModel Model) Services() =>
        _controller is { } controller && _model is { } model
            ? (controller, model)
            : throw new InvalidOperationException("The Git plugin is not active.");
}
