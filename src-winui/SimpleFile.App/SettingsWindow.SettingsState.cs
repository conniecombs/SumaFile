using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SimpleFile.Core;
using Windows.Storage.Pickers;

namespace SimpleFile.App;

public sealed partial class SettingsWindow
{
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
        // Settings is a global default: apply the chosen preset to both panes.
        settings.SecondaryColumnPreset = settings.ColumnPreset;
        settings.ShowHidden = ShowHiddenSwitch.IsOn;
        settings.ConfirmDelete = ConfirmDeleteSwitch.IsOn;
        settings.KeepFoldersOnTop = KeepFoldersOnTopSwitch.IsOn;
        settings.StartLocation = UiSettings.NormalizeStartLocation(
            ((ComboBoxItem?)StartLocationComboBox.SelectedItem)?.Tag?.ToString());
        settings.CustomPath = CustomPathBox.Text.Trim();
        settings.OpenInNewTab = OpenInNewTabSwitch.IsOn;
        settings.PreviewRenderHtml = PreviewRenderHtmlSwitch.IsOn;
        settings.PreviewVideoPlaybackEnabled = PreviewVideoPlaybackSwitch.IsOn;
        settings.SidebarVisible = ShowSideMenuSwitch.IsOn;
        settings.ShowQuickAccess = ShowQuickAccessSwitch.IsOn;
        settings.ShowFolderTree = ShowFolderTreeSwitch.IsOn;
        settings.ShowBookmarks = ShowBookmarksSwitch.IsOn;
        settings.ShowRecentLocations = ShowRecentSwitch.IsOn;
        settings.ShowSmartFolders = ShowSmartFoldersSwitch.IsOn;
        settings.EnableGitIntegration = EnableGitSwitch.IsOn;
        settings.ShowFolderSizes = ShowFolderSizesSwitch.IsOn;
        settings.ShortcutOverrides = CurrentShortcutOverrides();
        settings.CommandSurface = CurrentCommandSurfaceLayout();
        settings.ThumbnailCacheMaxMb = EnableThumbnailCacheSwitch.IsOn ? (uint)Math.Round(CacheSizeSlider.Value) : 0;
        settings.ThumbnailCachePath = ThumbnailCachePathBox.Text.Trim();
    }

    public async Task LoadSettingsAsync(
        FileOperationService fileOps,
        UiSettings settings,
        CancellationToken cancellationToken = default)
    {
        _fileOps = fileOps;
        ApplySettingsSnapshot(settings);
        await CheckRarInstalledAsync(cancellationToken).ConfigureAwait(true);
        await LoadVersionAsync(fileOps, cancellationToken).ConfigureAwait(true);
    }

    private void ApplySettingsSnapshot(UiSettings settings)
    {
        SelectTheme(settings.Theme);
        SelectDefaultView(settings.DefaultView);
        SelectDefaultIconSize(settings.DefaultIconSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SelectColumnPreset(settings.ColumnPreset);

        ShowHiddenSwitch.IsOn = settings.ShowHidden;
        ConfirmDeleteSwitch.IsOn = settings.ConfirmDelete;
        KeepFoldersOnTopSwitch.IsOn = settings.KeepFoldersOnTop;

        var startLoc = UiSettings.NormalizeStartLocation(settings.StartLocation);
        StartLocationComboBox.SelectedIndex = startLoc == "custom" ? 2 : (startLoc == "last" ? 1 : 0);
        CustomPathBox.Text = settings.CustomPath ?? "";
        OpenInNewTabSwitch.IsOn = settings.OpenInNewTab;
        PreviewRenderHtmlSwitch.IsOn = settings.PreviewRenderHtml;
        PreviewVideoPlaybackSwitch.IsOn = settings.PreviewVideoPlaybackEnabled;

        ShowSideMenuSwitch.IsOn = settings.SidebarVisible;
        ShowQuickAccessSwitch.IsOn = settings.ShowQuickAccess;
        ShowFolderTreeSwitch.IsOn = settings.ShowFolderTree;
        ShowBookmarksSwitch.IsOn = settings.ShowBookmarks;
        ShowRecentSwitch.IsOn = settings.ShowRecentLocations;
        ShowSmartFoldersSwitch.IsOn = settings.ShowSmartFolders;

        EnableGitSwitch.IsOn = settings.EnableGitIntegration;
        ShowFolderSizesSwitch.IsOn = settings.ShowFolderSizes;
        ApplyShortcutOverrides(settings.ShortcutOverrides);
        ApplyCommandSurfaceLayout(CloneCommandSurfaceLayout(settings.CommandSurface));

        var cacheMaxMb = settings.ThumbnailCacheMaxMb;
        EnableThumbnailCacheSwitch.IsOn = cacheMaxMb > 0;
        CacheSizeSlider.Value = cacheMaxMb > 0 ? cacheMaxMb : 500;
        UpdateCacheSizeValueText((int)CacheSizeSlider.Value);
        CacheOptionsPanel.Visibility = EnableThumbnailCacheSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailCachePathBox.Text = settings.ThumbnailCachePath ?? "";
    }

    private static CommandSurfaceLayout CloneCommandSurfaceLayout(CommandSurfaceLayout layout)
    {
        return new CommandSurfaceLayout
        {
            Version = layout.Version,
            ToolbarDisplayMode = layout.ToolbarDisplayMode,
            PrimaryToolbar = layout.PrimaryToolbar
                .Select(item => item.IsSeparator ? CommandSurfaceItem.Separator() : CommandSurfaceItem.Command(item.Id))
                .ToList(),
        };
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
        ThemeComboBox.SelectedIndex = UiSettings.NormalizeTheme(theme) == "dark" ? 1 : 0;
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

    private void OnEnableThumbnailCacheToggled(object sender, RoutedEventArgs e)
    {
        CacheOptionsPanel.Visibility = EnableThumbnailCacheSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCacheSizeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateCacheSizeValueText((int)Math.Round(e.NewValue));
    }

    private void UpdateCacheSizeValueText(int sizeMb)
    {
        CacheSizeValueText.Text = sizeMb >= 1000 ? $"{sizeMb / 1000.0:0.#} GB ({sizeMb} MB)" : $"{sizeMb} MB";
    }

    private async void OnBrowseThumbnailCachePath(object sender, RoutedEventArgs e)
    {
        var browseButton = sender as Button;
        if (browseButton is not null)
        {
            browseButton.IsEnabled = false;
        }

        ThumbnailCachePathStatusText.Visibility = Visibility.Collapsed;
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
                ThumbnailCachePathBox.Text = folder.Path;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ThumbnailCachePathStatusText.Text = exception.Message;
            ThumbnailCachePathStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (browseButton is not null)
            {
                browseButton.IsEnabled = true;
            }
        }
    }

    private void OnResetCachePathClicked(object sender, RoutedEventArgs e)
    {
        ThumbnailCachePathBox.Text = "";
        ThumbnailCachePathStatusText.Visibility = Visibility.Collapsed;
    }
}
