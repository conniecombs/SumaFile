using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Contributions;

namespace SimpleFile.App;

/// <summary>
/// The sidebar's plugin sections (spec §7): one per manifest <c>contributions.sidebar</c> entry of an Enabled
/// plugin, below the built-in sections. The header comes from the manifest, so no plugin code loads until the body is
/// needed: startup has reached ready, the sidebar is shown and the section is expanded
/// (<see cref="PluginSidebarLayout.ShouldActivate"/>).
/// </summary>
public sealed partial class MainWindow
{
    private const string PluginSectionAutomationPrefix = "PluginSection.";
    private const string CollapseGlyph = "";
    private const string CommandGlyph = "";

    /// <summary>The rendered sections, by <see cref="PluginSidebarLayout.Key"/>.</summary>
    private readonly Dictionary<string, PluginSidebarSectionView> _pluginSectionViews = new(StringComparer.Ordinal);

    // False until startup has marked ready: the bodies load plugin code, which must stay out of the startup path.
    private bool _pluginSectionsReady;

    // Every plugin body load goes through this queue at low priority, so plugin code (the assembly load, activation,
    // the provider) never runs inline inside a host layout pass or workspace sync (carry-over #82). Built on the
    // constructing (UI) thread.
    private readonly PluginBodyLoadQueue _pluginBodyLoads = CreatePluginBodyLoadQueue(
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

    private static PluginBodyLoadQueue CreatePluginBodyLoadQueue(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher) =>
        new(load => dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => load()));

    private StackPanel PluginSectionsHost => SidebarPanel.PluginSections;

    /// <summary>One rendered plugin section: its manifest description and the controls built for it.</summary>
    private sealed class PluginSidebarSectionView(PluginSidebarSection section, StackPanel root, Button collapse, ContentPresenter body)
    {
        public PluginSidebarSection Section { get; } = section;

        public StackPanel Root { get; } = root;

        public Button Collapse { get; } = collapse;

        public ContentPresenter Body { get; } = body;

        /// <summary>True once the body has been asked for; it is asked for at most once per rendered section.</summary>
        public bool Activated { get; set; }

        /// <summary>True once the section left the sidebar; a body that arrives later is dropped.</summary>
        public bool Removed { get; set; }
    }

    /// <summary>
    /// Brings the rendered sections in line with the enabled plugins' manifests. A section whose key is unchanged keeps
    /// its body; a withdrawn one, or one from a retired runtime (a new epoch), is removed and its body dropped.
    /// </summary>
    private void SyncPluginSidebarSections(IReadOnlyList<PluginRecord> enabled)
    {
        var wanted = PluginContributionAdapter.SidebarSections(enabled);
        var (add, removeKeys) = PluginSidebarLayout.Diff(wanted, _pluginSectionViews.Keys.ToList());
        foreach (var key in removeKeys)
        {
            RemovePluginSection(key);
        }

        foreach (var section in add)
        {
            _pluginSectionViews[PluginSidebarLayout.Key(section)] = CreatePluginSectionView(section);
        }

        // The host lists the sections in the order the adapter gives: plugin by plugin, manifest order within each.
        var children = PluginSectionsHost.Children;
        for (var index = 0; index < wanted.Count; index++)
        {
            var root = _pluginSectionViews[PluginSidebarLayout.Key(wanted[index])].Root;
            var current = children.IndexOf(root);
            if (current < 0)
            {
                children.Insert(index, root);
            }
            else if (current != index)
            {
                children.Move((uint)current, (uint)index);
            }
        }

        ApplyPluginSectionCollapse();
        ActivateVisiblePluginSections();
    }

    private void RemovePluginSection(string key)
    {
        if (!_pluginSectionViews.Remove(key, out var view))
        {
            return;
        }

        view.Removed = true;
        _pluginBodyLoads.Forget(PluginSidebarLayout.BodyLoadKey(view.Section));
        // Drop the body so the plugin's element has no parent if the plugin hands the same element out again.
        view.Body.Content = null;
        PluginSectionsHost.Children.Remove(view.Root);
    }

    /// <summary>Forgets every plugin section; part of stopping the plugins.</summary>
    private void ClearPluginSidebarSections()
    {
        try
        {
            foreach (var key in _pluginSectionViews.Keys.ToList())
            {
                RemovePluginSection(key);
            }
        }
        catch (Exception exception)
        {
            // The window may already be closing; the sections go with it.
            AppendPluginLog("host", $"[warn] Clearing the plugin sidebar sections failed: {exception.Message}");
        }

        _pluginSectionViews.Clear();
    }

    private PluginSidebarSectionView CreatePluginSectionView(PluginSidebarSection section)
    {
        var automationId = PluginSectionAutomationPrefix + section.PluginId + "." + section.SectionId;
        var header = new Grid { Margin = new Thickness(4, 0, 0, 4), ColumnSpacing = 2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock { Text = section.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        if (ChromeStyle("SfSidebarHeaderStyle") is { } headerStyle)
        {
            title.Style = headerStyle;
        }

        AutomationProperties.SetAutomationId(title, automationId + ".Header");
        header.Children.Add(title);

        var root = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(root, automationId);
        AutomationProperties.SetName(root, section.Title);

        if (section.CommandId is { } commandId)
        {
            var label = section.CommandLabel ?? section.Title;
            var command = SidebarIconButton(CommandGlyph, automationId + ".Command", label, label);
            command.Click += (_, _) => _ = RunPluginCommandAsync(commandId);
            Grid.SetColumn(command, 1);
            header.Children.Add(command);
        }

        var collapse = SidebarIconButton(
            CollapseGlyph,
            automationId + ".Collapse",
            $"Collapse {section.Title}",
            $"Collapse or expand {section.Title}");
        Grid.SetColumn(collapse, 2);
        header.Children.Add(collapse);

        var body = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(header);
        root.Children.Add(body);

        var view = new PluginSidebarSectionView(section, root, collapse, body);
        collapse.Click += (_, _) => TogglePluginSection(view);
        return view;
    }

    private static Button SidebarIconButton(string glyph, string automationId, string name, string toolTip)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = 10,
                FontFamily = ChromeResource<FontFamily>("SfIconFont") ?? new FontFamily("Segoe Fluent Icons"),
            },
        };
        if (ChromeStyle("SfSidebarIconButtonStyle") is { } style)
        {
            button.Style = style;
        }

        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private bool IsPluginSectionCollapsed(PluginSidebarSection section) =>
        _workspace?.Settings.CollapsedPluginSidebarSections.Contains(PluginSidebarLayout.CollapseKey(section), StringComparer.Ordinal) == true;

    private void TogglePluginSection(PluginSidebarSectionView view)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        // Copy-on-write: a settings save may be reading the old list on the thread pool right now.
        var settings = workspace.Settings;
        var old = settings.CollapsedPluginSidebarSections;
        var key = PluginSidebarLayout.CollapseKey(view.Section);
        if (old.Contains(key, StringComparer.Ordinal))
        {
            settings.CollapsedPluginSidebarSections = [.. old.Where(k => !string.Equals(k, key, StringComparison.Ordinal))];
        }
        else
        {
            settings.CollapsedPluginSidebarSections = [.. old, key];
        }

        ApplyPluginSectionCollapse();
        // Expanding is one of the moments a body may load.
        ActivateVisiblePluginSections();
    }

    /// <summary>Shows or hides each section's body from the saved collapse state (also after a layout switch).</summary>
    private void ApplyPluginSectionCollapse()
    {
        foreach (var view in _pluginSectionViews.Values)
        {
            var collapsed = IsPluginSectionCollapsed(view.Section);
            view.Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            SetExpandGlyph(view.Collapse, collapsed);
        }
    }

    /// <summary>
    /// After startup has marked ready, lets section bodies load: at low priority, so the first paint and input come
    /// first. The loads themselves are queued again (<see cref="_pluginBodyLoads"/>), never run inside this callback.
    /// </summary>
    private void QueuePluginSectionActivation()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pluginSectionsReady = true;
            ActivateVisiblePluginSections();
        });
    }

    /// <summary>
    /// Queues the body load of every section that may load now. Called when startup reaches ready, when the sidebar is
    /// shown, when a section is expanded and when the sections change, often from inside a layout pass or a workspace
    /// sync, so it never loads a body itself: <see cref="_pluginBodyLoads"/> runs the load later, at low priority.
    /// </summary>
    private void ActivateVisiblePluginSections()
    {
        if (_plugins is null)
        {
            return;
        }

        var sidebarVisible = _workspace?.Settings.SidebarVisible == true;
        foreach (var view in _pluginSectionViews.Values.ToList())
        {
            if (PluginSidebarLayout.ShouldActivate(_pluginSectionsReady, sidebarVisible, IsPluginSectionCollapsed(view.Section), view.Activated))
            {
                view.Activated = true;
                _pluginBodyLoads.Request(PluginSidebarLayout.BodyLoadKey(view.Section), () => LoadPluginSectionBodyAsync(view));
            }
        }
    }

    private async Task LoadPluginSectionBodyAsync(PluginSidebarSectionView view)
    {
        // Queued: the section may have left the sidebar, or the plugins stopped, before this ran.
        if (view.Removed || _plugins is not { } manager)
        {
            return;
        }

        var section = view.Section;
        try
        {
            // Activation and the provider run on the UI thread (PluginUiThread); this continues here on it too.
            var element = await manager.CreateSidebarSectionAsync(
                section.PluginId,
                section.SectionId,
                _pluginCts?.Token ?? CancellationToken.None);
            if (view.Removed || !ReferenceEquals(manager, _plugins))
            {
                return;
            }

            // ValidateUiElement accepted it, so it is a FrameworkElement.
            view.Body.Content = element;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppendPluginLog(section.PluginId, $"[error] Sidebar section '{section.SectionId}' could not be loaded: {exception.Message}");
            if (view.Removed)
            {
                return;
            }

            var failure = new TextBlock
            {
                Text = $"This section could not be loaded: {exception.Message}",
                TextWrapping = TextWrapping.Wrap,
            };
            if (ChromeStyle("SfEmptyHintStyle") is { } hintStyle)
            {
                failure.Style = hintStyle;
            }

            AutomationProperties.SetAutomationId(failure, PluginSectionAutomationPrefix + section.PluginId + "." + section.SectionId + ".Error");
            view.Body.Content = failure;
        }
    }
}
