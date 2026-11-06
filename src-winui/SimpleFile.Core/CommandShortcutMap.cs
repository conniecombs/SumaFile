namespace SimpleFile.Core;

/// <summary>
/// Links palette/toolbar/context-menu command ids to the keyboard shortcut ids that trigger
/// them, so every displayed chord can be resolved from the user's effective shortcut map
/// instead of a hard-coded string.
/// </summary>
public static class CommandShortcutMap
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["go-home"] = "nav.home",
        ["go-back"] = "nav.back",
        ["go-forward"] = "nav.forward",
        ["go-up"] = "nav.parent",
        ["focus-path"] = "path.focus",
        ["refresh"] = "directory.refresh",
        ["copy"] = "file.copy",
        ["cut"] = "file.cut",
        ["paste"] = "file.paste",
        ["copy-path"] = "file.copyPath",
        ["clipboard-history"] = "clipboard.history",
        ["undo"] = "history.undo",
        ["redo"] = "history.redo",
        ["delete"] = "file.delete.trash",
        ["delete-permanent"] = "file.delete.permanent",
        ["rename"] = "file.rename",
        ["new-folder"] = "file.newFolder",
        ["new-file"] = "file.newFile",
        ["terminal"] = "terminal.open",
        ["preview"] = "preview.toggle",
        ["toggle-hidden"] = "view.toggleHidden",
        ["dual-pane"] = "pane.toggleDual",
        ["switch-pane"] = "pane.switch",
        ["copy-to-pane"] = "pane.copyToOther",
        ["move-to-pane"] = "pane.moveToOther",
        ["search"] = "search.focus",
        ["quick-look"] = "quickLook.toggle",
        ["open-selected-tab"] = "file.openTab",
        ["reopen-closed-tab"] = "tabs.reopen",
        ["properties"] = "file.properties",
        ["bookmark-folder"] = "places.bookmark",
        ["settings"] = "settings.open",
        ["command-palette"] = "commandPalette.open",
        ["keyboard-help"] = "help.keyboard",
    };

    public static string? ShortcutIdFor(string commandId) =>
        Map.TryGetValue(commandId, out var shortcutId) ? shortcutId : null;
}
