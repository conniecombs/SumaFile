using SimpleFile.Ipc;

namespace SimpleFile.Core;

internal static class WorkspaceNavigation
{
    public static string ResolveStartPath(UiSettings settings, string homePath, string primaryPath)
    {
        var mode = UiSettings.NormalizeStartLocation(settings.StartLocation);
        if (mode == "custom" && !string.IsNullOrWhiteSpace(settings.CustomPath))
        {
            return settings.CustomPath.Trim();
        }

        if (mode == "last" && !string.IsNullOrWhiteSpace(settings.LastPath))
        {
            return settings.LastPath.Trim();
        }

        return string.IsNullOrEmpty(homePath) ? primaryPath : homePath;
    }

    public static bool CanUsePresortedEntries(ExplorerPane pane, bool keepFoldersOnTop)
    {
        return pane.ListingInProgress
            && !pane.PathIsNetwork
            && keepFoldersOnTop
            && string.Equals(pane.SortBy, "name", StringComparison.OrdinalIgnoreCase)
            && pane.SortAscending;
    }

    public static ListDirectoryOptions? BuildStreamedListingOptions(ExplorerPane pane)
    {
        if (pane.PathIsNetwork)
        {
            return null;
        }

        return new ListDirectoryOptions
        {
            Mode = "light",
            FinalEntries = false,
            SortBy = pane.SortBy,
            SortAscending = pane.SortAscending,
            IncludeHidden = true,
        };
    }

    public static void ApplyListingChunk(
        ExplorerPane pane,
        DirectoryListingChunk chunk,
        List<FileEntry> progressiveEntries)
    {
        if (chunk.IsNetwork)
        {
            pane.PathIsNetwork = true;
        }

        if (!string.IsNullOrEmpty(chunk.Path))
        {
            pane.Path = chunk.Path;
        }

        progressiveEntries.AddRange(chunk.Entries);
        pane.Entries = [.. progressiveEntries];
        if (progressiveEntries.Count > 0)
        {
            pane.IsNavigating = false;
        }
    }
}
