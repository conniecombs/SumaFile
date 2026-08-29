namespace SimpleFile.Core;

public static class CommandAliasCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["overflow-search"] = "search",
        ["overflow-filter"] = "filter",
        ["overflow-new-folder"] = "new-folder",
        ["overflow-new-file"] = "new-file",
        ["overflow-dual-pane"] = "dual-pane",
        ["overflow-settings"] = "settings",
        ["ctx-open-tab"] = "open-selected-tab",
        ["ctx-open-other-pane"] = "open-other-pane",
        ["ctx-preview"] = "quick-look",
        ["ctx-terminal"] = "terminal",
        ["ctx-powershell-admin"] = "powershell-admin",
        ["ctx-color-label"] = "color-label",
        ["ctx-folder-metrics"] = "folder-metrics",
        ["ctx-cleanup"] = "disk-cleanup",
        ["ctx-duplicates"] = "duplicate-checker",
        ["ctx-rename"] = "rename",
        ["ctx-advanced-rename"] = "advanced-rename",
        ["ctx-copy"] = "copy",
        ["ctx-cut"] = "cut",
        ["ctx-paste"] = "paste",
        ["ctx-copy-path"] = "copy-path",
        ["ctx-bookmark"] = "bookmark-selected-folder",
        ["ctx-copy-to-pane"] = "copy-to-pane",
        ["ctx-move-to-pane"] = "move-to-pane",
        ["ctx-close-left-pane"] = "close-left-pane",
        ["ctx-close-dual-pane"] = "close-right-pane",
        ["ctx-compress"] = "create-archive",
        ["ctx-delete-recycle"] = "delete",
        ["ctx-delete-permanent"] = "delete-permanent",
        ["ctx-info"] = "properties",
    };

    public static string Normalize(string id)
    {
        return Aliases.TryGetValue(id, out var canonical) ? canonical : id;
    }
}
