using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Contributions;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Trust;
using SimpleFile.PluginSdk;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private const long MaxPluginLogBytes = 1_048_576; // 1 MB, as service.log
    private static readonly object PluginLogGate = new();

    private PluginManager? _plugins;
    private PluginWorkspaceView? _pluginWorkspace;
    private CancellationTokenSource? _pluginCts;
    private IReadOnlyList<PluginContextMenuGroup> _pluginMenus = [];
    // False until the workspace has loaded its settings: before that, toolbar placements would land in
    // throwaway defaults (and a save would overwrite the user's real layout).
    private bool _pluginLayoutReady;
    private bool _pluginConsentQueued;

    /// <summary>
    /// Builds the plugin manager and discovers plugins. Runs before the workspace loads its settings, because
    /// loading the toolbar layout drops ids it does not know, so plugin toolbar actions must be registered first.
    /// </summary>
    private async Task InitializePluginsAsync(FileOperationService fileOps, StartupTimer timer)
    {
        var cts = new CancellationTokenSource();
        _pluginCts = cts;
        _pluginWorkspace = new PluginWorkspaceView(
            DispatcherQueue,
            () => _workspace,
            () => SelectedPaths ?? [],
            line => AppendPluginLog("host", line));
        var manager = new PluginManager(new PluginManagerOptions
        {
            Roots = PluginDiscovery.DefaultRoots(),
            ApprovalStorePath = PluginApprovalStore.DefaultPath(),
            DataRoot = Path.Combine(LocalSumaFileDirectory(), "plugins-data"),
            Launcher = new ProcessLauncher(),
            PipeNameFor = PluginPipeName,
            Settings = fileOps,
            Progress = new PluginProgressSink(DispatcherQueue, () => _transfer, PluginDisplayName),
            Notifier = new WindowPluginNotifier(this),
            DialogsFor = pluginId => new PluginDialogs(
                pluginId,
                DispatcherQueue,
                () => Content?.XamlRoot,
                PluginDisplayName,
                () => PickFolderAsync(null)),
            Workspace = _pluginWorkspace,
            Log = AppendPluginLog,
            // The current client, so a reconnected service is used; engines registered before a service
            // restart are not replayed (the backend has to be restarted).
            ArchiveEngines = new ArchiveEngineRegistrar(() => _backend?.Client),
        });
        _plugins = manager;
        manager.Changed += OnPluginsChanged;

        try
        {
            await manager.DiscoverAsync(cts.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken plugin folder must never stop SumaFile from starting.
            AppendPluginLog("host", $"[error] Plugin discovery failed: {exception}");
        }

        timer.Mark("plugins-discovered", $"count={manager.Plugins.Count}");
        ApplyPluginContributions();
    }

    /// <summary>
    /// The workspace has loaded its settings: apply the toolbar placements that waited for it. The toolbar
    /// itself is rebuilt by the first sync that follows, together with its enabled state.
    /// </summary>
    private void OnPluginWorkspaceInitialized()
    {
        if (_plugins is not { } manager)
        {
            return;
        }

        _pluginLayoutReady = true;
        // From here on, catalog changes rebuild the toolbar; before this the first sync builds it.
        AttachCommandCatalogEvents();
        ApplyPluginToolbarPlacements(manager.EnabledPlugins);
        _pluginWorkspace?.Refresh();
    }

    private void OnPluginsChanged(object? sender, EventArgs e)
    {
        // Raised on whichever thread changed the plugin; contributions touch UI and static catalogs.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _plugins))
            {
                return;
            }

            ApplyPluginContributions();
            RefreshStatusCenter();
        });
    }

    private void ApplyPluginContributions()
    {
        var enabled = _plugins?.EnabledPlugins ?? [];
        AppCommandCatalog.SetPluginCommands(PluginContributionAdapter.Commands(enabled));
        ToolbarActionCatalog.SetPluginActions(PluginContributionAdapter.ToolbarActions(enabled));
        _pluginMenus = PluginContributionAdapter.ContextMenus(enabled);
        if (ApplyPluginToolbarPlacements(enabled))
        {
            ApplyCommandSurfaceLayout();
        }
    }

    /// <summary>
    /// Puts primary-placement plugin actions on the toolbar layout and saves it when that added any.
    /// Returns false (and does nothing) until the workspace has loaded its settings.
    /// </summary>
    private bool ApplyPluginToolbarPlacements(IReadOnlyList<PluginRecord> enabled)
    {
        if (!_pluginLayoutReady || _workspace is not { } workspace)
        {
            return false;
        }

        var layout = workspace.Settings.CommandSurface;
        var added = false;
        foreach (var id in PluginContributionAdapter.PrimaryToolbarCommandIds(enabled))
        {
            added |= layout.EnsurePrimaryItem(id);
        }

        if (added)
        {
            _ = RunUiActionAsync("Toolbar", () => workspace.SaveUiSettingsAsync());
        }

        return true;
    }

    private Task RunPluginCommandAsync(string hostCommandId) =>
        RunUiActionAsync("Plugin", async () =>
        {
            var plugins = _plugins ?? throw new InvalidOperationException("Plugins are not available.");
            var activePath = _workspace?.Active.Path;
            var context = new PluginCommandContext(
                hostCommandId,
                string.IsNullOrWhiteSpace(activePath) ? null : activePath,
                SelectedPaths ?? []);
            await plugins.RunCommandAsync(hostCommandId, context, _pluginCts?.Token ?? CancellationToken.None);
        });

    /// <summary>Snapshots the active folder and selection for plugins after a workspace change.</summary>
    private void RefreshPluginWorkspace()
    {
        DispatcherQueue.TryEnqueue(() => _pluginWorkspace?.Refresh());
    }

    /// <summary>
    /// Asks about each plugin waiting for consent, one at a time, once the window has painted. Called after
    /// startup reaches ready; "Keep disabled" leaves the plugin waiting for the Settings page.
    /// </summary>
    private void QueuePluginConsentPrompts()
    {
        if (_pluginConsentQueued || _plugins is not { } manager)
        {
            return;
        }

        _pluginConsentQueued = true;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => _ = RunUiActionAsync("Plugin", () => PromptForPluginConsentAsync(manager)));
    }

    private async Task PromptForPluginConsentAsync(PluginManager manager)
    {
        var token = _pluginCts?.Token ?? CancellationToken.None;
        foreach (var id in manager.Plugins.Where(record => record.State == PluginState.NeedsConsent).Select(record => record.Id).ToList())
        {
            // The list may have changed while an earlier dialog was open.
            var record = manager.Plugins.FirstOrDefault(item => item.Id == id && item.State == PluginState.NeedsConsent);
            if (record is null)
            {
                continue;
            }

            if (!ReferenceEquals(_plugins, manager) || token.IsCancellationRequested || Content?.XamlRoot is not { } root)
            {
                return;
            }

            var dialog = new PluginConsentDialog(record) { XamlRoot = root };
            if (await DialogHost.ShowAsync(dialog, token) == ContentDialogResult.Primary)
            {
                await RunUiActionAsync("Plugin", () => manager.EnableAsync(id, token));
            }
        }
    }

    private IEnumerable<StatusCenterTaskRow> PluginStatusRows()
    {
        if (_plugins is not { } manager)
        {
            return [];
        }

        return manager.Plugins
            .Where(record => record.State == PluginState.Faulted)
            .Select(record => new StatusCenterTaskRow
            {
                Title = $"Plugin {record.Discovered.Manifest?.Name ?? record.Id} stopped",
                Detail = record.Error ?? "The plugin backend stopped unexpectedly.",
                Badge = "Stopped",
                UndoVisibility = Visibility.Visible,
                ActionLabel = "Restart",
                ActionAutomationName = $"Restart plugin {record.Discovered.Manifest?.Name ?? record.Id}",
                UndoCommand = new StatusCenterCommand(() => RestartPluginBackend(manager, record.Id)),
            })
            .ToList();
    }

    private async void RestartPluginBackend(PluginManager manager, string id)
    {
        StatusCenterButton.Flyout?.Hide();
        await RunUiActionAsync("Plugin", () => manager.RestartBackendAsync(id, _pluginCts?.Token ?? CancellationToken.None));
    }

    /// <summary>Stops every plugin backend and forgets the plugins' contributions; part of session cleanup.</summary>
    private async Task DisposePluginsAsync()
    {
        if (_plugins is not { } manager)
        {
            return;
        }

        _plugins = null;
        manager.Changed -= OnPluginsChanged;
        DetachCommandCatalogEvents();
        // Cancelled, not disposed: a consent prompt or command may still hold its token.
        _pluginCts?.Cancel();
        _pluginCts = null;
        _pluginWorkspace = null;
        _pluginLayoutReady = false;
        _pluginMenus = [];
        AppCommandCatalog.SetPluginCommands([]);
        ToolbarActionCatalog.SetPluginActions([]);
        try
        {
            await manager.DisposeAsync();
        }
        catch (Exception exception)
        {
            AppendPluginLog("host", $"[error] Stopping plugins failed: {exception}");
        }
    }

    private string PluginDisplayName(string pluginId) =>
        _plugins?.Plugins.FirstOrDefault(record => record.Id == pluginId)?.Discovered.Manifest?.Name ?? pluginId;

    private static readonly Lazy<int> WindowsSessionId = new(() => System.Diagnostics.Process.GetCurrentProcess().SessionId);

    // Same session.pid scheme as the file service's pipe (BackendSession), so pipes never collide across sessions.
    private static string PluginPipeName(string pluginId) =>
        $"SumaFile.Plugin.{WindowsSessionId.Value}.{Environment.ProcessId}.{pluginId}";

    private static string LocalSumaFileDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SumaFile");

    /// <summary>Appends to %LOCALAPPDATA%\SumaFile\plugins.log, rotating at 1 MB like service.log.</summary>
    private static void AppendPluginLog(string pluginId, string line)
    {
        try
        {
            var directory = LocalSumaFileDirectory();
            Directory.CreateDirectory(directory);
            var logPath = Path.Combine(directory, "plugins.log");
            lock (PluginLogGate)
            {
                RotatePluginLogIfNeeded(logPath);
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] [{pluginId}] {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort: plugin logging must never crash the host.
        }
    }

    private static void RotatePluginLogIfNeeded(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length < MaxPluginLogBytes)
            {
                return;
            }

            var backup = logPath + ".1";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(logPath, backup);
        }
        catch
        {
            // Best-effort rotation.
        }
    }

    /// <summary>Shows a plugin's notification in the message bar, from any thread.</summary>
    private sealed class WindowPluginNotifier(MainWindow window) : IPluginNotifier
    {
        public void Notify(string pluginId, string message, bool isError)
        {
            window.DispatcherQueue.TryEnqueue(() => window.ShowMessage(
                window.PluginDisplayName(pluginId),
                message,
                isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational));
        }
    }
}
