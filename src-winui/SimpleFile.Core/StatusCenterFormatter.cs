namespace SimpleFile.Core;

public sealed record StatusCenterSnapshot(
    string BadgeText,
    string HeaderText,
    string DetailText,
    bool HasWork,
    bool HasAttention);

public static class StatusCenterFormatter
{
    public static StatusCenterSnapshot Format(
        int activeTransfers,
        int queuedTransfers,
        bool hasCompletedTransfers,
        bool listingInProgress,
        string? workspaceStatus,
        int operationHistoryCount)
    {
        var active = Math.Max(0, activeTransfers);
        var queued = Math.Max(0, queuedTransfers);
        var totalTransferWork = active + queued;
        if (totalTransferWork > 0)
        {
            var badge = active > 0 ? $"{active} active" : $"{queued} queued";
            var detail = queued > 0
                ? $"{Transfers(active)} running, {queued} waiting"
                : $"{Transfers(active)} running";
            return new StatusCenterSnapshot(
                badge,
                "Transfers in progress",
                detail,
                HasWork: true,
                HasAttention: false);
        }

        if (listingInProgress)
        {
            return new StatusCenterSnapshot(
                "Loading",
                "Folder is loading",
                "SumaFile is still receiving items for the active pane.",
                HasWork: true,
                HasAttention: false);
        }

        if (hasCompletedTransfers)
        {
            return new StatusCenterSnapshot(
                "Done",
                "Transfers finished",
                "Open the Status Center to review completed transfers.",
                HasWork: false,
                HasAttention: true);
        }

        if (!string.IsNullOrWhiteSpace(workspaceStatus))
        {
            return new StatusCenterSnapshot(
                "Status",
                workspaceStatus.Trim(),
                operationHistoryCount > 0
                    ? Operations(operationHistoryCount)
                    : "No recent file operations",
                HasWork: false,
                HasAttention: false);
        }

        return new StatusCenterSnapshot(
            "Idle",
            "No active tasks",
            operationHistoryCount > 0
                ? Operations(operationHistoryCount)
                : "SumaFile is ready.",
            HasWork: false,
            HasAttention: false);
    }

    private static string Transfers(int count) => count == 1 ? "1 transfer" : $"{count} transfers";

    private static string Operations(int count) => count == 1 ? "1 recent operation" : $"{count} recent operations";
}
