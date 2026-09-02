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

    public string Theme { get; set; } = "system";
    public string DefaultView { get; set; } = "details";
    public int DefaultIconSize { get; set; } = 16;
    public bool ShowHidden { get; set; }
    public bool ConfirmDelete { get; set; } = true;
    public bool KeepFoldersOnTop { get; set; } = true;
    public bool OpenInNewTab { get; set; }
    public bool AutoCollapseTree { get; set; }
    public bool ShowQuickAccess { get; set; } = true;
    public bool ShowFolderTree { get; set; }
    public bool ShowBookmarks { get; set; } = true;
    public bool ShowRecentLocations { get; set; } = true;
    public bool ShowSmartFolders { get; set; } = true;
    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = SidebarDefaultWidth;
    public bool ShowFolderSizes { get; set; }
    public bool EnableGitIntegration { get; set; } = true;
    public bool ProgressQueueVisible { get; set; }
    public string StartLocation { get; set; } = "home";
    public string CustomPath { get; set; } = "";
    public string LastPath { get; set; } = "";
    public bool PreviewVisible { get; set; } = true;
    public double PreviewWidth { get; set; } = PreviewDefaultWidth;
    public double DualPanePrimaryPercent { get; set; } = DualPaneDefaultPercent;
    public double DualPanePrimaryWidth { get; set; }
    public bool QuickAccessCollapsed { get; set; }
    public bool MyPcCollapsed { get; set; }
    public int PhotoFolderImageThreshold { get; set; } = 70;
    public string ColumnPreset { get; set; } = "default";
    public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> ShortcutOverrides { get; set; } = new(StringComparer.Ordinal);
    public FolderViewSettingsDocument FolderViewSettings { get; set; } = new();

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
        (128, "Jumbo icons"),
        (192, "Huge icons"),
        (256, "Maximum icons"),
    ];

    public static string NormalizeTheme(string? theme)
    {
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

        return "dark";
    }

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
        if (iconSize is null)
        {
            return IconSizeMin;
        }

        var clamped = Math.Clamp(iconSize.Value, IconSizeMin, IconSizeMax);
        var snappedOffset = (int)Math.Round((clamped - IconSizeMin) / (double)IconSizeStep, MidpointRounding.AwayFromZero)
            * IconSizeStep;
        return Math.Clamp(IconSizeMin + snappedOffset, IconSizeMin, IconSizeMax);
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
