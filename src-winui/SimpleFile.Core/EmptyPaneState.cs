namespace SimpleFile.Core;

public sealed class EmptyPaneState
{
    public bool Visible { get; init; }
    public string Title { get; init; } = "";
    public string Hint { get; init; } = "";

    public static EmptyPaneState Hidden { get; } = new();

    public static EmptyPaneState Resolve(
        int visibleCount,
        int rawCount,
        bool listingInProgress,
        bool searching,
        string? errorMessage,
        string? filterQuery,
        bool showHidden,
        string? path)
    {
        if (visibleCount > 0)
        {
            return Hidden;
        }

        if (listingInProgress)
        {
            return VisibleState("Loading…", "Reading this folder");
        }

        if (searching)
        {
            return VisibleState("No search results", "Try a different query or path");
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return VisibleState("Can't open this folder", errorMessage.Trim());
        }

        if (!string.IsNullOrWhiteSpace(filterQuery))
        {
            return VisibleState("No items match this filter", "Clear the filter or try another name");
        }

        if (string.IsNullOrEmpty(path))
        {
            return VisibleState("Select a folder", "Choose a location from the side menu or path bar");
        }

        if (rawCount > 0 && !showHidden)
        {
            return VisibleState("Hidden items are hidden", "Press Ctrl+H to show hidden files");
        }

        return VisibleState(
            "This folder is empty",
            "Drop files here, or press Ctrl+Shift+N to create a folder");
    }

    private static EmptyPaneState VisibleState(string title, string hint)
    {
        return new EmptyPaneState
        {
            Visible = true,
            Title = title,
            Hint = hint,
        };
    }
}
