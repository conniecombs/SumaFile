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
    private static readonly string[] LoadSettingKeys =
    [
        "theme",
        "defaultView",
        "defaultIconSize",
        "showHidden",
        "confirmDelete",
        "keepFoldersOnTop",
        "startLocation",
        "customPath",
        "lastPath",
        "openInNewTab",
        "enableGitIntegration",
        "progressQueue.visible",
        "showFolderSizes",
        "previewVisible",
        "preview.width",
        "preview.renderHtml",
        "preview.videoPlayback",
        "dualPane.primaryPercent",
        "dualPane.primaryWidth",
        "columnPreset",
        "columnPreset.secondary",
        "columnWidths",
        KeyboardShortcutMap.SettingsKey,
        CommandSurfaceLayout.SettingsKey,
        FolderViewSettingsDocument.SettingsKey,
        "sidebar.showQuickAccess",
        "sidebar.showFolders",
        "sidebar.showBookmarks",
        "sidebar.showRecent",
        "sidebar.showSmartFolders",
        "sidebar.showTags",
        "sidebar.visible",
        "sidebar.width",
        "sidebar.quickAccessCollapsed",
        "sidebar.myPcCollapsed",
        "sidebar.tagsCollapsed",
        "thumbnailCacheMaxMb",
        "thumbnailCachePath",
        BookmarksSettingsKey,
        RecentPathsSettingsKey,
    ];

    public static async Task<WorkspaceSettingsState> LoadAsync(
        ISettingsBackend fileOps,
        CancellationToken cancellationToken)
    {
        var timer = new StartupTimer("WorkspaceSettingsStore.Load");
        timer.Mark("begin");
        var values = await fileOps.GetSettingsAsync(LoadSettingKeys, cancellationToken).ConfigureAwait(false);
        timer.Mark("settings-batch", $"keys={LoadSettingKeys.Length} returned={values.Count}");

        var settings = UiSettings.CreateDefault();
        settings.Theme = UiSettings.NormalizeTheme(ReadSetting(values, "theme"));
        settings.DefaultView = UiSettings.NormalizeDefaultView(ReadSetting(values, "defaultView"));
        settings.DefaultIconSize = UiSettings.NormalizeIconSize(ReadSetting(values, "defaultIconSize"));
        settings.ShowHidden = ReadBoolSetting(values, "showHidden", false);
        settings.ConfirmDelete = ReadBoolSetting(values, "confirmDelete", true);
        settings.KeepFoldersOnTop = ReadBoolSetting(values, "keepFoldersOnTop", true);
        settings.StartLocation = UiSettings.NormalizeStartLocation(
            ReadSetting(values, "startLocation"));
        settings.CustomPath = ReadSetting(values, "customPath") ?? "";
        settings.LastPath = ReadSetting(values, "lastPath") ?? "";
        settings.OpenInNewTab = ReadBoolSetting(values, "openInNewTab", false);
        settings.EnableGitIntegration = ReadBoolSetting(values, "enableGitIntegration", true);
        settings.ProgressQueueVisible = ReadBoolSetting(values, "progressQueue.visible", false);
        settings.ShowFolderSizes = ReadBoolSetting(values, "showFolderSizes", false);
        settings.PreviewVisible = ReadBoolSetting(values, "previewVisible", true);
        settings.PreviewWidth = UiSettings.NormalizePreviewWidth(
            ReadDoubleSetting(values, "preview.width", UiSettings.PreviewDefaultWidth));
        settings.PreviewRenderHtml = ReadBoolSetting(values, "preview.renderHtml", false);
        settings.PreviewVideoPlaybackEnabled = ReadBoolSetting(values, "preview.videoPlayback", false);
        settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(
            ReadDoubleSetting(values, "dualPane.primaryPercent", UiSettings.DualPaneDefaultPercent));
        settings.DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(
            ReadDoubleSetting(values, "dualPane.primaryWidth", 0));
        var columnPresets = ReadColumnPresets(values);
        settings.ColumnPreset = columnPresets.Primary;
        settings.SecondaryColumnPreset = columnPresets.Secondary;
        var columnWidths = ReadColumnWidths(values);
        settings.ColumnWidths = columnWidths.Primary;
        settings.SecondaryColumnWidths = columnWidths.Secondary;
        settings.ShortcutOverrides = ReadShortcutOverrides(values);
        settings.CommandSurface = CommandSurfaceLayout.FromJson(
            ReadSetting(values, CommandSurfaceLayout.SettingsKey));
        settings.FolderViewSettings = FolderViewSettingsDocument.FromJson(
            ReadSetting(values, FolderViewSettingsDocument.SettingsKey));
        settings.ShowQuickAccess = ReadBoolSetting(values, "sidebar.showQuickAccess", true);
        settings.ShowFolderTree = ReadBoolSetting(values, "sidebar.showFolders", false);
        settings.ShowBookmarks = ReadBoolSetting(values, "sidebar.showBookmarks", true);
        settings.ShowRecentLocations = ReadBoolSetting(values, "sidebar.showRecent", true);
        settings.ShowSmartFolders = ReadBoolSetting(values, "sidebar.showSmartFolders", true);
        settings.ShowTags = ReadBoolSetting(values, "sidebar.showTags", true);
        settings.SidebarVisible = ReadBoolSetting(values, "sidebar.visible", true);
        settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(
            ReadDoubleSetting(values, "sidebar.width", UiSettings.SidebarDefaultWidth));
        settings.QuickAccessCollapsed = ReadBoolSetting(values, "sidebar.quickAccessCollapsed", false);
        settings.MyPcCollapsed = ReadBoolSetting(values, "sidebar.myPcCollapsed", false);
        settings.TagsCollapsed = ReadBoolSetting(values, "sidebar.tagsCollapsed", false);
        settings.ThumbnailCacheMaxMb = ReadUIntSetting(values, "thumbnailCacheMaxMb", 500);
        settings.ThumbnailCachePath = ReadSetting(values, "thumbnailCachePath") ?? "";
        var bookmarks = ReadBookmarks(values);
        var recentPaths = ReadRecentPaths(values);
        timer.Mark("parsed");

        return new WorkspaceSettingsState
        {
            Settings = settings,
            Bookmarks = bookmarks,
            RecentPaths = recentPaths,
        };
    }

    public static async Task SaveAsync(
        ISettingsBackend fileOps,
        UiSettings settings,
        ColumnLayout primaryColumns,
        ColumnLayout secondaryColumns,
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
        settings.SecondaryColumnPreset = UiSettings.NormalizeColumnPreset(settings.SecondaryColumnPreset);
        settings.ColumnWidths = primaryColumns.SnapshotWidths();
        settings.SecondaryColumnWidths = secondaryColumns.SnapshotWidths();
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
        await fileOps.SetSettingAsync("preview.renderHtml", settings.PreviewRenderHtml ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("preview.videoPlayback", settings.PreviewVideoPlaybackEnabled ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("dualPane.primaryPercent", settings.DualPanePrimaryPercent.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("dualPane.primaryWidth", settings.DualPanePrimaryWidth.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("columnPreset", settings.ColumnPreset, cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("columnPreset.secondary", settings.SecondaryColumnPreset, cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync(
            "columnWidths",
            JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal)
            {
                ["primary"] = settings.ColumnWidths,
                ["secondary"] = settings.SecondaryColumnWidths,
            }),
            cancellationToken).ConfigureAwait(false);
        settings.ShortcutOverrides = KeyboardShortcutMap.NormalizeOverrides(settings.ShortcutOverrides);
        await fileOps.SetSettingAsync(
            KeyboardShortcutMap.SettingsKey,
            KeyboardShortcutMap.WriteOverridesJson(settings.ShortcutOverrides),
            cancellationToken).ConfigureAwait(false);
        settings.CommandSurface.Normalize();
        await fileOps.SetSettingAsync(
            CommandSurfaceLayout.SettingsKey,
            settings.CommandSurface.ToJson(),
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
        await fileOps.SetSettingAsync("sidebar.showTags", settings.ShowTags ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.visible", settings.SidebarVisible ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.width", settings.SidebarWidth.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.quickAccessCollapsed", settings.QuickAccessCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.myPcCollapsed", settings.MyPcCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("sidebar.tagsCollapsed", settings.TagsCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("thumbnailCacheMaxMb", settings.ThumbnailCacheMaxMb.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await fileOps.SetSettingAsync("thumbnailCachePath", settings.ThumbnailCachePath, cancellationToken).ConfigureAwait(false);
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

    private readonly record struct PaneColumnPresets(string Primary, string Secondary);

    private readonly record struct PaneColumnWidths(
        Dictionary<string, double> Primary,
        Dictionary<string, double> Secondary);

    private static string? ReadSetting(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static PaneColumnPresets ReadColumnPresets(IReadOnlyDictionary<string, string?> values)
    {
        var primary = UiSettings.NormalizeColumnPreset(
            ReadSetting(values, "columnPreset"));
        var secondaryRaw = ReadSetting(values, "columnPreset.secondary");
        var secondary = string.IsNullOrWhiteSpace(secondaryRaw)
            ? primary
            : UiSettings.NormalizeColumnPreset(secondaryRaw);
        return new PaneColumnPresets(primary, secondary);
    }

    private static PaneColumnWidths ReadColumnWidths(IReadOnlyDictionary<string, string?> values)
    {
        var empty = () => new Dictionary<string, double>(StringComparer.Ordinal);
        var raw = ReadSetting(values, "columnWidths");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PaneColumnWidths(empty(), empty());
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new PaneColumnWidths(empty(), empty());
            }

            // Nested { "primary": {...}, "secondary": {...} }.
            if (LooksLikePaneColumnWidths(document.RootElement))
            {
                var primary = ReadWidthMap(document.RootElement, "primary") ?? empty();
                var secondary = ReadWidthMap(document.RootElement, "secondary") ?? CloneWidths(primary);
                return new PaneColumnWidths(primary, secondary);
            }

            // Legacy flat { "name": 240, ... } — seed both panes.
            var flat = ReadWidthMap(document.RootElement) ?? empty();
            return new PaneColumnWidths(flat, CloneWidths(flat));
        }
        catch
        {
            return new PaneColumnWidths(empty(), empty());
        }
    }

    private static bool LooksLikePaneColumnWidths(JsonElement root)
    {
        if (!root.TryGetProperty("primary", out var primary) && !root.TryGetProperty("secondary", out primary))
        {
            return false;
        }

        return primary.ValueKind == JsonValueKind.Object;
    }

    private static Dictionary<string, double>? ReadWidthMap(JsonElement root, string? property = null)
    {
        JsonElement target = root;
        if (property is not null)
        {
            if (!root.TryGetProperty(property, out target) || target.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var propertyElement in target.EnumerateObject())
        {
            if (propertyElement.Value.ValueKind == JsonValueKind.Number
                && propertyElement.Value.TryGetDouble(out var width)
                && !double.IsNaN(width)
                && !double.IsInfinity(width))
            {
                result[propertyElement.Name] = width;
            }
        }

        return result;
    }

    private static Dictionary<string, double> CloneWidths(IReadOnlyDictionary<string, double> source) =>
        new(source, StringComparer.Ordinal);

    private static Dictionary<string, List<string>> ReadShortcutOverrides(
        IReadOnlyDictionary<string, string?> values)
    {
        var raw = ReadSetting(values, KeyboardShortcutMap.SettingsKey);
        return KeyboardShortcutMap.ReadOverridesJson(raw);
    }

    private static List<BookmarkItem> ReadBookmarks(IReadOnlyDictionary<string, string?> values)
    {
        var raw = ReadSetting(values, BookmarksSettingsKey);
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

    private static List<string> ReadRecentPaths(IReadOnlyDictionary<string, string?> values)
    {
        var raw = ReadSetting(values, RecentPathsSettingsKey);
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

    private static bool ReadBoolSetting(
        IReadOnlyDictionary<string, string?> values,
        string key,
        bool fallback)
    {
        var raw = ReadSetting(values, key);
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

    private static double ReadDoubleSetting(
        IReadOnlyDictionary<string, string?> values,
        string key,
        double fallback)
    {
        var raw = ReadSetting(values, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static uint ReadUIntSetting(
        IReadOnlyDictionary<string, string?> values,
        string key,
        uint fallback)
    {
        var raw = ReadSetting(values, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
