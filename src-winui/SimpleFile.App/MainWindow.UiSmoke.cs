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
            var empty = Path.Combine(root, "empty");
            var folders = Directory.GetDirectories(Path.Combine(root, "fixtures"))
                .OrderBy(path => int.Parse(Path.GetFileName(path))).ToArray();
            foreach (var folder in folders)
            {
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
            var sample = folders[0];
            await workspace.NavigatePaneAsync(PaneId.Primary, sample);
            if (!workspace.DualPaneEnabled) await workspace.ToggleDualPaneAsync();
            await workspace.NavigatePaneAsync(PaneId.Secondary, sample);
            var sawDetails = false;
            var sawDetailsFallback = false;
            foreach (var width in new[] { 1000, 1280, 1920 })
            {
                var scale = RootGrid.XamlRoot.RasterizationScale;
                AppWindow.Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(840 * scale)));
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
                        sawDetailsFallback |= EffectiveFileListViewForPane(PaneId.Primary) == "list";
                    }
                    foreach (var list in new[] { PrimaryFileList, SecondaryFileList }.Where(list => list.Visibility == Visibility.Visible))
                        await SmokeCheckListAsync(list, baseline);
                    foreach (var details in new[] { PrimaryDetailsFileList, SecondaryDetailsFileList }.Where(list => list.Visibility == Visibility.Visible))
                    {
                        var inner = SmokeDescendants<ListView>(details).Single();
                        await SmokeCheckListAsync(inner, baseline);
                    }
                    results.Add(new { scenario = "dual-pane-layout", width, requestedView = view,
                        primaryViewportWidth = PrimaryFileViewport.ActualWidth,
                        secondaryViewportWidth = SecondaryFileViewport.ActualWidth,
                        effectivePrimary = EffectiveFileListViewForPane(PaneId.Primary), passed = true });
                }
            }
            SmokeAssert(sawDetails && sawDetailsFallback, "Layout scenarios must exercise real Details view and its narrow-pane fallback");

            // Use the same transfer runner, progress model, and cancel route as the UI.
            var destination = Path.Combine(root, "destination");
            var source = folders[^1];
            var transfer = TransferWithConflictAsync([source], destination, move: false);
            await SmokeWaitAsync(() => _transfer!.Operations.Count > 0 && _transfer.Operations[0].OperationId is not null, "transfer start");
            var operation = _transfer!.Operations[0];
            await SmokeWaitAsync(() => operation.ProgressPercent > 0 || operation.IsTerminal, "transfer progress");
            SmokeAssert(!operation.IsTerminal, "Fixture completed before cancellation could be exercised");
            _transfer.Cancel(operation);
            await transfer.WaitAsync(TimeSpan.FromSeconds(30));
            SmokeAssert(operation.Status == TransferOperationStatus.Cancelled, $"Unexpected transfer state: {operation.Status}");
            SmokeAssert(!operation.IsIndeterminate && operation.SummaryText == "Transfer cancelled", "Cancelled UI remained busy");
            SmokeAssert(!Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories)
                .Any(path => path.Contains(".sumafile-partial", StringComparison.Ordinal)), "Cancellation left staging files");
            SmokeAssert(Directory.GetFiles(source).Length == int.Parse(Path.GetFileName(source)), "Cancellation changed the source files");
            results.Add(new { scenario = "transfer-cancellation", status = operation.Status.ToString(), passed = true });
            CloseTransferProgressWindow();
        }
        catch (Exception exception) { failure = exception.ToString(); }
        finally
        {
            heartbeat.Stop();
            gaps.Sort();
            var report = new { passed = failure is null, failure, results,
                uiHeartbeat = new { samples = gaps.Count, maxGapMs = gaps.Count == 0 ? 0 : gaps[^1],
                    p95GapMs = gaps.Count == 0 ? 0 : gaps[(int)((gaps.Count - 1) * .95)] } };
            await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            await CleanupSessionAsync(saveWorkspace: false, unwatchDirectory: true);
            Close();
        }
        return true;
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
