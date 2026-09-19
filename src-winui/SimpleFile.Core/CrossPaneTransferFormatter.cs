namespace SimpleFile.Core;

public static class CrossPaneTransferFormatter
{
    public static PaneId OtherPaneFor(PaneId pane) =>
        pane == PaneId.Secondary ? PaneId.Primary : PaneId.Secondary;

    public static string CommandLabel(string verb, PaneId targetPane, string? destination = null)
    {
        var label = $"{verb} to {TargetPaneName(targetPane)}";
        return string.IsNullOrWhiteSpace(destination)
            ? label
            : $"{label}: {destination}";
    }

    public static string TargetPaneName(PaneId pane) =>
        pane == PaneId.Primary ? "left pane" : "right pane";
}
