#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.Ipc;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Trust;
using Windows.Foundation;
using Windows.Graphics;

namespace SimpleFile.App;

// Runs against the real XAML host and Rust service. Excluded from release builds.
public sealed partial class MainWindow
{
    private static string? UiSmokeRoot()
    {
        var root = Environment.GetEnvironmentVariable("SIMPLEFILE_UI_SMOKE_ROOT");
        return !string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, ".sumafile-ui-smoke"))
            ? Path.GetFullPath(root) : null;
    }

    private async Task<bool> RunUiSmokeIfRequestedAsync()
    {
        var root = UiSmokeRoot();
        if (root is null) return false;
        // ConnectAsync marked ready just before this call: the smoke's record of it.
        var readyAt = DateTimeOffset.UtcNow;
        var results = new List<object>();
        string? failure = null;
        var gaps = new List<double>();
        var clock = Stopwatch.StartNew();
        var currentStage = "initializing";
        async Task CheckpointAsync(string stage)
        {
            currentStage = stage;
            var path = Path.Combine(root, "progress.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(new
            {
                stage, elapsedMs = clock.Elapsed.TotalMilliseconds, results
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        var lastTick = clock.Elapsed.TotalMilliseconds;
        var heartbeat = DispatcherQueue.CreateTimer();
        heartbeat.Interval = TimeSpan.FromMilliseconds(30);
        heartbeat.Tick += (_, _) =>
        {
            var now = clock.Elapsed.TotalMilliseconds;
            gaps.Add(now - lastTick);
            lastTick = now;
        };
        heartbeat.Start();
        try
        {
            var workspace = _workspace!;
            var baseline = Environment.GetEnvironmentVariable("SIMPLEFILE_UI_SMOKE_BASELINE") == "1";
            var settings = UiSettings.CreateDefault();
            settings.PreviewVisible = false;
            settings.SidebarVisible = false;
            workspace.ApplyUiSettings(settings);
            ApplyPreviewVisibility();
            SyncFromWorkspace();
            // The startup folder, the decoration gate's real path. Nothing has yielded since ConnectAsync queued the
            // Low-priority ready callback, so the gate must still be closed; the navigation starts now, before the first
            // await, and races that callback as a real startup navigation does. SmokePluginsAsync checks the result.
            SmokeStartupFolder? startupFolder = null;
            if (Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_PLUGINS") == "with")
            {
                var gateOpen = workspace.RowDecorations.IsStartupReady;
                var folder = Path.Combine(root, "hello-decorated");
                var navigation = workspace.NavigatePaneAsync(PaneId.Primary, folder);
                startupFolder = new SmokeStartupFolder(gateOpen, folder, navigation, workspace.Primary.NavigationToken, readyAt);
            }
            await CheckpointAsync("plugins");
            await SmokePluginsAsync(results, CheckpointAsync, startupFolder);
            var empty = Path.Combine(root, "empty");
            var folders = Directory.GetDirectories(Path.Combine(root, "fixtures"))
                .OrderBy(path => int.Parse(Path.GetFileName(path))).ToArray();
            foreach (var folder in folders)
            {
                await CheckpointAsync($"large-folder-{Path.GetFileName(folder)}");
                workspace.SetFileListView(PaneId.Primary, "list");
                var expected = Directory.GetFiles(folder).Length;
                double? firstPaint = null;
                var timer = Stopwatch.StartNew();
                void OnLayout(object? sender, object args)
                {
                    if (firstPaint is null && PrimaryFiles.Count > 0 && PrimaryFileList.ContainerFromIndex(0) is not null)
                        firstPaint = timer.Elapsed.TotalMilliseconds;
                }
                PrimaryFileList.LayoutUpdated += OnLayout;
                try
                {
                    await workspace.NavigatePaneAsync(PaneId.Primary, folder);
                    await SmokeWaitAsync(() => PrimaryFiles.Count == expected && firstPaint is not null, "listing and first painted row");
                    await Task.Delay(200);
                    var navigationMs = timer.Elapsed.TotalMilliseconds;
                    var realized = SmokeDescendants<ListViewItem>(PrimaryFileList).Count();
                    if (!baseline) SmokeAssert(realized < 1000 && realized < expected / 2, $"List view realized {realized} of {expected} rows");
                    await SmokeCheckListAsync(PrimaryFileList, baseline);
                    using var process = Process.GetCurrentProcess();
                    results.Add(new { scenario = "large-folder", entries = expected, firstPaintMs = firstPaint,
                        navigationMs, realizedRows = realized, workingSetBytes = process.WorkingSet64,
                        managedBytes = GC.GetTotalMemory(false) });
                }
                finally { PrimaryFileList.LayoutUpdated -= OnLayout; }

                // Start a second navigation while the first is pending; late chunks must not repaint it.
                await workspace.NavigatePaneAsync(PaneId.Primary, empty);
                var abandoned = workspace.NavigatePaneAsync(PaneId.Primary, folder);
                var replacement = workspace.NavigatePaneAsync(PaneId.Primary, empty);
                await Task.WhenAll(abandoned, replacement);
                await SmokeWaitAsync(() => !workspace.Primary.ListingInProgress && PrimaryFiles.Count == 0, "navigation replacement");
                await Task.Delay(200);
                SmokeAssert(workspace.Primary.Path == empty && PrimaryFiles.Count == 0, "Stale listing replaced the newer folder");
                results.Add(new { scenario = "navigate-during-loading", entries = expected, passed = true });
            }

            if (baseline) return true;

            // Exercise narrow panes and repeated changes between virtualizing panel types.
            await CheckpointAsync("layout");
            var sample = folders[0];
            await workspace.NavigatePaneAsync(PaneId.Primary, sample);
            if (!workspace.DualPaneEnabled) await workspace.ToggleDualPaneAsync();
            await workspace.NavigatePaneAsync(PaneId.Secondary, sample);
            var sawDetails = false;
            var sawNarrowDetails = false;
            foreach (var width in new[] { 1000, 1280, 1920 })
            {
                var scale = RootGrid.XamlRoot.RasterizationScale;
                var appliedWidth = int.TryParse(Environment.GetEnvironmentVariable("SIMPLEFILE_UI_SMOKE_MAX_WIDTH"), out var maximum)
                    ? Math.Min(width, maximum) : width;
                AppWindow.Resize(new SizeInt32((int)Math.Round(appliedWidth * scale), (int)Math.Round(840 * scale)));
                await Task.Delay(200);
                // The product preserves the primary pane's pixel width on resize.
                // Recenter the divider for each test size so both panes get tested.
                workspace.Settings.DualPanePrimaryWidth = 0;
                workspace.Settings.DualPanePrimaryPercent = UiSettings.DualPaneDefaultPercent;
                ApplyDualPaneLayout();
                await Task.Delay(200);
                foreach (var view in new[] { "list", "tiles", "details", "list" })
                {
                    workspace.SetFileListView(PaneId.Primary, view);
                    workspace.SetFileListView(PaneId.Secondary, view);
                    ApplyFileListViewPresentation();
                    await Task.Delay(200);
                    if (view == "details")
                    {
                        sawDetails |= EffectiveFileListViewForPane(PaneId.Primary) == "details";
                        SmokeAssert(EffectiveFileListViewForPane(PaneId.Primary) == "details", "Resizing changed the requested Details view");
                        sawNarrowDetails |= PrimaryFileViewport.ActualWidth < 720;
                    }
                    foreach (var list in new[] { PrimaryFileList, SecondaryFileList }.Where(list => list.Visibility == Visibility.Visible))
                        await SmokeCheckListAsync(list, baseline);
                    foreach (var details in new[] { PrimaryDetailsFileList, SecondaryDetailsFileList }.Where(list => list.Visibility == Visibility.Visible))
                    {
                        var inner = SmokeDescendants<ListView>(details).Single();
                        await SmokeCheckListAsync(inner, baseline);
                    }
                    results.Add(new { scenario = "dual-pane-layout", width, appliedWidth, requestedView = view,
                        actualWindowWidth = RootGrid.ActualWidth,
                        primaryViewportWidth = PrimaryFileViewport.ActualWidth,
                        secondaryViewportWidth = SecondaryFileViewport.ActualWidth,
                        effectivePrimary = EffectiveFileListViewForPane(PaneId.Primary), passed = true });
                }
            }

            // Also exercise Details after switching back to a full-width pane.
            await workspace.ToggleDualPaneAsync();
            ApplyDualPaneLayout();
            await SmokeWaitAsync(() => SecondaryPaneRoot.Visibility == Visibility.Collapsed
                && PrimaryFileViewport.ActualWidth >= 400, "full-width pane for Details");
            foreach (var view in new[] { "list", "tiles", "details", "list" })
            {
                workspace.SetFileListView(PaneId.Primary, view);
                ApplyColumnWidths();
                ApplyFileListViewPresentation();
                await Task.Delay(200);
                if (view == "details")
                {
                    SmokeAssert(EffectiveFileListViewForPane(PaneId.Primary) == "details",
                        $"Full-width Details is unavailable at {PrimaryFileViewport.ActualWidth}px");
                    sawDetails = true;
                    await SmokeCheckListAsync(SmokeDescendants<ListView>(PrimaryDetailsFileList).Single(), baseline);
                }
                else await SmokeCheckListAsync(PrimaryFileList, baseline);
                results.Add(new { scenario = "single-pane-layout", requestedView = view,
                    primaryViewportWidth = PrimaryFileViewport.ActualWidth,
                    effectivePrimary = EffectiveFileListViewForPane(PaneId.Primary), passed = true });
            }
            SmokeAssert(sawDetails && sawNarrowDetails, "Layout scenarios must exercise Details in wide and narrow panes");
            await CheckpointAsync("ui-polish");
            await SmokeUiPolishAsync(root, sample, results);

            // Use the same transfer runner, progress model, and cancel route as the UI.
            var destination = Path.Combine(root, "destination");
            // Rendering covers every requested size. Keep transfer regressions
            // on the smallest fixture so the 100k listing gate does not also
            // become a 100k durable-copy stress test inside a ten-minute smoke.
            var source = folders[0];
            await CheckpointAsync("transfer-cancellation");
            var transfer = TransferWithConflictAsync([source], destination, move: false);
            await SmokeWaitAsync(() => _transfer!.Operations.Count > 0 && _transfer.Operations[0].OperationId is not null, "transfer start");
            var operation = _transfer!.Operations[0];
            await SmokeWaitAsync(() => operation.ProgressPercent > 0 || operation.IsTerminal, "transfer progress");
            SmokeAssert(!operation.IsTerminal, "Fixture completed before cancellation could be exercised");
            _transfer.Cancel(operation);
            await transfer.WaitAsync(TimeSpan.FromSeconds(30));
            SmokeAssert(operation.Status == TransferOperationStatus.Cancelled, $"Unexpected transfer state: {operation.Status}");
            SmokeAssert(!operation.IsIndeterminate && operation.SummaryText == "Transfer cancelled", "Cancelled UI remained busy");
            var remainingStaging = Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains(".sumafile-partial", StringComparison.Ordinal)).ToArray();
            SmokeAssert(remainingStaging.Length == 0, "Cancellation left staging files: " + string.Join(", ", remainingStaging.Take(4)));
            SmokeAssert(Directory.GetFiles(source).Length == int.Parse(Path.GetFileName(source)), "Cancellation changed the source files");
            results.Add(new { scenario = "transfer-cancellation", status = operation.Status.ToString(), passed = true });
            await SmokeAdaptiveTransfersAsync(root, source, results, CheckpointAsync);
            CloseTransferProgressWindow();
            if (Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_PLUGINS") == "with")
            {
                // Last, just before the report: the reconnect resets the Transfers rows and swaps the IPC client.
                await CheckpointAsync("service-reconnect");
                await SmokeServiceReconnectAsync(results);
            }
        }
        catch (Exception exception) { failure = exception.ToString(); }
        finally
        {
            CloseTransferProgressWindow();
            heartbeat.Stop();
            gaps.Sort();
            var report = new { passed = failure is null, failure, stage = currentStage, results,
                uiHeartbeat = new { samples = gaps.Count, maxGapMs = gaps.Count == 0 ? 0 : gaps[^1],
                    p95GapMs = gaps.Count == 0 ? 0 : gaps[(int)((gaps.Count - 1) * .95)] } };
            await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            await CleanupSessionAsync(saveWorkspace: false, unwatchDirectory: true);
            Close();
        }
        return true;
    }

    /// <summary>
    /// SUMAFILE_SMOKE_PLUGINS=with: the Hello fixture is discovered, pre-approved, on the palette, and its command
    /// runs through the normal command path. =none: no plugin is discovered. Unset: nothing is checked.
    /// </summary>
    private async Task SmokePluginsAsync(List<object> results, Func<string, Task> checkpoint, SmokeStartupFolder? startupFolder)
    {
        const string helloId = "com.sumafile.hello";
        const string greetCommand = "plugin:" + helloId + ":greet";
        var mode = Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_PLUGINS");
        // Every mode: plugin backends write under the smoke's disposable data root, never the user's.
        var dataRootOverride = Environment.GetEnvironmentVariable(PluginDiscovery.DataRootVariable);
        SmokeAssert(!string.IsNullOrWhiteSpace(dataRootOverride), $"{PluginDiscovery.DataRootVariable} is not set");
        var dataRoot = PluginDiscovery.DataRoot();
        SmokeAssert(string.Equals(dataRoot, dataRootOverride!.Trim(), StringComparison.OrdinalIgnoreCase),
            $"Plugin data root is {dataRoot}, expected {dataRootOverride}");
        SmokeAssert(SmokeIsUnder(dataRoot, UiSmokeRoot()!), $"Plugin data root {dataRoot} is outside the smoke root");
        SmokePluginLocations(dataRoot);
        if (mode == "with")
        {
            await checkpoint("plugin-startup-folder");
            var startupDecorations = await SmokeStartupFolderDecoratedAsync(startupFolder);
            var manager = _plugins ?? throw new InvalidOperationException("Plugin manager was not created");
            var hello = manager.Plugins.FirstOrDefault(record => record.Id == helloId);
            SmokeAssert(hello is not null, $"Plugin {helloId} was not discovered");
            SmokeAssert(hello!.State == PluginState.Enabled,
                $"Plugin {helloId} is {hello.State}, expected Enabled: {hello.Error}");
            SmokeAssert(AppCommandCatalog.All.Any(command => command.Id == greetCommand), $"Palette is missing {greetCommand}");
            var markerDirectory = Environment.GetEnvironmentVariable("SUMAFILE_HELLO_MARKER_DIR");
            SmokeAssert(!string.IsNullOrWhiteSpace(markerDirectory), "SUMAFILE_HELLO_MARKER_DIR is not set");
            var commandsLog = Path.Combine(markerDirectory!, "commands.log");
            await RunAppCommandAsync(greetCommand);
            await SmokeWaitAsync(
                () => File.Exists(commandsLog) && File.ReadAllLines(commandsLog).Contains(greetCommand),
                "plugin command marker");

            // The fixture declares .hello, so its backend starts at startup and is registered as an archive engine.
            var running = Stopwatch.StartNew();
            while (!(manager.Plugins.FirstOrDefault(record => record.Id == helloId) is { BackendRunning: true }))
            {
                var current = manager.Plugins.FirstOrDefault(record => record.Id == helloId);
                SmokeAssert(running.Elapsed < TimeSpan.FromSeconds(15),
                    $"Plugin {helloId} backend did not start within 15 s ({current?.State}: {current?.Error})");
                await Task.Delay(50);
            }
            SmokeAssert(SmokePluginBackendTempFolders(manager, dataRoot).Contains(helloId),
                $"Plugin {helloId} backend runs but its temp folder was not checked");

            var fileOps = _workspace?.FileOps ?? throw new InvalidOperationException("File operations are not available");
            var capabilities = await fileOps.GetArchiveCapabilitiesAsync();
            var helloFormat = capabilities.Formats.FirstOrDefault(format =>
                string.Equals(format.Extension, ".hello", StringComparison.OrdinalIgnoreCase));
            SmokeAssert(helloFormat is not null, "Archive capabilities do not list .hello: "
                + string.Join(", ", capabilities.Formats.Select(format => format.Extension)));

            // Host to real Rust backend: the fixture logs ping:<greeting> once the backend answered.
            const string pingCommand = "plugin:" + helloId + ":backend-ping";
            await RunAppCommandAsync(pingCommand);
            string? pingLine = null;
            await SmokeWaitAsync(
                () => File.Exists(commandsLog)
                    && (pingLine = File.ReadAllLines(commandsLog).FirstOrDefault(line => line.StartsWith("ping:", StringComparison.Ordinal))) is not null,
                "plugin backend-ping marker");
            SmokeAssert(pingLine!.Length > "ping:".Length, $"backend-ping returned no greeting: {pingLine}");
            await checkpoint("plugin-sidebar");
            var sidebarSection = await SmokePluginSidebarSectionAsync(markerDirectory!, commandsLog);
            await checkpoint("plugin-window");
            var pluginWindow = await SmokePluginWindowAsync(markerDirectory!);
            // Read now: the disable check below closes the window.
            var pluginWindowTitle = pluginWindow.Title;
            await checkpoint("plugin-dialog");
            await SmokePluginDialogAsync(markerDirectory!);
            await checkpoint("plugin-settings-page");
            var settingsPage = await SmokePluginSettingsPageAsync(markerDirectory!);
            await checkpoint("plugin-disable-closes-windows");
            await SmokePluginDisableClosesWindowsAsync(manager, pluginWindow);
            await checkpoint("plugin-window-self-close");
            await SmokePluginWindowSelfCloseReleasesGateAsync();
            // After the window checks: this step disables and re-enables Hello, which would close their window.
            await checkpoint("plugin-panels");
            var pluginPanels = await SmokePluginPanelsAsync(manager, markerDirectory!);
            await checkpoint("plugin-decorations");
            var pluginDecorations = await SmokePluginDecorationsAsync(markerDirectory!, startupFolder!.ReadyAt);
            results.Add(new
            {
                scenario = "plugins",
                mode,
                plugins = manager.Plugins.Count,
                sidebarSection,
                pluginWindow = pluginWindowTitle,
                settingsPage,
                pluginPanels,
                startupDecorations,
                pluginDecorations,
                dataRoot,
                command = greetCommand,
                archiveFormat = helloFormat!.Extension,
                archiveEngine = helloFormat.Engine,
                backendPing = pingLine,
                passed = true,
            });
        }
        else if (mode == "none")
        {
            var pluginCommands = AppCommandCatalog.All.Where(command => command.Id.StartsWith("plugin:", StringComparison.Ordinal))
                .Select(command => command.Id).ToArray();
            SmokeAssert(pluginCommands.Length == 0, "Unexpected plugin commands: " + string.Join(", ", pluginCommands));
            var discovered = _plugins?.Plugins.Select(record => record.Id).ToArray() ?? [];
            SmokeAssert(discovered.Length == 0, "Unexpected plugins: " + string.Join(", ", discovered));
            SmokeAssert(PluginSectionsHost.Children.Count == 0,
                $"Unexpected plugin sidebar sections: {PluginSectionsHost.Children.Count}");
            SmokeAssert(_workspace?.RowDecorations.HasProviders == false, "A row decoration provider is set without plugins");
            results.Add(new { scenario = "plugins", mode, plugins = 0, dataRoot, passed = true });
        }

        if (mode is "with" or "none")
        {
            await checkpoint("sevenzip");
            await SmokeSevenZipAsync(results, mode);
            if (mode == "with")
            {
                await checkpoint("sevenzip-backend-loss");
                await SmokeSevenZipBackendLossAsync(results);
            }

            await checkpoint("remote");
            await SmokeRemoteAsync(results, mode);
            if (mode == "with")
            {
                await checkpoint("remote-backend-loss");
                await SmokeRemoteBackendLossAsync(results);
            }

            await checkpoint("git");
            await SmokeGitAsync(results, mode, checkpoint, startupFolder?.ReadyAt);
        }

        // Every mode, plain included, once the plugin scenarios have started what they start.
        if (_plugins is { } plugins) SmokePluginBackendTempFolders(plugins, dataRoot);
    }

    /// <summary>The startup-folder navigation <see cref="RunUiSmokeIfRequestedAsync"/> starts before its first await.</summary>
    private sealed record SmokeStartupFolder(bool GateOpenAtStart, string Folder, Task Navigation, int NavigationToken, DateTimeOffset ReadyAt);

    /// <summary>
    /// Carry-over (h), Review Focus 6: the folder shown at startup gets its decorations with no further navigation or
    /// refresh, whichever side wins the race between the startup navigation and the ready callback that opens the gate.
    /// </summary>
    private async Task<string> SmokeStartupFolderDecoratedAsync(SmokeStartupFolder? startup)
    {
        SmokeAssert(startup is not null, "The startup-folder navigation was not started");
        SmokeAssert(!startup!.GateOpenAtStart, "The row decoration gate was open before the ready callback could run");
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");
        await startup.Navigation.WaitAsync(TimeSpan.FromSeconds(30));
        string[] names = ["a.txt", "b.txt"];
        await SmokeWaitWithinAsync(
            () => Task.FromResult(names.All(name => PrimaryFiles.Any(row => row.Name == name && row.DecorationText == "hello"))),
            TimeSpan.FromSeconds(10),
            () => "the startup folder's rows to be decorated: "
                + string.Join(", ", PrimaryFiles.Select(row => $"{row.Name}='{row.DecorationText}'"))
                + $" (gate open {workspace.RowDecorations.IsStartupReady})");
        SmokeAssert(workspace.Primary.NavigationToken == startup.NavigationToken,
            $"The startup folder was navigated again: token {workspace.Primary.NavigationToken}, expected {startup.NavigationToken}");
        SmokeAssert(PathRules.PathsEqual(workspace.Primary.Path, startup.Folder),
            $"The primary pane shows {workspace.Primary.Path}, expected {startup.Folder}");
        SmokeAssert(workspace.RowDecorations.IsStartupReady, "The row decoration gate is still closed");
        return startup.Folder;
    }

    /// <summary>
    /// Hello's row decorations through the real coordinator, source and manager: rows, brush keys, folder flags and
    /// the <c>when</c> of its menu item, provider calls off the UI thread and after ready, and the 250 ms budget against
    /// <c>hello-slow</c>'s 600 ms provider. Every wait is bounded at 10 s.
    /// </summary>
    private async Task<object> SmokePluginDecorationsAsync(string markerDirectory, DateTimeOffset readyAt)
    {
        const string flag = SmokeHelloId + ":helloFolder";
        const string menuId = "ctx-plugin-menu:" + SmokeHelloId;
        const string decoratedItem = "Say hello to decorated";
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");
        var root = UiSmokeRoot()!;
        var decorated = Path.Combine(root, "hello-decorated");
        var empty = Path.Combine(root, "empty");
        var slow = Path.Combine(root, "hello-slow");
        var uiThread = Environment.CurrentManagedThreadId;
        var tenSeconds = TimeSpan.FromSeconds(10);
        string Rows() => string.Join(", ", PrimaryFiles.Select(row => $"{row.Name}='{row.DecorationText}'/{row.DecorationBrushKey}"));
        ContextMenuEntry? DecoratedItem(ContextMenuRequest request) =>
            SmokeFlatten(ContextMenuBuilder.Build(request)).FirstOrDefault(entry => entry.Id == menuId)?
                .Children.FirstOrDefault(child => child.Label == decoratedItem);

        // 1. Every row of hello-decorated gets Hello's text and brush key.
        await workspace.NavigatePaneAsync(PaneId.Primary, decorated);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(PrimaryFiles.Count == 2 && PrimaryFiles.All(row =>
                row.DecorationText == "hello" && row.DecorationBrushKey == "SfDecorationAddedBrush"
                && row.DecorationTooltip == "Hello decoration" && row.DecorationPlugins.Contains(SmokeHelloId))),
            tenSeconds,
            () => "hello-decorated's rows to be decorated: " + Rows());

        // 2. With a.txt selected, the menu request carries the folder flag and the selection's decorating plugin, and
        //    Hello's `folderFlag(helloFolder) && hasDecoration` item shows.
        var aPath = Path.Combine(decorated, "a.txt");
        workspace.SelectPath(aPath, PaneId.Primary);
        SyncFromWorkspace();
        IReadOnlyList<FileRow> selected = [];
        await SmokeWaitWithinAsync(
            () => Task.FromResult((selected = SelectedRowsForPane(PaneId.Primary)).Count == 1 && selected[0].Path == aPath),
            tenSeconds,
            () => "a.txt to be selected: " + string.Join(", ", selected.Select(row => row.Name)));
        var request = BuildContextMenuRequest(selected);
        SmokeAssert(request.FolderFlags.Contains(flag), "The menu request's folder flags are: " + string.Join(", ", request.FolderFlags));
        SmokeAssert(request.SelectionDecorationPlugins.Contains(SmokeHelloId),
            "The menu request's selection decoration plugins are: " + string.Join(", ", request.SelectionDecorationPlugins));
        var item = DecoratedItem(request);
        SmokeAssert(item is { Hidden: false }, $"{menuId} does not show '{decoratedItem}' (hidden {item?.Hidden})");

        // 3. A folder Hello does not decorate: no flag, no decoration, and the item is hidden.
        await workspace.NavigatePaneAsync(PaneId.Primary, empty);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(PathRules.PathsEqual(workspace.Primary.Path, empty) && !workspace.Primary.ListingInProgress && PrimaryFiles.Count == 0),
            tenSeconds,
            () => "the empty folder: " + Rows());
        SmokeAssert(PrimaryFiles.All(row => row.DecorationText.Length == 0), "A row of the empty folder is decorated: " + Rows());
        SmokeAssert(workspace.Primary.Decorations.FolderFlags.Count == 0,
            "The empty folder has flags: " + string.Join(", ", workspace.Primary.Decorations.FolderFlags));
        var emptyRequest = BuildContextMenuRequest(SelectedRowsForPane(PaneId.Primary));
        SmokeAssert(!emptyRequest.FolderFlags.Contains(flag), "The empty folder's menu request carries " + flag);
        SmokeAssert(DecoratedItem(emptyRequest) is null or { Hidden: true }, $"'{decoratedItem}' shows in the empty folder");

        // 4. The provider ran on the thread pool, never on the UI thread.
        var log = Path.Combine(markerDirectory, "decorations.log");
        var calls = File.Exists(log) ? File.ReadAllLines(log).Where(line => line.Length > 0).ToArray() : [];
        SmokeAssert(calls.Length > 0, "decorations.log is empty");
        var last = calls[^1];
        SmokeAssert(int.TryParse(last[(last.LastIndexOf('|') + 1)..], out var lastThread) && lastThread != uiThread,
            $"The last decoration call '{last}' ran on the UI thread {uiThread}");
        var onUiThread = calls.Where(line => line.EndsWith("|" + uiThread, StringComparison.Ordinal)).ToArray();
        SmokeAssert(onUiThread.Length == 0, "Decoration calls ran on the UI thread: " + string.Join(", ", onUiThread));

        // 5. No request reached a provider before ready.
        var firstRequest = workspace.RowDecorations.FirstRequestAt;
        SmokeAssert(firstRequest is { } first && first > readyAt, $"The first decoration request was at {firstRequest:O}, ready at {readyAt:O}");

        // 6. hello-slow answers after 600 ms: the listing is published undecorated within the 250 ms budget, and the late
        //    result decorates it afterwards.
        var watch = Stopwatch.StartNew();
        var navigation = workspace.NavigatePaneAsync(PaneId.Primary, slow);
        FileRow? listed;
        while ((listed = PrimaryFiles.FirstOrDefault(row => row.Name == "c.txt")) is null)
        {
            SmokeAssert(watch.Elapsed < tenSeconds, "hello-slow was not listed within 10 s: " + Rows());
            await Task.Delay(10);
        }

        // A row that arrives undecorated was published before the 600 ms provider answered; one held for the provider
        // would arrive decorated. The elapsed time is only recorded: a cold navigation on a busy runner can itself
        // take longer than the provider's delay, so a wall-clock bound would test the runner, not the listing.
        var listedMs = watch.Elapsed.TotalMilliseconds;
        SmokeAssert(listed.DecorationText.Length == 0,
            $"hello-slow's c.txt appeared already decorated after {listedMs:0} ms ('{listed.DecorationText}'): the listing waited for the provider");
        await navigation.WaitAsync(tenSeconds);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(PrimaryFiles.Any(row => row.Name == "c.txt" && row.DecorationText == "hello")),
            tenSeconds,
            () => "hello-slow's late decoration: " + Rows());
        var decoratedMs = watch.Elapsed.TotalMilliseconds;

        return new
        {
            flag,
            menuItem = decoratedItem,
            providerCalls = calls.Length,
            firstRequestAfterReadyMs = (firstRequest!.Value - readyAt).TotalMilliseconds,
            slowListedMs = listedMs,
            slowDecoratedMs = decoratedMs,
        };
    }

    /// <summary>
    /// Phase 4: every plugin file lives in the install folder, so the smoke points the plugin log, the approvals file
    /// and the data root into its own root, and no plugin root may lie in the pre-release %LOCALAPPDATA% location.
    /// </summary>
    private static void SmokePluginLocations(string dataRoot)
    {
        var smokeRoot = UiSmokeRoot()!;
        var logPath = PluginDiscovery.LogPath();
        SmokeAssert(SmokeIsUnder(logPath, smokeRoot), $"Plugin log {logPath} is outside the smoke root");
        var approvalsPath = PluginApprovalStore.DefaultPath();
        SmokeAssert(SmokeIsUnder(approvalsPath, smokeRoot), $"Plugin approvals file {approvalsPath} is outside the smoke root");
        SmokeAssert(SmokeIsUnder(dataRoot, smokeRoot), $"Plugin data root {dataRoot} is outside the smoke root");
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SumaFile");
        var legacyRoots = PluginDiscovery.DefaultRoots()
            .Where(root => SmokeSameFolder(root, legacy) || SmokeIsUnder(root, legacy)).ToArray();
        SmokeAssert(legacyRoots.Length == 0, "Plugin roots lie in a pre-release location: " + string.Join(", ", legacyRoots));
    }

    /// <summary>
    /// Every plugin whose backend runs got its own temp folder, <c>&lt;data root&gt;\&lt;id&gt;\tmp</c> (Task 1 points
    /// its TMP and TEMP there). Returns the ids it checked.
    /// </summary>
    private static string[] SmokePluginBackendTempFolders(PluginManager manager, string dataRoot)
    {
        var running = manager.Plugins.Where(record => record.BackendRunning).Select(record => record.Id).ToArray();
        foreach (var id in running)
        {
            var temp = Path.Combine(dataRoot, id, "tmp");
            SmokeAssert(Directory.Exists(temp), $"Plugin {id} backend runs but its temp folder {temp} does not exist");
        }

        return running;
    }

    /// <summary>
    /// Hello's sidebar section: the manifest header renders, the body loads once the sidebar is shown, the header
    /// command runs the plugin command, and collapse then expand keeps the body. Leaves the sidebar as it found it.
    /// </summary>
    private async Task<string> SmokePluginSidebarSectionAsync(string markerDirectory, string commandsLog)
    {
        const string helloId = "com.sumafile.hello";
        const string sectionId = "hello";
        const string automationId = "PluginSection." + helloId + "." + sectionId;
        const string greetCommand = "plugin:" + helloId + ":greet";
        const string collapseKey = helloId + ":" + sectionId;
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");
        var view = _pluginSectionViews.Values.SingleOrDefault(item => item.Section.PluginId == helloId && item.Section.SectionId == sectionId);
        SmokeAssert(view is not null, $"The sidebar has no section {automationId}");
        SmokeAssert(PluginSectionsHost.Children.Contains(view!.Root), $"{automationId} is not in the sidebar's plugin sections");
        SmokeAssert(AutomationProperties.GetAutomationId(view.Root) == automationId,
            $"Section automation id is {AutomationProperties.GetAutomationId(view.Root)}");
        var header = SmokeLogicalDescendants<TextBlock>(view.Root)
            .FirstOrDefault(text => AutomationProperties.GetAutomationId(text) == automationId + ".Header");
        SmokeAssert(header?.Text == "Hello section", $"Section header reads '{header?.Text}', expected 'Hello section'");

        // The smoke hides the sidebar; showing it is one of the moments the body loads.
        var sidebarWasVisible = workspace.Settings.SidebarVisible;
        workspace.Settings.SidebarVisible = true;
        ApplySidebarLayout();
        try
        {
            var sidebarMarker = Path.Combine(markerDirectory, "sidebar.txt");
            var loading = Stopwatch.StartNew();
            while (!(view.Body.Content is FrameworkElement body && AutomationProperties.GetAutomationId(body) == "Hello.SidebarSection"))
            {
                SmokeAssert(loading.Elapsed < TimeSpan.FromSeconds(10),
                    $"{automationId} did not load its body within 10 s: "
                    + ((view.Body.Content as TextBlock)?.Text ?? view.Body.Content?.GetType().FullName ?? "empty"));
                await Task.Delay(25);
            }

            var element = (TextBlock)view.Body.Content;
            SmokeAssert(element.Text == "Hello from the fixture", $"Section body reads '{element.Text}'");
            SmokeAssert(File.Exists(sidebarMarker) && File.ReadAllText(sidebarMarker) == sectionId,
                $"sidebar.txt does not read '{sectionId}'");

            // The header command button runs the plugin command through the normal command path.
            var command = SmokeLogicalDescendants<Button>(view.Root)
                .FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == automationId + ".Command");
            SmokeAssert(command is not null, $"{automationId}.Command is missing");
            SmokeAssert(AutomationProperties.GetName(command!) == "Hello: greet",
                $"Command button is named '{AutomationProperties.GetName(command!)}'");
            var greetsBefore = File.Exists(commandsLog) ? File.ReadAllLines(commandsLog).Count(line => line == greetCommand) : 0;
            SmokeInvoke(command!);
            await SmokeWaitAsync(
                () => File.Exists(commandsLog) && File.ReadAllLines(commandsLog).Count(line => line == greetCommand) > greetsBefore,
                "sidebar section command marker");

            // Collapse, then expand: the body is hidden, then shown again, and stays the same element.
            var collapse = SmokeLogicalDescendants<Button>(view.Root)
                .FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == automationId + ".Collapse");
            SmokeAssert(collapse is not null, $"{automationId}.Collapse is missing");
            SmokeInvoke(collapse!);
            SmokeAssert(view.Body.Visibility == Visibility.Collapsed, "Collapse did not hide the section body");
            SmokeAssert(workspace.Settings.CollapsedPluginSidebarSections.Contains(collapseKey), "Collapse was not recorded in the settings");
            SmokeInvoke(collapse!);
            SmokeAssert(view.Body.Visibility == Visibility.Visible, "Expand did not show the section body");
            SmokeAssert(!workspace.Settings.CollapsedPluginSidebarSections.Contains(collapseKey), "Expand was not recorded in the settings");
            SmokeAssert(ReferenceEquals(view.Body.Content, element), "Collapse and expand replaced the section body");
            return automationId;
        }
        finally
        {
            workspace.Settings.SidebarVisible = sidebarWasVisible;
            ApplySidebarLayout();
        }
    }

    private const string SmokeHelloId = "com.sumafile.hello";

    /// <summary>
    /// Hello's open-window command: the window is the plugin's own code-built <c>HelloWindow</c> from its load context,
    /// tracked by Hello's <see cref="PluginWindowHost"/>, themed like the main window, with a XamlReader fragment that
    /// resolves the theme's brush; its button shows a dialog on the window's own XamlRoot through the host's gate.
    /// Leaves the window open for the disable check.
    /// </summary>
    private async Task<Window> SmokePluginWindowAsync(string markerDirectory)
    {
        const string openWindow = "plugin:" + SmokeHelloId + ":open-window";
        var dialogLog = Path.Combine(markerDirectory, "dialog.txt");
        await RunAppCommandAsync(openWindow);
        Window? window;
        var opening = Stopwatch.StartNew();
        while ((window = PluginWindowHostFor(SmokeHelloId)?.OpenWindows.SingleOrDefault()) is null)
        {
            SmokeAssert(opening.Elapsed < TimeSpan.FromSeconds(10),
                $"Hello's window host holds {PluginWindowHostFor(SmokeHelloId)?.OpenWindows.Count ?? -1} windows 10 s after {openWindow}");
            await Task.Delay(25);
        }

        SmokeAssert(window.Title == "Hello window", $"The plugin window is titled '{window.Title}'");
        var type = window.GetType();
        SmokeAssert(type.FullName == "SimpleFile.Tests.Fixtures.HelloPlugin.HelloWindow", $"The plugin window is a {type.FullName}");
        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly);
        SmokeAssert(context is not null && !ReferenceEquals(context, System.Runtime.Loader.AssemblyLoadContext.Default),
            $"HelloWindow was loaded into the {context?.Name ?? "unknown"} load context, not the plugin's");
        SmokeAssert(File.Exists(Path.Combine(markerDirectory, "window.txt")), "window.txt was not written");

        var root = window.Content as FrameworkElement;
        SmokeAssert(root is not null, "The plugin window has no FrameworkElement content");
        var mainTheme = (Content as FrameworkElement)?.RequestedTheme;
        SmokeAssert(root!.RequestedTheme == mainTheme, $"The plugin window's theme is {root.RequestedTheme}, the main window's {mainTheme}");
        var body = SmokeLogicalDescendants<TextBlock>(root).FirstOrDefault(text => AutomationProperties.GetAutomationId(text) == "Hello.WindowBody");
        SmokeAssert(body?.Text == "Hello window body", $"Hello.WindowBody reads '{body?.Text}'");
        await SmokeWaitAsync(() => body!.IsLoaded, "plugin window body loaded");
        var themeKey = body!.ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        var expected = Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary)
            && dictionary is ResourceDictionary themed && themed.TryGetValue("SfTextPrimaryBrush", out var brush)
                ? (brush as SolidColorBrush)?.Color
                : null;
        SmokeAssert(expected is not null, $"The {themeKey} theme has no SfTextPrimaryBrush");
        SmokeAssert((body.Foreground as SolidColorBrush)?.Color == expected,
            $"Hello.WindowBody's foreground is {(body.Foreground as SolidColorBrush)?.Color}, expected the {themeKey} SfTextPrimaryBrush {expected}");

        // The window's button shows a dialog on the window's own XamlRoot, never the main window's.
        var button = SmokeLogicalDescendants<Button>(root).FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == "Hello.WindowDialog");
        SmokeAssert(button is not null, "Hello.WindowDialog is missing");
        await SmokeWaitAsync(() => button!.IsLoaded && root.XamlRoot is not null, "plugin window button loaded");
        SmokeInvoke(button!);
        var dialog = await SmokeOpenDialogAsync(root.XamlRoot!, "Hello window dialog");
        SmokeAssert(Content?.XamlRoot is not { } mainRoot
            || !VisualTreeHelper.GetOpenPopupsForXamlRoot(mainRoot).Any(popup => ReferenceEquals(popup.Child, dialog)),
            "The plugin window's dialog opened on the main window");
        SmokeCloseDialog(dialog);
        await SmokeWaitAsync(() => SmokeLastLine(dialogLog) == "window:None", "plugin window dialog result");
        return window;
    }

    /// <summary>Hello's show-dialog command: a plugin-built dialog with no XamlRoot opens on the main window through the gate.</summary>
    private async Task SmokePluginDialogAsync(string markerDirectory)
    {
        const string showDialog = "plugin:" + SmokeHelloId + ":show-dialog";
        var dialogLog = Path.Combine(markerDirectory, "dialog.txt");
        // The command awaits the dialog, so it runs while the smoke closes it.
        var command = RunAppCommandAsync(showDialog);
        var dialog = await SmokeOpenDialogAsync(Content.XamlRoot, "Hello dialog");
        SmokeAssert(DialogHost.IsDialogOpen, "The plugin dialog did not go through DialogHost");
        SmokeCloseDialog(dialog);
        await command.WaitAsync(TimeSpan.FromSeconds(10));
        await SmokeWaitAsync(() => SmokeLastLine(dialogLog) == "None", "plugin dialog result");
    }

    /// <summary>
    /// Settings > Plugins: the Hello card holds the custom page slot with Hello's page, and settings search finds the
    /// page's switch once the page is inserted. Closes the Settings window again.
    /// </summary>
    private async Task<string> SmokePluginSettingsPageAsync(string markerDirectory)
    {
        var slotId = PluginSettingsPanelBuilder.SettingsPageAutomationId(SmokeHelloId);
        var pageMarker = Path.Combine(markerDirectory, "settings-page.txt");
        var showing = _fileOperationDialogs.ShowSettingsAsync("Plugins");
        try
        {
            SettingsWindow? settings = null;
            // Shown, not only loaded: the window renders its cards before ShowAsync activates it.
            await SmokeWaitAsync(() => (settings = _fileOperationDialogs.OpenSettingsWindow) is { IsShown: true }
                && settings.Content is FrameworkElement { IsLoaded: true }
                && settings.SmokePluginsList.Children.Count > 0, "Settings window with plugin cards");

            // Settings > Plugins opens on the overview: a row and a navigation item per plugin, and no plugin's page
            // shown, so Hello's custom page (plugin code) has not been asked for yet.
            var helloTag = PluginSettingsPanelBuilder.NavigationTag(SmokeHelloId);
            SmokeAssert(settings!.SmokeSelectedPlugin is null, $"Settings > Plugins opened on the page of '{settings.SmokeSelectedPlugin}', not on the overview");
            SmokeAssert(settings.SmokePluginNavigationTags.Contains(helloTag),
                $"The Plugins navigation item has no child {helloTag}: [{string.Join(", ", settings.SmokePluginNavigationTags)}]");
            SmokeAssert(settings.SmokePluginsOverviewList.Children.Count == settings.SmokePluginsList.Children.Count,
                $"The overview lists {settings.SmokePluginsOverviewList.Children.Count} plugins for {settings.SmokePluginsList.Children.Count} pages");
            SmokeAssert(!File.Exists(pageMarker), "Hello's settings page was created before its page was opened");
            var pages = settings.SmokePluginsList.Children.OfType<FrameworkElement>().ToArray();
            SmokeAssert(pages.All(item => item.Visibility == Visibility.Collapsed), "A plugin's page is visible on the overview");

            // Hello's own item: only its page is visible, and its custom page loads now.
            settings.SmokeSelectPlugin(SmokeHelloId);
            await SmokeWaitAsync(() => settings.SmokeSelectedPlugin == SmokeHelloId
                && settings.SmokePluginsList.Children.OfType<FrameworkElement>().Count(item => item.Visibility == Visibility.Visible) == 1,
                "Hello's page to be the one visible plugin page");
            ContentPresenter? slot = null;
            var loading = Stopwatch.StartNew();
            while (true)
            {
                // The panel re-renders on plugin changes, so look the card and its slot up again each time.
                var card = settings!.SmokePluginsList.Children.OfType<FrameworkElement>().FirstOrDefault(item =>
                    SmokeLogicalDescendants<TextBlock>(item).Any(text => text.Text == "Hello fixture"));
                slot = card is null ? null : SmokeLogicalDescendants<ContentPresenter>(card)
                    .FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == slotId);
                if (slot is { Content: FrameworkElement page }
                    && SmokeLogicalDescendants<TextBlock>(page).Any(text => AutomationProperties.GetAutomationId(text) == "Hello.SettingsPage"))
                {
                    break;
                }

                SmokeAssert(loading.Elapsed < TimeSpan.FromSeconds(10),
                    $"{slotId} did not show Hello.SettingsPage within 10 s: "
                    + ((slot?.Content as TextBlock)?.Text ?? slot?.Content?.GetType().FullName ?? (slot is null ? "no slot" : "empty")));
                await Task.Delay(25);
            }

            SmokeAssert(File.Exists(pageMarker) && File.ReadAllText(pageMarker) == "hello-page", "settings-page.txt does not read 'hello-page'");

            settings!.SmokeSearch("Hello page switch");
            var searching = Stopwatch.StartNew();
            while (!(settings.SmokePluginsPanel.Visibility == Visibility.Visible
                && settings.SmokeFirstSearchMatch is ToggleSwitch { Header: "Hello page switch" }))
            {
                SmokeAssert(searching.Elapsed < TimeSpan.FromSeconds(10),
                    $"Settings search for 'Hello page switch' shows the Plugins panel {settings.SmokePluginsPanel.Visibility} "
                    + $"with first match {settings.SmokeFirstSearchMatch?.GetType().Name ?? "none"} "
                    + $"'{(settings.SmokeFirstSearchMatch as ToggleSwitch)?.Header ?? (settings.SmokeFirstSearchMatch as TextBlock)?.Text}'");
                await Task.Delay(25);
            }
            return slotId;
        }
        finally
        {
            _fileOperationDialogs.CloseSettingsWindow();
            await showing.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Disabling Hello closes its window and withdraws its sidebar section; enabling brings the section back.</summary>
    private async Task SmokePluginDisableClosesWindowsAsync(PluginManager manager, Window window)
    {
        const string sectionId = "PluginSection." + SmokeHelloId + ".hello";
        var host = PluginWindowHostFor(SmokeHelloId);
        SmokeAssert(host is not null && host.OpenWindows.Contains(window), "The Hello window is not tracked before the disable");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        bool HasSection() => PluginSectionsHost.Children.OfType<FrameworkElement>()
            .Any(item => AutomationProperties.GetAutomationId(item) == sectionId);
        SmokeAssert(HasSection(), $"{sectionId} is missing before the disable");

        // Carry-over (d): a dialog left open on the plugin window holds the app-wide dialog gate until the host hides it.
        var root = window.Content as FrameworkElement;
        SmokeAssert(root?.XamlRoot is not null, "The Hello window has no XamlRoot before the disable");
        var dialogButton = SmokeLogicalDescendants<Button>(root!).FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == "Hello.WindowDialog");
        SmokeAssert(dialogButton is not null, "Hello.WindowDialog is missing before the disable");
        SmokeInvoke(dialogButton!);
        await SmokeOpenDialogAsync(root!.XamlRoot!, "Hello window dialog");
        SmokeAssert(DialogHost.IsDialogOpen, "The Hello window dialog does not hold the dialog gate");

        await manager.DisableAsync(SmokeHelloId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = Stopwatch.StartNew();
        while (!(closed && host!.OpenWindows.Count == 0 && !HasSection() && !DialogHost.IsDialogOpen))
        {
            SmokeAssert(stopping.Elapsed < TimeSpan.FromSeconds(10),
                $"10 s after the disable: window closed {closed}, tracked {host!.OpenWindows.Count}, section shown {HasSection()}, "
                + $"dialog gate held {DialogHost.IsDialogOpen}");
            await Task.Delay(25);
        }

        // The gate is free again: a host dialog on the main window opens and closes.
        var gateCheck = DialogHost.ShowAsync(new ContentDialog
        {
            Title = "Smoke gate check",
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        });
        SmokeCloseDialog(await SmokeOpenDialogAsync(Content.XamlRoot, "Smoke gate check"));
        await gateCheck.WaitAsync(TimeSpan.FromSeconds(10));

        SmokeAssert(host!.IsSealed, "The disabled runtime's window host is not sealed");
        await manager.EnableAsync(SmokeHelloId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await SmokeWaitAsync(HasSection, "Hello sidebar section after re-enable");
        var record = manager.Plugins.FirstOrDefault(item => item.Id == SmokeHelloId);
        SmokeAssert(record?.State == PluginState.Enabled, $"Hello is {record?.State} after re-enable: {record?.Error}");
    }

    /// <summary>
    /// Carry-over (d), user-initiated close: the re-enabled Hello opens its window again and shows its dialog, and the
    /// window then closes on its own (<c>Window.Close()</c>, the path the title-bar X and Alt+F4 take), never through the
    /// host's <c>CloseEach</c>. The host's <c>Closed</c> handler hides the dialog, so the app-wide gate is released.
    /// </summary>
    private async Task SmokePluginWindowSelfCloseReleasesGateAsync()
    {
        const string openWindow = "plugin:" + SmokeHelloId + ":open-window";
        SmokeAssert(!DialogHost.IsDialogOpen, "The dialog gate is held before the self-close check");
        await RunAppCommandAsync(openWindow);
        PluginWindowHost? host = null;
        Window? window = null;
        await SmokeWaitAsync(
            () => (host = PluginWindowHostFor(SmokeHelloId)) is { IsSealed: false }
                && (window = host.OpenWindows.SingleOrDefault()) is { Content: FrameworkElement { XamlRoot: not null } },
            "Hello window after re-enable");
        var closed = false;
        window!.Closed += (_, _) => closed = true;
        var root = (FrameworkElement)window.Content;
        var dialogButton = SmokeLogicalDescendants<Button>(root).FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == "Hello.WindowDialog");
        SmokeAssert(dialogButton is not null, "Hello.WindowDialog is missing before the self-close");
        await SmokeWaitAsync(() => dialogButton!.IsLoaded, "Hello window button loaded before the self-close");
        SmokeInvoke(dialogButton!);
        await SmokeOpenDialogAsync(root.XamlRoot!, "Hello window dialog");
        SmokeAssert(DialogHost.IsDialogOpen, "The Hello window dialog does not hold the dialog gate before the self-close");

        window.Close();
        var closing = Stopwatch.StartNew();
        while (!(closed && host!.OpenWindows.Count == 0 && !DialogHost.IsDialogOpen))
        {
            SmokeAssert(closing.Elapsed < TimeSpan.FromSeconds(10),
                $"10 s after the window closed itself: window closed {closed}, tracked {host!.OpenWindows.Count}, "
                + $"dialog gate held {DialogHost.IsDialogOpen}");
            await Task.Delay(25);
        }

        SmokeAssert(!host!.IsSealed, "A window closing on its own sealed its runtime's window host");
        var gateCheck = DialogHost.ShowAsync(new ContentDialog
        {
            Title = "Smoke gate check after self-close",
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        });
        SmokeCloseDialog(await SmokeOpenDialogAsync(Content.XamlRoot, "Smoke gate check after self-close"));
        await gateCheck.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Hello's panels in the panel slot (phase 4 Task 10), every wait bounded at 10 s:
    /// <list type="number">
    /// <item>Its toggle command shows <c>hello-panel</c> docked on the right, with Hello's body, and never reaches Hello's
    /// command handler.</item>
    /// <item><see cref="ResizePluginPanelBy"/> grows the saved width by 40, clamped, and the region follows.</item>
    /// <item><c>show-bottom</c>, through <c>IPluginHost.Panels</c>, shows <c>hello-bottom</c> and hides the right panel.</item>
    /// <item>Detach opens one <see cref="PluginPanelWindow"/>, tracked by Hello's window host, with a new body whose
    /// <c>HelloGrip</c> is a <see cref="Grid"/> subclass from Hello's load context.</item>
    /// <item>The window's Dock closes it and docks a new body.</item>
    /// <item>A dialog on the detached window's XamlRoot, then Disable: the window closes, the gate is free and no panel
    /// is shown. Then Enable.</item>
    /// <item>The panel opens docked again, and Close hides it: <c>IsVisible</c> is false.</item>
    /// <item>The panel's primary toolbar button toggles it, and reads "Show/Hide Hello panel" after every toolbar rebuild
    /// and chrome refresh, even when the overflow set is unchanged.</item>
    /// </list>
    /// Between steps 1 and 2, a second show and a second body load for the same presentation create no second body.
    /// </summary>
    private async Task<string> SmokePluginPanelsAsync(PluginManager manager, string markerDirectory)
    {
        const string panelId = "hello-panel";
        const string bottomId = "hello-bottom";
        const string toggleCommand = "plugin:" + SmokeHelloId + ":toggle-panel";
        const string showBottomCommand = "plugin:" + SmokeHelloId + ":show-bottom";
        const string rightId = "PluginPanel." + SmokeHelloId + "." + panelId;
        const string bottomChromeId = "PluginPanel." + SmokeHelloId + "." + bottomId;
        const string windowId = "PluginPanelWindow." + SmokeHelloId + "." + panelId;
        var panelLog = Path.Combine(markerDirectory, "panel.txt");
        var dialogLog = Path.Combine(markerDirectory, "dialog.txt");
        var commandsLog = Path.Combine(markerDirectory, "commands.log");
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");
        var panels = CreatePluginPanels(SmokeHelloId);

        static Task WaitAsync(Func<bool> condition, Func<string> describe) =>
            SmokeWaitWithinAsync(() => Task.FromResult(condition()), TimeSpan.FromSeconds(10), describe);
        static string IdOf(object? element) => element is DependencyObject item ? AutomationProperties.GetAutomationId(item) : "";
        static FrameworkElement? BodyOf(FrameworkElement? chrome) => chrome is null
            ? null
            : SmokeLogicalDescendants<ContentPresenter>(chrome)
                .FirstOrDefault(item => IdOf(item) == IdOf(chrome) + ".Body")?.Content as FrameworkElement;
        static bool IsHelloBody(FrameworkElement? body) => body is not null
            && SmokeLogicalDescendants<TextBlock>(body).Any(text => IdOf(text) == "Hello.PanelBody");
        static T? Find<T>(FrameworkElement root, string automationId) where T : FrameworkElement =>
            SmokeLogicalDescendants<T>(root).FirstOrDefault(item => IdOf(item) == automationId);
        FrameworkElement? RightBody() => BodyOf(PluginPanelRightRegion.Child as FrameworkElement);
        string Describe() =>
            $"shown {(_shownPluginPanel is { } shown ? PluginPanelLayout.Key(shown) : "none")}, window {(_pluginPanelWindow is null ? "none" : "open")}, "
            + $"right {PluginPanelRightRegion.Visibility}/{IdOf(PluginPanelRightRegion.Child)}, "
            + $"bottom {PluginPanelBottomRegion.Visibility}/{IdOf(PluginPanelBottomHost.Child)}, "
            + $"panel.txt '{SmokeLastLine(panelLog)}'";
        bool NothingShown() => _shownPluginPanel is null && _pluginPanelWindow is null
            && PluginPanelRightRegion.Visibility == Visibility.Collapsed && PluginPanelRightRegion.Child is null
            && PluginPanelBottomRegion.Visibility == Visibility.Collapsed && PluginPanelBottomHost.Child is null;

        SmokeAssert(NothingShown(), "A plugin panel is shown before the panel check: " + Describe());

        // Hello's primary toolbar button for toggle-panel, drawn as the panel's toggle by every toolbar build.
        Button? ToggleButton() => _primaryToolbarActionElements.TryGetValue(toggleCommand, out var element) ? element as Button : null;
        bool ToggleSelected() => ToggleButton() is { } button && ReferenceEquals(button.Background, Brush("SfBgSelectedBrush"));
        string DescribeToggle() => ToggleButton() is { } button
            ? $"toggle button '{AutomationProperties.GetName(button)}', tooltip '{ToolTipService.GetToolTip(button)}', selected {ToggleSelected()}"
            : "no toggle button";
        void AssertToggle(bool shown, string when) => SmokeAssert(ToggleButton() is { } button
                && AutomationProperties.GetName(button) == (shown ? "Hide Hello panel" : "Show Hello panel")
                && ToolTipService.GetToolTip(button) as string == AutomationProperties.GetName(button)
                && ToggleSelected() == shown,
            $"{when}: {DescribeToggle()}, expected {(shown ? "Hide" : "Show")} Hello panel");
        void RebuildToolbar()
        {
            // A full rebuild: every button is new, while the overflow set stays what it was.
            _appliedPrimaryToolbarSignature = null;
            ApplyCommandSurfaceLayout();
        }

        SmokeAssert(ToggleButton() is not null, $"Hello's primary toolbar button {toggleCommand} is missing");
        AssertToggle(false, "Before any panel is shown");
        RebuildToolbar();
        AssertToggle(false, "After a toolbar rebuild with no panel shown");

        // 1. The toggle command from RunAppCommandAsync shows the right panel, and only the host handles it.
        await RunAppCommandAsync(toggleCommand);
        await WaitAsync(() => PluginPanelRightRegion.Visibility == Visibility.Visible
            && IdOf(PluginPanelRightRegion.Child) == rightId && IsHelloBody(RightBody())
            && SmokeLastLine(panelLog) == panelId + ":False", () => "hello-panel docked: " + Describe());
        SmokeAssert(PluginPanelBottomRegion.Visibility == Visibility.Collapsed, "The bottom region is shown with the right panel");
        SmokeAssert(panels.IsVisible(panelId) && !panels.IsVisible(bottomId), "IsVisible does not report hello-panel alone");
        SmokeAssert(!File.Exists(commandsLog) || !File.ReadAllLines(commandsLog).Contains(toggleCommand),
            "Hello's command handler received the panel command");
        AssertToggle(true, "With hello-panel shown");
        RebuildToolbar();
        AssertToggle(true, "After a toolbar rebuild with hello-panel shown");
        ApplyCommandSurfaceLayout();
        AssertToggle(true, "After a toolbar chrome refresh with hello-panel shown");

        // Once per presentation: showing the shown panel again and a second load for this presentation (as a show
        // during a running load would start) neither call Hello's CreatePanel nor replace the body.
        int DockedBodies() => File.Exists(panelLog) ? File.ReadAllLines(panelLog).Count(line => line == panelId + ":False") : 0;
        var bodiesBefore = DockedBodies();
        var bodyBefore = RightBody();
        await ShowPluginPanelAsync(SmokeHelloId, panelId);
        var shownChrome = _pluginPanelChromes[PluginPanelLayout.Key(_shownPluginPanel!)];
        await LoadPluginPanelBodyAsync(shownChrome, shownChrome.Presentation, detached: false);
        var drained = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => drained.TrySetResult());
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
        SmokeAssert(DockedBodies() == bodiesBefore && ReferenceEquals(RightBody(), bodyBefore),
            $"A second show or body load of the same presentation built another body: panel.txt has {DockedBodies()} docked "
            + $"bodies, expected {bodiesBefore}; body replaced {!ReferenceEquals(RightBody(), bodyBefore)}");

        // 2. The resize path the grips use: the saved width grows by 40, clamped, and the region follows.
        var widthBefore = CurrentPluginPanelRightWidth();
        var expectedWidth = UiSettings.NormalizePluginPanelRightWidth(
            PluginPanelLayout.ClampRightWidth(widthBefore + 40, PanesAndPanelGrid.ActualWidth));
        ResizePluginPanelBy(40);
        SmokeAssert(Math.Abs(workspace.Settings.PluginPanelRightWidth - expectedWidth) < 0.01,
            $"PluginPanelRightWidth is {workspace.Settings.PluginPanelRightWidth} after +40 from {widthBefore}, expected {expectedWidth}");
        SmokeAssert(Math.Abs(PluginPanelRightColumn.Width.Value - expectedWidth) < 0.01,
            $"The right panel column is {PluginPanelRightColumn.Width.Value}, expected {expectedWidth}");
        await WaitAsync(() => Math.Abs(PluginPanelRightRegion.ActualWidth - expectedWidth) < 1,
            () => $"right region width {PluginPanelRightRegion.ActualWidth}, expected {expectedWidth}");

        // 3. show-bottom goes through IPluginHost.Panels: the bottom panel shows and the right one hides.
        await RunAppCommandAsync(showBottomCommand);
        await WaitAsync(() => PluginPanelBottomRegion.Visibility == Visibility.Visible
            && IdOf(PluginPanelBottomHost.Child) == bottomChromeId
            && IsHelloBody(BodyOf(PluginPanelBottomHost.Child as FrameworkElement))
            && SmokeLastLine(panelLog) == bottomId + ":False", () => "hello-bottom docked: " + Describe());
        SmokeAssert(PluginPanelRightRegion.Visibility == Visibility.Collapsed && PluginPanelRightRegion.Child is null,
            "The right region still shows a panel next to the bottom one");
        SmokeAssert(panels.IsVisible(bottomId) && !panels.IsVisible(panelId), "IsVisible does not report hello-bottom alone");
        SmokeAssert(Math.Abs(PluginPanelBottomRegion.Height - CurrentPluginPanelBottomHeight()) < 0.01,
            $"The bottom region is {PluginPanelBottomRegion.Height} high, expected {CurrentPluginPanelBottomHeight()}");
        var bottomChrome = (FrameworkElement)PluginPanelBottomHost.Child!;
        SmokeAssert(Find<Button>(bottomChrome, bottomChromeId + ".Detach") is null, "The non-detachable bottom panel offers Detach");

        // 4. Show hello-panel again and detach it: one tracked window with a new body; the right region is empty.
        await RunAppCommandAsync(toggleCommand);
        await WaitAsync(() => IdOf(PluginPanelRightRegion.Child) == rightId && IsHelloBody(RightBody())
            && PluginPanelBottomRegion.Visibility == Visibility.Collapsed, () => "hello-panel docked again: " + Describe());
        var dockedChrome = (FrameworkElement)PluginPanelRightRegion.Child!;
        var detach = Find<Button>(dockedChrome, rightId + ".Detach");
        SmokeAssert(detach is not null && AutomationProperties.GetName(detach) == "Detach Hello panel",
            $"{rightId}.Detach is missing or named '{(detach is null ? "" : AutomationProperties.GetName(detach))}'");
        await WaitAsync(() => detach!.IsLoaded, () => "Detach button loaded");
        SmokeInvoke(detach!);
        PluginPanelWindow? window = null;
        await WaitAsync(() => (window = _pluginPanelWindow) is { IsClosed: false } && IsHelloBody(BodyOf(window.Chrome.Root))
            && SmokeLastLine(panelLog) == panelId + ":True", () => "hello-panel window: " + Describe());
        var host = PluginWindowHostFor(SmokeHelloId) ?? throw new InvalidOperationException("Hello has no window host");
        SmokeAssert(host.OpenWindows.Count == 1 && ReferenceEquals(host.OpenWindows[0], window),
            $"Hello's window host tracks {host.OpenWindows.Count} windows, expected the panel window alone");
        SmokeAssert(window!.Title == "Hello panel", $"The panel window is titled '{window.Title}'");
        SmokeAssert(IdOf(window.Content) == windowId, $"The panel window's content is {IdOf(window.Content)}");
        SmokeAssert(PluginPanelRightRegion.Child is null && PluginPanelRightRegion.Visibility == Visibility.Collapsed,
            "The right region still holds the detached panel");
        SmokeAssert(panels.IsVisible(panelId), "IsVisible is false for the detached panel");
        var windowBody = BodyOf(window.Chrome.Root)!;
        var grip = Find<Grid>(windowBody, "Hello.PanelGrip");
        SmokeAssert(grip is not null, "Hello.PanelGrip is missing from the detached body");
        var gripType = grip!.GetType();
        var gripContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(gripType.Assembly);
        SmokeAssert(gripType.IsSubclassOf(typeof(Grid)) && gripType.FullName == "SimpleFile.Tests.Fixtures.HelloPlugin.HelloGrip",
            $"Hello.PanelGrip is a {gripType.FullName}, not Hello's Grid subclass");
        SmokeAssert(gripContext is not null && !ReferenceEquals(gripContext, System.Runtime.Loader.AssemblyLoadContext.Default),
            $"HelloGrip was loaded into the {gripContext?.Name ?? "unknown"} load context, not the plugin's");

        // 5. The window's Dock closes it and docks a new body.
        var dock = Find<Button>(window.Chrome.Root, windowId + ".Detach");
        SmokeAssert(dock is not null && AutomationProperties.GetName(dock) == "Dock Hello panel",
            $"{windowId}.Detach is missing or named '{(dock is null ? "" : AutomationProperties.GetName(dock))}'");
        await WaitAsync(() => dock!.IsLoaded, () => "Dock button loaded");
        var detachedWindow = window;
        SmokeInvoke(dock!);
        await WaitAsync(() => detachedWindow.IsClosed && _pluginPanelWindow is null && host.OpenWindows.Count == 0
            && IdOf(PluginPanelRightRegion.Child) == rightId && IsHelloBody(RightBody())
            && SmokeLastLine(panelLog) == panelId + ":False", () => "hello-panel docked after Dock: " + Describe());
        SmokeAssert(!ReferenceEquals(RightBody(), windowBody), "Dock reused the window's body element");

        // 6. Detach again, open a dialog on the window's XamlRoot, then Disable: the window closes and the gate is free.
        detach = Find<Button>((FrameworkElement)PluginPanelRightRegion.Child!, rightId + ".Detach");
        await WaitAsync(() => detach!.IsLoaded, () => "Detach button loaded again");
        SmokeInvoke(detach!);
        PluginPanelWindow? again = null;
        await WaitAsync(() => (again = _pluginPanelWindow) is { IsClosed: false } && IsHelloBody(BodyOf(again.Chrome.Root))
            && host.OpenWindows.Count == 1, () => "hello-panel window again: " + Describe());
        var detachedAgain = again!;
        var dialogButton = Find<Button>(BodyOf(detachedAgain.Chrome.Root)!, "Hello.PanelDialog");
        SmokeAssert(dialogButton is not null, "Hello.PanelDialog is missing");
        await WaitAsync(() => dialogButton!.IsLoaded && dialogButton.XamlRoot is not null, () => "Hello.PanelDialog loaded");
        SmokeInvoke(dialogButton!);
        await SmokeOpenDialogAsync(dialogButton!.XamlRoot!, "Hello panel dialog");
        SmokeAssert(DialogHost.IsDialogOpen, "The panel dialog does not hold the dialog gate");
        SmokeAssert(Content?.XamlRoot is not { } mainRoot
            || !VisualTreeHelper.GetOpenPopupsForXamlRoot(mainRoot).Select(popup => popup.Child).OfType<ContentDialog>()
                .Any(item => Equals(item.Title, "Hello panel dialog")),
            "The detached panel's dialog opened on the main window");

        await manager.DisableAsync(SmokeHelloId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => detachedAgain.IsClosed && host.OpenWindows.Count == 0 && !DialogHost.IsDialogOpen && NothingShown(),
            () => $"after the disable: window closed {detachedAgain.IsClosed}, tracked {host.OpenWindows.Count}, "
                + $"dialog gate held {DialogHost.IsDialogOpen}, " + Describe());
        SmokeAssert(!panels.IsVisible(panelId), "IsVisible is true after the disable");
        await WaitAsync(() => SmokeLastLine(dialogLog) == "panel:None", () => $"dialog.txt ends '{SmokeLastLine(dialogLog)}'");
        await manager.EnableAsync(SmokeHelloId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => _pluginPanels.Any(panel => panel.PluginId == SmokeHelloId && panel.PanelId == panelId),
            () => "Hello's panels after the re-enable: " + string.Join(", ", _pluginPanels.Select(PluginPanelLayout.Key)));
        var record = manager.Plugins.FirstOrDefault(item => item.Id == SmokeHelloId);
        SmokeAssert(record?.State == PluginState.Enabled, $"Hello is {record?.State} after re-enable: {record?.Error}");
        // The re-enable rebuilt the toolbar with a new button for Hello's toggle; it reads as the toggle at once.
        await WaitAsync(() => ToggleButton() is { } rebuilt && AutomationProperties.GetName(rebuilt) == "Show Hello panel",
            () => "Hello's toggle button after the re-enable: " + DescribeToggle());
        AssertToggle(false, "After the re-enable");

        // 7. Shown again it opens docked, not in a window; Close hides it.
        await RunAppCommandAsync(toggleCommand);
        await WaitAsync(() => IdOf(PluginPanelRightRegion.Child) == rightId && IsHelloBody(RightBody())
            && _pluginPanelWindow is null, () => "hello-panel docked after the re-enable: " + Describe());
        SmokeAssert((PluginWindowHostFor(SmokeHelloId)?.OpenWindows.Count ?? 0) == 0, "A panel window is open after the re-enable");
        SmokeAssert(panels.IsVisible(panelId), "IsVisible is false for the shown panel");
        var close = Find<Button>((FrameworkElement)PluginPanelRightRegion.Child!, rightId + ".Close");
        SmokeAssert(close is not null && AutomationProperties.GetName(close) == "Close Hello panel",
            $"{rightId}.Close is missing or named '{(close is null ? "" : AutomationProperties.GetName(close))}'");
        await WaitAsync(() => close!.IsLoaded, () => "Close button loaded");
        SmokeInvoke(close!);
        await WaitAsync(NothingShown, () => "after Close: " + Describe());
        SmokeAssert(!panels.IsVisible(panelId), "IsVisible is true after Close");
        AssertToggle(false, "After Close");

        // 8. The toolbar button's click toggles the panel (it shows, then hides), and the button follows.
        OnGeneratedToolbarCommandClick(ToggleButton()!, new RoutedEventArgs());
        await WaitAsync(() => IdOf(PluginPanelRightRegion.Child) == rightId && IsHelloBody(RightBody()) && panels.IsVisible(panelId),
            () => "hello-panel from its toolbar button: " + Describe());
        AssertToggle(true, "After the toolbar button showed hello-panel");
        OnGeneratedToolbarCommandClick(ToggleButton()!, new RoutedEventArgs());
        await WaitAsync(NothingShown, () => "after the toolbar button hid hello-panel: " + Describe());
        AssertToggle(false, "After the toolbar button hid hello-panel");
        SmokeAssert(!File.Exists(commandsLog) || !File.ReadAllLines(commandsLog).Contains(toggleCommand),
            "Hello's command handler received the panel command from the toolbar");
        return panelId + "," + bottomId;
    }

    private static async Task<ContentDialog> SmokeOpenDialogAsync(XamlRoot root, string title)
    {
        ContentDialog? dialog;
        var opening = Stopwatch.StartNew();
        while ((dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(popup => popup.Child).OfType<ContentDialog>()
            .FirstOrDefault(item => Equals(item.Title, title) && item.IsLoaded
                && SmokeDescendants<Button>(item).Any(button => button.Name == "CloseButton"))) is null)
        {
            SmokeAssert(opening.Elapsed < TimeSpan.FromSeconds(10), $"The dialog '{title}' did not open within 10 s");
            await Task.Delay(25);
        }

        return dialog;
    }

    /// <summary>Closes a dialog through its Close button's automation peer, as a user would.</summary>
    private static void SmokeCloseDialog(ContentDialog dialog) =>
        SmokeInvoke(SmokeDescendants<Button>(dialog).First(button => button.Name == "CloseButton"));

    private static string? SmokeLastLine(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).LastOrDefault(line => line.Length > 0) : null;

    /// <summary>The panels and content of a code-built tree, without waiting for layout to realize it.</summary>
    private static IEnumerable<T> SmokeLogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        IEnumerable<DependencyObject> children = root switch
        {
            Panel panel => panel.Children,
            Border { Child: { } child } => [child],
            ContentPresenter { Content: DependencyObject content } => [content],
            ContentControl { Content: DependencyObject content } => [content],
            _ => [],
        };
        foreach (var child in children)
        {
            if (child is T match) yield return match;
            foreach (var descendant in SmokeLogicalDescendants<T>(child)) yield return descendant;
        }
    }

    private const string SevenZipPluginId = "com.sumafile.sevenzip";
    private const string SevenZipEngine = "plugin:" + SevenZipPluginId;
    private const string SevenZipNote = "7-Zip support requires the SumaFile 7-Zip plugin.";
    private const string SevenZipOverrideKey = "plugin." + SevenZipPluginId + ".binaryOverride";
    private const string SevenZipUnicodeFolder = "café 資料";
    private const string SevenZipLiteralFile = "literal [brackets] & spaces.txt";

    // The files scripts/smoke-winui-ui.ps1 packs into sample.7z, relative to <archives>\fixture, '/'-separated.
    private static readonly string[] SevenZipFixtureFiles = ["docs/a.txt", SevenZipUnicodeFolder + "/hello.txt", SevenZipLiteralFile];

    /// <summary>
    /// The 7-Zip plugin end to end. "with": the bundled com.sumafile.sevenzip is trusted through bundled.json and
    /// serves .7z listing, browsing, mutation, Transfers-row create and extract, Cancel, and its binaryOverride
    /// setting. "none": .7z is unavailable everywhere with the plugin note.
    /// </summary>
    private async Task SmokeSevenZipAsync(List<object> results, string mode)
    {
        var workspace = _workspace ?? throw new InvalidOperationException("The workspace is not available");
        var fileOps = workspace.FileOps ?? throw new InvalidOperationException("File operations are not available");
        var archives = SmokeArchivesRoot();
        var sample = Path.Combine(archives, "sample.7z");
        var fixture = Path.Combine(archives, "fixture");
        if (mode == "none")
        {
            await SmokeSevenZipUnavailableAsync(results, workspace, fileOps, sample);
            return;
        }

        // 1. Discovered from the install root, Enabled through bundled.json alone, backend running.
        var manager = _plugins ?? throw new InvalidOperationException("Plugin manager was not created");
        var installRoot = PluginDiscovery.InstallRoot();
        PluginRecord? record = null;
        await SmokeWaitWithinAsync(
            () => Task.FromResult((record = manager.Plugins.FirstOrDefault(item => item.Id == SevenZipPluginId)) is { BackendRunning: true }),
            TimeSpan.FromSeconds(15),
            () => record is null
                ? $"{SevenZipPluginId} was not discovered under {installRoot}"
                : $"{SevenZipPluginId} backend is not running ({record.State}: {record.Error})");
        SmokeAssert(record!.State == PluginState.Enabled, $"{SevenZipPluginId} is {record.State}, expected Enabled: {record.Error}");
        SmokeAssert(SmokeSameFolder(record.Discovered.Root, installRoot),
            $"{SevenZipPluginId} was discovered in {record.Discovered.Root}, expected the install root {installRoot}");
        var approvalsPath = PluginApprovalStore.DefaultPath();
        var approvals = File.Exists(approvalsPath) ? await File.ReadAllTextAsync(approvalsPath) : "";
        SmokeAssert(!approvals.Contains(SevenZipPluginId, StringComparison.OrdinalIgnoreCase),
            $"{approvalsPath} holds a record for {SevenZipPluginId}; bundled trust must not write one");

        // 2. The service serves .7z through the plugin, and the UI recognizes it.
        ArchiveFormatCapability? row = null;
        await SmokeWaitWithinAsync(
            async () => (row = await SmokeSevenZipRowAsync(fileOps)) is { Engine: SevenZipEngine } && ArchivePaths.IsArchiveFile("x.7z"),
            TimeSpan.FromSeconds(15),
            () => $".7z is not served by {SevenZipEngine}: {SmokeDescribe(row)}, IsArchiveFile={ArchivePaths.IsArchiveFile("x.7z")}");
        SmokeAssert(row!.CanList && row.CanExtract && row.CanCreate && row.CanModify && row.Note is null,
            $"Unexpected .7z capabilities: {SmokeDescribe(row)}");

        // 3. The listing names every fixture file exactly.
        var listed = SmokeArchiveFiles(await fileOps.ListArchiveAsync(sample));
        SmokeAssertNames(listed, SmokeExpectedFiles(""), "sample.7z listing");

        // 4. Browse into the archive, into folders inside it, and open it from its parent folder.
        SmokeAssertNames(await SmokePaneNamesAsync(sample), ["docs", SevenZipUnicodeFolder, SevenZipLiteralFile], "pane listing of sample.7z");
        SmokeAssertNames(await SmokePaneNamesAsync(Path.Combine(sample, "docs")), ["a.txt"], "pane listing of sample.7z\\docs");
        SmokeAssertNames(await SmokePaneNamesAsync(Path.Combine(sample, SevenZipUnicodeFolder)), ["hello.txt"],
            $"pane listing of sample.7z\\{SevenZipUnicodeFolder}");
        await SmokePaneNamesAsync(archives);
        var archiveEntry = workspace.Primary.Entries.ToArray()
            .FirstOrDefault(entry => string.Equals(entry.Name, "sample.7z", StringComparison.OrdinalIgnoreCase));
        SmokeAssert(archiveEntry is not null, $"sample.7z is not listed in {archives}");
        await workspace.OpenEntryAsync(archiveEntry!, PaneId.Primary);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(PathRules.PathsEqual(workspace.Primary.Path, sample) && !workspace.Primary.ListingInProgress
                && workspace.Primary.Entries.Count == 3),
            TimeSpan.FromSeconds(15),
            () => $"Opening sample.7z left the pane at {workspace.Primary.Path} ({workspace.Primary.Entries.Count} entries, error '{workspace.ErrorMessage}')");

        // 5. Mutate inside the archive.
        await fileOps.CreateFolderAsync(sample, "smoke-folder");
        var afterFolder = await fileOps.ListArchiveAsync(sample);
        SmokeAssert(afterFolder.Entries.Any(entry => entry.IsDir && SmokeEntryPath(entry) == "smoke-folder"),
            "The listing after creating smoke-folder does not show it: " + string.Join(", ", afterFolder.Entries.Select(SmokeEntryPath)));
        await workspace.RefreshAsync(PaneId.Primary);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(!workspace.Primary.ListingInProgress && workspace.Primary.Entries.Any(entry => entry.Name == "smoke-folder")),
            TimeSpan.FromSeconds(15),
            () => "The pane re-list of sample.7z does not show smoke-folder");

        // 6. Create as a Transfers row.
        var created = Path.Combine(archives, "created.7z");
        var createRow = SmokeEnqueueArchiveTransfer("Creating created.7z", created,
            (progress, started, token) => fileOps.CreateArchiveTrackedAsync([fixture], created, "7z", progress, started, token), archives);
        await SmokeAwaitRowAsync(createRow, TimeSpan.FromSeconds(120), "create created.7z");
        SmokeAssert(createRow.Status == TransferOperationStatus.Completed && createRow.ProgressPercent >= 100,
            $"create created.7z ended {createRow.Status} at {createRow.ProgressPercent}%: {createRow.ErrorMessage}");
        var createdFiles = SmokeArchiveFiles(await fileOps.ListArchiveAsync(created));
        SmokeAssertNames(createdFiles, SmokeExpectedFiles("fixture/"), "created.7z listing");

        // 7. Extract as a Transfers row; the bytes match the fixture.
        var extracted = Path.Combine(archives, "extracted");
        var extractRow = SmokeEnqueueArchiveTransfer("Extracting sample.7z", extracted,
            (progress, started, token) => fileOps.ExtractArchiveTrackedAsync(sample, extracted, progress, started, token), extracted);
        await SmokeAwaitRowAsync(extractRow, TimeSpan.FromSeconds(120), "extract sample.7z");
        SmokeAssert(extractRow.Status == TransferOperationStatus.Completed,
            $"extract sample.7z ended {extractRow.Status}: {extractRow.ErrorMessage}");
        foreach (var file in SevenZipFixtureFiles)
        {
            var expected = await File.ReadAllBytesAsync(Path.Combine(fixture, file));
            var actualPath = Path.Combine(extracted, file);
            SmokeAssert(File.Exists(actualPath), $"Extract did not write {actualPath}");
            var actual = await File.ReadAllBytesAsync(actualPath);
            SmokeAssert(expected.AsSpan().SequenceEqual(actual), $"Extracted bytes differ: {actualPath}");
        }

        // 8. Cancel a long create from its Transfers row.
        var bigFolder = Path.Combine(archives, "big");
        Directory.CreateDirectory(bigFolder);
        var bigFile = Path.Combine(bigFolder, "random.bin");
        await Task.Run(() =>
        {
            using var stream = File.Create(bigFile);
            var buffer = new byte[1 << 20];
            for (var index = 0; index < 256; index++)
            {
                Random.Shared.NextBytes(buffer);
                stream.Write(buffer);
            }
        });
        var big = Path.Combine(archives, "big.7z");
        var cancelRow = SmokeEnqueueArchiveTransfer("Creating big.7z", big,
            (progress, started, token) => fileOps.CreateArchiveTrackedAsync([bigFolder], big, "7z", progress, started, token), archives);
        // The service hands the row its id with a 0/0 "running" update before the engine runs, so a Cancel
        // sent then can stop the create in the service before the plugin sees it. Cancel only once 7za itself
        // reports progress: a non-zero percent needs a plugin update with a known total, and the plugin sends
        // those only while it runs the create, after 7za has started.
        int[] runningSevenZa = [];
        await SmokeWaitWithinAsync(
            () =>
            {
                if (cancelRow.IsTerminal) return Task.FromResult(true);
                if (cancelRow.OperationId is null || cancelRow.ProgressPercent <= 0) return Task.FromResult(false);
                runningSevenZa = SmokeProcessIdsUnder("7za", installRoot);
                return Task.FromResult(runningSevenZa.Length > 0);
            },
            TimeSpan.FromSeconds(60),
            () => $"create big.7z before Cancel: {cancelRow.Status}, id {cancelRow.OperationId ?? "none"}, "
                + $"{cancelRow.ProgressPercent}%, 7za under {installRoot}: {SmokeProcessIdsUnder("7za", installRoot).Length}");
        SmokeAssert(!cancelRow.IsTerminal, $"create big.7z ended {cancelRow.Status} before Cancel: {cancelRow.ErrorMessage}");
        var cancelledAtPercent = cancelRow.ProgressPercent;
        _transfer!.Cancel(cancelRow);
        await SmokeAwaitRowAsync(cancelRow, TimeSpan.FromSeconds(30), "cancel big.7z");
        SmokeAssert(cancelRow.Status == TransferOperationStatus.Cancelled, $"create big.7z ended {cancelRow.Status}: {cancelRow.ErrorMessage}");
        // The backend kills and waits for 7za before it answers the cancelled create.
        var survivors = SmokeProcessIdsUnder("7za", installRoot).Intersect(runningSevenZa).ToArray();
        SmokeAssert(survivors.Length == 0, $"Cancel left 7za running: {string.Join(", ", survivors)}");
        SmokeAssert(!File.Exists(big), "Cancel left big.7z behind");
        var leftovers = Directory.EnumerateFileSystemEntries(archives, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains(".simplefile-new-", StringComparison.Ordinal)).ToArray();
        SmokeAssert(leftovers.Length == 0, "Cancel left staging files: " + string.Join(", ", leftovers));
        File.Delete(bigFile);

        // 9. binaryOverride, which the backend reads through the host on every operation.
        var where = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        SmokeAssert(File.Exists(where), $"{where} is missing");
        string overrideOutcome;
        await fileOps.SetSettingAsync(SevenZipOverrideKey, where);
        try
        {
            try
            {
                var overridden = await fileOps.ListArchiveAsync(sample);
                SmokeAssert(overridden.Entries.Count == 0,
                    $"binaryOverride {where} was ignored: the listing returned {overridden.Entries.Count} entries");
                overrideOutcome = "zero entries";
            }
            catch (IpcException exception)
            {
                overrideOutcome = "failed: " + exception.Message;
            }
        }
        finally
        {
            await fileOps.SetSettingAsync(SevenZipOverrideKey, "");
        }

        SmokeAssertNames(SmokeArchiveFiles(await fileOps.ListArchiveAsync(sample)), SmokeExpectedFiles(""),
            "sample.7z listing after clearing binaryOverride");

        _transfer.ClearCompleted();
        CloseTransferProgressWindow();
        results.Add(new
        {
            scenario = "sevenzip",
            mode,
            engine = row.Engine,
            discoveredFrom = record.Discovered.Root,
            files = listed.Length,
            createdFiles = createdFiles.Length,
            cancelled = cancelRow.Status.ToString(),
            cancelledAtPercent,
            cancelled7zaPids = runningSevenZa,
            binaryOverride = overrideOutcome,
            passed = true,
        });
    }

    private async Task SmokeSevenZipUnavailableAsync(List<object> results, ExplorerWorkspace workspace, FileOperationService fileOps, string sample)
    {
        var capabilities = await fileOps.GetArchiveCapabilitiesAsync();
        var row = await SmokeSevenZipRowAsync(fileOps);
        SmokeAssert(row is { Engine: "unavailable", CanList: false, CanExtract: false, CanCreate: false, CanModify: false, Note: SevenZipNote },
            $"Unexpected .7z row without the plugin: {SmokeDescribe(row)}");
        SmokeAssert(!ArchivePaths.IsArchiveFile("x.7z"), "ArchivePaths recognizes .7z without the plugin");
        SmokeAssert(!WorkspaceNavigationController.IsSupportedArchivePath("x.7z"), "Navigation browses .7z without the plugin");
        string? listError = null;
        try
        {
            await fileOps.ListArchiveAsync(sample);
        }
        catch (IpcException exception)
        {
            listError = exception.Message;
        }

        SmokeAssert(listError == SevenZipNote, $"Listing sample.7z without the plugin gave '{listError}', expected the plugin note");
        await workspace.NavigatePaneAsync(PaneId.Primary, sample);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(!workspace.Primary.ListingInProgress
                && (workspace.ErrorMessage ?? "").Contains(SevenZipNote, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15),
            () => $"The pane at sample.7z shows '{workspace.ErrorMessage}', expected the plugin note");
        var creatable = capabilities.Formats.Where(format => format.CanCreate).ToArray();
        SmokeAssert(!creatable.Any(format => string.Equals(format.Format, "7z", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format.Extension, ".7z", StringComparison.OrdinalIgnoreCase)),
            "Create formats include 7z without the plugin: " + string.Join(", ", creatable.Select(format => format.Format)));
        results.Add(new { scenario = "sevenzip", mode = "none", engine = row!.Engine, note = row.Note, passed = true });
    }

    /// <summary>
    /// The 7-Zip backend dies while a pane is inside a .7z: the plugin faults, .7z becomes unavailable and the
    /// pane shows the note, all with no user action. A restart brings the pane's listing back, also unprompted.
    /// </summary>
    private async Task SmokeSevenZipBackendLossAsync(List<object> results)
    {
        var workspace = _workspace ?? throw new InvalidOperationException("The workspace is not available");
        var fileOps = workspace.FileOps ?? throw new InvalidOperationException("File operations are not available");
        var manager = _plugins ?? throw new InvalidOperationException("Plugin manager was not created");
        var docs = Path.Combine(SmokeArchivesRoot(), "sample.7z", "docs");
        SmokeAssertNames(await SmokePaneNamesAsync(docs), ["a.txt"], "pane listing of sample.7z\\docs before the kill");

        var installRoot = PluginDiscovery.InstallRoot();
        var killed = new List<int>();
        foreach (var pid in SmokeProcessIdsUnder("sumafile-sevenzip-backend", installRoot))
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(5000);
            killed.Add(pid);
        }

        SmokeAssert(killed.Count == 1, $"Expected one 7-Zip backend under {installRoot}, killed {killed.Count}");
        PluginRecord? record = null;
        ArchiveFormatCapability? row = null;
        await SmokeWaitWithinAsync(
            async () =>
            {
                record = manager.Plugins.FirstOrDefault(item => item.Id == SevenZipPluginId);
                row = await SmokeSevenZipRowAsync(fileOps);
                return record is { State: PluginState.Faulted }
                    && row is { Engine: "unavailable", Note: SevenZipNote }
                    && PathRules.PathsEqual(workspace.Primary.Path, docs)
                    && !workspace.Primary.ListingInProgress
                    && workspace.Primary.Entries.Count == 0
                    && (workspace.ErrorMessage ?? "").Contains(SevenZipNote, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(15),
            () => $"After the kill: plugin {record?.State}, .7z {SmokeDescribe(row)}, pane {workspace.Primary.Path} "
                + $"with {workspace.Primary.Entries.Count} entries, error '{workspace.ErrorMessage}'");
        var faulted = record!.State.ToString();

        await manager.RestartBackendAsync(SevenZipPluginId, _pluginCts?.Token ?? CancellationToken.None);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(manager.Plugins.FirstOrDefault(item => item.Id == SevenZipPluginId) is { BackendRunning: true }
                && PathRules.PathsEqual(workspace.Primary.Path, docs)
                && !workspace.Primary.ListingInProgress
                && workspace.Primary.Entries.Any(entry => entry.Name == "a.txt")
                && !(workspace.ErrorMessage ?? "").Contains(SevenZipNote, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15),
            () => $"After the restart: backend running {manager.Plugins.FirstOrDefault(item => item.Id == SevenZipPluginId)?.BackendRunning}, "
                + $"pane {workspace.Primary.Path} with {workspace.Primary.Entries.Count} entries, error '{workspace.ErrorMessage}'");
        results.Add(new { scenario = "sevenzip-backend-loss", mode = "with", killedPid = killed[0], faulted, passed = true });
    }

    /// <summary>
    /// The service dies; after the reconnect the new service serves .7z through the plugin again, and the remote and
    /// Git backends (the App's own clients) survive it, with repo decorated again once listed through the new service.
    /// </summary>
    private async Task SmokeServiceReconnectAsync(List<object> results)
    {
        var backend = _backend ?? throw new InvalidOperationException("The backend session is not available");
        var sample = Path.Combine(SmokeArchivesRoot(), "sample.7z");
        var before = backend.ReconnectCount;
        var pid = backend.ServiceProcessId ?? throw new InvalidOperationException("The service process id is unknown");
        // The remote backend is the App's own client, not the service's: a service restart must leave it alone.
        var installRoot = PluginDiscovery.InstallRoot();
        var remoteBackends = SmokeProcessIdsUnder(SmokeRemoteBackendName, installRoot);
        SmokeAssert(remoteBackends.Length == 1, $"Expected one {SmokeRemoteBackendName} under {installRoot} before the service kill, found {remoteBackends.Length}");
        var gitBackends = await SmokeGitBackendsBeforeServiceKillAsync(installRoot);
        using (var service = Process.GetProcessById(pid))
        {
            service.Kill();
            service.WaitForExit(5000);
        }

        await SmokeWaitWithinAsync(
            () => Task.FromResult(backend.ReconnectCount > before),
            TimeSpan.FromSeconds(30),
            () => $"ReconnectCount stayed {backend.ReconnectCount} after the service was killed");
        ArchiveFormatCapability? row = null;
        string[] files = [];
        await SmokeWaitWithinAsync(
            async () =>
            {
                if (_workspace?.FileOps is not { } fileOps) return false;
                row = await SmokeSevenZipRowAsync(fileOps);
                if (row is not { Engine: SevenZipEngine }) return false;
                files = SmokeArchiveFiles(await fileOps.ListArchiveAsync(sample));
                return true;
            },
            TimeSpan.FromSeconds(30),
            () => $"After the reconnect: .7z {SmokeDescribe(row)}");
        SmokeAssertNames(files, SmokeExpectedFiles(""), "sample.7z listing after the reconnect");
        var remoteAfter = SmokeProcessIdsUnder(SmokeRemoteBackendName, installRoot);
        SmokeAssert(remoteAfter.SequenceEqual(remoteBackends),
            $"The service restart changed {SmokeRemoteBackendName} from [{string.Join(", ", remoteBackends)}] to [{string.Join(", ", remoteAfter)}]");
        var remote = _plugins?.Plugins.FirstOrDefault(item => item.Id == SmokeRemoteId);
        SmokeAssert(remote?.State == PluginState.Enabled, $"{SmokeRemoteId} is {remote?.State} after the service restart: {remote?.Error}");
        await SmokeGitAfterServiceReconnectAsync(installRoot, gitBackends);
        results.Add(new
        {
            scenario = "service-reconnect",
            mode = "with",
            killedPid = pid,
            servicePid = backend.ServiceProcessId,
            reconnectCount = backend.ReconnectCount,
            remoteBackendPid = remoteBackends[0],
            gitBackendPid = gitBackends[0],
            files = files.Length,
            passed = true,
        });
    }

    private static string SmokeArchivesRoot()
    {
        var archives = Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_ARCHIVES");
        SmokeAssert(!string.IsNullOrWhiteSpace(archives) && File.Exists(Path.Combine(archives, "sample.7z")),
            $"SUMAFILE_SMOKE_ARCHIVES does not hold sample.7z: '{archives}'");
        return Path.GetFullPath(archives!);
    }

    private static async Task<ArchiveFormatCapability?> SmokeSevenZipRowAsync(FileOperationService fileOps)
    {
        var rows = (await fileOps.GetArchiveCapabilitiesAsync()).Formats
            .Where(format => string.Equals(format.Extension, ".7z", StringComparison.OrdinalIgnoreCase)).ToArray();
        SmokeAssert(rows.Length <= 1, $"The capabilities hold {rows.Length} .7z rows");
        return rows.FirstOrDefault();
    }

    private static string SmokeDescribe(ArchiveFormatCapability? row) => row is null
        ? "no .7z row"
        : $"engine {row.Engine}, list {row.CanList}, extract {row.CanExtract}, create {row.CanCreate}, modify {row.CanModify}, note '{row.Note}'";

    private static string SmokeEntryPath(ArchiveEntry entry) => entry.Path.Replace('\\', '/').Trim('/');

    private static string[] SmokeArchiveFiles(ArchiveInfo info) =>
        [.. info.Entries.Where(entry => !entry.IsDir).Select(SmokeEntryPath)];

    private static string[] SmokeExpectedFiles(string prefix) => [.. SevenZipFixtureFiles.Select(file => prefix + file)];

    private static void SmokeAssertNames(IEnumerable<string> actual, IEnumerable<string> expected, string what)
    {
        var actualSorted = actual.Order(StringComparer.Ordinal).ToArray();
        var expectedSorted = expected.Order(StringComparer.Ordinal).ToArray();
        SmokeAssert(actualSorted.SequenceEqual(expectedSorted, StringComparer.Ordinal),
            $"{what}: got [{string.Join(" | ", actualSorted)}], expected [{string.Join(" | ", expectedSorted)}]");
    }

    private async Task<string[]> SmokePaneNamesAsync(string path)
    {
        var workspace = _workspace ?? throw new InvalidOperationException("The workspace is not available");
        await workspace.NavigatePaneAsync(PaneId.Primary, path);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(PathRules.PathsEqual(workspace.Primary.Path, path) && !workspace.Primary.ListingInProgress),
            TimeSpan.FromSeconds(30),
            () => $"Listing {path}: the pane is at {workspace.Primary.Path}, error '{workspace.ErrorMessage}'");
        return [.. workspace.Primary.Entries.ToArray().Select(entry => entry.Name)];
    }

    private TransferOperationViewModel SmokeEnqueueArchiveTransfer(
        string title,
        string detail,
        Func<IProgress<ProgressUpdate>, Action<string>, CancellationToken, Task> run,
        string refreshPath)
    {
        var row = EnqueueArchiveTransfer(new ArchiveTransferRequest(title, detail, run, [refreshPath]));
        SmokeAssert(row is not null, $"{title}: no Transfers row was created");
        return row!;
    }

    private static async Task SmokeAwaitRowAsync(TransferOperationViewModel row, TimeSpan timeout, string what)
    {
        try
        {
            await row.CompletionTask.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"UI smoke timed out after {timeout.TotalSeconds:0} s: {what} is {row.Status} at {row.ProgressPercent}%");
        }
    }

    private static async Task SmokeWaitWithinAsync(Func<Task<bool>> condition, TimeSpan timeout, Func<string> describe)
    {
        var timer = Stopwatch.StartNew();
        string? lastError = null;
        while (true)
        {
            bool met;
            try
            {
                met = await condition();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                met = false;
                lastError = exception.Message;
            }

            if (met) return;
            if (timer.Elapsed > timeout)
            {
                throw new TimeoutException($"UI smoke timed out after {timeout.TotalSeconds:0} s: {describe()}"
                    + (lastError is null ? "" : $" (last error: {lastError})"));
            }

            await Task.Delay(50);
        }
    }

    /// <summary>The ids of the running <paramref name="name"/> processes whose image lies under <paramref name="folder"/>.</summary>
    private static int[] SmokeProcessIdsUnder(string name, string folder)
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                string? file;
                try
                {
                    file = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    continue;
                }

                if (file is not null && SmokeIsUnder(file, folder)) ids.Add(process.Id);
            }
        }

        return [.. ids];
    }

    private static string SmokeFolderKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SmokeSameFolder(string left, string right) =>
        string.Equals(SmokeFolderKey(left), SmokeFolderKey(right), StringComparison.OrdinalIgnoreCase);

    private static bool SmokeIsUnder(string path, string folder) =>
        SmokeFolderKey(path).StartsWith(SmokeFolderKey(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static async Task SmokeWaitAsync(Func<bool> condition, string stage)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException($"UI smoke timed out: {stage}");
            await Task.Delay(25);
        }
    }

    private static void SmokeAssert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<T> SmokeDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in SmokeDescendants<T>(child)) yield return descendant;
        }
    }

    private static async Task SmokeCheckListAsync(ListView list, bool baseline)
    {
        var scroll = SmokeDescendants<ScrollViewer>(list).First();
        foreach (var fraction in new[] { 0.0, .5, 1.0, 0.0 })
        {
            scroll.ChangeView(null, scroll.ScrollableHeight * fraction, null, disableAnimation: true);
            await Task.Delay(120);
            var items = SmokeDescendants<ListViewItem>(list).Where(item => item.ActualHeight > 0)
                .Select(item => (item, bounds: item.TransformToVisual(list).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight))))
                .Where(row => row.bounds.Bottom > 0 && row.bounds.Top < list.ActualHeight)
                .OrderBy(row => row.bounds.Top).ThenBy(row => row.bounds.Left).ToArray();
            SmokeAssert(items.Length > 0, "No visible rows after scrolling");
            for (var index = 1; index < items.Length; index++)
            {
                var previous = items[index - 1].bounds;
                var current = items[index].bounds;
                SmokeAssert(current.Top >= previous.Bottom - 1 || current.Left >= previous.Right - 1, "Rows overlap after scrolling/resizing");
            }
            if (!baseline) SmokeAssert(SmokeDescendants<ListViewItem>(list).Count() < 1000, "Too many realized row containers");
        }
        var target = list.Items[^1];
        list.SelectedItem = target;
        list.ScrollIntoView(target);
        await Task.Delay(150);
        SmokeAssert(list.ContainerFromItem(target) is ListViewItem, "Last selected item did not scroll into view");
        SmokeAssert(ReferenceEquals(list.SelectedItem, target), "Selection was lost while recycling containers");
    }
}
#endif
