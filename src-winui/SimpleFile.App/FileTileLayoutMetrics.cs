using SimpleFile.Core;

namespace SimpleFile.App;

internal static class FileTileLayoutMetrics
{
    public const int StackedIconThreshold = 128;

    public static bool UsesStackedLayout(int iconSize) =>
        UiSettings.NormalizeIconSize(iconSize) > StackedIconThreshold;

    public static double ContentWidthFor(int iconSize)
    {
        var normalized = UiSettings.NormalizeIconSize(iconSize);
        return UsesStackedLayout(normalized)
            ? Math.Max(188, normalized + 28)
            : Math.Max(188, normalized + 140);
    }

    public static double ContainerWidthFor(int iconSize) =>
        ContentWidthFor(iconSize) + 20;

    public static double ContainerHeightFor(int iconSize) =>
        MinHeightFor(iconSize) + 6;

    public static double MinHeightFor(int iconSize)
    {
        var normalized = UiSettings.NormalizeIconSize(iconSize);
        return UsesStackedLayout(normalized)
            ? normalized + 76
            : Math.Max(72, normalized + 24);
    }
}
