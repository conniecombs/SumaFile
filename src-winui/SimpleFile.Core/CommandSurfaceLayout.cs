using System.Text.Json;

namespace SimpleFile.Core;

public sealed class CommandSurfaceItem
{
    public const string CommandKind = "command";
    public const string SeparatorKind = "separator";

    public string Kind { get; set; } = CommandKind;
    public string Id { get; set; } = "";

    public bool IsCommand => string.Equals(Kind, CommandKind, StringComparison.OrdinalIgnoreCase);
    public bool IsSeparator => string.Equals(Kind, SeparatorKind, StringComparison.OrdinalIgnoreCase);

    public static CommandSurfaceItem Command(string id) => new()
    {
        Kind = CommandKind,
        Id = id,
    };

    public static CommandSurfaceItem Separator() => new()
    {
        Kind = SeparatorKind,
    };
}

public sealed class CommandSurfaceLayout
{
    public const string SettingsKey = "commandSurface.layout";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public int Version { get; set; } = CurrentVersion;
    public string ToolbarDisplayMode { get; set; } = ToolbarActionCatalog.IconOnlyDisplayMode;
    public List<CommandSurfaceItem> PrimaryToolbar { get; set; } = DefaultPrimaryToolbarItems();

    public static IReadOnlyList<string> DefaultPrimaryActionIds { get; } =
    [
        ToolbarOverflowPlanner.New,
        ToolbarOverflowPlanner.DualPane,
        ToolbarOverflowPlanner.Profiles,
        ToolbarOverflowPlanner.ViewOptions,
        ToolbarOverflowPlanner.Settings,
    ];

    public static CommandSurfaceLayout CreateDefault() => new();

    public static List<CommandSurfaceItem> DefaultPrimaryToolbarItems() =>
        DefaultPrimaryActionIds
            .Select(CommandSurfaceItem.Command)
            .ToList();

    public static CommandSurfaceLayout FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefault();
        }

        try
        {
            var layout = JsonSerializer.Deserialize<CommandSurfaceLayout>(json, SerializerOptions) ?? CreateDefault();
            layout.Normalize();
            return layout;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    public void Normalize()
    {
        Version = CurrentVersion;
        ToolbarDisplayMode = ToolbarActionCatalog.NormalizeDisplayMode(ToolbarDisplayMode);

        var source = PrimaryToolbar.Count == 0
            ? DefaultPrimaryToolbarItems()
            : PrimaryToolbar;
        var normalized = new List<CommandSurfaceItem>();
        var seenCommands = new HashSet<string>(StringComparer.Ordinal);
        var previousWasSeparator = true;

        foreach (var item in source)
        {
            if (item.IsSeparator)
            {
                if (!previousWasSeparator && normalized.Count > 0)
                {
                    normalized.Add(CommandSurfaceItem.Separator());
                    previousWasSeparator = true;
                }

                continue;
            }

            var id = (item.Id ?? "").Trim();
            if (!item.IsCommand
                || id.Length == 0
                || !ToolbarActionCatalog.IsActionId(id)
                || !seenCommands.Add(id))
            {
                continue;
            }

            normalized.Add(CommandSurfaceItem.Command(id));
            previousWasSeparator = false;
        }

        while (normalized.Count > 0 && normalized[^1].IsSeparator)
        {
            normalized.RemoveAt(normalized.Count - 1);
        }

        PrimaryToolbar = normalized.Count == 0
            ? DefaultPrimaryToolbarItems()
            : normalized;
    }

    public IReadOnlyList<string> VisiblePrimaryActionIds()
    {
        Normalize();
        return PrimaryToolbar
            .Where(item => item.IsCommand)
            .Select(item => item.Id)
            .ToList();
    }

    public string Signature()
    {
        Normalize();
        return ToolbarDisplayMode + "|" + string.Join(
            ",",
            PrimaryToolbar.Select(item => item.IsSeparator ? "|" : item.Id));
    }
}

public sealed class ToolbarAction
{
    public ToolbarAction(
        string id,
        string label,
        string group,
        string iconGlyph,
        string? commandId = null,
        string? shortcut = null,
        bool usesBuiltInControl = false)
    {
        Id = id;
        Label = label;
        Group = group;
        IconGlyph = iconGlyph;
        CommandId = commandId;
        Shortcut = shortcut;
        UsesBuiltInControl = usesBuiltInControl;
    }

    public string Id { get; }
    public string Label { get; }
    public string Group { get; }
    public string IconGlyph { get; }
    public string? CommandId { get; }
    public string? Shortcut { get; }
    public bool UsesBuiltInControl { get; }
}

public static class ToolbarActionCatalog
{
    public const string IconOnlyDisplayMode = "icons";
    public const string IconAndLabelDisplayMode = "labels";

    private static readonly IReadOnlyList<ToolbarAction> BuiltInActions =
    [
        new(ToolbarOverflowPlanner.New, "New", "File", ContextMenuIconCatalog.NewFolder, usesBuiltInControl: true),
        new(ToolbarOverflowPlanner.DualPane, "Second pane", "View", ContextMenuIconCatalog.OpenPane, ToolbarOverflowPlanner.DualPane, "F6", usesBuiltInControl: true),
        new(ToolbarOverflowPlanner.Profiles, "Profiles", "Profiles", ContextMenuIconCatalog.Switch, usesBuiltInControl: true),
        new(ToolbarOverflowPlanner.ViewOptions, "View", "View", ContextMenuIconCatalog.ViewAll, usesBuiltInControl: true),
        new(ToolbarOverflowPlanner.Settings, "Settings", "App", ContextMenuIconCatalog.Settings, ToolbarOverflowPlanner.Settings, "Ctrl+Shift+S", usesBuiltInControl: true),
    ];

    public static IReadOnlyList<ToolbarAction> All { get; } = BuildAll();
    private static IReadOnlyDictionary<string, ToolbarAction> ById { get; } =
        All.ToDictionary(action => action.Id, StringComparer.Ordinal);

    public static ToolbarAction? Find(string id) =>
        ById.TryGetValue(id, out var action) ? action : null;

    public static bool IsActionId(string id) => Find(id) is not null;

    public static string NormalizeDisplayMode(string? displayMode)
    {
        return string.Equals(displayMode, IconAndLabelDisplayMode, StringComparison.OrdinalIgnoreCase)
            ? IconAndLabelDisplayMode
            : IconOnlyDisplayMode;
    }

    public static double WidthFor(string id, string displayMode)
    {
        if (Find(id) is not { } action)
        {
            return 0;
        }

        if (NormalizeDisplayMode(displayMode) == IconOnlyDisplayMode)
        {
            return 32;
        }

        return Math.Clamp(48 + (action.Label.Length * 6), 74, 164);
    }

    private static IReadOnlyList<ToolbarAction> BuildAll()
    {
        var actions = new List<ToolbarAction>(BuiltInActions);
        var existing = new HashSet<string>(actions.Select(action => action.Id), StringComparer.Ordinal);
        foreach (var command in AppCommandCatalog.All)
        {
            if (existing.Contains(command.Id)
                || string.Equals(command.Id, ToolbarOverflowPlanner.Search, StringComparison.Ordinal)
                || string.Equals(command.Id, ToolbarOverflowPlanner.Filter, StringComparison.Ordinal))
            {
                continue;
            }

            actions.Add(new ToolbarAction(
                command.Id,
                command.Label,
                command.Group,
                GlyphForCommand(command.Id),
                command.Id,
                command.Shortcut));
            existing.Add(command.Id);
        }

        return actions;
    }

    private static string GlyphForCommand(string commandId)
    {
        return commandId switch
        {
            "go-home" => ContextMenuIconCatalog.Folder,
            "go-recycle-bin" => ContextMenuIconCatalog.Delete,
            "restore-selected" => ContextMenuIconCatalog.Import,
            "empty-recycle-bin" => ContextMenuIconCatalog.EraseTool,
            "go-back" => "\uE72B",
            "go-forward" => "\uE72A",
            "go-up" => "\uE74A",
            "focus-path" => ContextMenuIconCatalog.Edit,
            "refresh" => "\uE72C",
            "copy" or "copy-path" or "copy-to-pane" => ContextMenuIconCatalog.Copy,
            "cut" => ContextMenuIconCatalog.Cut,
            "paste" => ContextMenuIconCatalog.Paste,
            "clipboard-history" or "operation-history" or "transfers" => ContextMenuIconCatalog.BulletedList,
            "clear-recent-history" => ContextMenuIconCatalog.EraseTool,
            "undo" => "\uE7A7",
            "redo" => "\uE7A6",
            "delete" or "delete-permanent" => ContextMenuIconCatalog.Delete,
            "rename" => ContextMenuIconCatalog.Rename,
            "advanced-rename" => ContextMenuIconCatalog.Edit,
            "new-folder" => ContextMenuIconCatalog.NewFolder,
            "new-file" or "new-shortcut" => ContextMenuIconCatalog.Document,
            "create-archive" => ContextMenuIconCatalog.Package,
            "terminal" => ContextMenuIconCatalog.CommandPrompt,
            "powershell-admin" => ContextMenuIconCatalog.Admin,
            "preview" or "quick-look" => ContextMenuIconCatalog.Preview,
            "toggle-hidden" => ContextMenuIconCatalog.ViewAll,
            "toggle-side-menu" => ContextMenuIconCatalog.BulletedList,
            "dual-pane" or "open-other-pane" => ContextMenuIconCatalog.OpenPane,
            "switch-pane" => ContextMenuIconCatalog.Switch,
            "close-left-pane" or "close-right-pane" => ContextMenuIconCatalog.ClosePane,
            "move-to-pane" => ContextMenuIconCatalog.MoveToFolder,
            "open-selected-tab" or "reopen-closed-tab" => ContextMenuIconCatalog.NewTab,
            "view-details" => ContextMenuIconCatalog.BulletedList,
            "view-list" => ContextMenuIconCatalog.List,
            "view-tiles" => ContextMenuIconCatalog.Tiles,
            "view-content" => ContextMenuIconCatalog.Document,
            "customize-toolbar" or "toggle-toolbar-labels" => ContextMenuIconCatalog.Settings,
            "icon-size-small" or "icon-size-medium" or "icon-size-large"
                or "icon-size-extra-large" or "icon-size-jumbo" or "icon-size-huge"
                or "icon-size-maximum" => ContextMenuIconCatalog.ViewAll,
            "properties" => ContextMenuIconCatalog.Info,
            "color-label" => ContextMenuIconCatalog.Label,
            "bookmark-folder" => ContextMenuIconCatalog.Favorite,
            "folder-metrics" => ContextMenuIconCatalog.AreaChart,
            "disk-cleanup" => ContextMenuIconCatalog.EraseTool,
            "duplicate-checker" => ContextMenuIconCatalog.SelectAll,
            "command-palette" => ContextMenuIconCatalog.Search,
            "keyboard-help" => ContextMenuIconCatalog.Info,
            "settings" => ContextMenuIconCatalog.Settings,
            "profile-manage" or "profile-save" or "profile-standard"
                or "profile-developer" or "profile-photos" or "profile-transfer"
                or "profile-minimal" => ContextMenuIconCatalog.Switch,
            "git-panel" => ContextMenuIconCatalog.Branch,
            "git-refresh" => "\uE72C",
            "git-fetch" or "git-pull" => ContextMenuIconCatalog.Import,
            "git-push" or "git-commit" => ContextMenuIconCatalog.Save,
            "git-stage-selected" => "\uE73E",
            "git-unstage-selected" => "\uE738",
            "git-discard-selected" => ContextMenuIconCatalog.EraseTool,
            "git-diff-selected" => ContextMenuIconCatalog.Switch,
            _ => ContextMenuIconCatalog.Document,
        };
    }
}
