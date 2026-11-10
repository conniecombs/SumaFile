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
    private DispatcherQueue? _pluginWindowQueue;
    // The window host of each plugin's current runtime (WindowsFor replaces it for every new runtime). Written from
    // whichever thread the manager creates a runtime on, so guarded.
    private readonly Dictionary<string, PluginWindowHost> _pluginWindowHosts = new(StringComparer.Ordinal);
    private readonly object _pluginWindowHostsGate = new();

    /// <summary>The plugin roots, approvals file and bundled root startup uses, and the scan prepared from them.</summary>
    private sealed record PendingPluginScan(
        IReadOnlyList<string> Roots, string ApprovalStorePath, string BundledRoot, Task<PreparedScan?> Scan);

    /// <summary>
    /// Starts reading and judging every plugin (its manifest, <c>bundled.json</c>, the approvals file and its
    /// signature) on the thread pool, so that work (and its cold-start cost) overlaps the file service's start. A
    /// failure only means discovery reads for itself.
    /// </summary>
    private static PendingPluginScan PreparePluginScan()
    {
        var roots = PluginDiscovery.DefaultRoots();
        var approvals = PluginApprovalStore.DefaultPath();
        var bundledRoot = PluginDiscovery.InstallRoot();
        var scan = Task.Run<PreparedScan?>(() =>
        {
            try
            {
                return PluginManager.PrepareScan(roots, bundledRoot, approvals);
            }
            catch (Exception exception)
            {
                AppendPluginLog("host", $"[warn] Preparing the plugin scan failed; discovery reads again: {exception.Message}");
                return null;
            }
        });
        return new PendingPluginScan(roots, approvals, bundledRoot, scan);
    }

    /// <summary>
    /// Carry-over (h): runs, once, the code the first contribution render runs, so its first-use JIT happens while the
    /// file service starts rather than inside the budgeted <c>plugins-*</c> span (spec §5.2). It runs the contribution
    /// adapters on empty lists and builds one sidebar section header for a placeholder section, then drops it. It never
    /// touches <c>PluginSectionsHost</c>, the catalogs or any plugin state, and loads no plugin code.
    /// </summary>
    private void WarmPluginRendering()
    {
        try
        {
            PluginRecord[] none = [];
            _ = PluginContributionAdapter.Commands(none);
            _ = PluginContributionAdapter.ContextMenus(none);
            _ = PluginContributionAdapter.ToolbarActions(none);
            _ = PluginContributionAdapter.PrimaryToolbarCommandIds(none);
            _ = PluginContributionAdapter.SidebarSections(none);
            _ = PluginContributionAdapter.Panels(none);
            _ = PluginContributionAdapter.RowDecorationProviders(none);
            _ = CreatePluginSectionView(new PluginSidebarSection("warm-up", "warm-up", "Warm-up", "warm-up", null, 0));
        }
        catch (Exception exception)
        {
            // Only a warm-up: the real render builds the same controls and reports its own failures.
            System.Diagnostics.Debug.WriteLine($"Plugin rendering warm-up failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Builds the plugin manager, discovers plugins and renders their contributions. Runs before the workspace loads its
    /// settings, because loading the toolbar layout drops ids it does not know, so plugin toolbar actions must be
    /// registered first.
    /// </summary>
    /// <remarks>
    /// Spec §5.2 budgets the span from <c>backend-started</c> to <c>plugins-discovered</c>, which the startup budget
    /// check adds up over the <c>plugins-*</c> laps below (laps are written with the mark, so they add no file write to
    /// the span). It holds the manager, the hand-over of the prepared scan (no read, hash or hop) and one contribution
    /// render; the backends of startup plugins start after it.
    /// </remarks>
    private async Task InitializePluginsAsync(FileOperationService fileOps, StartupTimer timer, PendingPluginScan pending)
    {
        timer.Lap("plugins-begin");
        var cts = new CancellationTokenSource();
        _pluginCts = cts;
        // Read here, on the UI thread: WindowsFor may run on any thread.
        _pluginWindowQueue = DispatcherQueue;
        _pluginWorkspace = new PluginWorkspaceView(
            DispatcherQueue,
            () => _workspace,
            () => SelectedPaths ?? [],
            line => AppendPluginLog("host", line));
        var manager = new PluginManager(new PluginManagerOptions
        {
            // The prepared scan's own inputs: DiscoverAsync uses it only when they match.
            Roots = pending.Roots,
            ApprovalStorePath = pending.ApprovalStorePath,
            // <install>\plugins-data, or the smokes' disposable SUMAFILE_PLUGIN_DATA_ROOT; never %LOCALAPPDATA%.
            DataRoot = PluginDiscovery.DataRoot(),
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
                () => PickFolderAsync(null),
                (message, isError) => ShowMessage(
                    PluginDisplayName(pluginId),
                    message,
                    isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational)),
            Workspace = _pluginWorkspace,
            Log = AppendPluginLog,
            // The current client, so a reconnected service is used; ReconnectBackendAsync then registers the
            // live engines with it again (ReregisterArchiveEnginesAsync).
            ArchiveEngines = new ArchiveEngineRegistrar(() => _backend?.Client),
            // First-party plugins listed in <install root>\bundled.json are trusted without a consent dialog.
            BundledRoot = pending.BundledRoot,
            // Plugin code (activation, commands, sidebar and settings-page providers) runs on this window's thread.
            UiThread = new PluginUiThread(DispatcherQueue),
            // A new, unsealed host for every plugin runtime; teardown seals and closes the old one.
            WindowsFor = WindowsFor,
            // IPluginHost.Panels: shows and hides the plugin's own panels, always queued to this window's thread.
            PanelsFor = CreatePluginPanels,
            // IPluginHost.Decorations and WriteSettingAsync: re-decorate the shown folders. Only starts thread-pool work
            // (the coordinator calls every source with Task.Run), so it is safe from the UI thread and from plugin code.
            InvalidateDecorations = pluginId => _ = ObserveAsync(_workspace?.InvalidateDecorationsAsync() ?? Task.CompletedTask),
            ValidateUiElement = element => element is FrameworkElement
                ? null
                : $"returned {element.GetType().FullName}, not a Microsoft.UI.Xaml.FrameworkElement",
        });
        _plugins = manager;
        timer.Lap("plugins-manager-built");

        try
        {
            // Usually complete by now: it ran while the file service started.
            var prepared = await pending.Scan;
            timer.Lap("plugins-scan-awaited");
            // Nothing left to read or hash, so this completes here, on the UI thread. No backend starts yet.
            await manager.DiscoverAsync(prepared, startBackends: false, cts.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken plugin folder must never stop SumaFile from starting.
            AppendPluginLog("host", $"[error] Plugin discovery failed: {exception}");
        }

        timer.Lap("plugins-evaluated");
        // The one render inside the span (spec §5.2 counts contribution rendering).
        ApplyPluginContributions();
        var discovered = manager.Plugins;
        timer.Mark("plugins-discovered", $"count={discovered.Count}");

        // Only now: the scan's own Changed would have queued a second render into the span. Every later change renders
        // again, and nothing can change a plugin before this line: no backend has started and the user cannot act yet.
        manager.Changed += OnPluginsChanged;
        // After the mark, so no launch competes with the span; an archive engine registers once its backend runs.
        manager.StartStartupBackends();
        // After the mark too: copies legacy settings into bundled plugins' declared settings (spec §8.1). Activation
        // waits for it, bounded, so no plugin reads a setting before the copy.
        _ = ObserveAsync(manager.MigrateLegacySettingsAsync(cts.Token));

        // After the mark, so log writes stay out of the discovery budget. The smokes point the overrides at
        // disposable locations; say so, so a stray override on a user machine is visible.
        foreach (var line in PluginDiscovery.DescribeOverrides())
        {
            AppendPluginLog("host", line);
        }

        // One line per plugin: the startup smoke reads it as its proof of bundled trust.
        foreach (var record in discovered)
        {
            AppendPluginLog(record.Id, $"[info] Discovered: {record.State}");
        }
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
        // The workspace exists now: hand the coordinator the enabled decoration providers. Its startup gate holds every
        // request until ready (QueuePluginSectionActivation).
        ApplyRowDecorationSource();
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
            // Changed follows every engine-set change, so this never reads the capabilities from before it.
            _ = RefreshArchiveCapabilitiesAsync();
        });
    }

    private void ApplyPluginContributions()
    {
        var enabled = _plugins?.EnabledPlugins ?? [];
        AppCommandCatalog.SetPluginCommands(PluginContributionAdapter.Commands(enabled));
        ToolbarActionCatalog.SetPluginActions(PluginContributionAdapter.ToolbarActions(enabled));
        _pluginMenus = PluginContributionAdapter.ContextMenus(enabled);
        SyncPluginSidebarSections(enabled);
        SyncPluginPanels(enabled);
        ApplyRowDecorationSource();
        if (ApplyPluginToolbarPlacements(enabled))
        {
            ApplyCommandSurfaceLayout();
        }
    }

    /// <summary>
    /// Gives the workspace's decoration coordinator the enabled plugins' row decoration providers, and re-decorates the
    /// shown folders when that set changed. A no-op until the workspace exists. It only queues: it awaits nothing and
    /// calls no source, and the coordinator starts every source call with <c>Task.Run</c>, so a provider's activation
    /// never runs inside <see cref="ApplyPluginContributions"/> (carry-over #82).
    /// </summary>
    private void ApplyRowDecorationSource()
    {
        if (_plugins is not { } manager || _workspace is not { } workspace)
        {
            return;
        }

        var source = new PluginRowDecorationSource(manager, PluginContributionAdapter.RowDecorationProviders(manager.EnabledPlugins));
        if (workspace.RowDecorations.SetSource(source))
        {
            _ = ObserveAsync(workspace.InvalidateDecorationsAsync());
        }
    }

    /// <summary>
    /// Settings applied its toolbar layout: the enabled plugins offer their primary items again, under the offered-once
    /// rule, then the toolbar is rebuilt. After a Reset the saved layout has no offers, so their items come back (spec
    /// §7); after any other save every offer was carried, so an item the user removed stays removed
    /// (ToolbarResetOfferTests). The Settings save that follows writes the layout, so nothing is saved here.
    /// </summary>
    private void ApplySettingsCommandSurfaceLayout()
    {
        ApplyPluginToolbarPlacements(_plugins?.EnabledPlugins ?? [], save: false);
        ApplyCommandSurfaceLayout();
    }

    /// <summary>
    /// Puts primary-placement plugin actions on the toolbar layout and, when <paramref name="save"/>, saves it when that
    /// added any. Returns false (and does nothing) until the workspace has loaded its settings.
    /// </summary>
    private bool ApplyPluginToolbarPlacements(IReadOnlyList<PluginRecord> enabled, bool save = true)
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

        if (added && save)
        {
            _ = RunUiActionAsync("Toolbar", () => workspace.SaveUiSettingsAsync());
        }

        return true;
    }

    /// <summary>
    /// The manager asks for a host whenever it creates a plugin runtime, possibly off the UI thread and under its lock,
    /// so this only creates and records one. A closed host stays sealed, so it is never handed out again.
    /// </summary>
    private IPluginWindowHost WindowsFor(string pluginId)
    {
        var host = new PluginWindowHost(pluginId, _pluginWindowQueue ?? throw new InvalidOperationException("Plugins are not available."));
        lock (_pluginWindowHostsGate)
        {
            _pluginWindowHosts[pluginId] = host;
        }

        return host;
    }

    /// <summary>The window host of a plugin's current (or last) runtime; the UI smoke reads its open windows.</summary>
    internal PluginWindowHost? PluginWindowHostFor(string pluginId)
    {
        lock (_pluginWindowHostsGate)
        {
            return _pluginWindowHosts.GetValueOrDefault(pluginId);
        }
    }

    /// <summary>
    /// Seals every plugin's window host and closes its windows now, on the UI thread. <c>OnClosed</c> calls it, so plugin
    /// windows close as soon as the main window does; the plugin teardown that follows finds nothing to close.
    /// </summary>
    private void ClosePluginWindows()
    {
        PluginWindowHost[] hosts;
        lock (_pluginWindowHostsGate)
        {
            hosts = [.. _pluginWindowHosts.Values];
        }

        foreach (var host in hosts)
        {
            try
            {
                host.CloseAllNow();
            }
            catch (Exception exception)
            {
                AppendPluginLog(host.PluginId, $"[warn] Closing the plugin's windows failed: {exception.Message}");
            }
        }

        // A detached panel's window that no host tracks yet (its plugin's first activation had not finished) closes too.
        if (_pluginPanelWindow is not null)
        {
            try
            {
                HidePluginPanel();
            }
            catch (Exception exception)
            {
                AppendPluginLog("host", $"[warn] Closing the plugin panel's window failed: {exception.Message}");
            }
        }
    }

    private Task RunPluginCommandAsync(string hostCommandId) =>
        RunUiActionAsync("Plugin", async () =>
        {
            var plugins = _plugins ?? throw new InvalidOperationException("Plugins are not available.");
            // A panel's command (palette, menus, RunAppCommandAsync) shows its panel; the plugin's ICommandHandler never
            // receives it. Its toolbar button toggles the panel instead (OnGeneratedToolbarCommandClick).
            if (PluginPanelLayout.ForCommand(_pluginPanels, hostCommandId) is { } panel)
            {
                await ShowPluginPanelAsync(panel.PluginId, panel.PanelId);
                return;
            }

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
        // The plugins are going: no source, so no decoration request reaches the manager while it stops.
        _workspace?.RowDecorations.SetSource(null);
        AppCommandCatalog.SetPluginCommands([]);
        ToolbarActionCatalog.SetPluginActions([]);
        ClearPluginSidebarSections();
        // Before the manager stops: closes a detached panel's window and drops every panel body.
        ClearPluginPanels();
        try
        {
            await manager.DisposeAsync();
        }
        catch (Exception exception)
        {
            AppendPluginLog("host", $"[error] Stopping plugins failed: {exception}");
        }

        // Every runtime is retired, so every host is sealed: forget them.
        lock (_pluginWindowHostsGate)
        {
            _pluginWindowHosts.Clear();
        }
    }

    private string PluginDisplayName(string pluginId) =>
        _plugins?.Plugins.FirstOrDefault(record => record.Id == pluginId)?.Discovered.Manifest?.Name ?? pluginId;

    private static readonly Lazy<int> WindowsSessionId = new(() => System.Diagnostics.Process.GetCurrentProcess().SessionId);

    // Same session.pid scheme as the file service's pipe (BackendSession), so pipes never collide across sessions.
    private static string PluginPipeName(string pluginId) =>
        $"SumaFile.Plugin.{WindowsSessionId.Value}.{Environment.ProcessId}.{pluginId}";

    /// <summary>Awaits background plugin work that nothing else awaits and logs a failure to plugins.log.</summary>
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppendPluginLog("host", $"[error] Background plugin work failed: {exception}");
        }
    }

    /// <summary>
    /// <c>&lt;install&gt;\plugins.log</c>, or the smokes' disposable <c>SUMAFILE_PLUGIN_LOG_PATH</c>; resolved once.
    /// </summary>
    private static readonly string PluginLogPath = PluginDiscovery.LogPath();

    /// <summary>
    /// Appends to <see cref="PluginLogPath"/>, rotating at 1 MB like service.log. When its folder cannot be written
    /// (an install folder the user cannot write to) the line is dropped, like any failed log write; it never falls back
    /// to %LOCALAPPDATA%.
    /// </summary>
    private static void AppendPluginLog(string pluginId, string line)
    {
        try
        {
            var logPath = PluginLogPath;
            if (Path.GetDirectoryName(logPath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

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
