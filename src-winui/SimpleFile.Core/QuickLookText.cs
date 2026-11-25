namespace SimpleFile.Core;

/// <summary>Small rules of the Quick Look lightbox that need no window.</summary>
public static class QuickLookText
{
    /// <summary>
    /// Where the shown item is among the pane's rows, beside its name: <c>3 of 12</c>. Nothing for a pane with a
    /// single row, or when the item is no longer among the rows.
    /// </summary>
    public static string Position(int index, int count) =>
        index < 0 || index >= count || count <= 1 ? "" : $"{index + 1} of {count}";
}

/// <summary>The zoom of an image in Quick Look: it opens fitted, and steps up to eight times that.</summary>
public static class QuickLookZoom
{
    public const double Fit = 1;
    public const double Max = 8;

    private static readonly double[] Steps = [1, 1.25, 1.5, 2, 3, 4, 6, 8];

    /// <summary>
    /// The next zoom from <paramref name="current"/>: one step in (a positive <paramref name="delta"/>) or out. A
    /// zoom between two steps, as a pinch or Ctrl+wheel leaves it, goes to the nearest step in that direction.
    /// </summary>
    public static double Step(double current, int delta)
    {
        if (delta == 0 || !double.IsFinite(current))
        {
            return Fit;
        }

        if (delta > 0)
        {
            foreach (var step in Steps)
            {
                if (step > current + 0.01)
                {
                    return step;
                }
            }

            return Max;
        }

        for (var index = Steps.Length - 1; index >= 0; index--)
        {
            if (Steps[index] < current - 0.01)
            {
                return Steps[index];
            }
        }

        return Fit;
    }
}
