namespace SimpleFile.Core;

public sealed record PaneRoleText(string Caption, string Tooltip, string AutomationName);

public static class PaneRoleFormatter
{
    public static PaneRoleText Format(PaneId pane, PaneId activePane, bool dualPaneEnabled, string? path)
    {
        var paneName = PaneName(pane);
        var shortName = ShortPaneName(pane);
        var pathText = string.IsNullOrWhiteSpace(path) ? "No folder loaded" : path.Trim();
        if (!dualPaneEnabled)
        {
            var singleTooltip = $"{paneName}: {pathText}";
            return new PaneRoleText(paneName, singleTooltip, singleTooltip);
        }

        var active = pane == activePane;
        var caption = active ? $"{shortName} - Active" : $"{shortName} - Destination";
        var tooltip = active
            ? $"{paneName} is active. Copy or move to other pane sends selected items from here. Path: {pathText}"
            : $"{paneName} is the destination for Copy/Move to other pane. Path: {pathText}";
        return new PaneRoleText(caption, tooltip, $"{caption}. {pathText}");
    }

    private static string ShortPaneName(PaneId pane) =>
        pane == PaneId.Secondary ? "Right" : "Left";

    private static string PaneName(PaneId pane) =>
        pane == PaneId.Secondary ? "Right pane" : "Left pane";
}
