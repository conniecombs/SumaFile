namespace SimpleFile.Core;

/// <summary>
/// Decides which command-strip items move into the More menu when the shell
/// is too narrow. Uses intrinsic item widths so the set is stable while
/// collapsing (no show/hide oscillation).
/// </summary>
public static class ToolbarOverflowPlanner
{
    public const string Settings = "settings";
    public const string Profiles = "profiles";
    public const string DualPane = "dual-pane";
    public const string ViewOptions = "view-options";
    public const string New = "new";

    public const double PathMinWidth = 140;
    public const double ColumnSpacing = 8;

    /// <summary>Hide first as the pane shrinks. Nav, omnibar, and More stay.</summary>
    public static readonly string[] PrimaryHideOrder =
    [
        Settings,
        Profiles,
        ViewOptions,
        New,
        "disk-cleanup",
        "duplicate-checker",
        "ftp-sftp-manager",
        "operation-history",
        "move-to-pane",
        "copy-to-pane",
        DualPane,
    ];

    public static IReadOnlyList<string> PrimaryHideOrderFor(CommandSurfaceLayout layout)
    {
        var actionIds = layout.VisiblePrimaryActionIds();
        if (actionIds.SequenceEqual(CommandSurfaceLayout.DefaultPrimaryActionIds, StringComparer.Ordinal))
        {
            return PrimaryHideOrder;
        }

        return [.. actionIds.Reverse()];
    }

    public static IReadOnlyDictionary<string, double> PrimaryItemWidthsFor(
        double availableWidth,
        CommandSurfaceLayout layout)
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var id in layout.VisiblePrimaryActionIds())
        {
            widths[id] = ToolbarActionCatalog.WidthFor(id, layout.ToolbarDisplayMode);
        }

        return widths;
    }

    public static HashSet<string> OverflowIds(
        double availableWidth,
        double reservedWidth,
        IReadOnlyDictionary<string, double> itemWidths,
        IReadOnlyList<string> hideOrder)
    {
        var overflowed = new HashSet<string>(StringComparer.Ordinal);
        if (double.IsNaN(availableWidth) || availableWidth <= 0 || hideOrder.Count == 0)
        {
            return overflowed;
        }

        var needed = reservedWidth;
        foreach (var id in hideOrder)
        {
            if (itemWidths.TryGetValue(id, out var width) && width > 0)
            {
                needed += width;
            }
        }

        foreach (var id in hideOrder)
        {
            if (needed <= availableWidth)
            {
                break;
            }

            if (!itemWidths.TryGetValue(id, out var width) || width <= 0)
            {
                continue;
            }

            overflowed.Add(id);
            needed -= width;
        }

        return overflowed;
    }
}
