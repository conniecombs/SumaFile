using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Storage.Pickers;

namespace SimpleFile.App;

public sealed class ShortcutHelpRow
{
    public string Keys { get; init; } = "";
    public string Action { get; init; } = "";
}

public sealed partial class SettingsDialog : ContentDialog
{
    private const string RepositoryUrl = "https://github.com/conniecombs/SumaFile";
    private FileOperationService? _fileOps;
    private bool _checkedUpdateIsInstallable;

    public SettingsDialog()
    {
        InitializeComponent();
        CategoryList.SelectedIndex = 0;
        UpdateDefaultIconSizeValueText(DefaultIconSize);
        ShortcutsList.ItemsSource = KeyboardShortcutMap.Defaults
            .Select(item => new ShortcutHelpRow { Keys = item.Keys, Action = item.Label })
            .ToList();
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HideCategoryPanels();

        var selected = CategoryList.SelectedItem as ListViewItem;
        if (selected == null) return;

        switch (selected.Content.ToString())
        {
            case "Appearance": AppearancePanel.Visibility = Visibility.Visible; break;
            case "Navigation": NavigationPanel.Visibility = Visibility.Visible; break;
            case "Behavior": BehaviorPanel.Visibility = Visibility.Visible; break;
            case "Shortcuts": ShortcutsPanel.Visibility = Visibility.Visible; break;
            case "Tools": ToolsPanel.Visibility = Visibility.Visible; break;
            case "Updates": UpdatesPanel.Visibility = Visibility.Visible; break;
            case "About": AboutPanel.Visibility = Visibility.Visible; break;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        ListViewItem? firstVisible = null;
        foreach (var item in CategoryList.Items.OfType<ListViewItem>())
        {
            var label = item.Content?.ToString() ?? "";
            var visible = query.Length == 0 || label.Contains(query, StringComparison.OrdinalIgnoreCase);
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && firstVisible is null)
            {
                firstVisible = item;
            }
        }

        if (CategoryList.SelectedItem is not ListViewItem selected
            || selected.Visibility != Visibility.Visible)
        {
            CategoryList.SelectedItem = firstVisible;
            if (firstVisible is null)
            {
                HideCategoryPanels();
            }
        }
    }

    private void HideCategoryPanels()
    {
        AppearancePanel.Visibility = Visibility.Collapsed;
        NavigationPanel.Visibility = Visibility.Collapsed;
        BehaviorPanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.Visibility = Visibility.Collapsed;
        UpdatesPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
    }

    public string Theme => ((ComboBoxItem?)ThemeComboBox.SelectedItem)?.Tag?.ToString() ?? "System";
    public string DefaultView => ((ComboBoxItem?)DefaultViewComboBox.SelectedItem)?.Tag?.ToString() ?? "details";
    public int DefaultIconSize => UiSettings.NormalizeIconSize((int)Math.Round(DefaultIconSizeSlider.Value));
    public string ColumnPreset => ((ComboBoxItem?)ColumnPresetComboBox.SelectedItem)?.Tag?.ToString() ?? "default";
    public bool ShowHidden => ShowHiddenSwitch.IsOn;
    public bool SidebarVisible => ShowSideMenuSwitch.IsOn;
    public bool ShowQuickAccess => ShowQuickAccessSwitch.IsOn;
    public bool ShowFolderTree => ShowFolderTreeSwitch.IsOn;
    public bool ShowBookmarks => ShowBookmarksSwitch.IsOn;
    public bool ShowRecentLocations => ShowRecentSwitch.IsOn;
    public bool ShowSmartFolders => ShowSmartFoldersSwitch.IsOn;
    public nint OwnerHwnd { get; set; }
    public Func<Task>? ClearRecentHistoryAction { get; set; }

    public void ApplyTo(UiSettings settings)
    {
        settings.Theme = UiSettings.NormalizeTheme(Theme);
        settings.DefaultView = UiSettings.NormalizeDefaultView(DefaultView);
        settings.DefaultIconSize = UiSettings.NormalizeIconSize(DefaultIconSize);
        settings.ColumnPreset = UiSettings.NormalizeColumnPreset(ColumnPreset);
        settings.ShowHidden = ShowHiddenSwitch.IsOn;
        settings.ConfirmDelete = ConfirmDeleteSwitch.IsOn;
        settings.KeepFoldersOnTop = KeepFoldersOnTopSwitch.IsOn;
        settings.StartLocation = UiSettings.NormalizeStartLocation(
            ((ComboBoxItem?)StartLocationComboBox.SelectedItem)?.Tag?.ToString());
        settings.CustomPath = CustomPathBox.Text.Trim();
        settings.OpenInNewTab = OpenInNewTabSwitch.IsOn;
        settings.SidebarVisible = ShowSideMenuSwitch.IsOn;
        settings.ShowQuickAccess = ShowQuickAccessSwitch.IsOn;
        settings.ShowFolderTree = ShowFolderTreeSwitch.IsOn;
        settings.ShowBookmarks = ShowBookmarksSwitch.IsOn;
        settings.ShowRecentLocations = ShowRecentSwitch.IsOn;
        settings.ShowSmartFolders = ShowSmartFoldersSwitch.IsOn;
        settings.EnableGitIntegration = EnableGitSwitch.IsOn;
        settings.ShowFolderSizes = ShowFolderSizesSwitch.IsOn;
    }

    public async Task LoadSettingsAsync(FileOperationService fileOps, CancellationToken cancellationToken = default)
    {
        _fileOps = fileOps;
        var defaults = UiSettings.CreateDefault();

        SelectTheme(await GetSettingOrDefaultAsync(fileOps, "theme", defaults.Theme, cancellationToken).ConfigureAwait(true));

        SelectDefaultView(await GetSettingOrDefaultAsync(fileOps, "defaultView", defaults.DefaultView, cancellationToken).ConfigureAwait(true));

        SelectDefaultIconSize(await GetSettingOrDefaultAsync(fileOps, "defaultIconSize", defaults.DefaultIconSize.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(true));

        SelectColumnPreset(await GetSettingOrDefaultAsync(fileOps, "columnPreset", defaults.ColumnPreset, cancellationToken).ConfigureAwait(true) ?? defaults.ColumnPreset);

        ShowHiddenSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "showHidden", defaults.ShowHidden, cancellationToken).ConfigureAwait(true);

        ConfirmDeleteSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "confirmDelete", defaults.ConfirmDelete, cancellationToken).ConfigureAwait(true);
        KeepFoldersOnTopSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "keepFoldersOnTop", defaults.KeepFoldersOnTop, cancellationToken).ConfigureAwait(true);

        var startLoc = UiSettings.NormalizeStartLocation(
            await GetSettingOrDefaultAsync(fileOps, "startLocation", defaults.StartLocation, cancellationToken).ConfigureAwait(true));
        StartLocationComboBox.SelectedIndex = startLoc == "custom" ? 2 : (startLoc == "last" ? 1 : 0);

        CustomPathBox.Text = await GetSettingOrDefaultAsync(fileOps, "customPath", defaults.CustomPath, cancellationToken).ConfigureAwait(true) ?? "";

        OpenInNewTabSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "openInNewTab", defaults.OpenInNewTab, cancellationToken).ConfigureAwait(true);
        ShowSideMenuSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.visible", defaults.SidebarVisible, cancellationToken).ConfigureAwait(true);
        ShowQuickAccessSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showQuickAccess", defaults.ShowQuickAccess, cancellationToken).ConfigureAwait(true);
        ShowFolderTreeSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showFolders", defaults.ShowFolderTree, cancellationToken).ConfigureAwait(true);
        ShowBookmarksSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showBookmarks", defaults.ShowBookmarks, cancellationToken).ConfigureAwait(true);
        ShowRecentSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showRecent", defaults.ShowRecentLocations, cancellationToken).ConfigureAwait(true);
        ShowSmartFoldersSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showSmartFolders", defaults.ShowSmartFolders, cancellationToken).ConfigureAwait(true);

        EnableGitSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "enableGitIntegration", defaults.EnableGitIntegration, cancellationToken).ConfigureAwait(true);
        ShowFolderSizesSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "showFolderSizes", defaults.ShowFolderSizes, cancellationToken).ConfigureAwait(true);

        await CheckRarInstalledAsync(cancellationToken).ConfigureAwait(true);

        await LoadVersionAsync(fileOps, cancellationToken).ConfigureAwait(true);
    }

    private void SelectColumnPreset(string preset)
    {
        var normalized = UiSettings.NormalizeColumnPreset(preset);
        var selected = ColumnPresetComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), normalized, StringComparison.Ordinal));
        ColumnPresetComboBox.SelectedItem = selected ?? ColumnPresetComboBox.Items[0];
    }

    private void SelectDefaultView(string? view)
    {
        var normalized = UiSettings.NormalizeDefaultView(view);
        var selected = DefaultViewComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), normalized, StringComparison.Ordinal));
        DefaultViewComboBox.SelectedItem = selected ?? DefaultViewComboBox.Items[0];
    }

    private void SelectDefaultIconSize(string? iconSize)
    {
        var normalized = UiSettings.NormalizeIconSize(iconSize);
        DefaultIconSizeSlider.Value = normalized;
        UpdateDefaultIconSizeValueText(normalized);
    }

    private void OnDefaultIconSizeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var normalized = UiSettings.NormalizeIconSize((int)Math.Round(e.NewValue));
        if (sender is Slider slider && Math.Abs(slider.Value - normalized) > 0.01)
        {
            slider.Value = normalized;
            return;
        }

        UpdateDefaultIconSizeValueText(normalized);
    }

    private void UpdateDefaultIconSizeValueText(int iconSize)
    {
        DefaultIconSizeValueText.Text = $"{UiSettings.NormalizeIconSize(iconSize)} px";
    }

    private void SelectTheme(string? theme)
    {
        ThemeComboBox.SelectedIndex = UiSettings.NormalizeTheme(theme) switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
    }

    private static async Task<string?> GetSettingOrDefaultAsync(
        FileOperationService fileOps,
        string key,
        string? fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return await fileOps.GetSettingAsync(key, cancellationToken).ConfigureAwait(true) ?? fallback;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return fallback;
        }
    }

    private static async Task<bool> ReadBoolSettingAsync(
        FileOperationService fileOps,
        string key,
        bool fallback,
        CancellationToken cancellationToken)
    {
        var value = await GetSettingOrDefaultAsync(fileOps, key, fallback ? "true" : "false", cancellationToken).ConfigureAwait(true);
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
    }

    private async void OnClearRecentHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (ClearRecentHistoryAction is null)
        {
            return;
        }

        ClearRecentHistoryButton.IsEnabled = false;
        RecentHistoryStatusText.Visibility = Visibility.Collapsed;
        try
        {
            await ClearRecentHistoryAction().ConfigureAwait(true);
            RecentHistoryStatusText.Text = "Recent history cleared.";
            RecentHistoryStatusText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            RecentHistoryStatusText.Text = "Clear recent history cancelled.";
            RecentHistoryStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            RecentHistoryStatusText.Text = exception.Message;
            RecentHistoryStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            ClearRecentHistoryButton.IsEnabled = true;
        }
    }

    private async Task LoadVersionAsync(FileOperationService fileOps, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await fileOps.GetAppVersionAsync(cancellationToken).ConfigureAwait(true);
            CurrentVersionText.Text = $"Current Version: {version}";
            AboutVersionText.Text = $"Version {version}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CurrentVersionText.Text = "Current Version: unavailable";
            AboutVersionText.Text = "Version unavailable";
            UpdateStatusText.Text = $"Unable to load version: {exception.Message}";
        }
    }

    private async Task CheckRarInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (_fileOps == null) return;
        try
        {
            var installed = await _fileOps.CheckRarInstalledAsync(cancellationToken).ConfigureAwait(true);
            RarStatusText.Text = installed ? "Installed" : "Not installed";
            InstallRarButton.IsEnabled = !installed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RarStatusText.Text = $"Unable to check RAR support: {exception.Message}";
            InstallRarButton.IsEnabled = true;
        }
    }

    private async void OnInstallRarClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        InstallRarButton.IsEnabled = false;
        RarStatusText.Text = "Preparing install...";

        try
        {
            var prepResult = await _fileOps.PrepareRarInstallAsync().ConfigureAwait(true);
            if (prepResult != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Install RAR Support",
                    Content = "This will download and install third-party components to support RAR extraction. Do you agree to their terms?",
                    PrimaryButtonText = "Install",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    RarStatusText.Text = "Installing...";
                    await _fileOps.InstallRarAsync(prepResult.ConfirmationToken).ConfigureAwait(true);
                    await CheckRarInstalledAsync().ConfigureAwait(true);
                }
                else
                {
                    await _fileOps.DiscardRarInstallAsync(prepResult.ConfirmationToken).ConfigureAwait(true);
                    await CheckRarInstalledAsync().ConfigureAwait(true);
                }
            }
            else
            {
                RarStatusText.Text = "Failed to prepare installation.";
                InstallRarButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            RarStatusText.Text = "Installation cancelled.";
            InstallRarButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            RarStatusText.Text = exception.Message;
            InstallRarButton.IsEnabled = true;
        }
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _checkedUpdateIsInstallable = false;
        UpdateStatusText.Text = "Checking...";
        try
        {
            var update = await _fileOps.CheckForUpdateAsync().ConfigureAwait(true);
            if (update != null)
            {
                InstallUpdateButton.Visibility = Visibility.Visible;
                InstallUpdateButton.IsEnabled = true;
                _checkedUpdateIsInstallable = update.Installable;
                InstallUpdateButton.Content = update.Installable ? "Download & Install" : "Open GitHub Releases";
                var version = string.IsNullOrWhiteSpace(update.Version)
                    ? "an update"
                    : update.Version;
                UpdateStatusText.Text = update.Installable
                    ? $"Update available: {version}. The installer metadata is signed and ready to verify before launch."
                    : $"Update available: {version}. This build cannot verify the installer metadata, so download it from GitHub Releases.";
            }
            else
            {
                UpdateStatusText.Text = "No updates available.";
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "Update check cancelled.";
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = exception.Message;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = _checkedUpdateIsInstallable
            ? "Downloading and verifying installer..."
            : "Opening GitHub Releases...";
        try
        {
            if (_checkedUpdateIsInstallable)
            {
                var progress = new Progress<long[]>(values =>
                {
                    if (values.Length >= 2)
                    {
                        var downloaded = values[0];
                        var total = values[1];
                        UpdateStatusText.Text = total > 0
                            ? $"Downloading update: {downloaded:N0} of {total:N0} bytes"
                            : $"Downloading update: {downloaded:N0} bytes";
                    }
                });
                await _fileOps.InstallUpdateAsync(progress).ConfigureAwait(true);
                UpdateStatusText.Text = "Verified installer launched. SumaFile may close while the update finishes.";
            }
            else
            {
                await _fileOps.OpenExternalUrlAsync(RepositoryUrl + "/releases").ConfigureAwait(true);
                UpdateStatusText.Text = "Download the latest release from the GitHub page.";
            }
        }
        catch (Exception exception)
        {
            InstallUpdateButton.Content = "Open GitHub Releases";
            _checkedUpdateIsInstallable = false;
            UpdateStatusText.Text = "Could not install in-app. Please visit " + RepositoryUrl + "/releases to download the update. " + exception.Message;
        }
        finally
        {
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private async void OnBrowseCustomPath(object sender, RoutedEventArgs e)
    {
        var browseButton = sender as Button;
        if (browseButton is not null)
        {
            browseButton.IsEnabled = false;
        }

        CustomPathStatusText.Visibility = Visibility.Collapsed;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            if (OwnerHwnd != 0)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                CustomPathBox.Text = folder.Path;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CustomPathStatusText.Text = exception.Message;
            CustomPathStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (browseButton is not null)
            {
                browseButton.IsEnabled = true;
            }
        }
    }

    private async void OnGitHubClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps is null)
        {
            return;
        }

        var link = sender as Control;
        if (link is not null)
        {
            link.IsEnabled = false;
        }

        AboutStatusText.Visibility = Visibility.Collapsed;
        try
        {
            await _fileOps.OpenExternalUrlAsync(RepositoryUrl).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AboutStatusText.Text = exception.Message;
            AboutStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (link is not null)
            {
                link.IsEnabled = true;
            }
        }
    }
}
