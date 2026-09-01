namespace SimpleFile.Core;

public enum ContextMenuKind
{
    Item,
    Divider,
}

public sealed class ContextMenuEntry
{
    public ContextMenuKind Kind { get; init; } = ContextMenuKind.Item;
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Shortcut { get; init; }
    public string? IconGlyph { get; init; }
    public string? CommandParameter { get; init; }
    public bool Disabled { get; init; }
    public bool Hidden { get; init; }
    public IReadOnlyList<ContextMenuEntry> Children { get; init; } = [];
}

public sealed class ContextMenuRequest
{
    public int SelectionCount { get; init; }
    public bool HasClipboard { get; init; }
    public bool DualPaneEnabled { get; init; }
    public PaneId MenuPane { get; init; } = PaneId.Primary;
    public bool OtherPaneHasPath { get; init; }
    public bool SelectedIsDirectory { get; init; }
    public string? SelectedDirectoryPath { get; init; }
    public bool HasFolderSelection { get; init; }
    public bool AllSelectedAreFiles { get; init; }
    public bool SelectedIsArchive { get; init; }
    public string? ArchiveExtractFolderName { get; init; }
    public string? SelectedExtension { get; init; }
    public IReadOnlyList<OpenWithApplication> OpenWithApplications { get; init; } = [];
    public IReadOnlyCollection<string> OverflowedToolbarIds { get; init; } = [];
    public bool InRecycleBin { get; init; }
}

/// <summary>
/// Shared menu IDs and visibility for the WinUI shell.
/// </summary>
public static class ContextMenuBuilder
{
    public static IReadOnlyList<ContextMenuEntry> Build(ContextMenuRequest request)
    {
        var hasOtherPane = request.DualPaneEnabled && request.OtherPaneHasPath;
        var canCompare = request.SelectionCount == 2 && request.AllSelectedAreFiles;
        var canUnpack = request.SelectionCount == 1 && request.SelectedIsDirectory;
        var extractFolder = string.IsNullOrEmpty(request.ArchiveExtractFolderName)
            ? "Extract to Folder"
            : $"Extract to {request.ArchiveExtractFolderName}/";

        if (request.InRecycleBin)
        {
            return VisibleEntries(
            [
                Item("ctx-restore", "Restore", request.SelectionCount == 0),
                Item("ctx-open", "Open", request.SelectionCount != 1 || request.SelectedIsDirectory, "Enter"),
                Item("ctx-copy", "Copy", request.SelectionCount == 0, "Ctrl+C"),
                Item("ctx-copy-path", "Copy original path", request.SelectionCount == 0, "Ctrl+Shift+C"),
                Divider(),
                Item("ctx-delete-permanent", "Delete Permanently", request.SelectionCount == 0, "Shift+Delete"),
                Item("ctx-empty-recycle-bin", "Empty Recycle Bin"),
                Divider(),
                Item("ctx-info", "Properties", request.SelectionCount != 1, "Alt+Enter"),
            ]);
        }

        var entries = new List<ContextMenuEntry>
        {
            Item("ctx-open", "Open", request.SelectionCount != 1, "Enter"),
            OpenWithMenu(request),
            Item("ctx-open-tab", "Open in new tab", request.SelectionCount != 1 || !request.SelectedIsDirectory, "Ctrl+Enter"),
            Item("ctx-open-other-pane", "Open in other pane", request.SelectionCount != 1 || !request.SelectedIsDirectory),
            Item("ctx-preview", "Quick Look", request.SelectionCount != 1, "Space"),
            Item("ctx-compare", "Compare files", !canCompare),
            Item("ctx-terminal", "Open terminal here", false, "F4"),
            Item("ctx-powershell-admin", "Open PowerShell as administrator"),
            Divider(),
            Item("ctx-color-label", "Set color label...", request.SelectionCount == 0),
            Item("ctx-folder-metrics", "Folder metrics", !request.HasFolderSelection),
            Item("ctx-cleanup", "Disk cleanup here..."),
            Item("ctx-duplicates", "Find duplicates here..."),
            Divider(),
            Item("ctx-rename", "Rename", request.SelectionCount != 1, "F2"),
            Item("ctx-advanced-rename", "Advanced rename...", request.SelectionCount == 0),
            Item("ctx-copy", "Copy", request.SelectionCount == 0, "Ctrl+C"),
            Item("ctx-cut", "Cut", request.SelectionCount == 0, "Ctrl+X"),
            Item(
                "ctx-paste",
                request.SelectedIsDirectory ? "Paste into folder" : "Paste",
                !request.HasClipboard,
                "Ctrl+V",
                commandParameter: request.SelectedIsDirectory ? request.SelectedDirectoryPath : null),
            Item("ctx-copy-path", "Copy path", request.SelectionCount == 0, "Ctrl+Shift+C"),
            Item("ctx-bookmark", "Bookmark folder", request.SelectionCount != 1 || !request.SelectedIsDirectory, "Ctrl+B"),
            Item("ctx-copy-to-pane", "Copy to other pane", request.SelectionCount == 0 || !hasOtherPane, "Ctrl+Alt+C"),
            Item("ctx-move-to-pane", "Move to other pane", request.SelectionCount == 0 || !hasOtherPane, "Ctrl+Alt+M"),
            Divider(),
            Item("ctx-pack", "Pack into folder...", request.SelectionCount == 0),
            Item("ctx-unpack", "Unpack folder here", !canUnpack),
            Item("ctx-compress", "Create archive...", request.SelectionCount == 0),
            new ContextMenuEntry
            {
                Kind = ContextMenuKind.Item,
                Id = "ctx-extract-menu",
                Label = "Extract",
                Disabled = !request.SelectedIsArchive,
                IconGlyph = ContextMenuIconCatalog.GlyphFor("ctx-extract-menu"),
                Children =
                [
                    Item("ctx-extract-folder", extractFolder, !request.SelectedIsArchive, showIcon: false),
                    Item("ctx-extract", "Extract here", !request.SelectedIsArchive, showIcon: false),
                    Item("ctx-extract-to", "Extract to...", !request.SelectedIsArchive, showIcon: false),
                ],
            },
            Divider(),
            DeleteMenu(request.SelectionCount == 0),
            Divider(),
            Item("ctx-info", "Properties", request.SelectionCount != 1, "Alt+Enter"),
        };

        return VisibleEntries(entries);
    }

    public static IReadOnlyList<ContextMenuEntry> BuildPaneMoreMenu(ContextMenuRequest request)
    {
        var hasSelection = request.SelectionCount > 0;
        var singleSelection = request.SelectionCount == 1;

        var entries = new List<ContextMenuEntry>();
        entries.AddRange(BuildToolbarOverflowItems(request));
        if (entries.Count > 0)
        {
            entries.Add(Divider());
        }

        if (request.InRecycleBin)
        {
            entries.Add(Item("ctx-empty-recycle-bin", "Empty Recycle Bin"));
            entries.Add(Divider());
        }

        entries.AddRange(
        [
            Item("ctx-close-left-pane", "Close left pane", !request.DualPaneEnabled || request.MenuPane != PaneId.Primary),
            Item("ctx-close-dual-pane", "Close right pane", !request.DualPaneEnabled, "F6"),
            Divider(),
            Item("ctx-rename", "Rename", !singleSelection, "F2"),
            DeleteMenu(!hasSelection),
            Item("ctx-color-label", "Set color label...", !hasSelection),
            Divider(),
            Item("ctx-view-archive", "View archive contents", !request.SelectedIsArchive),
            Item("ctx-extract-to", "Extract archive...", !request.SelectedIsArchive),
            Item("ctx-compress", "Create archive...", !hasSelection),
            Divider(),
            Item("ctx-folder-metrics", "Folder metrics", !request.HasFolderSelection),
            Item("ctx-duplicates", "Find duplicates here..."),
            Item("ctx-cleanup", "Disk cleanup here..."),
            Divider(),
            Item("ctx-terminal", "Open terminal here", false, "F4"),
        ]);

        return VisibleEntries(entries);
    }

    public static IReadOnlyList<ContextMenuEntry> BuildToolbarOverflowItems(ContextMenuRequest request)
    {
        var overflowed = request.OverflowedToolbarIds;
        if (overflowed.Count == 0)
        {
            return [];
        }

        bool Has(string id) =>
            overflowed.Contains(id);

        var items = new List<ContextMenuEntry>();
        if (Has(ToolbarOverflowPlanner.Search))
        {
            items.Add(Item("overflow-search", "Search", shortcut: "Ctrl+F"));
        }

        if (Has(ToolbarOverflowPlanner.Filter))
        {
            items.Add(Item("overflow-filter", "Filter"));
        }

        if (Has(ToolbarOverflowPlanner.NewFolder))
        {
            items.Add(Item("overflow-new-folder", "New folder", shortcut: "Ctrl+Shift+N"));
        }

        if (Has(ToolbarOverflowPlanner.NewFile))
        {
            items.Add(Item("overflow-new-file", "New file", shortcut: "Ctrl+N"));
        }

        if (Has(ToolbarOverflowPlanner.DualPane) && !request.DualPaneEnabled)
        {
            items.Add(Item("overflow-dual-pane", "Open second pane", shortcut: "F6"));
        }

        if (Has(ToolbarOverflowPlanner.ViewOptions))
        {
            items.Add(new ContextMenuEntry
            {
                Id = "overflow-view",
                Label = "View options",
                IconGlyph = ContextMenuIconCatalog.GlyphFor("overflow-view"),
                Children =
                [
                    Item("view:details", "Details"),
                    Item("view:list", "List"),
                    Item("view:tiles", "Tiles"),
                    Item("view:content", "Content"),
                    Divider(),
                    Item("icon:16", "Small icons"),
                    Item("icon:32", "Medium icons"),
                    Item("icon:48", "Large icons"),
                    Item("icon:96", "Extra large icons"),
                    Item("icon:128", "Jumbo icons"),
                    Item("icon:192", "Huge icons"),
                    Item("icon:256", "Maximum icons"),
                ],
            });
        }

        if (Has(ToolbarOverflowPlanner.Settings))
        {
            items.Add(Item("overflow-settings", "Settings", shortcut: "Ctrl+Shift+S"));
        }

        return items;
    }

    public static IReadOnlyList<ContextMenuEntry> VisibleEntries(IEnumerable<ContextMenuEntry> source)
    {
        var visible = new List<ContextMenuEntry>();
        foreach (var entry in source)
        {
            if (entry.Kind == ContextMenuKind.Divider)
            {
                if (visible.Count > 0 && visible[^1].Kind != ContextMenuKind.Divider)
                {
                    visible.Add(entry);
                }

                continue;
            }

            if (entry.Hidden || entry.Disabled)
            {
                continue;
            }

            if (entry.Children.Count > 0)
            {
                var children = VisibleEntries(entry.Children);
                if (children.Count == 0)
                {
                    continue;
                }

                visible.Add(new ContextMenuEntry
                {
                    Kind = entry.Kind,
                    Id = entry.Id,
                    Label = entry.Label,
                    Shortcut = entry.Shortcut,
                    IconGlyph = entry.IconGlyph,
                    CommandParameter = entry.CommandParameter,
                    Children = children,
                });
                continue;
            }

            visible.Add(entry);
        }

        while (visible.Count > 0 && visible[^1].Kind == ContextMenuKind.Divider)
        {
            visible.RemoveAt(visible.Count - 1);
        }

        return visible;
    }

    private static ContextMenuEntry Item(
        string id,
        string label,
        bool disabled = false,
        string? shortcut = null,
        string? iconGlyph = null,
        string? commandParameter = null,
        bool showIcon = true)
    {
        return new ContextMenuEntry
        {
            Kind = ContextMenuKind.Item,
            Id = id,
            Label = label,
            Shortcut = shortcut,
            IconGlyph = showIcon ? iconGlyph ?? ContextMenuIconCatalog.GlyphFor(id) : null,
            CommandParameter = commandParameter,
            Disabled = disabled,
        };
    }

    private static ContextMenuEntry OpenWithMenu(ContextMenuRequest request)
    {
        if (request.SelectionCount != 1
            || request.SelectedIsDirectory
            || OpenWithPolicy.IsDeniedTargetExtension(request.SelectedExtension))
        {
            return Item("ctx-open-with", "Open with...", disabled: true);
        }

        var children = new List<ContextMenuEntry>();
        var index = 0;
        foreach (var app in request.OpenWithApplications.Take(OpenWithPreferences.MaxMenuApplications))
        {
            children.Add(Item(
                $"ctx-open-with-app-{index}",
                app.MenuLabel,
                iconGlyph: ContextMenuIconCatalog.OpenWith,
                commandParameter: app.ApplicationPath));
            index++;
        }

        if (children.Count > 0)
        {
            children.Add(Divider());
        }

        children.Add(Item("ctx-open-with-choose", "Choose another app...", iconGlyph: ContextMenuIconCatalog.OpenWith));

        return new ContextMenuEntry
        {
            Id = "ctx-open-with",
            Label = "Open with",
            IconGlyph = ContextMenuIconCatalog.GlyphFor("ctx-open-with"),
            Children = children,
        };
    }

    private static ContextMenuEntry Divider()
    {
        return new ContextMenuEntry { Kind = ContextMenuKind.Divider };
    }

    private static ContextMenuEntry DeleteMenu(bool disabled)
    {
        return new ContextMenuEntry
        {
            Kind = ContextMenuKind.Item,
            Id = "ctx-delete-menu",
            Label = "Delete:",
            Disabled = disabled,
            IconGlyph = ContextMenuIconCatalog.GlyphFor("ctx-delete-menu"),
            Children =
            [
                Item("ctx-delete-recycle", "Move to Recycle Bin", disabled, "Delete", showIcon: false),
                Item("ctx-delete-permanent", "Delete Permanently", disabled, "Shift+Delete", showIcon: false),
            ],
        };
    }
}
