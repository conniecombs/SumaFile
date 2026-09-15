namespace SimpleFile.Core;

public static class CommandAliasCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["overflow-search"] = "search",
        ["overflow-filter"] = "filter",
        ["overflow-dual-pane"] = "dual-pane",
        ["overflow-profiles"] = "profile-manage",
        ["overflow-settings"] = "settings",
        ["ctx-customize-toolbar"] = "customize-toolbar",
        ["ctx-toggle-toolbar-labels"] = "toggle-toolbar-labels",
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
        ["ctx-restore"] = "restore-selected",
        ["ctx-empty-recycle-bin"] = "empty-recycle-bin",
        ["ctx-info"] = "properties",
        ["ctx-git-panel"] = "git-panel",
        ["ctx-git-diff"] = "git-diff-selected",
        ["ctx-git-stage"] = "git-stage-selected",
        ["ctx-git-unstage"] = "git-unstage-selected",
        ["ctx-git-discard"] = "git-discard-selected",
        ["ctx-git-refresh"] = "git-refresh",
        ["ctx-git-fetch"] = "git-fetch",
        ["ctx-git-pull"] = "git-pull",
        ["ctx-git-push"] = "git-push",
        ["ctx-git-commit"] = "git-commit",
    };

    public static string Normalize(string id)
    {
        if (Aliases.TryGetValue(id, out var canonical))
        {
            return canonical;
        }

        const string overflowPrefix = "overflow-";
        if (id.StartsWith(overflowPrefix, StringComparison.Ordinal))
        {
            var commandId = id[overflowPrefix.Length..];
            if (AppCommandCatalog.Find(commandId) is not null)
            {
                return commandId;
            }
        }

        return id;
    }
}
