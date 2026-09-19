namespace SimpleFile.Core;

public sealed record OperationHistoryDisplay(
    string RowText,
    string StatusLabel,
    string SourceSummary,
    string DestinationSummary,
    string RetryLabel);

public static class OperationHistoryFormatter
{
    public const string RetrySelectedLabel = "Retry selected transfer";

    public static OperationHistoryDisplay Format(OperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var status = NormalizeStatus(record.Status);
        return new OperationHistoryDisplay(
            RowText(record, status),
            StatusLabel(status),
            SourceSummary(record.Sources),
            DestinationSummary(record.Destination),
            RetrySelectedLabel);
    }

    private static string RowText(OperationRecord record, string status)
    {
        var count = Math.Max(record.Sources.Length, 1);
        if (!string.IsNullOrWhiteSpace(record.Destination))
        {
            return status switch
            {
                "completed" => TransferProgressFormatter.CompletionReceipt(record.Move, count, record.Destination),
                "skipped" => TransferProgressFormatter.SkippedReceipt(record.Move, count, record.Destination),
                "failed" => FailedReceipt(record.Move, count, record.Destination),
                "cancelled" => CancelledReceipt(record.Move, count, record.Destination),
                _ => PendingReceipt(record.Move, count, record.Destination),
            };
        }

        if (!string.IsNullOrWhiteSpace(record.Description))
        {
            return record.Description;
        }

        return status switch
        {
            "completed" => TransferProgressFormatter.CompletionReceipt(record.Move, count, null),
            "skipped" => TransferProgressFormatter.SkippedReceipt(record.Move, count, null),
            "failed" => FailedReceipt(record.Move, count, null),
            "cancelled" => CancelledReceipt(record.Move, count, null),
            _ => PendingReceipt(record.Move, count, null),
        };
    }

    private static string SourceSummary(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return "From: -";
        }

        var preview = string.Join(", ", sources.Take(3).Select(PathRules.Basename));
        if (sources.Count > 3)
        {
            preview += $", and {sources.Count - 3} more";
        }

        return $"From: {preview}";
    }

    private static string DestinationSummary(string destination) =>
        string.IsNullOrWhiteSpace(destination)
            ? "Destination: -"
            : $"Destination: {destination}";

    private static string StatusLabel(string status) => status switch
    {
        "completed" => "Completed",
        "skipped" => "Skipped",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        _ => "Pending",
    };

    private static string NormalizeStatus(string? status)
    {
        var normalized = (status ?? "").Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "completed" : normalized;
    }

    private static string FailedReceipt(bool move, int itemCount, string? destination)
    {
        var verb = move ? "Move" : "Copy";
        return Receipt($"{verb} failed", itemCount, destination);
    }

    private static string CancelledReceipt(bool move, int itemCount, string? destination)
    {
        var verb = move ? "Move" : "Copy";
        return Receipt($"{verb} cancelled", itemCount, destination);
    }

    private static string PendingReceipt(bool move, int itemCount, string? destination)
    {
        var verb = move ? "Move" : "Copy";
        return Receipt(verb, itemCount, destination);
    }

    private static string Receipt(string prefix, int itemCount, string? destination)
    {
        var noun = itemCount == 1 ? "item" : "items";
        var receipt = $"{prefix} for {itemCount} {noun}";
        return string.IsNullOrWhiteSpace(destination)
            ? receipt
            : $"{receipt} to {destination}";
    }
}
