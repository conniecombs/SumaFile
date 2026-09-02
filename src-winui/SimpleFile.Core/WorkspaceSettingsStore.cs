using System.Globalization;
using System.Text.Json;

namespace SimpleFile.Core;

internal sealed class WorkspaceSettingsState
{
    public UiSettings Settings { get; init; } = UiSettings.CreateDefault();
    public List<BookmarkItem> Bookmarks { get; init; } = [];
    public List<string> RecentPaths { get; init; } = [];
}

internal static class WorkspaceSettingsStore
{
    private const string BookmarksSettingsKey = "places.bookmarks";
    private const string RecentPathsSettingsKey = "places.recents";

    public static async Task<WorkspaceSettingsState> LoadAsync(
        ISettingsBackend fileOps,
        CancellationToken cancellationToken)
    {
        var settings = UiSettings.CreateDefault();
        settings.Theme = UiSettings.NormalizeTheme(await fileOps.GetSettingAsync("theme", cancellationToken).ConfigureAwait(false));
        settings.DefaultView = UiSettings.NormalizeDefaultView(await fileOps.GetSettingAsync("defaultView", cancellationToken).ConfigureAwait(false));
        settings.DefaultIconSize = UiSettings.NormalizeIconSize(await fileOps.GetSettingAsync("defaultIconSize", cancellationToken).ConfigureAwait(false));
        settings.ShowHidden = await ReadBoolSettingAsync(fileOps, "showHidden", false, cancellationToken).ConfigureAwait(false);
        settings.ConfirmDelete = await ReadBoolSettingAsync(fileOps, "confirmDelete", true, cancellationToken).ConfigureAwait(false);
        settings.KeepFoldersOnTop = await ReadBoolSettingAsync(fileOps, "keepFoldersOnTop", true, cancellationToken).ConfigureAwait(false);
        settings.StartLocation = UiSettings.NormalizeStartLocation(
            await fileOps.GetSettingAsync("startLocation", cancellationToken).ConfigureAwait(false));
        settings.CustomPath = await fileOps.GetSettingAsync("customPath", cancellationToken).ConfigureAwait(false) ?? "";
        settings.LastPath = await fileOps.GetSettingAsync("lastPath", cancellationToken).ConfigureAwait(false) ?? "";
        settings.OpenInNewTab = await ReadBoolSettingAsync(fileOps, "openInNewTab", false, cancellationToken).ConfigureAwait(false);
        settings.EnableGitIntegration = await ReadBoolSettingAsync(fileOps, "enableGitIntegration", true, cancellationToken).ConfigureAwait(false);
        settings.ProgressQueueVisible = await ReadBoolSettingAsync(fileOps, "progressQueue.visible", false, cancellationToken).ConfigureAwait(false);
        settings.ShowFolderSizes = await ReadBoolSettingAsync(fileOps, "showFolderSizes", false, cancellationToken).ConfigureAwait(false);
        settings.PreviewVisible = await ReadBoolSettingAsync(fileOps, "previewVisible", true, cancellationToken).ConfigureAwait(false);
        settings.PreviewWidth = UiSettings.NormalizePreviewWidth(
            await ReadDoubleSettingAsync(fileOps, "preview.width", UiSettings.PreviewDefaultWidth, cancellationToken).ConfigureAwait(false));
        settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(
            await ReadDoubleSettingAsync(fileOps, "dualPane.primaryPercent", UiSettings.DualPaneDefaultPercent, cancellationToken).ConfigureAwait(false));
        settings.DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(
            await ReadDoubleSettingAsync(fileOps, "dualPane.primaryWidth", 0, cancellationToken).ConfigureAwait(false));
        settings.ColumnPreset = UiSettings.NormalizeColumnPreset(
            await fileOps.GetSettingAsync("columnPreset", cancellationToken).ConfigureAwait(false));
        settings.ColumnWidths = await ReadColumnWidthsAsync(fileOps, cancellationToken).ConfigureAwait(false);
        settings.ShortcutOverrides = await ReadShortcutOverridesAsync(fileOps, cancellationToken).ConfigureAwait(false);
        settings.FolderViewSettings = FolderViewSettingsDocument.FromJson(
            await fileOps.GetSettingAsync(FolderViewSettingsDocument.SettingsKey, cancellationToken).ConfigureAwait(false));
        settings.ShowQuickAccess = await ReadBoolSettingAsync(fileOps, "sidebar.showQuickAccess", true, cancellationToken).ConfigureAwait(false);
        settings.ShowFolderTree = await ReadBoolSettingAsync(fileOps, "sidebar.showFolders", false, cancellationToken).ConfigureAwait(false);
        settings.ShowBookmarks = await ReadBoolSettingAsync(fileOps, "sidebar.showBookmarks", true, cancellationToken).ConfigureAwait(false);
        settings.ShowRecentLocations = await ReadBoolSettingAsync(fileOps, "sidebar.showRecent", true, cancellationToken).ConfigureAwait(false);
        settings.ShowSmartFolders = await ReadBoolSettingAsync(fileOps, "sidebar.showSmartFolders", true, cancellationToken).ConfigureAwait(false);
        settings.SidebarVisible = await ReadBoolSettingAsync(fileOps, "sidebar.visible", true, cancellationToken).ConfigureAwait(false);
        settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(
            await ReadDoubleSettingAsync(fileOps, "sidebar.width", UiSettings.SidebarDefaultWidth, cancellationToken).ConfigureAwait(false));
        settings.QuickAccessCollapsed = await ReadBoolSettingAsync(fileOps, "sidebar.quickAccessCollapsed", false, cancellationToken).ConfigureAwait(false);
        settings.MyPcCollapsed = await ReadBoolSettingAsync(fileOps, "sidebar.myPcCollapsed", false, cancellationToken).ConfigureAwait(false);

        return new WorkspaceSettingsState
        {
            Settings = settings,
            Bookmarks = await ReadBookmarksAsync(fileOps, cancellationToken).ConfigureAwait(false),
            RecentPaths = await ReadRecentPathsAsync(fileOps, cancellationToken).ConfigureAwait(false),
        };
    }

    public static async Task SaveAsync(
        ISettingsBackend fileOps,
        UiSettings settings,
        ColumnLayout columns,
        bool showHidden,
        IReadOnlyList<BookmarkItem> bookmarks,
        IReadOnlyList<string> recentPaths,
        CancellationToken cancellationToken)
    {
        settings.ShowHidden = showHidden;
        settings.Theme = UiSettings.NormalizeTheme(settings.Theme);
        settings.DefaultView = UiSettings.NormalizeDefaultView(settings.DefaultView);
        settings.DefaultIconSize = UiSettings.NormalizeIconSize(settings.DefaultIconSize);
        settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(settings.SidebarWidth);
        settings.PreviewWidth = UiSettings.NormalizePreviewWidth(settings.PreviewWidth);
        settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(settings.DualPanePrimaryPercent);
        settings.DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(settings.DualPanePrimaryWidth);
        settings.ColumnPreset = UiSettings.NormalizeColumnPreset(settings.ColumnPreset);
        settings.ColumnWidths = columns.SnapshotWidths();
        settings.FolderViewSettings.Normalize();
        await fileOps.SetSettingAsync("theme", settings.Theme, cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("defaultView", settings.DefaultView, cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("defaultIconSize", settings.DefaultIconSize.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("showHidden", settings.ShowHidden ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("confirmDelete", settings.ConfirmDelete ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("keepFoldersOnTop", settings.KeepFoldersOnTop ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("startLocation", settings.StartLocation, cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("customPath", settings.CustomPath, cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("openInNewTab", settings.OpenInNewTab ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("enableGitIntegration", settings.EnableGitIntegration ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("progressQueue.visible", settings.ProgressQueueVisible ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("showFolderSizes", settings.ShowFolderSizes ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("previewVisible", settings.PreviewVisible ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("preview.width", settings.PreviewWidth.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("dualPane.primaryPercent", settings.DualPanePrimaryPercent.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("dualPane.primaryWidth", settings.DualPanePrimaryWidth.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("columnPreset", settings.ColumnPreset, cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync(
            "columnWidths",
            JsonSerializer.Serialize(settings.ColumnWidths),
            cancellationToken).ConfigureAwait(false);
        settings.ShortcutOverrides = KeyboardShortcutMap.NormalizeOverrides(settings.ShortcutOverrides);
        await fileOps.SetSettingAsync(
            KeyboardShortcutMap.SettingsKey,
            KeyboardShortcutMap.WriteOverridesJson(settings.ShortcutOverrides),
            cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync(
            FolderViewSettingsDocument.SettingsKey,
            settings.FolderViewSettings.ToJson(),
            cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.showQuickAccess", settings.ShowQuickAccess ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.showFolders", settings.ShowFolderTree ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.showBookmarks", settings.ShowBookmarks ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.showRecent", settings.ShowRecentLocations ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.showSmartFolders", settings.ShowSmartFolders ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.visible", settings.SidebarVisible ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.width", settings.SidebarWidth.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.quickAccessCollapsed", settings.QuickAccessCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.myPcCollapsed", settings.MyPcCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("lastPath", settings.LastPath, cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync(
            BookmarksSettingsKey,
            JsonSerializer.Serialize(bookmarks),
            cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync(
            RecentPathsSettingsKey,
            JsonSerializer.Serialize(recentPaths),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, double>> ReadColumnWidthsAsync(
        ISettingsBackend fileOps,
        CancellationToken cancellationToken)
    {
        var raw = await fileOps.GetSettingAsync("columnWidths", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(raw)
                ?? new Dictionary<string, double>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }

    private static async Task<Dictionary<string, List<string>>> ReadShortcutOverridesAsync(
        ISettingsBackend fileOps,
        CancellationToken cancellationToken)
    {
        var raw = await fileOps.GetSettingAsync(KeyboardShortcutMap.SettingsKey, cancellationToken).ConfigureAwait(false);
        return KeyboardShortcutMap.ReadOverridesJson(raw);
    }

    private static async Task<List<BookmarkItem>> ReadBookmarksAsync(
        ISettingsBackend fileOps,
        CancellationToken cancellationToken)
    {
        var raw = await fileOps.GetSettingAsync(BookmarksSettingsKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            var saved = JsonSerializer.Deserialize<List<BookmarkItem>>(raw) ?? [];
            var result = new List<BookmarkItem>();
            foreach (var bookmark in saved)
            {
                var path = (bookmark.Path ?? "").Trim();
                if (string.IsNullOrWhiteSpace(path)
                    || result.Any(item => PathRules.PathsEqual(item.Path, path)))
                {
                    continue;
                }

                var name = (bookmark.Name ?? "").Trim();
                result.Add(new BookmarkItem
                {
                    Name = string.IsNullOrWhiteSpace(name) ? PathRules.Basename(path) : name,
                    Path = path,
                });
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<string>> ReadRecentPathsAsync(
        ISettingsBackend fileOps,
        CancellationToken cancellationToken)
    {
        var raw = await fileOps.GetSettingAsync(RecentPathsSettingsKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            var saved = JsonSerializer.Deserialize<List<string>>(raw) ?? [];
            var result = new List<string>();
            foreach (var path in saved)
            {
                var trimmed = (path ?? "").Trim();
                if (string.IsNullOrWhiteSpace(trimmed)
                    || result.Any(item => PathRules.PathsEqual(item, trimmed)))
                {
                    continue;
                }

                result.Add(trimmed);
                if (result.Count >= PlacesStore.RecentLimit)
                {
                    break;
                }
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<bool> ReadBoolSettingAsync(
        ISettingsBackend fileOps,
        string key,
        bool fallback,
        CancellationToken cancellationToken)
    {
        var raw = await fileOps.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
    }

    private static async Task<double> ReadDoubleSettingAsync(
        ISettingsBackend fileOps,
        string key,
        double fallback,
        CancellationToken cancellationToken)
    {
        var raw = await fileOps.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
