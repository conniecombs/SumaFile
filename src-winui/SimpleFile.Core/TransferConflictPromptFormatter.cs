namespace SimpleFile.Core;

public sealed record TransferConflictPromptContext(
    bool Move,
    int SourceCount,
    string Destination,
    PaneId? TargetPane,
    IReadOnlyList<string> Conflicts);

public sealed record TransferConflictPromptText(
    string Title,
    string OperationSummary,
    string DestinationSummary,
    string ConflictSummary,
    string ChoiceHint);

public static class TransferConflictPromptFormatter
{
    public static TransferConflictPromptText Format(TransferConflictPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var verb = context.Move ? "Move" : "Copy";
        var sourceCount = Math.Max(context.SourceCount, 1);
        var sourceNoun = sourceCount == 1 ? "item" : "items";
        var destination = string.IsNullOrWhiteSpace(context.Destination) ? "selected destination" : context.Destination;
        var destinationSummary = context.TargetPane is { } pane
            ? $"Destination: {CrossPaneTransferFormatter.TargetPaneName(pane)} - {destination}"
            : $"Destination: {destination}";

        return new TransferConflictPromptText(
            $"{verb} conflict",
            $"{verb} {sourceCount} selected {sourceNoun}",
            destinationSummary,
            ConflictSummary(context.Conflicts),
            "Choose how SumaFile should handle this transfer.");
    }

    private static string ConflictSummary(IReadOnlyList<string>? conflicts)
    {
        if (conflicts is null || conflicts.Count == 0)
        {
            return "A destination name already exists.";
        }

        if (conflicts.Count == 1)
        {
            return $"1 name already exists: {conflicts[0]}";
        }

        var preview = string.Join(Environment.NewLine, conflicts.Take(6).Select(name => $"- {name}"));
        var extra = conflicts.Count > 6
            ? $"{Environment.NewLine}- ...and {conflicts.Count - 6} more"
            : "";
        return $"{conflicts.Count} names already exist:{Environment.NewLine}{preview}{extra}";
    }
}
