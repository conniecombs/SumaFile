namespace SimpleFile.Core;

public sealed class UiSettings
{
    public const double SidebarDefaultWidth = 232;
    public const double SidebarMinWidth = 180;
    public const double SidebarMaxWidth = 520;
    public const double PreviewDefaultWidth = 296;
    public const double PreviewMinWidth = 200;
    public const double PreviewMaxWidth = 720;
    public const double DualPaneDefaultPercent = 50;
    public const double DualPaneMinPercent = 20;
    public const double DualPaneMaxPercent = 80;
    public const double FilePaneMinWidth = 180;
    public const double DualPaneDividerWidth = 8;
    public const int IconSizeMin = 16;
    public const int IconSizeMax = 256;
    public const int IconSizeStep = 8;

    /// <summary>
    /// The largest plugin panel size a setting keeps. The real upper bound depends on the window
    /// (<see cref="PluginPanelLayout.ClampBottomHeight"/>); this one only rejects nonsense.
    /// </summary>
    public const double PluginPanelMaxStoredSize = 10000;

    public string Theme { get; set; } = "system";
    public string DefaultView { get; set; } = "details";
    public int DefaultIconSize { get; set; } = 16;
    public bool ShowHidden { get; set; }
    public bool ConfirmDelete { get; set; } = true;
    public bool KeepFoldersOnTop { get; set; } = true;
    public bool OpenInNewTab { get; set; }
    public bool ShowQuickAccess { get; set; } = true;
    public bool ShowFolderTree { get; set; }
    public bool ShowBookmarks { get; set; } = true;
    public bool ShowRecentLocations { get; set; } = true;
    public bool ShowSmartFolders { get; set; } = true;
    public bool ShowTags { get; set; } = true;
    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = SidebarDefaultWidth;
    public bool ShowFolderSizes { get; set; }
    public bool EnableGitIntegration { get; set; } = true;
    public bool ProgressQueueVisible { get; set; }
    public string StartLocation { get; set; } = "home";
    public string CustomPath { get; set; } = "";
    public string LastPath { get; set; } = "";
    public bool PreviewVisible { get; set; } = true;
    public bool PreviewPinned { get; set; }
    public bool PreviewAutoCollapse { get; set; } = true;
    public double PreviewWidth { get; set; } = PreviewDefaultWidth;
    public bool PreviewRenderHtml { get; set; }
    public bool PreviewVideoPlaybackEnabled { get; set; }
    public double DualPanePrimaryPercent { get; set; } = DualPaneDefaultPercent;
    public double DualPanePrimaryWidth { get; set; }
    public bool QuickAccessCollapsed { get; set; }
    public bool MyPcCollapsed { get; set; }
    public bool TagsCollapsed { get; set; }
    /// <summary>
    /// The collapsed plugin sidebar sections, by <c>&lt;pluginId&gt;:&lt;sectionId&gt;</c>
    /// (<see cref="PluginSidebarLayout.CollapseKey"/>). Saved as a JSON array under <c>sidebar.pluginSectionsCollapsed</c>,
    /// in the host's settings rather than the plugin's, so a plugin cannot change it.
    /// </summary>
    public List<string> CollapsedPluginSidebarSections { get; set; } = [];
    /// <summary>The docked bottom plugin panel's height, saved as <c>pluginPanel.bottomHeight</c>.</summary>
    public double PluginPanelBottomHeight { get; set; } = PluginPanelLayout.BottomDefaultHeight;
    /// <summary>The docked right plugin panel's width, saved as <c>pluginPanel.rightWidth</c>.</summary>
    public double PluginPanelRightWidth { get; set; } = PluginPanelLayout.RightDefaultWidth;
    public int PhotoFolderImageThreshold { get; set; } = 70;
    /// <summary>Primary pane column preset. Settings dialog edits this and mirrors to secondary.</summary>
    public string ColumnPreset { get; set; } = "default";
    /// <summary>Secondary pane column preset (independent of primary).</summary>
    public string SecondaryColumnPreset { get; set; } = "default";
    /// <summary>Primary pane column widths.</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Secondary pane column widths (independent of primary).</summary>
    public Dictionary<string, double> SecondaryColumnWidths { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> ShortcutOverrides { get; set; } = new(StringComparer.Ordinal);
    public CommandSurfaceLayout CommandSurface { get; set; } = CommandSurfaceLayout.CreateDefault();
    public FolderViewSettingsDocument FolderViewSettings { get; set; } = new();
    public uint ThumbnailCacheMaxMb { get; set; } = 500;
    public string ThumbnailCachePath { get; set; } = "";

    public static UiSettings CreateDefault() => new();

    public static readonly IReadOnlyList<(string Id, string Label)> ViewOptions =
    [
        ("details", "Details"),
        ("list", "List"),
        ("tiles", "Tiles"),
        ("content", "Content"),
    ];

    public static readonly IReadOnlyList<(int Size, string Label)> IconSizeOptions =
    [
        (16, "Small icons"),
        (32, "Medium icons"),
        (48, "Large icons"),
        (96, "Extra large icons"),
        (256, "Huge icons"),
    ];

    public static string NormalizeTheme(string? theme)
    {
        if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return "dark";
        }

        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
        {
            return "light";
        }

        if (string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase)
            || string.Equals(theme, "windows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(theme, "default", StringComparison.OrdinalIgnoreCase))
        {
            return "system";
        }

        return "system";
    }

    /// <summary>The plugin section collapse keys without empty or duplicate entries, in their first order.</summary>
    public static List<string> NormalizePluginSectionKeys(IEnumerable<string?>? keys) =>
        keys is null
            ? []
            : keys
                .Select(key => key?.Trim() ?? "")
                .Where(key => key.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    public static string NormalizeStartLocation(string? startLocation)
    {
        return startLocation?.Trim().ToLowerInvariant() switch
        {
            "last" => "last",
            "custom" => "custom",
            _ => "home",
        };
    }

    public static string NormalizeColumnPreset(string? preset)
    {
        var normalized = (preset ?? "").Trim().ToLowerInvariant();
        return ColumnLayout.Presets.ContainsKey(normalized) ? normalized : "default";
    }

    public static string NormalizeDefaultView(string? view)
    {
        var normalized = (view ?? "").Trim().ToLowerInvariant();
        return ViewOptions.Any(option => string.Equals(option.Id, normalized, StringComparison.Ordinal))
            ? normalized
            : "details";
    }

    public static int NormalizeIconSize(int? iconSize)
    {
        return iconSize is null ? IconSizeMin : NormalizeIconSize(iconSize.Value);
    }

    /// <summary>Snaps any size (including the retired 128 and 192 steps) to the nearest standard option.</summary>
    public static int NormalizeIconSize(int size)
    {
        return size switch
        {
            128 => 96,
            192 => 256,
            _ => IconSizeOptions
                .OrderBy(option => Math.Abs(option.Size - size))
                .ThenByDescending(option => option.Size)
                .First().Size,
        };
    }

    public static int NormalizeIconSize(string? iconSize)
    {
        return int.TryParse(iconSize, out var parsed)
            ? NormalizeIconSize(parsed)
            : NormalizeIconSize((int?)null);
    }

    public static double NormalizeSidebarWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return SidebarDefaultWidth;
        }

        return Math.Clamp(width, SidebarMinWidth, SidebarMaxWidth);
    }

    public static double NormalizeSidebarWidth(string? width)
    {
        return double.TryParse(width, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? NormalizeSidebarWidth(parsed)
            : SidebarDefaultWidth;
    }

    public static double NormalizePreviewWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width))
        {
            return PreviewDefaultWidth;
        }

        return Math.Clamp(width, PreviewMinWidth, PreviewMaxWidth);
    }

    public static double NormalizePreviewWidth(string? width)
    {
        return double.TryParse(width, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? NormalizePreviewWidth(parsed)
            : PreviewDefaultWidth;
    }

    /// <summary>
    /// A stored bottom panel height: the default when it is not a number, otherwise at least
    /// <see cref="PluginPanelLayout.BottomMinHeight"/> and at most <see cref="PluginPanelMaxStoredSize"/>.
    /// </summary>
    public static double NormalizePluginPanelBottomHeight(double height) =>
        NormalizePluginPanelSize(height, PluginPanelLayout.BottomMinHeight, PluginPanelLayout.BottomDefaultHeight);

    /// <summary>A stored right panel width, by the same rule with <see cref="PluginPanelLayout.RightMinWidth"/>.</summary>
    public static double NormalizePluginPanelRightWidth(double width) =>
        NormalizePluginPanelSize(width, PluginPanelLayout.RightMinWidth, PluginPanelLayout.RightDefaultWidth);

    private static double NormalizePluginPanelSize(double size, double min, double fallback) =>
        double.IsFinite(size) ? Math.Clamp(size, min, PluginPanelMaxStoredSize) : fallback;

    public static double NormalizeDualPanePrimaryPercent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
        {
            return DualPaneDefaultPercent;
        }

        return Math.Clamp(percent, DualPaneMinPercent, DualPaneMaxPercent);
    }

    public static double NormalizeDualPanePrimaryPercent(string? percent)
    {
        return double.TryParse(percent, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? NormalizeDualPanePrimaryPercent(parsed)
            : DualPaneDefaultPercent;
    }

    public static double NormalizeDualPanePrimaryWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return 0;
        }

        return Math.Max(FilePaneMinWidth, width);
    }

    public static double NormalizeDualPanePrimaryWidth(string? width)
    {
        return double.TryParse(width, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? NormalizeDualPanePrimaryWidth(parsed)
            : 0;
    }

    public static double ResolveDualPanePrimaryWidth(double storedWidth, double storedPercent, double available)
    {
        var divider = DualPaneDividerWidth;
        var min = FilePaneMinWidth;
        if (double.IsNaN(available) || available <= 0)
        {
            return storedWidth > 0 ? NormalizeDualPanePrimaryWidth(storedWidth) : 0;
        }

        if (available < (min * 2) + divider)
        {
            return Math.Max(0, (available - divider) / 2);
        }

        var max = available - min - divider;
        if (storedWidth > 0)
        {
            return Math.Clamp(storedWidth, min, max);
        }

        var percent = NormalizeDualPanePrimaryPercent(storedPercent);
        return Math.Clamp(available * percent / 100.0, min, max);
    }
}
