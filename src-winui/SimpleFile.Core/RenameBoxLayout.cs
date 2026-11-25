namespace SimpleFile.Core;

/// <summary>
/// Where the box for renaming a file in place goes in its row: over the name, wherever the view puts it. In the
/// Details, List and Content views that is the row's line, and the box is a little wider than the name. A tile has
/// the name on the first of two lines beside the icon, or under the icon, where the box goes across the tile.
/// </summary>
public static class RenameBoxLayout
{
    /// <summary>The narrowest box a name is edited in, and the room left for typing a longer one.</summary>
    public const double MinWidth = 160;
    public const double ExtraWidth = 24;

    /// <summary>
    /// The box's top for a name whose own top is <paramref name="nameTop"/>: the box's text sits a little below its
    /// edge, so the edge goes that much above the name, and never above the row.
    /// </summary>
    public static double Top(double nameTop) => Math.Max(0, nameTop - TextInset);

    /// <summary>How far the box's text is from the box's top edge.</summary>
    public const double TextInset = 3;

    /// <summary>
    /// Whether the name is below the row's middle line, as in a tile, and not on it:
    /// <paramref name="nameTop"/> and <paramref name="nameHeight"/> are the name's, inside a row
    /// <paramref name="rowHeight"/> high.
    /// </summary>
    public static bool NameIsBelowIcon(double nameTop, double nameHeight, double rowHeight) =>
        rowHeight > 0 && nameTop + nameHeight / 2 - rowHeight / 2 > 8;

    /// <summary>
    /// The box's width beside an icon: the name's and a little more, at least <see cref="MinWidth"/>, and no wider
    /// than the row has left (but never under 80, so a narrow row still shows a box).
    /// </summary>
    public static double RowWidth(double nameWidth, double available) =>
        Math.Max(80, Math.Min(Math.Max(MinWidth, nameWidth + ExtraWidth), available));
}
