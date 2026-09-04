using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace SimpleFile.App;

public sealed partial class MainWindow
{

    private async void OnViewSaveProfileClicked(object sender, RoutedEventArgs e)
    {
        PrimaryViewButton.Flyout?.Hide();
        await RunUiActionAsync("Profile", PromptSaveWorkspaceProfileAsync);
    }

    private async void OnWorkspaceProfileSaveClicked(object sender, RoutedEventArgs e)
    {
        WorkspaceProfileButton.Flyout?.Hide();
        await RunUiActionAsync("Profile", PromptSaveWorkspaceProfileAsync);
    }

    private async void OnWorkspaceProfileManageClicked(object sender, RoutedEventArgs e)
    {
        WorkspaceProfileButton.Flyout?.Hide();
        await RunUiActionAsync("Profile", ShowWorkspaceProfileManagerAsync);
    }

    private async void OnWorkspaceProfilesFlyoutOpening(object sender, object e)
    {
        WorkspaceProfilesHost.Children.Clear();
        WorkspaceProfilesHost.Children.Add(new TextBlock
        {
            Text = "Loading profiles...",
            FontSize = 12,
            Opacity = 0.65,
        });

        try
        {
            await RefreshWorkspaceProfilesHostAsync(WorkspaceProfilesHost);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceProfilesHost.Children.Clear();
            WorkspaceProfilesHost.Children.Add(new TextBlock
            {
                Text = exception.Message,
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private async Task RefreshViewProfilesHostAsync()
    {
        await RefreshWorkspaceProfilesHostAsync(ViewProfilesHost);
    }

    private async Task RefreshWorkspaceProfilesHostAsync(StackPanel host)
    {
        host.Children.Clear();
        if (_workspace is null)
        {
            return;
        }

        var profiles = await _workspace.ListWorkspaceProfilesAsync();
        if (profiles.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "No profiles",
                FontSize = 12,
                Opacity = 0.65,
            });
            return;
        }

        foreach (var profile in profiles)
        {
            host.Children.Add(CreateWorkspaceProfileRow(profile));
        }
    }

    private Grid CreateWorkspaceProfileRow(WorkspaceProfile profile)
    {
        var row = new Grid
        {
            ColumnSpacing = 4,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var profileLabel = new Grid
        {
            ColumnSpacing = 8,
        };
        profileLabel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        profileLabel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        profileLabel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = CreateMenuIcon(ContextMenuIconCatalog.ViewAll);
        Grid.SetColumn(icon, 0);
        profileLabel.Children.Add(icon);
        var name = new TextBlock
        {
            Text = profile.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        profileLabel.Children.Add(name);
        if (profile.IsBuiltIn)
        {
            var badge = new TextBlock
            {
                Text = "Built-in",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Opacity = 0.62,
            };
            Grid.SetColumn(badge, 2);
            profileLabel.Children.Add(badge);
        }

        var apply = new Button
        {
            Tag = profile.Id,
            Style = ChromeStyle("SfGhostButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4, 8, 4),
            Content = profileLabel,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(apply, $"Apply profile {profile.Name}");
        ToolTipService.SetToolTip(apply, "Apply profile");
        apply.Click += OnApplyWorkspaceProfileClicked;
        Grid.SetColumn(apply, 0);
        row.Children.Add(apply);

        var more = CreateWorkspaceProfileMoreButton(profile);
        Grid.SetColumn(more, 1);
        row.Children.Add(more);

        return row;
    }

    private Button CreateWorkspaceProfileMoreButton(WorkspaceProfile profile)
    {
        var button = new Button
        {
            Tag = profile.Id,
            Width = 30,
            Height = 30,
            MinWidth = 30,
            Padding = new Thickness(0),
            Style = ChromeStyle("SfIconButtonStyle"),
            Content = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Glyph = "\uE712",
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Profile actions for {profile.Name}");
        ToolTipService.SetToolTip(button, "Profile actions");

        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateWorkspaceProfileMenuItem($"profile:duplicate:{profile.Id}", "Duplicate", ContextMenuIconCatalog.Copy));
        flyout.Items.Add(CreateWorkspaceProfileMenuItem($"profile:export:{profile.Id}", "Export...", ContextMenuIconCatalog.Import));
        if (profile.CanRename)
        {
            flyout.Items.Add(CreateWorkspaceProfileMenuItem($"profile:rename:{profile.Id}", "Rename...", ContextMenuIconCatalog.Rename));
        }

        if (profile.CanOverwrite)
        {
            flyout.Items.Add(CreateWorkspaceProfileMenuItem($"profile:overwrite:{profile.Id}", "Overwrite with current layout", ContextMenuIconCatalog.Save));
        }

        if (profile.CanReset)
        {
            flyout.Items.Add(CreateWorkspaceProfileMenuItem($"profile:reset:{profile.Id}", "Reset", ContextMenuIconCatalog.EraseTool));
        }

        if (profile.CanDelete)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateWorkspaceProfileMenuItem($"profile:delete:{profile.Id}", "Delete", ContextMenuIconCatalog.Delete));
        }

        button.Flyout = flyout;
        return button;
    }

    private MenuFlyoutItem CreateWorkspaceProfileMenuItem(string tag, string text, string glyph)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Tag = tag,
            Icon = CreateMenuIcon(glyph),
        };
        item.Click += OnWorkspaceProfileMenuActionClick;
        return item;
    }

    private async void OnApplyWorkspaceProfileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || _workspace is null)
        {
            return;
        }

        PrimaryViewButton.Flyout?.Hide();
        WorkspaceProfileButton.Flyout?.Hide();
        await RunUiActionAsync("Profile", () => ApplyWorkspaceProfileByIdAsync(id));
    }

    private async void OnWorkspaceProfileMenuActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } || _workspace is null)
        {
            return;
        }

        await RunUiActionAsync("Profile", () => RunWorkspaceProfileMenuActionAsync(tag));
    }

    private async Task ApplyWorkspaceProfileByIdAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        await _workspace.ApplyWorkspaceProfileAsync(id);
        SyncFromWorkspace();
        var profile = (await _workspace.ListWorkspaceProfilesAsync()).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        ShowMessage("Profile", $"Applied \"{profile?.Name ?? "profile"}\".", InfoBarSeverity.Success);
    }

    private async Task PromptSaveWorkspaceProfileAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var profiles = await _workspace.ListWorkspaceProfilesAsync();
        var suggestedName = SuggestedProfileName(profiles);
        var nameBox = new TextBox
        {
            Header = "Name",
            Text = suggestedName,
            SelectionStart = 0,
            SelectionLength = suggestedName.Length,
            MinWidth = 320,
        };
        var dialog = new ContentDialog
        {
            Title = "Save profile",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = WorkspaceProfilesDocument.NormalizeName(nameBox.Text);
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowMessage("Profile", "Profile name cannot be empty.", InfoBarSeverity.Warning);
            return;
        }

        var duplicate = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null
            && (!duplicate.CanOverwrite || await ConfirmWorkspaceProfileOverwriteAsync(name) != ContentDialogResult.Primary))
        {
            if (!duplicate.CanOverwrite)
            {
                ShowMessage("Profile", $"\"{name}\" is a built-in profile. Choose another name.", InfoBarSeverity.Warning);
            }

            return;
        }

        var saved = await _workspace.SaveWorkspaceProfileAsync(name, overwrite: duplicate is not null);
        await RefreshViewProfilesHostAsync();
        await RefreshWorkspaceProfilesHostAsync(WorkspaceProfilesHost);
        ShowMessage("Profile", $"Saved \"{saved.Name}\".", InfoBarSeverity.Success);
    }

    private static string SuggestedProfileName(IReadOnlyList<WorkspaceProfile> profiles)
    {
        const string baseName = "Profile";
        var used = profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return baseName;
    }

    private async Task PromptDuplicateWorkspaceProfileAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        var profile = (await _workspace.ListWorkspaceProfilesAsync()).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        var name = await PromptProfileNameAsync("Duplicate profile", $"{profile.Name} copy", "Duplicate");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var saved = await _workspace.DuplicateWorkspaceProfileAsync(id, name);
        await RefreshViewProfilesHostAsync();
        await RefreshWorkspaceProfilesHostAsync(WorkspaceProfilesHost);
        ShowMessage("Profile", $"Duplicated \"{saved.Name}\".", InfoBarSeverity.Success);
    }

    private async Task PromptRenameWorkspaceProfileAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        var profile = (await _workspace.ListWorkspaceProfilesAsync()).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null || !profile.CanRename)
        {
            ShowMessage("Profile", "Built-in profiles cannot be renamed. Duplicate it first.", InfoBarSeverity.Warning);
            return;
        }

        var name = await PromptProfileNameAsync("Rename profile", profile.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var saved = await _workspace.RenameWorkspaceProfileAsync(id, name);
        await RefreshViewProfilesHostAsync();
        await RefreshWorkspaceProfilesHostAsync(WorkspaceProfilesHost);
        ShowMessage("Profile", $"Renamed to \"{saved.Name}\".", InfoBarSeverity.Success);
    }

    private async Task PromptOverwriteWorkspaceProfileAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        var profile = (await _workspace.ListWorkspaceProfilesAsync()).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        if (!profile.CanOverwrite)
        {
            ShowMessage("Profile", "Built-in profiles cannot be overwritten. Duplicate it first.", InfoBarSeverity.Warning);
            return;
        }

        if (await ConfirmWorkspaceProfileOverwriteAsync(profile.Name) != ContentDialogResult.Primary)
        {
            return;
        }

        var saved = await _workspace.OverwriteWorkspaceProfileAsync(id);
        await RefreshViewProfilesHostAsync();
        await RefreshWorkspaceProfilesHostAsync(WorkspaceProfilesHost);
        ShowMessage("Profile", $"Updated \"{saved.Name}\".", InfoBarSeverity.Success);
    }

    private async Task<ContentDialogResult> ConfirmWorkspaceProfileOverwriteAsync(string name)
    {
        var dialog = new ContentDialog
        {
            Title = "Overwrite profile?",
            Content = $"Replace \"{name}\" with the current workspace profile?",
            PrimaryButtonText = "Overwrite",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync();
    }

    private async Task PromptResetWorkspaceProfileAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        var profile = (await _workspace.ListWorkspaceProfilesAsync()).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        if (!profile.CanReset)
        {
            ShowMessage("Profile", "This profile does not have a reset source.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Reset profile?",
            Content = profile.IsBuiltIn
                ? $"Apply the built-in defaults for \"{profile.Name}\"?"
                : $"Reset \"{profile.Name}\" from its source profile?",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _workspace.ResetWorkspaceProfileAsync(id);
        await _workspace.ApplyWorkspaceProfileAsync(id);
        SyncFromWorkspace();
        await RefreshViewProfilesHostAsync();
        await RefreshWorkspaceProfilesHostAsync(WorkspaceProfilesHost);
        ShowMessage("Profile", $"Reset \"{profile.Name}\".", InfoBarSeverity.Success);
    }

    private async Task PromptDeleteWorkspaceProfileAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        var profile = (await _workspace.ListWorkspaceProfilesAsync()).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        if (!profile.CanDelete)
        {
            ShowMessage("Profile", "Built-in profiles cannot be deleted.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete profile?",
            Content = $"Delete \"{profile.Name}\"?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _workspace.DeleteWorkspaceProfileAsync(id);
        await RefreshViewProfilesHostAsync();
        await RefreshWorkspaceProfilesHostAsync(WorkspaceProfilesHost);
        ShowMessage("Profile", $"Deleted \"{profile.Name}\".", InfoBarSeverity.Success);
    }

    private async Task PromptExportWorkspaceProfileAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        var profile = (await _workspace.ListWorkspaceProfilesAsync()).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        var json = await _workspace.ExportWorkspaceProfileAsync(id);
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SafeProfileExportName(profile.Name) + ".sumafile-profile",
        };
        picker.FileTypeChoices.Add("SumaFile profile", new[] { ".json" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await FileIO.WriteTextAsync(file, json);
        ShowMessage("Profile", $"Exported \"{profile.Name}\".", InfoBarSeverity.Success);
    }

    private async Task<string?> PromptProfileNameAsync(string title, string suggestedName, string action)
    {
        var nameBox = new TextBox
        {
            Header = "Name",
            Text = WorkspaceProfilesDocument.NormalizeName(suggestedName),
            SelectionStart = 0,
            SelectionLength = WorkspaceProfilesDocument.NormalizeName(suggestedName).Length,
            MinWidth = 320,
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = nameBox,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var name = WorkspaceProfilesDocument.NormalizeName(nameBox.Text);
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowMessage("Profile", "Profile name cannot be empty.", InfoBarSeverity.Warning);
            return null;
        }

        return name;
    }

    private static string SafeProfileExportName(string name)
    {
        var safe = new string((name ?? "profile")
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "profile" : safe;
    }

    private async Task ShowWorkspaceProfileManagerAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var list = new ListView
        {
            MinWidth = 520,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Single,
        };
        var apply = new Button { Content = "Apply" };
        var duplicate = new Button { Content = "Duplicate" };
        var rename = new Button { Content = "Rename" };
        var overwrite = new Button { Content = "Overwrite" };
        var export = new Button { Content = "Export" };
        var reset = new Button { Content = "Reset" };
        var delete = new Button { Content = "Delete" };

        WorkspaceProfile? SelectedProfile() =>
            (list.SelectedItem as WorkspaceProfileListRow)?.Profile;

        void UpdateButtons()
        {
            var selected = SelectedProfile();
            apply.IsEnabled = selected is not null;
            duplicate.IsEnabled = selected is not null;
            export.IsEnabled = selected is not null;
            rename.IsEnabled = selected?.CanRename == true;
            overwrite.IsEnabled = selected?.CanOverwrite == true;
            reset.IsEnabled = selected?.CanReset == true;
            delete.IsEnabled = selected?.CanDelete == true;
        }

        async Task RefreshListAsync(string? selectedId = null)
        {
            var profiles = await _workspace.ListWorkspaceProfilesAsync();
            list.Items.Clear();
            WorkspaceProfileListRow? selectedRow = null;
            foreach (var profile in profiles)
            {
                var row = new WorkspaceProfileListRow(profile);
                list.Items.Add(row);
                if (string.Equals(profile.Id, selectedId ?? _workspace.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedRow = row;
                }
            }

            list.SelectedItem = selectedRow ?? (list.Items.Count > 0 ? list.Items[0] : null);
            UpdateButtons();
        }

        list.SelectionChanged += (_, _) => UpdateButtons();
        apply.Click += async (_, _) =>
        {
            if (SelectedProfile() is { } profile)
            {
                await ApplyWorkspaceProfileByIdAsync(profile.Id);
                await RefreshListAsync(profile.Id);
            }
        };
        duplicate.Click += async (_, _) =>
        {
            if (SelectedProfile() is { } profile)
            {
                await PromptDuplicateWorkspaceProfileAsync(profile.Id);
                await RefreshListAsync(_workspace.ActiveProfileId);
            }
        };
        rename.Click += async (_, _) =>
        {
            if (SelectedProfile() is { } profile)
            {
                await PromptRenameWorkspaceProfileAsync(profile.Id);
                await RefreshListAsync(profile.Id);
            }
        };
        overwrite.Click += async (_, _) =>
        {
            if (SelectedProfile() is { } profile)
            {
                await PromptOverwriteWorkspaceProfileAsync(profile.Id);
                await RefreshListAsync(profile.Id);
            }
        };
        export.Click += async (_, _) =>
        {
            if (SelectedProfile() is { } profile)
            {
                await PromptExportWorkspaceProfileAsync(profile.Id);
            }
        };
        reset.Click += async (_, _) =>
        {
            if (SelectedProfile() is { } profile)
            {
                await PromptResetWorkspaceProfileAsync(profile.Id);
                await RefreshListAsync(profile.Id);
            }
        };
        delete.Click += async (_, _) =>
        {
            if (SelectedProfile() is { } profile)
            {
                await PromptDeleteWorkspaceProfileAsync(profile.Id);
                await RefreshListAsync();
            }
        };

        foreach (var button in new[] { apply, duplicate, rename, overwrite, export, reset, delete })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        var buttons = new Grid
        {
            ColumnSpacing = 8,
            RowSpacing = 8,
        };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddProfileManagerButton(buttons, apply, row: 0, column: 0);
        AddProfileManagerButton(buttons, duplicate, row: 0, column: 1);
        AddProfileManagerButton(buttons, rename, row: 0, column: 2);
        AddProfileManagerButton(buttons, overwrite, row: 0, column: 3);
        AddProfileManagerButton(buttons, export, row: 1, column: 0);
        AddProfileManagerButton(buttons, reset, row: 1, column: 1);
        AddProfileManagerButton(buttons, delete, row: 1, column: 2);
        var body = new StackPanel
        {
            Spacing = 12,
            Children = { list, buttons },
        };
        await RefreshListAsync();

        var dialog = new ContentDialog
        {
            Title = "Workspace profiles",
            Content = body,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private static void AddProfileManagerButton(Grid host, Button button, int row, int column)
    {
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        host.Children.Add(button);
    }

    private async Task AppendWorkspaceProfileOverflowMenuAsync(MenuFlyout flyout)
    {
        if (_workspace is null)
        {
            return;
        }

        var profilesMenu = flyout.Items
            .OfType<MenuFlyoutSubItem>()
            .FirstOrDefault(item => string.Equals(item.Name, "overflow-profiles", StringComparison.Ordinal));
        if (profilesMenu is null)
        {
            return;
        }

        profilesMenu.Items.Clear();
        profilesMenu.Items.Add(CreateWorkspaceProfileMenuItem("profile:save", "Save current profile...", ContextMenuIconCatalog.Save));
        profilesMenu.Items.Add(CreateWorkspaceProfileMenuItem("profile:manage", "Manage profiles...", ContextMenuIconCatalog.Settings));
        profilesMenu.Items.Add(new MenuFlyoutSeparator());

        foreach (var profile in await _workspace.ListWorkspaceProfilesAsync())
        {
            profilesMenu.Items.Add(CreateWorkspaceProfileSubMenu(profile));
        }
    }

    private MenuFlyoutSubItem CreateWorkspaceProfileSubMenu(WorkspaceProfile profile)
    {
        var sub = new MenuFlyoutSubItem
        {
            Text = profile.IsBuiltIn ? $"{profile.Name} (Built-in)" : profile.Name,
            Icon = CreateMenuIcon(ContextMenuIconCatalog.ViewAll),
        };
        sub.Items.Add(CreateWorkspaceProfileMenuItem($"profile:apply:{profile.Id}", "Apply", ContextMenuIconCatalog.ViewAll));
        sub.Items.Add(CreateWorkspaceProfileMenuItem($"profile:duplicate:{profile.Id}", "Duplicate", ContextMenuIconCatalog.Copy));
        sub.Items.Add(CreateWorkspaceProfileMenuItem($"profile:export:{profile.Id}", "Export...", ContextMenuIconCatalog.Import));
        if (profile.CanRename)
        {
            sub.Items.Add(CreateWorkspaceProfileMenuItem($"profile:rename:{profile.Id}", "Rename...", ContextMenuIconCatalog.Rename));
        }

        if (profile.CanOverwrite)
        {
            sub.Items.Add(CreateWorkspaceProfileMenuItem($"profile:overwrite:{profile.Id}", "Overwrite", ContextMenuIconCatalog.Save));
        }

        if (profile.CanReset)
        {
            sub.Items.Add(CreateWorkspaceProfileMenuItem($"profile:reset:{profile.Id}", "Reset", ContextMenuIconCatalog.EraseTool));
        }

        if (profile.CanDelete)
        {
            sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(CreateWorkspaceProfileMenuItem($"profile:delete:{profile.Id}", "Delete", ContextMenuIconCatalog.Delete));
        }

        return sub;
    }

    private async Task RunWorkspaceProfileMenuActionAsync(string tag)
    {
        if (tag == "profile:save")
        {
            await PromptSaveWorkspaceProfileAsync();
            return;
        }

        if (tag == "profile:manage")
        {
            await ShowWorkspaceProfileManagerAsync();
            return;
        }

        var parts = tag.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "profile")
        {
            return;
        }

        switch (parts[1])
        {
            case "apply":
                await ApplyWorkspaceProfileByIdAsync(parts[2]);
                break;
            case "duplicate":
                await PromptDuplicateWorkspaceProfileAsync(parts[2]);
                break;
            case "rename":
                await PromptRenameWorkspaceProfileAsync(parts[2]);
                break;
            case "overwrite":
                await PromptOverwriteWorkspaceProfileAsync(parts[2]);
                break;
            case "export":
                await PromptExportWorkspaceProfileAsync(parts[2]);
                break;
            case "reset":
                await PromptResetWorkspaceProfileAsync(parts[2]);
                break;
            case "delete":
                await PromptDeleteWorkspaceProfileAsync(parts[2]);
                break;
        }
    }

}

