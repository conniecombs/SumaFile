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
    public string ToolbarDisplayMode { get; set; } = ToolbarActionCatalog.IconAndLabelDisplayMode;
    public List<CommandSurfaceItem> PrimaryToolbar { get; set; } = DefaultPrimaryToolbarItems();

    private static readonly string[] LegacyDefaultPrimaryActionIds =
    [
        ToolbarOverflowPlanner.New,
        ToolbarOverflowPlanner.DualPane,
        ToolbarOverflowPlanner.Profiles,
        ToolbarOverflowPlanner.ViewOptions,
        ToolbarOverflowPlanner.Settings,
    ];

    public static IReadOnlyList<string> DefaultPrimaryActionIds { get; } =
    [
        ToolbarOverflowPlanner.New,
        ToolbarOverflowPlanner.DualPane,
        "copy-to-pane",
        "move-to-pane",
        ToolbarOverflowPlanner.ViewOptions,
        ToolbarOverflowPlanner.Settings,
    ];

    public static CommandSurfaceLayout CreateDefault() => new();

    /// <summary>Adds a command to the primary toolbar before Settings when it is not already there.</summary>
    public bool EnsurePrimaryItem(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)
            || PrimaryToolbar.Any(item => item.IsCommand && string.Equals(item.Id, commandId, StringComparison.Ordinal)))
        {
            return false;
        }

        var settingsIndex = PrimaryToolbar.FindIndex(item =>
            item.IsCommand && string.Equals(item.Id, ToolbarOverflowPlanner.Settings, StringComparison.Ordinal));
        var entry = CommandSurfaceItem.Command(commandId);
        if (settingsIndex >= 0)
        {
            PrimaryToolbar.Insert(settingsIndex, entry);
        }
        else
        {
            PrimaryToolbar.Add(entry);
        }

        return true;
    }

    public static List<CommandSurfaceItem> DefaultPrimaryToolbarItems() =>
    [
        CommandSurfaceItem.Command(ToolbarOverflowPlanner.New),
        CommandSurfaceItem.Command(ToolbarOverflowPlanner.DualPane),
        CommandSurfaceItem.Command("copy-to-pane"),
        CommandSurfaceItem.Command("move-to-pane"),
        CommandSurfaceItem.Separator(),
        CommandSurfaceItem.Command(ToolbarOverflowPlanner.ViewOptions),
        CommandSurfaceItem.Command(ToolbarOverflowPlanner.Settings),
    ];

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
            layout.UpgradeLegacyDefaultToolbar();
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

    private void UpgradeLegacyDefaultToolbar()
    {
        var previousDefault = "new,dual-pane,copy-to-pane,move-to-pane,operation-history,ftp-sftp-manager,|,duplicate-checker,disk-cleanup,|,profiles,view-options,settings";
        var storedOrder = string.Join(",", PrimaryToolbar.Select(item => item.IsSeparator ? "|" : item.Id));
        if (storedOrder == previousDefault)
        {
            PrimaryToolbar = DefaultPrimaryToolbarItems();
            return;
        }
        if (PrimaryToolbar.Any(item => item.IsSeparator))
        {
            return;
        }

        var actionIds = PrimaryToolbar
            .Where(item => item.IsCommand)
            .Select(item => item.Id)
            .ToList();
        if (actionIds.SequenceEqual(LegacyDefaultPrimaryActionIds, StringComparer.Ordinal))
        {
            PrimaryToolbar = DefaultPrimaryToolbarItems();
        }
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
        new(ToolbarOverflowPlanner.Settings, "Settings", "App", ContextMenuIconCatalog.Settings, ToolbarOverflowPlanner.Settings, "Ctrl+,", usesBuiltInControl: true),
    ];

    private static volatile CatalogSnapshot _snapshot = CatalogSnapshot.Create([]);

    public static IReadOnlyList<ToolbarAction> All => _snapshot.Actions;

    public static event EventHandler? Changed;

    /// <summary>Replaces the plugin-contributed toolbar actions; an empty list restores the built-in set.</summary>
    public static void SetPluginActions(IReadOnlyList<ToolbarAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _snapshot = CatalogSnapshot.Create(actions);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static ToolbarAction? Find(string id) =>
        _snapshot.ById.TryGetValue(id, out var action) ? action : null;

    private sealed class CatalogSnapshot
    {
        public required IReadOnlyList<ToolbarAction> Actions { get; init; }
        public required IReadOnlyDictionary<string, ToolbarAction> ById { get; init; }

        public static CatalogSnapshot Create(IReadOnlyList<ToolbarAction> pluginActions)
        {
            var actions = BuildAll();
            if (pluginActions.Count > 0)
            {
                var merged = new List<ToolbarAction>(actions);
                var seen = new HashSet<string>(merged.Select(action => action.Id), StringComparer.Ordinal);
                merged.AddRange(pluginActions.Where(action => seen.Add(action.Id)));
                actions = merged;
            }

            return new CatalogSnapshot
            {
                Actions = actions,
                ById = actions.ToDictionary(action => action.Id, StringComparer.Ordinal),
            };
        }
    }

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

        // Transfer labels include a destination direction, e.g. "Copy to right pane".
        var labelLength = id is "copy-to-pane" or "move-to-pane" ? 18 : action.Label.Length;
        return Math.Clamp(48 + (labelLength * 6), 74, 180);
    }

    private static IReadOnlyList<ToolbarAction> BuildAll()
    {
        var actions = new List<ToolbarAction>(BuiltInActions);
        var existing = new HashSet<string>(actions.Select(action => action.Id), StringComparer.Ordinal);
        foreach (var command in AppCommandCatalog.BuiltIn)
        {
            // "search" and "filter" focus the omnibar; they are not toolbar buttons.
            if (existing.Contains(command.Id)
                || string.Equals(command.Id, "search", StringComparison.Ordinal)
                || string.Equals(command.Id, "filter", StringComparison.Ordinal))
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
            "ftp-sftp-manager" => ContextMenuIconCatalog.Switch,
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
                or "icon-size-extra-large" or "icon-size-huge" => ContextMenuIconCatalog.ViewAll,
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
