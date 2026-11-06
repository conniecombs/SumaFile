namespace SimpleFile.Core;

public sealed class AppCommand
{
    public AppCommand(string id, string label, string group, string? shortcut = null)
    {
        Id = id;
        Label = label;
        Group = group;
        Shortcut = shortcut;
    }

    public string Id { get; }
    public string Label { get; }
    public string Group { get; }
    public string? Shortcut { get; }
}

/// <summary>
/// Command-palette catalog for the WinUI shell.
/// </summary>
public static class AppCommandCatalog
{
    public static readonly IReadOnlyList<AppCommand> All =
    [
        new("go-home", "Go home", "Navigation", "Alt+Home"),
        new("go-recycle-bin", "Go to Recycle Bin", "Navigation"),
        new("restore-selected", "Restore from Recycle Bin", "File"),
        new("empty-recycle-bin", "Empty Recycle Bin", "File"),
        new("go-back", "Go back", "Navigation", "Alt+Left"),
        new("go-forward", "Go forward", "Navigation", "Alt+Right"),
        new("go-up", "Go up", "Navigation", "Alt+Up"),
        new("focus-path", "Focus path bar", "Navigation", "Ctrl+L / Alt+D"),
        new("refresh", "Refresh", "Navigation", "F5"),
        new("copy", "Copy", "Clipboard", "Ctrl+C"),
        new("cut", "Cut", "Clipboard", "Ctrl+X"),
        new("paste", "Paste", "Clipboard", "Ctrl+V"),
        new("copy-path", "Copy path", "Clipboard", "Ctrl+Shift+C"),
        new("clipboard-history", "Show clipboard history", "Clipboard", "Ctrl+Shift+V"),
        new("operation-history", "Show operation history", "History"),
        new("transfers", "Show transfers", "History"),
        new("clear-recent-history", "Clear recent history", "History"),
        new("undo", "Undo", "History", "Ctrl+Z"),
        new("redo", "Redo", "History", "Ctrl+Y"),
        new("delete", "Delete", "File", "Delete"),
        new("delete-permanent", "Delete permanently", "File", "Shift+Delete"),
        new("rename", "Rename", "File", "F2"),
        new("advanced-rename", "Advanced rename", "File"),
        new("new-folder", "New folder", "File", "Ctrl+Shift+N"),
        new("new-file", "New text document", "File"),
        new("new-shortcut", "New shortcut", "File"),
        new("shortcut-for-selected", "Create shortcut to selection", "File"),
        new("create-archive", "Create archive", "Archive"),
        new("terminal", "Open terminal", "Tools", "Ctrl+`"),
        new("ftp-sftp-manager", "FTP/SFTP manager", "Tools"),
        new("preview", "Show or hide preview pane", "View"),
        new("toggle-hidden", "Show or hide hidden files", "View", "Ctrl+H"),
        new("toggle-side-menu", "Show or hide sidebar", "View"),
        new("dual-pane", "Open or close second pane", "View", "F6"),
        new("switch-pane", "Switch pane", "View", "Tab"),
        new("close-left-pane", "Close left pane", "View"),
        new("copy-to-pane", "Copy to other pane", "View", "Ctrl+Alt+C"),
        new("move-to-pane", "Move to other pane", "View", "Ctrl+Alt+M"),
        new("profile-manage", "Manage workspace profiles", "Profiles"),
        new("profile-save", "Save current workspace profile", "Profiles"),
        new("profile-standard", "Profile: Standard", "Profiles"),
        new("profile-developer", "Profile: Developer", "Profiles"),
        new("profile-photos", "Profile: Photos", "Profiles"),
        new("profile-transfer", "Profile: Transfer", "Profiles"),
        new("profile-minimal", "Profile: Minimal", "Profiles"),
        new("customize-toolbar", "Customize toolbar", "App"),
        new("toggle-toolbar-labels", "Show or hide toolbar labels", "App"),
        new("view-details", "View: details", "View"),
        new("view-list", "View: list", "View"),
        new("view-tiles", "View: tiles", "View"),
        new("view-content", "View: content", "View"),
        new("icon-size-small", "Icon size: small", "View"),
        new("icon-size-medium", "Icon size: medium", "View"),
        new("icon-size-large", "Icon size: large", "View"),
        new("icon-size-extra-large", "Icon size: extra large", "View"),
        new("icon-size-huge", "Icon size: huge", "View"),
        new("search", "Search this folder", "Search", "Ctrl+F / F3"),
        new("filter", "Filter this folder", "Search"),
        new("quick-look", "Quick Look", "Inspection", "Space"),
        new("open-selected-tab", "Open selection in new tab", "Tabs", "Ctrl+Enter"),
        new("open-other-pane", "Open selection in other pane", "Panes"),
        new("reopen-closed-tab", "Reopen closed tab", "Tabs"),
        new("properties", "Show properties", "Inspection", "Alt+Enter"),
        new("folder-properties", "Current folder properties", "Inspection"),
        new("color-label", "Set label", "Organization"),
        new("clear-tag-filter", "Clear label filter", "Organization"),
        new("save-smart-folder", "Save search as smart folder…", "Organization"),
        new("bookmark-folder", "Bookmark current folder", "Organization", "Ctrl+B"),
        new("folder-metrics", "Compare folder metrics", "Tools"),
        new("disk-cleanup", "Large files and duplicates", "Tools"),
        new("duplicate-checker", "Duplicate checker", "Tools"),
        new("settings", "Open settings", "App", "Ctrl+,"),
        new("command-palette", "Open command palette", "App", "Ctrl+Shift+P"),
        new("keyboard-help", "Show keyboard shortcuts", "App", "F1"),
        new("git-panel", "Git: show workbench", "Git"),
        new("git-refresh", "Git: refresh status", "Git"),
        new("git-fetch", "Git: fetch", "Git"),
        new("git-pull", "Git: pull", "Git"),
        new("git-push", "Git: push", "Git"),
        new("git-commit", "Git: commit staged changes", "Git"),
        new("git-stage-selected", "Git: stage selected paths", "Git"),
        new("git-unstage-selected", "Git: unstage selected paths", "Git"),
        new("git-discard-selected", "Git: discard selected changes", "Git"),
        new("git-diff-selected", "Git: show selected diff", "Git"),
    ];

    /// <summary>
    /// Returns the commands with their displayed shortcut taken from the effective keyboard map
    /// (defaults plus the user's overrides). Commands with no binding show none.
    /// </summary>
    public static IReadOnlyList<AppCommand> WithEffectiveShortcuts(
        IEnumerable<AppCommand> commands,
        IDictionary<string, List<string>>? overrides)
    {
        var effective = KeyboardShortcutMap.EffectiveShortcuts(overrides)
            .ToDictionary(
                assignment => assignment.Id,
                assignment => assignment.Shortcuts.Count > 0 ? assignment.Keys : "",
                StringComparer.Ordinal);
        return commands
            .Select(command =>
            {
                var shortcutId = CommandShortcutMap.ShortcutIdFor(command.Id);
                if (shortcutId is null)
                {
                    return command;
                }

                var keys = effective.TryGetValue(shortcutId, out var value) ? value : "";
                return new AppCommand(command.Id, command.Label, command.Group, keys);
            })
            .ToList();
    }

    public static IReadOnlyList<AppCommand> Filter(string? query)
    {
        var needle = (query ?? "").Trim();
        if (needle.Length == 0)
        {
            return All;
        }

        return All
            .Where(command =>
                command.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || command.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || command.Group.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static AppCommand? Find(string id)
    {
        return All.FirstOrDefault(command => string.Equals(command.Id, id, StringComparison.Ordinal));
    }
}
