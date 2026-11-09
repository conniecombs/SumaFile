using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Contributions;
using SimpleFile.PluginSdk;

namespace SimpleFile.App;

/// <summary>
/// The plugin panel host (spec §7's <c>PluginPanelHost</c>): not a class, but the <c>PluginPanelBottomRegion</c> and
/// <c>PluginPanelRightRegion</c> regions of <c>MainWindow.xaml</c>, driven from here. One panel is visible at a time,
/// docked in its placement's region with a resize grip, or detached into a host-owned <see cref="PluginPanelWindow"/>.
/// The chrome comes from the manifest; the body comes from <see cref="PluginManager.CreatePanelAsync"/>, through the
/// body-load queue, once per presentation. A hidden panel keeps no element.
/// </summary>
public sealed partial class MainWindow
{
    private const string PluginPanelRightPlacement = "right";
    private const double PluginPanelGripWidth = 8;

    /// <summary>The enabled plugins' panels, from the last contribution render.</summary>
    private IReadOnlyList<PluginPanel> _pluginPanels = [];

    /// <summary>The docked chrome of each panel shown so far, by <see cref="PluginPanelLayout.Key"/>; built on first use.</summary>
    private readonly Dictionary<string, PluginPanelChrome> _pluginPanelChromes = new(StringComparer.Ordinal);

    /// <summary>The one shown panel, docked or detached; null when none is.</summary>
    private PluginPanel? _shownPluginPanel;

    /// <summary>The shown panel's window while it is detached.</summary>
    private PluginPanelWindow? _pluginPanelWindow;

    /// <summary>
    /// <see cref="PluginPanelLayout.StateKey"/> of the shown panel. The UI thread writes it whenever a panel is shown or
    /// hidden; <see cref="PluginPanelsService.IsVisible"/> reads it from any thread.
    /// </summary>
    private volatile string? _shownPluginPanelStateKey;

    private bool _pluginPanelGripDragging;
    private bool _pluginPanelGripMoved;
    private double _pluginPanelGripStartPosition;
    private double _pluginPanelGripStartSize;

    /// <summary>The shown panel while it is docked; null when none is shown or it is in its window.</summary>
    private PluginPanel? DockedPluginPanel => _pluginPanelWindow is null ? _shownPluginPanel : null;

    /// <summary>
    /// Records the enabled plugins' panels and hides the shown panel when its plugin or epoch left. Called from
    /// <c>ApplyPluginContributions</c>, so also inside the startup span: it builds no element.
    /// </summary>
    private void SyncPluginPanels(IReadOnlyList<PluginRecord> enabled)
    {
        var wanted = PluginContributionAdapter.Panels(enabled);
        _pluginPanels = wanted;
        if (_shownPluginPanel is { } shown && PluginPanelLayout.StillWanted(wanted, PluginPanelLayout.Key(shown)) is null)
        {
            HidePluginPanel();
        }

        ForgetUnwantedPluginPanelChromes(wanted);
        RefreshPluginPanelToggleButtons();
    }

    /// <summary>Drops the chrome of every panel whose plugin or epoch left, with any body load still queued for it.</summary>
    private void ForgetUnwantedPluginPanelChromes(IReadOnlyList<PluginPanel> wanted)
    {
        var keep = wanted.Select(PluginPanelLayout.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _pluginPanelChromes.Keys.Where(key => !keep.Contains(key)).ToList())
        {
            if (_pluginPanelChromes.Remove(key, out var chrome))
            {
                ReleasePluginPanelChrome(chrome);
                chrome.Removed = true;
            }
        }
    }

    /// <summary>
    /// Shows the panel: hides any other shown panel, docked or detached, then docks this one in its placement's region,
    /// or activates its window when it is detached. Builds the chrome on first use and queues the body load at low
    /// priority, so a panel's first activation never runs inline inside a host pass (#82).
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin declares no such panel, or it is not enabled.</exception>
    internal Task ShowPluginPanelAsync(string pluginId, string panelId)
    {
        var panel = _pluginPanels.FirstOrDefault(item =>
            string.Equals(item.PluginId, pluginId, StringComparison.Ordinal)
            && string.Equals(item.PanelId, panelId, StringComparison.Ordinal));
        if (panel is null)
        {
            return Task.FromException(new InvalidOperationException($"Plugin '{pluginId}' has no panel '{panelId}'."));
        }

        if (_shownPluginPanel is { } shown && string.Equals(PluginPanelLayout.Key(shown), PluginPanelLayout.Key(panel), StringComparison.Ordinal))
        {
            if (_pluginPanelWindow is { } window)
            {
                window.Activate();
                return Task.CompletedTask;
            }
        }
        else
        {
            HidePluginPanel();
        }

        var chrome = DockedPluginPanelChrome(panel);
        _shownPluginPanel = panel;
        PlacePluginPanelChrome(chrome);
        PublishShownPluginPanel();
        if (chrome.Body.Content is null)
        {
            var presentation = chrome.Presentation;
            _pluginBodyLoads.Request(PluginPanelLayout.BodyLoadKey(panel), () => LoadPluginPanelBodyAsync(chrome, presentation, detached: false));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The toolbar button's action: hides the panel <paramref name="hostCommandId"/> toggles when it is shown (docked or
    /// detached), and shows it otherwise. A command that toggles no panel runs as a plugin command.
    /// </summary>
    internal Task TogglePluginPanelAsync(string hostCommandId)
    {
        if (PluginPanelLayout.ForCommand(_pluginPanels, hostCommandId) is not { } panel)
        {
            return RunPluginCommandAsync(hostCommandId);
        }

        if (IsPluginPanelShown(panel))
        {
            HidePluginPanel();
            return Task.CompletedTask;
        }

        return ShowPluginPanelAsync(panel.PluginId, panel.PanelId);
    }

    /// <summary>Hides the shown panel, docked or detached, and drops its body: a hidden panel keeps no element.</summary>
    internal void HidePluginPanel()
    {
        var shown = _shownPluginPanel;
        _shownPluginPanel = null;
        if (shown is not null)
        {
            _pluginBodyLoads.Forget(PluginPanelLayout.BodyLoadKey(shown));
            if (_pluginPanelChromes.TryGetValue(PluginPanelLayout.Key(shown), out var chrome))
            {
                ReleasePluginPanelChrome(chrome);
            }
        }

        ClearPluginPanelRegions();
        if (_pluginPanelWindow is { } window)
        {
            // Cleared first, so the window's Closed handler finds nothing left to hide.
            _pluginPanelWindow = null;
            ReleasePluginPanelWindow(window);
            if (!window.IsClosed)
            {
                try
                {
                    window.Close();
                }
                catch (Exception exception)
                {
                    AppendPluginLog(window.Panel.PluginId, $"[warn] Closing panel '{window.Panel.PanelId}''s window failed: {exception.Message}");
                }
            }
        }

        PublishShownPluginPanel();
    }

    /// <summary>Hides the panel with this <see cref="PluginPanelLayout.StateKey"/> when it is the shown one; else nothing.</summary>
    private void HidePluginPanelIfShown(string stateKey)
    {
        if (_shownPluginPanel is { } shown && string.Equals(PluginPanelLayout.StateKey(shown), stateKey, StringComparison.Ordinal))
        {
            HidePluginPanel();
        }
    }

    /// <summary>
    /// Moves the docked, detachable panel into a new <see cref="PluginPanelWindow"/>, tracked by the plugin's window
    /// host. The docked body is dropped and the plugin is asked again with <c>Detached</c> true, because a WinUI element
    /// cannot move to another window.
    /// </summary>
    private Task DetachPluginPanelAsync()
    {
        if (DockedPluginPanel is not { Detachable: true } panel)
        {
            return Task.CompletedTask;
        }

        _pluginBodyLoads.Forget(PluginPanelLayout.BodyLoadKey(panel));
        if (_pluginPanelChromes.TryGetValue(PluginPanelLayout.Key(panel), out var docked))
        {
            ReleasePluginPanelChrome(docked);
        }

        ClearPluginPanelRegions();
        var stateKey = PluginPanelLayout.StateKey(panel);
        var window = new PluginPanelWindow(
            panel,
            () => _ = RunUiActionAsync("Plugin panel", DockPluginPanelAsync),
            () => HidePluginPanelIfShown(stateKey));
        window.Closed += (_, _) => OnPluginPanelWindowClosed(window);
        _pluginPanelWindow = window;
        PublishShownPluginPanel();

        // The current runtime's host. Before the plugin's first activation there may be none yet, or only the sealed one
        // of a retired runtime; the body load then tracks the window once the activation created the runtime's host.
        if (PluginWindowHostFor(panel.PluginId) is { IsSealed: false } host)
        {
            window.TrackedByHost = true;
            host.TrackHostWindow(window);
        }

        if (window.IsClosed)
        {
            return Task.CompletedTask;
        }

        window.Activate();
        _pluginBodyLoads.Request(PluginPanelLayout.BodyLoadKey(panel), () => LoadPluginPanelBodyAsync(window.Chrome, window.Chrome.Presentation, detached: true, window));
        return Task.CompletedTask;
    }

    /// <summary>Closes the detached panel's window and docks the panel again, with a new body (<c>Detached</c> false).</summary>
    private Task DockPluginPanelAsync()
    {
        if (_pluginPanelWindow is not { } window || _shownPluginPanel is not { } panel)
        {
            return Task.CompletedTask;
        }

        _pluginPanelWindow = null;
        _pluginBodyLoads.Forget(PluginPanelLayout.BodyLoadKey(panel));
        ReleasePluginPanelWindow(window);
        window.CloseForDock();

        var chrome = DockedPluginPanelChrome(panel);
        PlacePluginPanelChrome(chrome);
        PublishShownPluginPanel();
        var presentation = chrome.Presentation;
        _pluginBodyLoads.Request(PluginPanelLayout.BodyLoadKey(panel), () => LoadPluginPanelBodyAsync(chrome, presentation, detached: false));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A panel window closed: by its Close button, the title-bar X, Alt+F4, the window host's teardown or the main
    /// window's close. Unless it closed for docking, that hides the panel.
    /// </summary>
    private void OnPluginPanelWindowClosed(PluginPanelWindow window)
    {
        ReleasePluginPanelWindow(window);
        if (window.ClosingForDock || !ReferenceEquals(window, _pluginPanelWindow))
        {
            return;
        }

        _pluginPanelWindow = null;
        try
        {
            HidePluginPanel();
        }
        catch (Exception exception)
        {
            // The main window may be closing too; its regions go with it.
            AppendPluginLog(window.Panel.PluginId, $"[warn] Hiding panel '{window.Panel.PanelId}' failed: {exception.Message}");
        }
    }

    /// <summary>Forgets every panel; part of stopping the plugins.</summary>
    private void ClearPluginPanels()
    {
        try
        {
            HidePluginPanel();
            foreach (var chrome in _pluginPanelChromes.Values)
            {
                ReleasePluginPanelChrome(chrome);
                chrome.Removed = true;
            }
        }
        catch (Exception exception)
        {
            // The window may already be closing; the panels go with it.
            AppendPluginLog("host", $"[warn] Clearing the plugin panels failed: {exception.Message}");
        }

        _pluginPanelChromes.Clear();
        _pluginPanels = [];
        _shownPluginPanel = null;
        _pluginPanelWindow = null;
        _shownPluginPanelStateKey = null;
    }

    private bool IsPluginPanelShown(PluginPanel panel) =>
        _shownPluginPanel is { } shown
        && string.Equals(PluginPanelLayout.StateKey(shown), PluginPanelLayout.StateKey(panel), StringComparison.Ordinal);

    private static bool IsRightPluginPanel(PluginPanel panel) =>
        string.Equals(panel.Placement, PluginPanelRightPlacement, StringComparison.Ordinal);

    private PluginPanelChrome DockedPluginPanelChrome(PluginPanel panel)
    {
        var key = PluginPanelLayout.Key(panel);
        if (!_pluginPanelChromes.TryGetValue(key, out var chrome))
        {
            var stateKey = PluginPanelLayout.StateKey(panel);
            chrome = PluginPanelChrome.Create(
                panel,
                detached: false,
                () => _ = RunUiActionAsync("Plugin panel", DetachPluginPanelAsync),
                () => HidePluginPanelIfShown(stateKey));
            _pluginPanelChromes[key] = chrome;
        }

        return chrome;
    }

    /// <summary>Ends the chrome's presentation: a body load still running for it is dropped, and its body is emptied.</summary>
    private static void ReleasePluginPanelChrome(PluginPanelChrome chrome)
    {
        chrome.Presentation++;
        chrome.Body.Content = null;
    }

    private static void ReleasePluginPanelWindow(PluginPanelWindow window)
    {
        window.Chrome.Removed = true;
        window.Chrome.Presentation++;
        try
        {
            window.Chrome.Body.Content = null;
        }
        catch (Exception)
        {
            // The window's content is already gone, and the body with it.
        }
    }

    private void PlacePluginPanelChrome(PluginPanelChrome chrome)
    {
        ClearPluginPanelRegions();
        if (IsRightPluginPanel(chrome.Panel))
        {
            PluginPanelRightRegion.Child = chrome.Root;
        }
        else
        {
            PluginPanelBottomHost.Child = chrome.Root;
        }

        ApplyPluginPanelLayout();
    }

    private void ClearPluginPanelRegions()
    {
        PluginPanelRightRegion.Child = null;
        PluginPanelBottomHost.Child = null;
        ApplyPluginPanelLayout();
    }

    /// <summary>Sizes and shows the region of the docked panel, and collapses the other (both when none is docked).</summary>
    private void ApplyPluginPanelLayout()
    {
        var docked = DockedPluginPanel;
        var right = docked is not null && IsRightPluginPanel(docked) && PluginPanelRightRegion.Child is not null;
        var bottom = docked is not null && !IsRightPluginPanel(docked) && PluginPanelBottomHost.Child is not null;

        PluginPanelRightGripColumn.Width = new GridLength(right ? PluginPanelGripWidth : 0);
        PluginPanelRightColumn.Width = new GridLength(right ? CurrentPluginPanelRightWidth() : 0);
        PluginPanelRightGrip.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        PluginPanelRightRegion.Visibility = right ? Visibility.Visible : Visibility.Collapsed;

        PluginPanelBottomRegion.Height = bottom ? CurrentPluginPanelBottomHeight() : double.NaN;
        PluginPanelBottomRegion.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The docked right panel's width: the saved width, clamped to the space beside the panes.</summary>
    private double CurrentPluginPanelRightWidth() => PluginPanelLayout.ClampRightWidth(
        _workspace?.Settings.PluginPanelRightWidth ?? PluginPanelLayout.RightDefaultWidth,
        PanesAndPanelGrid.ActualWidth);

    /// <summary>The docked bottom panel's height: the saved height, clamped to the file pane area.</summary>
    private double CurrentPluginPanelBottomHeight() => PluginPanelLayout.ClampBottomHeight(
        _workspace?.Settings.PluginPanelBottomHeight ?? PluginPanelLayout.BottomDefaultHeight,
        FilePaneShell.ActualHeight);

    private void OnPluginPanelHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DockedPluginPanel is not null)
        {
            ApplyPluginPanelLayout();
        }
    }

    /// <summary>
    /// Grows (positive) or shrinks the docked panel by <paramref name="delta"/> pixels, clamped by
    /// <see cref="PluginPanelLayout"/>, and writes the size to <see cref="UiSettings"/> (saved by the caller). The one
    /// code path the grips' drag and the UI smoke use.
    /// </summary>
    internal void ResizePluginPanelBy(double delta)
    {
        if (DockedPluginPanel is not { } panel || _workspace is not { } workspace || !double.IsFinite(delta))
        {
            return;
        }

        var settings = workspace.Settings;
        if (IsRightPluginPanel(panel))
        {
            settings.PluginPanelRightWidth = UiSettings.NormalizePluginPanelRightWidth(
                PluginPanelLayout.ClampRightWidth(CurrentPluginPanelRightWidth() + delta, PanesAndPanelGrid.ActualWidth));
        }
        else
        {
            settings.PluginPanelBottomHeight = UiSettings.NormalizePluginPanelBottomHeight(
                PluginPanelLayout.ClampBottomHeight(CurrentPluginPanelBottomHeight() + delta, FilePaneShell.ActualHeight));
        }

        ApplyPluginPanelLayout();
    }

    private double CurrentPluginPanelSize(PluginPanel panel) =>
        IsRightPluginPanel(panel) ? CurrentPluginPanelRightWidth() : CurrentPluginPanelBottomHeight();

    /// <summary>The pointer's position along the docked panel's resize axis: x for a right panel, y for a bottom one.</summary>
    private double PluginPanelGripPosition(PointerRoutedEventArgs e, PluginPanel panel) =>
        IsRightPluginPanel(panel)
            ? e.GetCurrentPoint(PanesAndPanelGrid).Position.X
            : e.GetCurrentPoint(FilePaneShell).Position.Y;

    private void OnPluginPanelGripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (DockedPluginPanel is not { } panel || sender is not UIElement grip)
        {
            return;
        }

        _pluginPanelGripDragging = true;
        _pluginPanelGripMoved = false;
        _pluginPanelGripStartPosition = PluginPanelGripPosition(e, panel);
        _pluginPanelGripStartSize = CurrentPluginPanelSize(panel);
        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPluginPanelGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pluginPanelGripDragging || DockedPluginPanel is not { } panel)
        {
            return;
        }

        // The panel sits right of or below its grip, so moving the pointer left or up makes it larger.
        var requested = _pluginPanelGripStartSize + (_pluginPanelGripStartPosition - PluginPanelGripPosition(e, panel));
        var before = CurrentPluginPanelSize(panel);
        ResizePluginPanelBy(requested - before);
        if (Math.Abs(CurrentPluginPanelSize(panel) - before) > 0.5)
        {
            _pluginPanelGripMoved = true;
        }

        e.Handled = true;
    }

    private async void OnPluginPanelGripReleased(object sender, PointerRoutedEventArgs e)
    {
        var wasDragging = _pluginPanelGripDragging;
        _pluginPanelGripDragging = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        var workspace = _workspace;
        if (wasDragging && _pluginPanelGripMoved && workspace is not null)
        {
            await RunUiActionAsync("Resize plugin panel", () => workspace.SaveUiSettingsAsync());
        }
    }

    /// <summary>A double-tap on a grip resets the docked panel to its default size.</summary>
    private async void OnPluginPanelGripDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (DockedPluginPanel is not { } panel || _workspace is not { } workspace)
        {
            return;
        }

        if (IsRightPluginPanel(panel))
        {
            workspace.Settings.PluginPanelRightWidth = PluginPanelLayout.RightDefaultWidth;
        }
        else
        {
            workspace.Settings.PluginPanelBottomHeight = PluginPanelLayout.BottomDefaultHeight;
        }

        ApplyPluginPanelLayout();
        await RunUiActionAsync("Resize plugin panel", () => workspace.SaveUiSettingsAsync());
    }

    /// <summary>Publishes the shown panel's state for <see cref="IPluginPanels.IsVisible"/> and redraws the toggles.</summary>
    private void PublishShownPluginPanel()
    {
        _shownPluginPanelStateKey = _shownPluginPanel is { } shown ? PluginPanelLayout.StateKey(shown) : null;
        RefreshPluginPanelToggleButtons();
    }

    /// <summary>Draws each panel's generated toolbar button as a toggle: "Hide &lt;title&gt;" while shown, else "Show".</summary>
    private void RefreshPluginPanelToggleButtons()
    {
        foreach (var pair in _primaryToolbarActionElements)
        {
            if (pair.Value is not Button button || PluginPanelLayout.ForCommand(_pluginPanels, pair.Key) is not { } panel)
            {
                continue;
            }

            SetToggleChromeState(button, IsPluginPanelShown(panel), "Hide " + panel.Title, "Show " + panel.Title);
        }
    }

    /// <summary>
    /// Loads a panel's body once per presentation: activates the plugin and calls its <see cref="IPanelProvider"/> on
    /// the UI thread. Runs from the body-load queue, never inline. A body that arrives after its presentation ended is
    /// dropped; a failure shows in the body.
    /// </summary>
    private async Task LoadPluginPanelBodyAsync(PluginPanelChrome chrome, int presentation, bool detached, PluginPanelWindow? window = null)
    {
        if (_plugins is not { } manager)
        {
            return;
        }

        bool Current() => !chrome.Removed && chrome.Presentation == presentation && ReferenceEquals(manager, _plugins);
        if (!Current())
        {
            return;
        }

        var panel = chrome.Panel;
        try
        {
            var element = await manager.CreatePanelAsync(
                panel.PluginId,
                panel.PanelId,
                detached,
                _pluginCts?.Token ?? CancellationToken.None);
            if (!Current())
            {
                return;
            }

            if (window is { TrackedByHost: false, IsClosed: false })
            {
                // The activation created the runtime, and with it the window host that teardown closes.
                window.TrackedByHost = true;
                PluginWindowHostFor(panel.PluginId)?.TrackHostWindow(window);
                if (window.IsClosed || !Current())
                {
                    return;
                }
            }

            // ValidateUiElement accepted it, so it is a FrameworkElement.
            chrome.Body.Content = element;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppendPluginLog(panel.PluginId, $"[error] Panel '{panel.PanelId}' could not be loaded: {exception.Message}");
            if (!Current())
            {
                return;
            }

            var failure = new TextBlock
            {
                Text = $"This panel could not be loaded: {exception.Message}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12),
            };
            if (ChromeStyle("SfEmptyHintStyle") is { } hintStyle)
            {
                failure.Style = hintStyle;
            }

            AutomationProperties.SetAutomationId(failure, AutomationProperties.GetAutomationId(chrome.Root) + ".Error");
            chrome.Body.Content = failure;
        }
    }

    /// <summary>A plugin panel service for <c>IPluginHost.Panels</c>; one per plugin runtime (<c>PanelsFor</c>).</summary>
    private IPluginPanels CreatePluginPanels(string pluginId) =>
        new PluginPanelsService(this, _pluginWindowQueue ?? throw new InvalidOperationException("Plugins are not available."), pluginId);

    /// <summary>
    /// <see cref="IPluginPanels"/> for one plugin, callable from any thread. <see cref="ShowAsync"/> and
    /// <see cref="Hide"/> are always queued to the window's thread, never run inline, so a plugin calling them from a
    /// command, a provider or a decoration request never re-enters a host pass.
    /// </summary>
    private sealed class PluginPanelsService(MainWindow window, DispatcherQueue dispatcher, string pluginId) : IPluginPanels
    {
        /// <summary>Completes once the panel is shown; fails for a panel the plugin does not declare or is not enabled.</summary>
        public Task ShowAsync(string panelId, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(panelId);
            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled(ct);
            }

            return PluginUiDispatch.EnqueueAsync(dispatcher, async () =>
            {
                ct.ThrowIfCancellationRequested();
                await window.ShowPluginPanelAsync(pluginId, panelId);
                return true;
            });
        }

        /// <summary>Hides the panel if it is the shown one; nothing when another panel is shown.</summary>
        public void Hide(string panelId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(panelId);
            var stateKey = pluginId + ":" + panelId;
            dispatcher.TryEnqueue(() => window.HidePluginPanelIfShown(stateKey));
        }

        public bool IsVisible(string panelId) =>
            string.Equals(window._shownPluginPanelStateKey, pluginId + ":" + panelId, StringComparison.Ordinal);
    }
}
