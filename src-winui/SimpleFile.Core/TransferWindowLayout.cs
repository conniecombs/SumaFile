namespace SimpleFile.Core;

/// <summary>
/// The WinUI-free vertical sizing rules for the Transfers window's expandable Details pane.
/// Each expanded task card stretches its Details scroll area to share the list viewport's height
/// while keeping at least <see cref="MinDetailsHeight"/> visible and reserving up to
/// <see cref="CollapsedSiblingReserveCap"/> for collapsed sibling cards.
/// </summary>
public static class TransferWindowLayout
{
    /// <summary>Smallest height given to an expanded Details scroll area (~4 lines of active paths).</summary>
    public const double MinDetailsHeight = 96;

    /// <summary>
    /// Maximum viewport height reserved for collapsed sibling rows (~1 collapsed card). When more
    /// collapsed rows exist, the outer list scrolls while an expanded Details pane still scales with the window.
    /// </summary>
    public const double CollapsedSiblingReserveCap = 160;

    /// <summary>
    /// Sub-pixel / DPI layout-rounding slack so heights snapped upward at non-integer scales
    /// never exceed the outer <c>ListView</c> viewport by half a physical pixel.
    /// </summary>
    public const double ViewportRoundingSlack = 1;

    /// <summary>
    /// Computes the target height (in DIPs) for each expanded task's Details <c>ScrollViewer</c>.
    /// </summary>
    /// <param name="viewportHeight">Actual height of the outer <c>TransferList</c> viewport.</param>
    /// <param name="expandedChromeHeights">
    /// Non-scroll outer height of each expanded card (card border/padding/header/progress/status +
    /// expander header/padding + tuning row + item container margins).
    /// </param>
    /// <param name="collapsedRowHeights">Outer height (including item margins) of collapsed sibling cards.</param>
    /// <returns>
    /// The height to assign to each expanded Details <c>ScrollViewer</c>, or <see cref="double.NaN"/> when
    /// no expanded cards are ready or <paramref name="viewportHeight"/> is not laid out yet.
    /// </returns>
    public static double DetailsScrollHeight(
        double viewportHeight,
        IReadOnlyList<double> expandedChromeHeights,
        IReadOnlyList<double> collapsedRowHeights)
    {
        ArgumentNullException.ThrowIfNull(expandedChromeHeights);
        ArgumentNullException.ThrowIfNull(collapsedRowHeights);

        if (expandedChromeHeights.Count == 0 || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            return double.NaN;
        }

        var expandedChromeTotal = 0.0;
        var validExpandedCount = 0;
        for (var i = 0; i < expandedChromeHeights.Count; i++)
        {
            var chrome = expandedChromeHeights[i];
            if (double.IsFinite(chrome) && chrome > 0)
            {
                expandedChromeTotal += chrome;
                validExpandedCount++;
            }
        }

        if (validExpandedCount == 0)
        {
            return double.NaN;
        }

        var collapsedTotal = 0.0;
        for (var i = 0; i < collapsedRowHeights.Count; i++)
        {
            var rowHeight = collapsedRowHeights[i];
            if (double.IsFinite(rowHeight) && rowHeight > 0)
            {
                collapsedTotal += rowHeight;
            }
        }

        var reservedCollapsed = Math.Min(collapsedTotal, CollapsedSiblingReserveCap);
        var available = viewportHeight - expandedChromeTotal - reservedCollapsed - ViewportRoundingSlack;
        var perExpanded = Math.Floor(available / validExpandedCount);
        return Math.Max(MinDetailsHeight, perExpanded);
    }

    /// <summary>
    /// True when <paramref name="currentHeight"/> already matches <paramref name="targetHeight"/> closely enough
    /// that reassigning <c>FrameworkElement.Height</c> would be a sub-pixel no-op.
    /// </summary>
    public static bool HeightMatches(double currentHeight, double targetHeight)
    {
        if (double.IsNaN(targetHeight))
        {
            return double.IsNaN(currentHeight);
        }

        return double.IsFinite(currentHeight) && Math.Abs(currentHeight - targetHeight) < 0.5;
    }
}
