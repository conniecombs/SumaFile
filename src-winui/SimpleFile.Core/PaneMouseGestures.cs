namespace SimpleFile.Core;

/// <summary>What the mouse's extra buttons do in the window.</summary>
public static class PaneMouseGestures
{
    /// <summary>
    /// The history step for a press of the mouse's side buttons: the first one goes back (-1), the second forward
    /// (+1), as in Explorer and in a browser. Neither, or both at once, is no step.
    /// </summary>
    public static int HistoryStep(bool xButton1Pressed, bool xButton2Pressed) =>
        xButton1Pressed == xButton2Pressed ? 0 : xButton1Pressed ? -1 : 1;
}

/// <summary>
/// Where an item dragged along its strip, or moved with the keyboard, lands: a tab among a pane's tabs, a column
/// header among the Details view's headers.
/// </summary>
public static class StripReorder
{
    /// <summary>
    /// The position a dragged item takes when it is let go at <paramref name="x"/>: the position of the item under
    /// the pointer, the first when the pointer is left of them all and the last when it is right of them all.
    /// <paramref name="items"/> are their left and right edges, in order, measured like <paramref name="x"/>.
    /// </summary>
    public static int TargetIndex(double x, IReadOnlyList<(double Left, double Right)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (var index = 0; index < items.Count; index++)
        {
            if (x < items[index].Right)
            {
                return index;
            }
        }

        return Math.Max(0, items.Count - 1);
    }

    /// <summary>
    /// The position an item at <paramref name="index"/> of <paramref name="count"/> moves to with the keyboard or
    /// from a menu: one place left or right, stopping at either end.
    /// </summary>
    public static int Step(int index, int count, int delta) =>
        count <= 0 ? 0 : Math.Clamp(index + Math.Sign(delta), 0, count - 1);
}
