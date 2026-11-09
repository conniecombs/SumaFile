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
    private const string PluginSectionsCollapsedSettingsKey = "sidebar.pluginSectionsCollapsed";
    private const string PluginPanelBottomHeightSettingsKey = "pluginPanel.bottomHeight";
    private const string PluginPanelRightWidthSettingsKey = "pluginPanel.rightWidth";
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
        "preview.pinned",
        "preview.autoCollapse",
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
        PluginSectionsCollapsedSettingsKey,
        PluginPanelBottomHeightSettingsKey,
        PluginPanelRightWidthSettingsKey,
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
        settings.PreviewPinned = ReadBoolSetting(values, "preview.pinned", false);
        settings.PreviewAutoCollapse = ReadBoolSetting(values, "preview.autoCollapse", true);
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
        settings.CollapsedPluginSidebarSections = ReadStringList(values, PluginSectionsCollapsedSettingsKey);
        settings.PluginPanelBottomHeight = UiSettings.NormalizePluginPanelBottomHeight(
            ReadDoubleSetting(values, PluginPanelBottomHeightSettingsKey, PluginPanelLayout.BottomDefaultHeight));
        settings.PluginPanelRightWidth = UiSettings.NormalizePluginPanelRightWidth(
            ReadDoubleSetting(values, PluginPanelRightWidthSettingsKey, PluginPanelLayout.RightDefaultWidth));
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

    /// <summary>
    /// Saves <paramref name="settings"/>. Every value it writes is computed on the caller's thread before the first
    /// await: the normalized settings, the JSON of the toolbar layout and the folder-view settings, the shortcut
    /// overrides, the plugin section keys, the bookmarks and the recents. A change the UI thread makes while the writes
    /// run (a section toggle swapping in a new list, a plugin offering a toolbar item) is therefore neither lost nor
    /// read half-way, and no list property of <paramref name="settings"/> is assigned after an await.
    /// </summary>
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
        settings.ShortcutOverrides = KeyboardShortcutMap.NormalizeOverrides(settings.ShortcutOverrides);
        settings.CommandSurface.Normalize();
        settings.CollapsedPluginSidebarSections = UiSettings.NormalizePluginSectionKeys(settings.CollapsedPluginSidebarSections);
        settings.PluginPanelBottomHeight = UiSettings.NormalizePluginPanelBottomHeight(settings.PluginPanelBottomHeight);
        settings.PluginPanelRightWidth = UiSettings.NormalizePluginPanelRightWidth(settings.PluginPanelRightWidth);

        (string Key, string Value)[] values =
        [
            ("theme", settings.Theme),
            ("defaultView", settings.DefaultView),
            ("defaultIconSize", settings.DefaultIconSize.ToString(CultureInfo.InvariantCulture)),
            ("showHidden", settings.ShowHidden ? "true" : "false"),
            ("confirmDelete", settings.ConfirmDelete ? "true" : "false"),
            ("keepFoldersOnTop", settings.KeepFoldersOnTop ? "true" : "false"),
            ("startLocation", settings.StartLocation),
            ("customPath", settings.CustomPath),
            ("openInNewTab", settings.OpenInNewTab ? "true" : "false"),
            ("enableGitIntegration", settings.EnableGitIntegration ? "true" : "false"),
            ("progressQueue.visible", settings.ProgressQueueVisible ? "true" : "false"),
            ("showFolderSizes", settings.ShowFolderSizes ? "true" : "false"),
            ("previewVisible", settings.PreviewVisible ? "true" : "false"),
            ("preview.pinned", settings.PreviewPinned ? "true" : "false"),
            ("preview.autoCollapse", settings.PreviewAutoCollapse ? "true" : "false"),
            ("preview.width", settings.PreviewWidth.ToString(CultureInfo.InvariantCulture)),
            ("preview.renderHtml", settings.PreviewRenderHtml ? "true" : "false"),
            ("preview.videoPlayback", settings.PreviewVideoPlaybackEnabled ? "true" : "false"),
            ("dualPane.primaryPercent", settings.DualPanePrimaryPercent.ToString(CultureInfo.InvariantCulture)),
            ("dualPane.primaryWidth", settings.DualPanePrimaryWidth.ToString(CultureInfo.InvariantCulture)),
            ("columnPreset", settings.ColumnPreset),
            ("columnPreset.secondary", settings.SecondaryColumnPreset),
            ("columnWidths", JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal)
            {
                ["primary"] = settings.ColumnWidths,
                ["secondary"] = settings.SecondaryColumnWidths,
            })),
            (KeyboardShortcutMap.SettingsKey, KeyboardShortcutMap.WriteOverridesJson(settings.ShortcutOverrides)),
            (CommandSurfaceLayout.SettingsKey, settings.CommandSurface.ToJson()),
            (FolderViewSettingsDocument.SettingsKey, settings.FolderViewSettings.ToJson()),
            ("sidebar.showQuickAccess", settings.ShowQuickAccess ? "true" : "false"),
            ("sidebar.showFolders", settings.ShowFolderTree ? "true" : "false"),
            ("sidebar.showBookmarks", settings.ShowBookmarks ? "true" : "false"),
            ("sidebar.showRecent", settings.ShowRecentLocations ? "true" : "false"),
            ("sidebar.showSmartFolders", settings.ShowSmartFolders ? "true" : "false"),
            ("sidebar.showTags", settings.ShowTags ? "true" : "false"),
            ("sidebar.visible", settings.SidebarVisible ? "true" : "false"),
            ("sidebar.width", settings.SidebarWidth.ToString(CultureInfo.InvariantCulture)),
            ("sidebar.quickAccessCollapsed", settings.QuickAccessCollapsed ? "true" : "false"),
            ("sidebar.myPcCollapsed", settings.MyPcCollapsed ? "true" : "false"),
            ("sidebar.tagsCollapsed", settings.TagsCollapsed ? "true" : "false"),
            (PluginSectionsCollapsedSettingsKey, JsonSerializer.Serialize(settings.CollapsedPluginSidebarSections)),
            (PluginPanelBottomHeightSettingsKey, settings.PluginPanelBottomHeight.ToString(CultureInfo.InvariantCulture)),
            (PluginPanelRightWidthSettingsKey, settings.PluginPanelRightWidth.ToString(CultureInfo.InvariantCulture)),
            ("thumbnailCacheMaxMb", settings.ThumbnailCacheMaxMb.ToString(CultureInfo.InvariantCulture)),
            ("thumbnailCachePath", settings.ThumbnailCachePath),
            ("lastPath", settings.LastPath),
            (BookmarksSettingsKey, JsonSerializer.Serialize(bookmarks)),
            (RecentPathsSettingsKey, JsonSerializer.Serialize(recentPaths)),
        ];

        // Only now the writes: nothing below reads settings again.
        foreach (var (key, value) in values)
        {
            await fileOps.SetSettingAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
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

    /// <summary>A JSON array of strings, normalized; empty when missing or malformed.</summary>
    private static List<string> ReadStringList(IReadOnlyDictionary<string, string?> values, string key)
    {
        var raw = ReadSetting(values, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return UiSettings.NormalizePluginSectionKeys(JsonSerializer.Deserialize<List<string?>>(raw));
        }
        catch (JsonException)
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
