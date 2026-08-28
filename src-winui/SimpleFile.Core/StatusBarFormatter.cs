using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class StatusBarSnapshot
{
    public string ItemText { get; init; } = "";
    public string? SelectionText { get; init; }
    public string PathText { get; init; } = "";
    public string? PaneLabel { get; init; }
    public string Combined { get; init; } = "";
}

/// <summary>
/// Status-bar copy for the WinUI shell.
/// </summary>
public static class StatusBarFormatter
{
    public static StatusBarSnapshot Format(
        int totalItems,
        IReadOnlyList<FileEntry> selected,
        string? currentPath,
        string? paneLabel,
        bool listingInProgress = false,
        bool isEmpty = false)
    {
        var itemText = listingInProgress && totalItems == 0
            ? "Loading…"
            : isEmpty && totalItems == 0
                ? "Empty folder"
                : totalItems == 1 ? "1 item" : $"{totalItems} items";

        string? selectionText = null;
        if (selected.Count > 0)
        {
            var size = selected.Where(entry => !entry.IsDir).Aggregate(0UL, (sum, entry) => sum + entry.Size);
            var sizeText = selected.Any(entry => !entry.IsDir)
                ? EntryPresentation.FormatFileSize(size)
                : null;
            selectionText = selected.Count == 1
                ? (sizeText is null ? "1 selected" : $"1 selected ({sizeText})")
                : (sizeText is null ? $"{selected.Count} selected" : $"{selected.Count} selected ({sizeText})");
        }

        var pathText = currentPath ?? "";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(paneLabel))
        {
            parts.Add(paneLabel);
        }

        parts.Add(itemText);
        if (selectionText is not null)
        {
            parts.Add(selectionText);
        }

        return new StatusBarSnapshot
        {
            ItemText = itemText,
            SelectionText = selectionText,
            PathText = pathText,
            PaneLabel = paneLabel,
            Combined = string.Join(" · ", parts),
        };
    }
}
