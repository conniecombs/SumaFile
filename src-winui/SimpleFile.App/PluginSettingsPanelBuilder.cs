using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.App;

/// <summary>
/// Builds the Settings card for one plugin: identity, state and signature, the enable switch (or "Review…" while it
/// waits for consent), Remove for user-installed plugins, and a control per declared setting. Every control carries
/// a <c>Header</c> so settings search indexes it. Values are read and written as strings under
/// <see cref="PluginHostMethods.SettingsKey"/>.
/// </summary>
internal sealed class PluginSettingsPanelBuilder
{
    public required Func<XamlRoot?> XamlRoot { get; init; }

    public required Func<PluginRecord, bool> CanRemove { get; init; }

    public required Func<PluginRecord, bool, Task> SetEnabledAsync { get; init; }

    public required Func<PluginRecord, Task> ReviewAsync { get; init; }

    public required Func<PluginRecord, Task> RemoveAsync { get; init; }

    public required Func<Task<string?>> PickFolderAsync { get; init; }

    /// <summary>
    /// Fills the custom settings page slot of an Enabled plugin that declares <c>contributions.settingsPage</c>: the
    /// Settings window's per-window page cache, or else <c>PluginManager.CreateSettingsPageAsync</c>. Null leaves the slot out.
    /// </summary>
    public Action<PluginRecord, ContentPresenter>? AttachSettingsPage { get; init; }

    /// <summary>The automation id of a plugin's custom settings page slot.</summary>
    public static string SettingsPageAutomationId(string pluginId) => $"PluginSettingsPage.{pluginId}";

    public FrameworkElement Build(PluginRecord record, Func<string, Task<string?>> read, Func<string, string, Task> write)
    {
        ArgumentNullException.ThrowIfNull(record);
        var manifest = record.Discovered.Manifest;
        var name = manifest?.Name ?? record.Id;

        var card = new StackPanel { Spacing = 10 };
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("SfTextMutedBrush"),
            Visibility = Visibility.Collapsed,
        };

        var identity = new StackPanel { Spacing = 2 };
        identity.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        identity.Children.Add(Muted(IdentityLine(record)));
        identity.Children.Add(Muted(StateText(record)));
        identity.Children.Add(Muted(PluginConsentDialog.SignatureText(record.Signature)));
        if (record.Discovered.Errors is { Count: > 0 } errors && record.State is PluginState.InvalidManifest or PluginState.IncompatibleSdk)
        {
            identity.Children.Add(Muted(string.Join(Environment.NewLine, errors)));
        }

        card.Children.Add(identity);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (record.State == PluginState.NeedsConsent)
        {
            var review = new Button { Content = "Review…" };
            AutomationName(review, $"Review plugin {name}");
            review.Click += async (_, _) => await RunAsync(status, () => ReviewAsync(record));
            actions.Children.Add(review);
        }
        else if (record.State is PluginState.Enabled or PluginState.Disabled or PluginState.Faulted or PluginState.LoadFailed)
        {
            var enabled = new ToggleSwitch { Header = "Enabled", IsOn = record.State != PluginState.Disabled };
            AutomationName(enabled, $"Enable plugin {name}");
            var reverting = false;
            enabled.Toggled += async (_, _) =>
            {
                if (reverting)
                {
                    return;
                }

                enabled.IsEnabled = false;
                if (!await RunAsync(status, () => SetEnabledAsync(record, enabled.IsOn)))
                {
                    // Put the switch back without asking the manager again.
                    reverting = true;
                    enabled.IsOn = !enabled.IsOn;
                    reverting = false;
                }

                enabled.IsEnabled = true;
            };
            actions.Children.Add(enabled);
        }

        if (CanRemove(record))
        {
            var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Bottom };
            AutomationName(remove, $"Remove plugin {name}");
            remove.Click += async (_, _) => await RunAsync(status, async () =>
            {
                if (await ConfirmRemoveAsync(name))
                {
                    await RemoveAsync(record);
                }
            });
            actions.Children.Add(remove);
        }

        if (actions.Children.Count > 0)
        {
            card.Children.Add(actions);
        }

        if (manifest?.Settings is { Count: > 0 } settings)
        {
            foreach (var group in settings.GroupBy(spec => spec.Group ?? ""))
            {
                if (group.Key.Length > 0)
                {
                    card.Children.Add(new TextBlock { Text = group.Key, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
                }

                foreach (var spec in group)
                {
                    card.Children.Add(SettingControl(record.Id, spec, read, write, status));
                }
            }
        }

        // The custom settings page, below the declarative settings and above the status line. Records that are not
        // Enabled never load plugin code, so they get no slot.
        if (record.State == PluginState.Enabled && manifest?.Contributions.SettingsPage is not null && AttachSettingsPage is { } attachPage)
        {
            var slot = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(slot, SettingsPageAutomationId(record.Id));
            card.Children.Add(slot);
            attachPage(record, slot);
        }

        card.Children.Add(status);
        return new Border
        {
            Background = Brush("SfBgSecondaryBrush"),
            BorderBrush = Brush("SfBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Child = card,
        };
    }

    private FrameworkElement SettingControl(
        string pluginId,
        PluginSettingSpec spec,
        Func<string, Task<string?>> read,
        Func<string, string, Task> write,
        TextBlock status)
    {
        var storageKey = PluginHostMethods.SettingsKey(pluginId, spec.Key);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("SfGitDeletedBrush"), Visibility = Visibility.Collapsed };
        var loading = true;

        async Task SaveAsync(string value)
        {
            if (loading)
            {
                return;
            }

            if (PluginSettingsSchema.ValidateValue(spec, value) is { } message)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                return;
            }

            error.Visibility = Visibility.Collapsed;
            await RunAsync(status, () => write(storageKey, value));
        }

        FrameworkElement input;
        Action<string> apply;
        switch (spec.Type)
        {
            case "boolean":
            {
                var toggle = new ToggleSwitch { Header = spec.Label };
                toggle.Toggled += async (_, _) => await SaveAsync(toggle.IsOn ? "true" : "false");
                apply = value => toggle.IsOn = value == "true";
                input = toggle;
                break;
            }
            case "integer":
            {
                var number = new NumberBox
                {
                    Header = spec.Label,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    SmallChange = 1,
                    LargeChange = 10,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                number.ValueChanged += async (_, args) =>
                {
                    if (double.IsNaN(args.NewValue))
                    {
                        return;
                    }

                    var rounded = Math.Round(args.NewValue);
                    if (rounded != args.NewValue)
                    {
                        number.Value = rounded; // raises ValueChanged again with the whole number
                        return;
                    }

                    await SaveAsync(((long)rounded).ToString(CultureInfo.InvariantCulture));
                };
                apply = value => number.Value = long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                input = number;
                break;
            }
            case "enum":
            {
                var combo = new ComboBox { Header = spec.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var option in spec.Options ?? [])
                {
                    combo.Items.Add(new ComboBoxItem { Content = option, Tag = option });
                }

                combo.SelectionChanged += async (_, _) =>
                {
                    if ((combo.SelectedItem as ComboBoxItem)?.Tag is string value)
                    {
                        await SaveAsync(value);
                    }
                };
                apply = value => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.Ordinal));
                input = combo;
                break;
            }
            case "path":
            {
                var box = new TextBox { Header = spec.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                box.LostFocus += async (_, _) => await SaveAsync(box.Text.Trim());
                var browse = new Button { Content = "Browse", VerticalAlignment = VerticalAlignment.Bottom };
                AutomationName(browse, $"Browse for {spec.Label}");
                browse.Click += async (_, _) => await RunAsync(status, async () =>
                {
                    if (await PickFolderAsync() is { } picked)
                    {
                        box.Text = picked;
                        await SaveAsync(picked);
                    }
                });
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(box);
                Grid.SetColumn(browse, 1);
                row.Children.Add(browse);
                apply = value => box.Text = value;
                input = row;
                break;
            }
            default:
            {
                var box = new TextBox { Header = spec.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                box.LostFocus += async (_, _) => await SaveAsync(box.Text);
                apply = value => box.Text = value;
                input = box;
                break;
            }
        }

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(input);
        if (!string.IsNullOrWhiteSpace(spec.Description))
        {
            panel.Children.Add(Muted(spec.Description));
        }

        panel.Children.Add(error);

        var fallback = PluginSettingsSchema.DefaultValue(spec);
        apply(fallback);
        _ = LoadAsync();
        return panel;

        async Task LoadAsync()
        {
            try
            {
                var stored = await read(storageKey);
                if (stored is not null && PluginSettingsSchema.ValidateValue(spec, stored) is null)
                {
                    apply(stored);
                }
            }
            catch (Exception exception)
            {
                ShowStatus(status, $"Could not load {spec.Label}: {exception.Message}");
            }
            finally
            {
                loading = false;
            }
        }
    }

    private async Task<bool> ConfirmRemoveAsync(string name)
    {
        if (XamlRoot() is not { } root)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Remove plugin?",
            Content = new TextBlock
            {
                Text = $"Remove {name}? Its folder in SumaFile's plugins folder is deleted. Its settings and data are kept.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await DialogHost.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Runs a card action, showing any failure on the card. Returns false when it failed or was cancelled.</summary>
    private static async Task<bool> RunAsync(TextBlock status, Func<Task> action)
    {
        try
        {
            status.Visibility = Visibility.Collapsed;
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            ShowStatus(status, exception.Message);
            return false;
        }
    }

    private static void ShowStatus(TextBlock status, string message)
    {
        status.Text = message;
        status.Visibility = Visibility.Visible;
    }

    private static string IdentityLine(PluginRecord record)
    {
        var manifest = record.Discovered.Manifest;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest?.Publisher.Name))
        {
            parts.Add(manifest.Publisher.Name);
        }

        parts.Add($"Version {manifest?.Version ?? "unknown"}");
        parts.Add(record.Id);
        return string.Join(" · ", parts);
    }

    internal static string StateText(PluginRecord record) => record.State switch
    {
        PluginState.NeedsConsent => "Waiting for your approval",
        PluginState.Enabled => record.BackendRunning ? "Enabled · background process running" : "Enabled",
        PluginState.Disabled => "Disabled",
        PluginState.IncompatibleSdk => "Not compatible with this version of SumaFile",
        PluginState.InvalidManifest => "Invalid plugin.json",
        PluginState.LoadFailed => $"Failed to load: {record.Error}",
        PluginState.Faulted => $"Stopped: {record.Error ?? "the background process stopped unexpectedly"}",
        PluginState.Shadowed => $"Overridden by {record.Discovered.ShadowedByFolder}",
        _ => record.State.ToString(),
    };

    internal static TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("SfTextMutedBrush"),
    };

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    private static Brush? Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
