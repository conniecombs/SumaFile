#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
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
            await CheckpointAsync("plugins");
            await SmokePluginsAsync(results);
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
    private async Task SmokePluginsAsync(List<object> results)
    {
        const string helloId = "com.sumafile.hello";
        const string greetCommand = "plugin:" + helloId + ":greet";
        var mode = Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_PLUGINS");
        if (mode == "with")
        {
            var manager = _plugins ?? throw new InvalidOperationException("Plugin manager was not created");
            var hello = manager.Plugins.FirstOrDefault(record => record.Id == helloId);
            SmokeAssert(hello is not null, $"Plugin {helloId} was not discovered");
            SmokeAssert(hello!.State == SimpleFile.PluginHost.PluginState.Enabled,
                $"Plugin {helloId} is {hello.State}, expected Enabled: {hello.Error}");
            SmokeAssert(AppCommandCatalog.All.Any(command => command.Id == greetCommand), $"Palette is missing {greetCommand}");
            var markerDirectory = Environment.GetEnvironmentVariable("SUMAFILE_HELLO_MARKER_DIR");
            SmokeAssert(!string.IsNullOrWhiteSpace(markerDirectory), "SUMAFILE_HELLO_MARKER_DIR is not set");
            var commandsLog = Path.Combine(markerDirectory!, "commands.log");
            await RunAppCommandAsync(greetCommand);
            await SmokeWaitAsync(
                () => File.Exists(commandsLog) && File.ReadAllLines(commandsLog).Contains(greetCommand),
                "plugin command marker");
            results.Add(new { scenario = "plugins", mode, plugins = manager.Plugins.Count, command = greetCommand, passed = true });
        }
        else if (mode == "none")
        {
            var pluginCommands = AppCommandCatalog.All.Where(command => command.Id.StartsWith("plugin:", StringComparison.Ordinal))
                .Select(command => command.Id).ToArray();
            SmokeAssert(pluginCommands.Length == 0, "Unexpected plugin commands: " + string.Join(", ", pluginCommands));
            var discovered = _plugins?.Plugins.Select(record => record.Id).ToArray() ?? [];
            SmokeAssert(discovered.Length == 0, "Unexpected plugins: " + string.Join(", ", discovered));
            results.Add(new { scenario = "plugins", mode, plugins = 0, passed = true });
        }
    }

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
