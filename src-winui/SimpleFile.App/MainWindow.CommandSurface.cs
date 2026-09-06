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
        if (string.Equals(_appliedPrimaryToolbarSignature, signature, StringComparison.Ordinal)
            && _primaryToolbarActionElements.Count > 0)
        {
            ApplyPrimaryToolbarOverflow();
            return;
        }

        _appliedPrimaryToolbarSignature = signature;
        _primaryToolbarActionElements.Clear();
        PrimaryActionsHost.Children.Clear();

        AddPrimaryToolbarElement(ToolbarOverflowPlanner.Filter, QuickFilterBox);
        var showLabels = layout.ToolbarDisplayMode == ToolbarActionCatalog.IconAndLabelDisplayMode;
        foreach (var item in layout.PrimaryToolbar)
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
        button.Content = CreateToolbarButtonContent(action, showLabels);
        button.MinWidth = 30;
        button.MinHeight = 30;
        button.Height = 30;
        button.Width = showLabels
            ? ToolbarActionCatalog.WidthFor(action.Id, ToolbarActionCatalog.IconAndLabelDisplayMode)
            : 30;
        button.Padding = showLabels ? new Thickness(9, 0, 10, 0) : new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(button, action.Label);
        ToolTipService.SetToolTip(button, ToolbarTooltip(action));
    }

    private static object CreateToolbarButtonContent(ToolbarAction action, bool showLabels)
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
                    Text = action.Label,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 104,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
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

    private static string ToolbarTooltip(ToolbarAction action)
    {
        return string.IsNullOrWhiteSpace(action.Shortcut)
            ? action.Label
            : $"{action.Label} ({action.Shortcut})";
    }

    private async void OnGeneratedToolbarCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string commandId })
        {
            await RunUiActionAsync("Toolbar", () => RunAppCommandAsync(commandId));
        }
    }

    private void ApplyPrimaryToolbarCommandVisibility(IReadOnlySet<string> overflowed)
    {
        foreach (var pair in _primaryToolbarActionElements)
        {
            if (string.Equals(pair.Key, ToolbarOverflowPlanner.Filter, StringComparison.Ordinal))
            {
                continue;
            }

            var enabledBySettings = !pair.Key.StartsWith("git-", StringComparison.Ordinal)
                || IsGitIntegrationEnabled;
            SetOverflowVisible(pair.Value, enabledBySettings && !overflowed.Contains(pair.Key));
        }

        RefreshToolbarSeparatorVisibility();
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
