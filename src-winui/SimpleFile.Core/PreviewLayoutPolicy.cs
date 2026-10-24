namespace SimpleFile.Core;

public static class PreviewLayoutPolicy
{
    public static bool ShouldCollapse(UiSettings settings, bool hasContent, bool dualPane, double availableWidth, bool manuallyExpanded)
    {
        if (!settings.PreviewVisible || settings.PreviewPinned || !settings.PreviewAutoCollapse || manuallyExpanded) return false;
        return !hasContent || (dualPane && availableWidth > 0
            && availableWidth - settings.PreviewWidth - UiSettings.DualPaneDividerWidth < 760);
    }
}
