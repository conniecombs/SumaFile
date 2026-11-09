using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;

namespace SimpleFile.App;

/// <summary>
/// The host's chrome around one plugin panel (spec §7): a <see cref="Border"/> whose header holds the manifest title, a
/// detach or dock button (only for a detachable panel) and a close button, above a <see cref="ContentPresenter"/> for
/// the plugin's body. Built in code from the manifest, so no plugin code runs until the body is asked for. The docked
/// chrome and the <see cref="PluginPanelWindow"/>'s chrome are built here alike; they differ in their automation id
/// prefix and in the detach button's name.
/// </summary>
internal sealed class PluginPanelChrome
{
    private PluginPanelChrome(PluginPanel panel, Border root, Button? detach, Button close, ContentPresenter body)
    {
        Panel = panel;
        Root = root;
        Detach = detach;
        Close = close;
        Body = body;
    }

    public PluginPanel Panel { get; }

    public Border Root { get; }

    /// <summary>Detach (docked) or Dock (in the window); null for a panel that is not detachable.</summary>
    public Button? Detach { get; }

    public Button Close { get; }

    /// <summary>The plugin's body, or the failure text. Empty while the panel is hidden: no element is kept.</summary>
    public ContentPresenter Body { get; }

    /// <summary>
    /// Bumped whenever the panel is shown or hidden, so a body load that started for an earlier presentation is dropped.
    /// </summary>
    public int Presentation { get; set; }

    /// <summary>True once the chrome left the host (its plugin or epoch left, or the plugins stopped).</summary>
    public bool Removed { get; set; }

    /// <summary>The docked chrome's automation id, <c>PluginPanel.&lt;pluginId&gt;.&lt;panelId&gt;</c>.</summary>
    public static string DockedAutomationId(PluginPanel panel) => "PluginPanel." + panel.PluginId + "." + panel.PanelId;

    /// <summary>The panel window's automation id, <c>PluginPanelWindow.&lt;pluginId&gt;.&lt;panelId&gt;</c>.</summary>
    public static string WindowAutomationId(PluginPanel panel) => "PluginPanelWindow." + panel.PluginId + "." + panel.PanelId;

    /// <param name="panel">The panel, from the manifest.</param>
    /// <param name="detached">True for the panel window's chrome: its detach button reads Dock.</param>
    /// <param name="toggleHost">Runs when Detach (docked) or Dock (detached) is invoked.</param>
    /// <param name="close">Runs when Close is invoked.</param>
    public static PluginPanelChrome Create(PluginPanel panel, bool detached, Action toggleHost, Action close)
    {
        ArgumentNullException.ThrowIfNull(panel);
        var automationId = detached ? WindowAutomationId(panel) : DockedAutomationId(panel);

        var header = new Grid { Padding = new Thickness(12, 6, 6, 6), ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = panel.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (Resource<Brush>("SfTextPrimaryBrush") is { } titleBrush)
        {
            title.Foreground = titleBrush;
        }

        AutomationProperties.SetAutomationId(title, automationId + ".Title");
        header.Children.Add(title);

        Button? detach = null;
        if (panel.Detachable)
        {
            var name = (detached ? "Dock " : "Detach ") + panel.Title;
            detach = new Button
            {
                MinWidth = 64,
                Content = new TextBlock { Text = detached ? "Dock" : "Detach", FontSize = 12 },
                Style = Resource<Style>("SfGhostButtonStyle"),
            };
            AutomationProperties.SetAutomationId(detach, automationId + ".Detach");
            AutomationProperties.SetName(detach, name);
            ToolTipService.SetToolTip(detach, name);
            detach.Click += (_, _) => toggleHost();
            Grid.SetColumn(detach, 1);
            header.Children.Add(detach);
        }

        var closeName = "Close " + panel.Title;
        var closeButton = new Button
        {
            Width = 30,
            Height = 30,
            MinWidth = 30,
            Padding = new Thickness(0),
            Style = Resource<Style>("SfGhostButtonStyle"),
            Content = new FontIcon
            {
                Glyph = "",
                FontSize = 12,
                FontFamily = Resource<FontFamily>("SfIconFont") ?? new FontFamily("Segoe Fluent Icons"),
            },
        };
        AutomationProperties.SetAutomationId(closeButton, automationId + ".Close");
        AutomationProperties.SetName(closeButton, closeName);
        ToolTipService.SetToolTip(closeButton, closeName);
        closeButton.Click += (_, _) => close();
        Grid.SetColumn(closeButton, 2);
        header.Children.Add(closeButton);

        var body = new ContentPresenter
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(body, automationId + ".Body");
        Grid.SetRow(body, 1);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(header);
        layout.Children.Add(body);

        var root = new Border { Child = layout };
        if (Resource<Brush>("SfBgSecondaryBrush") is { } background)
        {
            root.Background = background;
        }

        if (!detached && Resource<Brush>("SfBorderBrush") is { } border)
        {
            // The docked chrome sits next to the panes: a hairline on the side facing them.
            root.BorderBrush = border;
            root.BorderThickness = string.Equals(panel.Placement, "right", StringComparison.Ordinal)
                ? new Thickness(1, 0, 0, 0)
                : new Thickness(0, 1, 0, 0);
        }

        AutomationProperties.SetAutomationId(root, automationId);
        AutomationProperties.SetName(root, panel.Title);
        return new PluginPanelChrome(panel, root, detach, closeButton, body);
    }

    private static T? Resource<T>(string key) where T : class =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as T : null;
}

/// <summary>
/// A detached plugin panel's window, spec §7's generic successor of the Git workbench window. Code-built and host-owned:
/// the host's chrome (<see cref="SecondaryWindowChrome"/>) and the panel header with Dock and Close around the plugin's
/// body. <c>MainWindow</c> tracks it through the plugin's window host (<c>PluginWindowHost.TrackHostWindow</c>), so
/// teardown and the main window's close close it first. Closing it, unless it is closed for docking, hides the panel.
/// </summary>
internal sealed class PluginPanelWindow : Window
{
    public PluginPanelWindow(PluginPanel panel, Action dock, Action close)
    {
        ArgumentNullException.ThrowIfNull(panel);
        Panel = panel;
        Chrome = PluginPanelChrome.Create(panel, detached: true, dock, close);
        Content = Chrome.Root;
        SecondaryWindowChrome.Apply(this, panel.Title, PluginPanelLayout.WindowWidth, PluginPanelLayout.WindowHeight);
        Closed += (_, _) => IsClosed = true;
    }

    public PluginPanel Panel { get; }

    public PluginPanelChrome Chrome { get; }

    public bool IsClosed { get; private set; }

    /// <summary>True once the plugin's window host tracks the window (<c>PluginWindowHost.TrackHostWindow</c>).</summary>
    public bool TrackedByHost { get; set; }

    /// <summary>True while the host closes the window to dock the panel: that close does not hide the panel.</summary>
    public bool ClosingForDock { get; private set; }

    /// <summary>Closes the window so the panel can dock again; its <c>Closed</c> handlers see <see cref="ClosingForDock"/>.</summary>
    public void CloseForDock()
    {
        ClosingForDock = true;
        try
        {
            Close();
        }
        catch (Exception)
        {
            // Already closed: nothing left to dock from.
        }
    }
}
