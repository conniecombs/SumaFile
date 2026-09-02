namespace SimpleFile.Core;

/// <summary>
/// Decides which command-strip items move into the More menu when the shell
/// is too narrow. Uses intrinsic item widths so the set is stable while
/// collapsing (no show/hide oscillation).
/// </summary>
public static class ToolbarOverflowPlanner
{
    public const string Filter = "filter";
    public const string Search = "search";
    public const string Settings = "settings";
    public const string Profiles = "profiles";
    public const string DualPane = "dual-pane";
    public const string ViewOptions = "view-options";
    public const string NewFile = "new-file";
    public const string NewFolder = "new-folder";

    public const double PathMinWidth = 140;
    public const double ColumnSpacing = 8;
    public const double SearchMinWidth = 260;
    public const double SearchMaxWidth = 480;
    public const double FilterMinWidth = 128;
    public const double FilterMaxWidth = 200;

    private const double SearchWidthRatio = 0.24;
    private const double FilterWidthRatio = 0.10;
    private const double FilterOuterChromeWidth = 12;

    /// <summary>Hide first as the pane shrinks. Nav, path, and More stay.</summary>
    public static readonly string[] PrimaryHideOrder =
    [
        Filter,
        Search,
        Settings,
        Profiles,
        DualPane,
        ViewOptions,
        NewFile,
        NewFolder,
    ];

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

    public static double SearchWidthFor(double availableWidth) =>
        ResponsiveWidth(availableWidth, SearchMinWidth, SearchMaxWidth, SearchWidthRatio);

    public static double FilterWidthFor(double availableWidth) =>
        ResponsiveWidth(availableWidth, FilterMinWidth, FilterMaxWidth, FilterWidthRatio);

    public static double SearchOverflowWidthFor(double availableWidth) =>
        SearchWidthFor(availableWidth) + ColumnSpacing;

    public static double FilterOverflowWidthFor(double availableWidth) =>
        FilterWidthFor(availableWidth) + FilterOuterChromeWidth;

    private static double ResponsiveWidth(double availableWidth, double minWidth, double maxWidth, double ratio)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return minWidth;
        }

        return Math.Clamp(availableWidth * ratio, minWidth, maxWidth);
    }
}
