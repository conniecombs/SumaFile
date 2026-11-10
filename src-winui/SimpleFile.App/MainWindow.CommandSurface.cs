using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, FrameworkElement> _primaryToolbarActionElements = new(StringComparer.Ordinal);
    private string? _appliedPrimaryToolbarSignature;

    private void ApplyCommandSurfaceLayout()
    {
        var layout = _workspace?.Settings.CommandSurface ?? CommandSurfaceLayout.CreateDefault();
        layout.Normalize();
        var signature = layout.Signature();
        var showLabels = layout.ToolbarDisplayMode == ToolbarActionCatalog.IconAndLabelDisplayMode;
        if (string.Equals(_appliedPrimaryToolbarSignature, signature, StringComparison.Ordinal)
            && _primaryToolbarActionElements.Count > 0)
        {
            RefreshPrimaryToolbarActionChrome(showLabels);
            ApplyPrimaryToolbarOverflow();
            return;
        }

        _appliedPrimaryToolbarSignature = signature;
        _primaryToolbarActionElements.Clear();
        PrimaryActionsHost.Children.Clear();

        // The toolbar as drawn: a saved item of an absent plugin stays in the layout and comes back with its plugin.
        foreach (var item in layout.VisibleItems())
        {
            if (item.IsSeparator)
            {
                PrimaryActionsHost.Children.Add(CreateToolbarSeparator());
                continue;
            }

            if (ToolbarActionCatalog.Find(item.Id) is not { } action)
            {
                continue;
            }

            var element = ToolbarElementFor(action, showLabels);
            if (element is not null)
            {
                AddPrimaryToolbarElement(action.Id, element);
            }
        }

        SetOverflowVisible(ClosePrimaryPaneButton, false);
        ApplyPrimaryToolbarOverflow();
    }

    private void AttachCommandCatalogEvents()
    {
        ToolbarActionCatalog.Changed -= OnToolbarActionCatalogChanged;
        ToolbarActionCatalog.Changed += OnToolbarActionCatalogChanged;
    }

    private void DetachCommandCatalogEvents()
    {
        ToolbarActionCatalog.Changed -= OnToolbarActionCatalogChanged;
    }

    // Plugin toolbar actions came or went: rebuild the toolbar from the layout (raised on any thread).
    private void OnToolbarActionCatalogChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_workspace is not null)
            {
                ApplyCommandSurfaceLayout();
            }
        });
    }

    private IReadOnlyList<string> CurrentPrimaryToolbarActionOrder()
    {
        var layout = _workspace?.Settings.CommandSurface ?? CommandSurfaceLayout.CreateDefault();
        return layout.VisiblePrimaryActionIds();
    }

    private void AddPrimaryToolbarElement(string id, FrameworkElement element)
    {
        if (element.Parent is Panel parent)
        {
            parent.Children.Remove(element);
        }

        element.Tag = id;
        _primaryToolbarActionElements[id] = element;
        PrimaryActionsHost.Children.Add(element);
    }

    private FrameworkElement? ToolbarElementFor(ToolbarAction action, bool showLabels)
    {
        if (action.UsesBuiltInControl)
        {
            var button = action.Id switch
            {
                ToolbarOverflowPlanner.New => PrimaryNewButton,
                ToolbarOverflowPlanner.DualPane => DualPaneButton,
                ToolbarOverflowPlanner.Profiles => WorkspaceProfileButton,
                ToolbarOverflowPlanner.ViewOptions => PrimaryViewButton,
                ToolbarOverflowPlanner.Settings => PrimarySettingsButton,
                _ => null,
            };

            if (button is null)
            {
                return null;
            }

            ConfigureToolbarButton(button, action, showLabels);
            return button;
        }

        if (action.CommandId is null)
        {
            return null;
        }

        var generated = new Button
        {
            Style = ChromeStyle("SfToolbarButtonStyle"),
            Tag = action.CommandId,
        };
        ConfigureToolbarButton(generated, action, showLabels);
        generated.Click += OnGeneratedToolbarCommandClick;
        return generated;
    }

    private void ConfigureToolbarButton(Button button, ToolbarAction action, bool showLabels)
    {
        var displayLabel = ToolbarDisplayLabel(action);
        var tooltipLabel = ToolbarTooltipLabel(action, displayLabel);
        button.Content = CreateToolbarButtonContent(action, showLabels, displayLabel);
        button.MinWidth = 30;
        button.MinHeight = 30;
        button.Height = 30;
        button.Width = showLabels
            ? ToolbarActionCatalog.WidthFor(action.Id, ToolbarActionCatalog.IconAndLabelDisplayMode)
            : 30;
        button.Padding = showLabels ? new Thickness(9, 0, 10, 0) : new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(button, tooltipLabel);
        ToolTipService.SetToolTip(button, ToolbarTooltip(action, tooltipLabel));

        // Cross-pane copy/move need a second pane with a folder open. WinUI shows no tooltip on a
        // disabled Button, so the button stays enabled but dimmed: hovering explains why, and a
        // click repeats the explanation in the status bar instead of running the command.
        if (CrossPaneUnavailableText(action.CommandId ?? action.Id) is { } unavailable)
        {
            button.Opacity = 0.45;
            ToolTipService.SetToolTip(button, unavailable);
        }
        else if (CrossPaneVerb(action) is not null)
        {
            button.Opacity = 1;
        }

        // Last, because the label above reset the name and tooltip: a plugin panel's button is its toggle on every
        // build and chrome refresh, whether or not the overflow set changed.
        ApplyPluginPanelToggleChrome(button, action.CommandId ?? action.Id);
    }

    /// <summary>
    /// Why a cross-pane copy/move button cannot run now ("Open the second pane (F6) to copy
    /// there"), or null when it can (or the command is not cross-pane).
    /// </summary>
    private string? CrossPaneUnavailableText(string commandId)
    {
        var verb = commandId switch
        {
            "copy-to-pane" => "Copy",
            "move-to-pane" => "Move",
            _ => null,
        };
        if (verb is null)
        {
            return null;
        }

        var dualPaneEnabled = _workspace?.DualPaneEnabled == true;
        var otherPaneHasPath = !string.IsNullOrEmpty(_workspace?.OtherPanePath());
        return dualPaneEnabled && otherPaneHasPath
            ? null
            : $"Open the second pane (F6) to {verb.ToLowerInvariant()} there";
    }

    private static object CreateToolbarButtonContent(ToolbarAction action, bool showLabels, string displayLabel)
    {
        var icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Glyph = action.IconGlyph,
        };

        if (!showLabels)
        {
            return icon;
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = displayLabel,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    private void RefreshPrimaryToolbarActionChrome(bool showLabels)
    {
        foreach (var pair in _primaryToolbarActionElements)
        {
            if (pair.Value is Button button && ToolbarActionCatalog.Find(pair.Key) is { } action)
            {
                ConfigureToolbarButton(button, action, showLabels);
            }
        }
    }

    private Border CreateToolbarSeparator()
    {
        return new Border
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(5, 0, 5, 0),
            Background = Brush("SfBorderBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = CommandSurfaceItem.SeparatorKind,
        };
    }

    private string ToolbarTooltip(ToolbarAction action, string label)
    {
        var shortcut = ShortcutLabelFor(action.Id) ?? action.Shortcut;
        return string.IsNullOrWhiteSpace(shortcut)
            ? label
            : $"{label} ({shortcut})";
    }

    private string ToolbarDisplayLabel(ToolbarAction action)
    {
        var verb = CrossPaneVerb(action);
        if (verb is null || _workspace?.DualPaneEnabled != true)
        {
            return action.Label;
        }

        return CrossPaneTransferFormatter.CommandLabel(verb, _workspace.OtherPane().Id);
    }

    private string ToolbarTooltipLabel(ToolbarAction action, string fallback)
    {
        var verb = CrossPaneVerb(action);
        var destination = _workspace?.OtherPanePath();
        if (verb is null || destination is null || _workspace is null)
        {
            return fallback;
        }

        return CrossPaneTransferFormatter.CommandLabel(verb, _workspace.OtherPane().Id, destination);
    }

    private static string? CrossPaneVerb(ToolbarAction action)
    {
        var commandId = action.CommandId ?? action.Id;
        return commandId switch
        {
            "copy-to-pane" => "Copy",
            "move-to-pane" => "Move",
            _ => null,
        };
    }

    private async void OnGeneratedToolbarCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string commandId })
        {
            if (CrossPaneUnavailableText(commandId) is { } unavailable)
            {
                SetStatusText(unavailable);
                return;
            }

            // A plugin panel's button toggles the panel; its command from the palette or a menu only shows it.
            if (PluginPanelLayout.ForCommand(_pluginPanels, commandId) is not null)
            {
                await RunUiActionAsync("Toolbar", () => TogglePluginPanelAsync(commandId));
                return;
            }

            await RunUiActionAsync("Toolbar", () => RunAppCommandAsync(commandId));
        }
    }

    private void ApplyPrimaryToolbarCommandVisibility(IReadOnlySet<string> overflowed)
    {
        // Built at most once per pass, and only when a plugin action has a `when`.
        ContextMenuRequest? whenRequest = null;
        foreach (var pair in _primaryToolbarActionElements)
        {
            SetOverflowVisible(
                pair.Value,
                !overflowed.Contains(pair.Key) && PluginToolbarWhenAllows(pair.Key, ref whenRequest));
        }

        RefreshToolbarSeparatorVisibility();
        RefreshPluginPanelToggleButtons();
    }

    /// <summary>
    /// False when <paramref name="actionId"/> is a plugin action whose <c>when</c> is false for the active pane, from the
    /// same request menus use (so from the pane's current decorations; no provider is asked). A panel toggle stays
    /// visible while its panel is shown, so the user can always hide it.
    /// </summary>
    private bool PluginToolbarWhenAllows(string actionId, ref ContextMenuRequest? request)
    {
        if (ToolbarActionCatalog.Find(actionId) is not { PluginId: { } pluginId } action
            || string.IsNullOrWhiteSpace(action.When))
        {
            return true;
        }

        if (IsPluginPanelShownForCommand(action.CommandId ?? action.Id))
        {
            return true;
        }

        if (_workspace is null)
        {
            return false;
        }

        request ??= BuildContextMenuRequest(SelectedRowsForPane(_workspace.ActivePane), forWhenOnly: true);
        return PluginMenuWhen.Evaluate(action.When, pluginId, request);
    }

    private void RefreshToolbarSeparatorVisibility()
    {
        var hasVisibleBefore = false;
        Border? pendingSeparator = null;
        foreach (var child in PrimaryActionsHost.Children.OfType<FrameworkElement>())
        {
            if (string.Equals(child.Tag?.ToString(), CommandSurfaceItem.SeparatorKind, StringComparison.Ordinal))
            {
                SetOverflowVisible(child, false);
                pendingSeparator = child as Border;
                continue;
            }

            if (child.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (pendingSeparator is not null && hasVisibleBefore)
            {
                SetOverflowVisible(pendingSeparator, true);
            }

            pendingSeparator = null;
            hasVisibleBefore = true;
        }

        if (pendingSeparator is not null)
        {
            SetOverflowVisible(pendingSeparator, false);
        }
    }

    private bool HasVisiblePrimaryToolbarAction()
    {
        return PrimaryActionsHost.Children
            .OfType<FrameworkElement>()
            .Any(child => child.Visibility == Visibility.Visible
                && !string.Equals(child.Tag?.ToString(), CommandSurfaceItem.SeparatorKind, StringComparison.Ordinal));
    }
}
