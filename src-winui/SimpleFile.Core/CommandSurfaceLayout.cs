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

    /// <summary>
    /// The plugin toolbar items <see cref="EnsurePrimaryItem"/> has offered, saved with the layout. An item is offered
    /// once: after the user removes it, it is never added again. <see cref="Normalize"/> deduplicates the list and
    /// never filters it by the catalog, so an offer outlives a disabled, failed or missing plugin.
    /// </summary>
    public List<string> OfferedPrimaryItems { get; set; } = [];

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

    /// <summary>
    /// Offers a plugin's primary toolbar item once. False, and nothing changes, when the item was offered before, so
    /// the user's removal of an offered item is final, and an item that is merely hidden (its plugin is absent) comes
    /// back by itself when the plugin does. Otherwise it adds the item before Settings when it is not there yet,
    /// records it in <see cref="OfferedPrimaryItems"/> and returns true, so the caller saves the layout.
    /// </summary>
    public bool EnsurePrimaryItem(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)
            || OfferedPrimaryItems.Contains(commandId, StringComparer.Ordinal))
        {
            return false;
        }

        OfferedPrimaryItems.Add(commandId);
        if (PrimaryToolbar.Any(item => item.IsCommand && string.Equals(item.Id, commandId, StringComparison.Ordinal)))
        {
            return true;
        }

        InsertBeforeSettings(commandId);
        return true;
    }

    /// <summary>
    /// Carries into this layout, which Settings edited from <paramref name="baseline"/>, the offers the
    /// <paramref name="live"/> layout holds. An offer made while Settings was open (not in the baseline) also carries its
    /// item: when the live toolbar has it and neither the baseline nor this layout does, it goes before Settings, as
    /// <see cref="EnsurePrimaryItem"/> put it. Otherwise the edited rows win, so only the user's removal is final.
    /// </summary>
    public void MergeOffersFrom(CommandSurfaceLayout live, CommandSurfaceLayout baseline)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(baseline);
        var baselineOffers = new HashSet<string>(baseline.OfferedPrimaryItems ?? [], StringComparer.Ordinal);
        var baselineItems = CommandIds(baseline);
        var liveItems = CommandIds(live);
        var editedItems = CommandIds(this);
        foreach (var id in live.OfferedPrimaryItems ?? [])
        {
            if (string.IsNullOrWhiteSpace(id) || baselineOffers.Contains(id))
            {
                continue;
            }

            if (liveItems.Contains(id) && !baselineItems.Contains(id) && editedItems.Add(id))
            {
                InsertBeforeSettings(id);
            }
        }

        OfferedPrimaryItems = [.. OfferedPrimaryItems ?? [], .. live.OfferedPrimaryItems ?? []];
        Normalize();

        static HashSet<string> CommandIds(CommandSurfaceLayout layout) =>
            (layout.PrimaryToolbar ?? [])
                .Where(item => item.IsCommand && !string.IsNullOrEmpty(item.Id))
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
    }

    private void InsertBeforeSettings(string commandId)
    {
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
    }

    /// <summary>The longest command a plugin item id carries (<see cref="IsPluginCommand"/>).</summary>
    public const int MaxPluginCommandLength = 128;

    /// <summary>
    /// The plugin command grammar: 1 to <see cref="MaxPluginCommandLength"/> characters with no whitespace. The
    /// manifest reader enforces it on every command id a plugin declares or references, so every plugin command makes
    /// an item id <see cref="IsPluginItemId"/> accepts.
    /// </summary>
    public static bool IsPluginCommand(ReadOnlySpan<char> command)
    {
        if (command.Length is < 1 or > MaxPluginCommandLength)
        {
            return false;
        }

        foreach (var c in command)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True for a well-formed plugin item id, <c>plugin:&lt;id&gt;:&lt;command&gt;</c>: the plugin id follows the plugin id
    /// grammar (<c>^[a-z0-9][a-z0-9.-]{1,63}$</c>) and the command follows <see cref="IsPluginCommand"/>. Such an item
    /// stays in a saved toolbar while its plugin is absent.
    /// </summary>
    public static bool IsPluginItemId(string? id)
    {
        const string prefix = "plugin:";
        if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = id.IndexOf(':', prefix.Length);
        if (separator < 0)
        {
            return false;
        }

        var pluginId = id.AsSpan(prefix.Length, separator - prefix.Length);
        var command = id.AsSpan(separator + 1);
        if (pluginId.Length is < 2 or > 64 || !IsPluginCommand(command) || !IsPluginIdStart(pluginId[0]))
        {
            return false;
        }

        foreach (var c in pluginId[1..])
        {
            if (!IsPluginIdStart(c) && c is not ('.' or '-'))
            {
                return false;
            }
        }

        return true;

        static bool IsPluginIdStart(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
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
            // A plugin item stays while its plugin is absent (hidden by VisibleItems); an unknown core id is dropped.
            if (!item.IsCommand
                || id.Length == 0
                || !(ToolbarActionCatalog.IsActionId(id) || IsPluginItemId(id))
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
        OfferedPrimaryItems = (OfferedPrimaryItems ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private void UpgradeLegacyDefaultToolbar()
    {
        // The previous default named the core ftp-sftp-manager command. That command moved to the remote plugin, and
        // Normalize drops core ids the catalog no longer knows, so a stored previous default always arrives here
        // without it.
        const string previousDefaultWithoutFtp = "new,dual-pane,copy-to-pane,move-to-pane,operation-history,|,duplicate-checker,disk-cleanup,|,profiles,view-options,settings";
        var storedOrder = string.Join(",", PrimaryToolbar.Select(item => item.IsSeparator ? "|" : item.Id));
        if (storedOrder == previousDefaultWithoutFtp)
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

    /// <summary>
    /// The toolbar as drawn: only the items whose id the catalog knows now, with no leading, trailing or doubled
    /// separators. A plugin's item stays in <see cref="PrimaryToolbar"/> while its plugin is absent, and is left out here.
    /// </summary>
    public IReadOnlyList<CommandSurfaceItem> VisibleItems()
    {
        Normalize();
        var visible = new List<CommandSurfaceItem>(PrimaryToolbar.Count);
        var pendingSeparator = false;
        foreach (var item in PrimaryToolbar)
        {
            if (item.IsSeparator)
            {
                pendingSeparator = visible.Count > 0;
                continue;
            }

            if (!ToolbarActionCatalog.IsActionId(item.Id))
            {
                continue;
            }

            if (pendingSeparator)
            {
                visible.Add(CommandSurfaceItem.Separator());
                pendingSeparator = false;
            }

            visible.Add(item);
        }

        return visible;
    }

    public IReadOnlyList<string> VisiblePrimaryActionIds() =>
        VisibleItems()
            .Where(item => item.IsCommand)
            .Select(item => item.Id)
            .ToList();

    /// <summary>The display mode and the drawn items, so a hidden plugin item coming back re-renders the toolbar.</summary>
    public string Signature()
    {
        var visible = VisibleItems();
        return ToolbarDisplayMode + "|" + string.Join(
            ",",
            visible.Select(item => item.IsSeparator ? "|" : item.Id));
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

    /// <summary>
    /// A plugin toolbar item's <c>when</c> expression (<see cref="PluginMenuWhen"/>'s grammar), which hides the button
    /// while it is false; empty for built-in actions and for plugin items that are always shown.
    /// </summary>
    public string When { get; init; } = "";

    /// <summary>The plugin that contributes this action, or null for a built-in action.</summary>
    public string? PluginId { get; init; }
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
            _ => ContextMenuIconCatalog.Document,
        };
    }
}
