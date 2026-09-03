using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleFile.Core;

public sealed class KeyboardShortcut
{
    public KeyboardShortcut(string id, string keys, string label)
        : this(id, KeyboardShortcutMap.SplitShortcutText(keys), label)
    {
    }

    public KeyboardShortcut(
        string id,
        IEnumerable<string> defaultShortcuts,
        string label,
        string group = "General",
        bool isEditable = true)
    {
        Id = id;
        Label = label;
        Group = string.IsNullOrWhiteSpace(group) ? "General" : group.Trim();
        IsEditable = isEditable;
        DefaultShortcuts = isEditable
            ? KeyboardShortcutMap.NormalizeShortcutList(defaultShortcuts)
            : defaultShortcuts
                .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut))
                .Select(shortcut => shortcut.Trim())
                .ToList();
    }

    public string Id { get; }
    public string Label { get; }
    public string Group { get; }
    public bool IsEditable { get; }
    public IReadOnlyList<string> DefaultShortcuts { get; }
    public string Keys => KeyboardShortcutMap.FormatShortcuts(DefaultShortcuts);
}

public sealed class KeyboardShortcutAssignment
{
    public KeyboardShortcutAssignment(
        KeyboardShortcut definition,
        IEnumerable<string>? shortcuts)
    {
        Definition = definition;
        Shortcuts = definition.IsEditable
            ? KeyboardShortcutMap.NormalizeShortcutList(shortcuts)
            : (shortcuts ?? [])
                .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut))
                .Select(shortcut => shortcut.Trim())
                .ToList();
    }

    public KeyboardShortcut Definition { get; }
    public string Id => Definition.Id;
    public string Label => Definition.Label;
    public string Group => Definition.Group;
    public bool IsEditable => Definition.IsEditable;
    public IReadOnlyList<string> DefaultShortcuts => Definition.DefaultShortcuts;
    public IReadOnlyList<string> Shortcuts { get; }
    public string Keys => KeyboardShortcutMap.FormatShortcuts(Shortcuts);
    public bool IsModified => !KeyboardShortcutMap.ShortcutListsEqual(Shortcuts, DefaultShortcuts);
}

public sealed class KeyboardShortcutGesture
{
    public string DisplayText { get; init; } = "";
    public string Key { get; init; } = "";
    public bool Control { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Windows { get; init; }
}

public enum KeyboardShortcutIssueSeverity
{
    Warning,
    Error,
}

public sealed class KeyboardShortcutIssue
{
    public KeyboardShortcutIssue(
        KeyboardShortcutIssueSeverity severity,
        string commandId,
        string shortcut,
        string message)
    {
        Severity = severity;
        CommandId = commandId;
        Shortcut = shortcut;
        Message = message;
    }

    public KeyboardShortcutIssueSeverity Severity { get; }
    public string CommandId { get; }
    public string Shortcut { get; }
    public string Message { get; }
}

public sealed class KeyboardShortcutExportDocument
{
    public const int CurrentVersion = 1;
    public const string CurrentFormat = "sumafile.shortcuts";

    public string Format { get; set; } = CurrentFormat;
    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, List<string>> Overrides { get; set; } = new(StringComparer.Ordinal);

    public static string ToJson(IDictionary<string, List<string>>? overrides)
    {
        var document = new KeyboardShortcutExportDocument
        {
            Overrides = KeyboardShortcutMap.NormalizeOverrides(overrides),
        };
        return JsonSerializer.Serialize(document, KeyboardShortcutMap.JsonOptions);
    }

    public static Dictionary<string, List<string>> FromJson(string? json)
    {
        return KeyboardShortcutMap.ReadOverridesJson(json);
    }
}

/// <summary>
/// Default shortcut map from docs/winui-migration/inventory.md section 5.3.
/// </summary>
public static class KeyboardShortcutMap
{
    public const string SettingsKey = "shortcutOverrides";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly IReadOnlyList<KeyboardShortcut> Defaults =
    [
        new("path.focus", ["Ctrl+L", "Alt+D"], "Focus path bar", "Navigation"),
        new("nav.parent", ["Alt+Up", "Backspace"], "Go up", "Navigation"),
        new("nav.back", ["Alt+Left"], "Back", "Navigation"),
        new("nav.forward", ["Alt+Right"], "Forward", "Navigation"),
        new("directory.refresh", ["F5"], "Refresh", "Navigation"),
        new("file.open", ["Enter"], "Open", "File"),
        new("file.rename", ["F2"], "Rename", "File"),
        new("file.delete.trash", ["Delete"], "Move to Recycle Bin", "File"),
        new("file.delete.permanent", ["Shift+Delete"], "Permanently delete", "File"),
        new("file.copy", ["Ctrl+C"], "Copy", "Clipboard"),
        new("file.cut", ["Ctrl+X"], "Cut", "Clipboard"),
        new("file.paste", ["Ctrl+V"], "Paste", "Clipboard"),
        new("file.copyPath", ["Ctrl+Shift+C"], "Copy path", "Clipboard"),
        new("file.properties", ["Alt+Enter"], "Properties", "Inspection"),
        new("file.openTab", ["Ctrl+Enter"], "Open folder in new tab", "Tabs"),
        new("selection.all", ["Ctrl+A"], "Select all", "Selection"),
        new("file.newFile", ["Ctrl+N"], "New text file", "File"),
        new("file.newFolder", ["Ctrl+Shift+N"], "New folder", "File"),
        new("tabs.new", ["Ctrl+T"], "New tab", "Tabs"),
        new("tabs.close", ["Ctrl+W"], "Close tab", "Tabs"),
        new("tabs.reopen", [], "Reopen closed tab", "Tabs"),
        new("tabs.next", ["Ctrl+Tab"], "Next tab", "Tabs"),
        new("tabs.previous", ["Ctrl+Shift+Tab"], "Previous tab", "Tabs"),
        new("tabs.jump", ["Ctrl+1-9"], "Switch to tab (9 is last)", "Tabs", isEditable: false),
        new("quickLook.toggle", ["Space"], "Quick Look", "Inspection"),
        new("preview.toggle", [], "Toggle preview pane", "View"),
        new("search.focus", ["Ctrl+F", "F3"], "Focus find in folder", "Search"),
        new("view.toggleHidden", ["Ctrl+H"], "Show or hide hidden files", "View"),
        new("view.iconSize", ["Ctrl+Mouse wheel"], "Change icon size", "View", isEditable: false),
        new("places.bookmark", ["Ctrl+B"], "Bookmark current folder", "Organization"),
        new("help.keyboard", ["F1", "Ctrl+Divide"], "Keyboard shortcuts", "App"),
        new("commandPalette.open", ["Ctrl+Shift+P"], "Command palette", "App"),
        new("history.undo", ["Ctrl+Z"], "Undo", "History"),
        new("history.redo", ["Ctrl+Y", "Ctrl+Shift+Z"], "Redo", "History"),
        new("terminal.open", ["F4"], "Open terminal", "Tools"),
        new("settings.open", ["Ctrl+Shift+S"], "Settings", "App"),
        new("pane.toggleDual", ["F6"], "Open or close second pane", "Panes"),
        new("pane.switch", ["Tab"], "Switch pane", "Panes"),
        new("pane.focusPrimary", ["Alt+1", "Ctrl+Shift+Left"], "Focus left pane", "Panes"),
        new("pane.focusSecondary", ["Alt+2", "Ctrl+Shift+Right"], "Focus right pane", "Panes"),
        new("pane.copyToOther", ["Ctrl+Alt+C"], "Copy to other pane", "Panes"),
        new("pane.moveToOther", ["Ctrl+Alt+M"], "Move to other pane", "Panes"),
        new("escape", ["Escape"], "Dismiss overlay / clear", "App", isEditable: false),
    ];

    public static KeyboardShortcut? Find(string id)
    {
        return Defaults.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
    }

    public static IReadOnlyList<KeyboardShortcut> ApplyOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return Defaults;
        }

        var normalized = NormalizeLegacyOverrides(overrides);
        return EffectiveShortcuts(normalized)
            .Select(item => new KeyboardShortcut(
                item.Id,
                item.Shortcuts,
                item.Label,
                item.Group,
                item.IsEditable))
            .ToList();
    }

    public static IReadOnlyList<KeyboardShortcutAssignment> EffectiveShortcuts(
        IDictionary<string, List<string>>? overrides)
    {
        var normalized = NormalizeOverrides(overrides);
        return Defaults
            .Select(definition => normalized.TryGetValue(definition.Id, out var custom)
                ? new KeyboardShortcutAssignment(definition, custom)
                : new KeyboardShortcutAssignment(definition, definition.DefaultShortcuts))
            .ToList();
    }

    public static Dictionary<string, List<string>> NormalizeOverrides(
        IDictionary<string, List<string>>? overrides)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (overrides is null)
        {
            return result;
        }

        foreach (var (id, shortcuts) in overrides)
        {
            var definition = Find(id);
            if (definition is null || !definition.IsEditable)
            {
                continue;
            }

            var normalized = NormalizeShortcutList(shortcuts);
            if (!ShortcutListsEqual(normalized, definition.DefaultShortcuts))
            {
                result[id] = normalized.ToList();
            }
        }

        return result;
    }

    public static Dictionary<string, List<string>> NormalizeLegacyOverrides(
        IReadOnlyDictionary<string, string>? overrides)
    {
        var intermediate = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (overrides is null)
        {
            return intermediate;
        }

        foreach (var (id, text) in overrides)
        {
            intermediate[id] = SplitShortcutText(text);
        }

        return NormalizeOverrides(intermediate);
    }

    public static IReadOnlyList<KeyboardShortcutIssue> ValidateOverrides(
        IDictionary<string, List<string>>? overrides)
    {
        return ValidateAssignments(EffectiveShortcuts(overrides));
    }

    public static IReadOnlyList<KeyboardShortcutIssue> ValidateAssignments(
        IEnumerable<KeyboardShortcutAssignment> assignments)
    {
        var issues = new List<KeyboardShortcutIssue>();
        var byShortcut = new Dictionary<string, List<KeyboardShortcutAssignment>>(StringComparer.Ordinal);

        foreach (var assignment in assignments.Where(item => item.IsEditable))
        {
            foreach (var shortcut in assignment.Shortcuts)
            {
                if (!TryParseShortcut(shortcut, out var gesture, out var error) || gesture is null)
                {
                    issues.Add(new KeyboardShortcutIssue(
                        KeyboardShortcutIssueSeverity.Error,
                        assignment.Id,
                        shortcut,
                        error ?? "Shortcut is not valid."));
                    continue;
                }

                if (TryGetReservedWindowsWarning(gesture.DisplayText, out var warning))
                {
                    issues.Add(new KeyboardShortcutIssue(
                        KeyboardShortcutIssueSeverity.Warning,
                        assignment.Id,
                        gesture.DisplayText,
                        warning));
                }

                if (!byShortcut.TryGetValue(gesture.DisplayText, out var owners))
                {
                    owners = [];
                    byShortcut[gesture.DisplayText] = owners;
                }

                if (!owners.Any(owner => string.Equals(owner.Id, assignment.Id, StringComparison.Ordinal)))
                {
                    owners.Add(assignment);
                }
            }
        }

        foreach (var (shortcut, owners) in byShortcut.Where(item => item.Value.Count > 1))
        {
            foreach (var owner in owners)
            {
                var others = owners
                    .Where(item => !string.Equals(item.Id, owner.Id, StringComparison.Ordinal))
                    .Select(item => item.Label)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                issues.Add(new KeyboardShortcutIssue(
                    KeyboardShortcutIssueSeverity.Error,
                    owner.Id,
                    shortcut,
                    $"{shortcut} also belongs to {string.Join(", ", others)}."));
            }
        }

        return issues;
    }

    public static Dictionary<string, List<string>> ReadOverridesJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, List<string>>(StringComparer.Ordinal);
            }

            if (root.TryGetProperty("overrides", out var overrides)
                && overrides.ValueKind == JsonValueKind.Object)
            {
                return ReadOverrideObject(overrides);
            }

            return ReadOverrideObject(root);
        }
        catch
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
    }

    public static string WriteOverridesJson(IDictionary<string, List<string>>? overrides)
    {
        return JsonSerializer.Serialize(NormalizeOverrides(overrides), JsonOptions);
    }

    public static string FormatShortcuts(IEnumerable<string>? shortcuts)
    {
        var list = shortcuts?
            .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut))
            .Select(shortcut => shortcut.Trim())
            .ToArray() ?? [];
        return list.Length == 0 ? "Unassigned" : string.Join(", ", list);
    }

    public static bool ShortcutListsEqual(
        IEnumerable<string>? left,
        IEnumerable<string>? right)
    {
        var leftList = (left ?? []).ToArray();
        var rightList = (right ?? []).ToArray();
        if (leftList.Length != rightList.Length)
        {
            return false;
        }

        return leftList.Zip(rightList, (a, b) => string.Equals(a, b, StringComparison.Ordinal)).All(item => item);
    }

    public static List<string> NormalizeShortcutList(IEnumerable<string>? shortcuts)
    {
        var result = new List<string>();
        foreach (var shortcut in shortcuts ?? [])
        {
            if (!TryParseShortcut(shortcut, out var gesture, out _)
                || gesture is null
                || result.Contains(gesture.DisplayText, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(gesture.DisplayText);
        }

        return result;
    }

    public static List<string> SplitShortcutText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var separator = text.Contains(';', StringComparison.Ordinal)
            ? ';'
            : text.Contains(" / ", StringComparison.Ordinal) ? '/' : '\0';
        var parts = separator == '\0'
            ? [text]
            : text.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .ToList();
    }

    public static bool TryParseShortcut(
        string? shortcut,
        out KeyboardShortcutGesture? gesture,
        out string? error)
    {
        gesture = null;
        error = null;
        var text = shortcut?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Shortcut is blank.";
            return false;
        }

        var control = false;
        var alt = false;
        var shift = false;
        var windows = false;
        string? key = null;
        foreach (var rawToken in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryReadModifier(rawToken, ref control, ref alt, ref shift, ref windows, out error))
            {
                continue;
            }

            if (error is not null)
            {
                return false;
            }

            var normalizedKey = NormalizeKey(rawToken);
            if (normalizedKey is null)
            {
                error = $"Unknown key \"{rawToken}\".";
                return false;
            }

            if (key is not null)
            {
                error = "Shortcut can only contain one non-modifier key.";
                return false;
            }

            key = normalizedKey;
        }

        if (key is null)
        {
            error = "Shortcut must include a key.";
            return false;
        }

        gesture = new KeyboardShortcutGesture
        {
            Control = control,
            Alt = alt,
            Shift = shift,
            Windows = windows,
            Key = key,
            DisplayText = FormatGesture(control, alt, shift, windows, key),
        };
        return true;
    }

    public static bool TryGetReservedWindowsWarning(string shortcut, out string warning)
    {
        warning = "";
        if (!TryParseShortcut(shortcut, out var gesture, out _) || gesture is null)
        {
            return false;
        }

        if (gesture.Windows)
        {
            warning = "Windows logo shortcuts are reserved by Windows or the shell.";
            return true;
        }

        if (gesture.Alt && !gesture.Control && !gesture.Shift && gesture.Key == "Tab")
        {
            warning = "Alt+Tab is reserved by Windows.";
            return true;
        }

        if (gesture.Alt && !gesture.Control && !gesture.Shift && gesture.Key == "F4")
        {
            warning = "Alt+F4 is reserved by Windows.";
            return true;
        }

        if (gesture.Control && gesture.Alt && !gesture.Shift && gesture.Key == "Delete")
        {
            warning = "Ctrl+Alt+Delete is reserved by Windows.";
            return true;
        }

        if (gesture.Control && gesture.Shift && !gesture.Alt && gesture.Key == "Escape")
        {
            warning = "Ctrl+Shift+Escape is reserved by Windows.";
            return true;
        }

        if (gesture.Control && !gesture.Alt && !gesture.Shift && gesture.Key == "Escape")
        {
            warning = "Ctrl+Escape is reserved by Windows.";
            return true;
        }

        return false;
    }

    private static Dictionary<string, List<string>> ReadOverrideObject(JsonElement element)
    {
        var raw = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                raw[property.Name] = SplitShortcutText(property.Value.GetString());
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                raw[property.Name] = property.Value
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "")
                    .ToList();
            }
        }

        return NormalizeOverrides(raw);
    }

    private static bool TryReadModifier(
        string token,
        ref bool control,
        ref bool alt,
        ref bool shift,
        ref bool windows,
        out string? error)
    {
        error = null;
        switch (token.Trim().ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                if (control)
                {
                    error = "Control was specified more than once.";
                    return false;
                }

                control = true;
                return true;
            case "alt":
            case "menu":
                if (alt)
                {
                    error = "Alt was specified more than once.";
                    return false;
                }

                alt = true;
                return true;
            case "shift":
                if (shift)
                {
                    error = "Shift was specified more than once.";
                    return false;
                }

                shift = true;
                return true;
            case "win":
            case "windows":
                if (windows)
                {
                    error = "Windows was specified more than once.";
                    return false;
                }

                windows = true;
                return true;
            default:
                return false;
        }
    }

    private static string? NormalizeKey(string token)
    {
        var key = token.Trim();
        if (key.Length == 0)
        {
            return null;
        }

        if (key.Length == 1)
        {
            var character = key[0];
            if (char.IsLetter(character))
            {
                return char.ToUpperInvariant(character).ToString();
            }

            if (char.IsDigit(character))
            {
                return character.ToString();
            }
        }

        var lowered = key.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (lowered.Length is >= 2 and <= 3
            && lowered[0] == 'f'
            && int.TryParse(lowered[1..], out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            return $"F{functionKey}";
        }

        return lowered switch
        {
            "esc" or "escape" => "Escape",
            "return" or "enter" => "Enter",
            "space" or "spacebar" => "Space",
            "back" or "backspace" => "Backspace",
            "del" or "delete" => "Delete",
            "ins" or "insert" => "Insert",
            "tab" => "Tab",
            "home" => "Home",
            "end" => "End",
            "pageup" or "pgup" => "PageUp",
            "pagedown" or "pgdn" => "PageDown",
            "left" or "leftarrow" => "Left",
            "right" or "rightarrow" => "Right",
            "up" or "uparrow" => "Up",
            "down" or "downarrow" => "Down",
            "plus" or "add" => "Plus",
            "minus" or "subtract" => "Minus",
            "comma" => "Comma",
            "period" or "dot" => "Period",
            "slash" => "Slash",
            "backslash" => "Backslash",
            "divide" => "Divide",
            "multiply" => "Multiply",
            "printscreen" => "PrintScreen",
            _ => null,
        };
    }

    private static string FormatGesture(
        bool control,
        bool alt,
        bool shift,
        bool windows,
        string key)
    {
        var parts = new List<string>();
        if (control)
        {
            parts.Add("Ctrl");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        if (windows)
        {
            parts.Add("Win");
        }

        parts.Add(key);
        return string.Join("+", parts);
    }
}
