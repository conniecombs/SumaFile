namespace SimpleFile.Core;

public sealed class KeyboardShortcut
{
    public KeyboardShortcut(string id, string keys, string label)
    {
        Id = id;
        Keys = keys;
        Label = label;
    }

    public string Id { get; }
    public string Keys { get; }
    public string Label { get; }
}

/// <summary>
/// Default shortcut map from docs/winui-migration/inventory.md §5.3.
/// </summary>
public static class KeyboardShortcutMap
{
    public static readonly IReadOnlyList<KeyboardShortcut> Defaults =
    [
        new("path.focus", "Ctrl+L", "Focus path bar"),
        new("path.focus.alt", "Alt+D", "Focus path bar"),
        new("nav.parent", "Alt+Up", "Go up"),
        new("nav.parent.backspace", "Backspace", "Go up"),
        new("nav.back", "Alt+Left", "Back"),
        new("nav.forward", "Alt+Right", "Forward"),
        new("directory.refresh", "F5", "Refresh"),
        new("file.open", "Enter", "Open"),
        new("file.rename", "F2", "Rename"),
        new("file.delete.trash", "Delete", "Move to Recycle Bin"),
        new("file.delete.permanent", "Shift+Delete", "Permanently delete"),
        new("file.copy", "Ctrl+C", "Copy"),
        new("file.cut", "Ctrl+X", "Cut"),
        new("file.paste", "Ctrl+V", "Paste"),
        new("file.copyPath", "Ctrl+Shift+C", "Copy path"),
        new("file.properties", "Alt+Enter", "Properties"),
        new("file.openTab", "Ctrl+Enter", "Open folder in new tab"),
        new("selection.all", "Ctrl+A", "Select all"),
        new("file.newFile", "Ctrl+N", "New file"),
        new("file.newFolder", "Ctrl+Shift+N", "New folder"),
        new("tabs.new", "Ctrl+T", "New tab"),
        new("tabs.close", "Ctrl+W", "Close tab"),
        new("tabs.next", "Ctrl+Tab", "Next tab"),
        new("tabs.previous", "Ctrl+Shift+Tab", "Previous tab"),
        new("quickLook.toggle", "Space", "Quick Look"),
        new("search.focus", "Ctrl+F", "Focus search"),
        new("search.focus.f3", "F3", "Focus search"),
        new("view.toggleHidden", "Ctrl+H", "Show or hide hidden files"),
        new("view.iconSize", "Ctrl+Mouse wheel", "Change icon size"),
        new("places.bookmark", "Ctrl+B", "Bookmark current folder"),
        new("tabs.jump", "Ctrl+1–9", "Switch to tab (9 is last)"),
        new("help.keyboard", "F1", "Keyboard shortcuts"),
        new("commandPalette.open", "Ctrl+Shift+P", "Command palette"),
        new("history.undo", "Ctrl+Z", "Undo"),
        new("history.redo", "Ctrl+Y", "Redo"),
        new("terminal.open", "F4", "Open terminal"),
        new("pane.toggleDual", "F6", "Open or close second pane"),
        new("pane.switch", "Tab", "Switch pane"),
        new("pane.focusPrimary", "Alt+1", "Focus left pane"),
        new("pane.focusSecondary", "Alt+2", "Focus right pane"),
        new("pane.copyToOther", "Ctrl+Alt+C", "Copy to other pane"),
        new("pane.moveToOther", "Ctrl+Alt+M", "Move to other pane"),
        new("escape", "Escape", "Dismiss overlay / clear"),
    ];

    public static IReadOnlyList<KeyboardShortcut> ApplyOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return Defaults;
        }

        return Defaults
            .Select(item => overrides.TryGetValue(item.Id, out var keys) && !string.IsNullOrWhiteSpace(keys)
                ? new KeyboardShortcut(item.Id, keys, item.Label)
                : item)
            .ToList();
    }
}
