using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

[Collection("Plugin catalogs")]
public class CommandSurfaceLayoutTests
{
    [Fact]
    public void FromJson_SanitizesToolbarLayout()
    {
        var layout = CommandSurfaceLayout.FromJson("""
            {
              "version": 99,
              "toolbarDisplayMode": "labels",
              "primaryToolbar": [
                { "kind": "separator" },
                { "kind": "command", "id": "settings" },
                { "kind": "command", "id": "missing" },
                { "kind": "separator" },
                { "kind": "separator" },
                { "kind": "command", "id": "copy" },
                { "kind": "command", "id": "copy" },
                { "kind": "separator" }
              ]
            }
            """);

        Assert.Equal(CommandSurfaceLayout.CurrentVersion, layout.Version);
        Assert.Equal(ToolbarActionCatalog.IconAndLabelDisplayMode, layout.ToolbarDisplayMode);
        Assert.Equal(["settings", "copy"], layout.VisiblePrimaryActionIds());
        Assert.Collection(
            layout.PrimaryToolbar,
            item => Assert.Equal("settings", item.Id),
            item => Assert.True(item.IsSeparator),
            item => Assert.Equal("copy", item.Id));
    }

    [Fact]
    public void Catalog_IncludesBuiltInAndCommandPaletteActions()
    {
        Assert.True(ToolbarActionCatalog.Find(ToolbarOverflowPlanner.New)?.UsesBuiltInControl);
        Assert.True(ToolbarActionCatalog.Find(ToolbarOverflowPlanner.Settings)?.UsesBuiltInControl);
        Assert.Equal("Copy", ToolbarActionCatalog.Find("copy")?.Label);
        Assert.Equal("Tools", ToolbarActionCatalog.Find("terminal")?.Group);
        Assert.Equal("Git", ToolbarActionCatalog.Find("git-panel")?.Group);
        Assert.Equal(ContextMenuIconCatalog.Branch, ToolbarActionCatalog.Find("git-panel")?.IconGlyph);
        Assert.Null(ToolbarActionCatalog.Find("search"));
        Assert.Null(ToolbarActionCatalog.Find("filter"));
        Assert.Equal(ToolbarActionCatalog.IconOnlyDisplayMode, ToolbarActionCatalog.NormalizeDisplayMode("wide"));
        Assert.True(ToolbarActionCatalog.WidthFor("copy", ToolbarActionCatalog.IconAndLabelDisplayMode) > 32);
    }

    [Fact]
    public void CreateDefault_PrioritizesEverydayActionsWithLabels()
    {
        var layout = CommandSurfaceLayout.CreateDefault();

        Assert.Equal(
            [
                ToolbarOverflowPlanner.New,
                ToolbarOverflowPlanner.DualPane,
                "copy-to-pane",
                "move-to-pane",
                ToolbarOverflowPlanner.ViewOptions,
                ToolbarOverflowPlanner.Settings,
            ],
            layout.VisiblePrimaryActionIds());
        Assert.Collection(
            layout.PrimaryToolbar,
            item => Assert.Equal(ToolbarOverflowPlanner.New, item.Id),
            item => Assert.Equal(ToolbarOverflowPlanner.DualPane, item.Id),
            item => Assert.Equal("copy-to-pane", item.Id),
            item => Assert.Equal("move-to-pane", item.Id),
            item => Assert.True(item.IsSeparator),
            item => Assert.Equal(ToolbarOverflowPlanner.ViewOptions, item.Id),
            item => Assert.Equal(ToolbarOverflowPlanner.Settings, item.Id));
        Assert.Equal(ToolbarActionCatalog.IconAndLabelDisplayMode, layout.ToolbarDisplayMode);
    }

    [Fact]
    public void FromJson_UpgradesLegacyDefaultToolbarToCurrentDefault()
    {
        var layout = CommandSurfaceLayout.FromJson("""
            {
              "version": 1,
              "toolbarDisplayMode": "labels",
              "primaryToolbar": [
                { "kind": "command", "id": "new" },
                { "kind": "command", "id": "dual-pane" },
                { "kind": "command", "id": "profiles" },
                { "kind": "command", "id": "view-options" },
                { "kind": "command", "id": "settings" }
              ]
            }
            """);

        Assert.Equal(ToolbarActionCatalog.IconAndLabelDisplayMode, layout.ToolbarDisplayMode);
        Assert.Equal(CommandSurfaceLayout.DefaultPrimaryActionIds, layout.VisiblePrimaryActionIds());
    }

    [Fact]
    public void PrimaryHideOrderForDefaultLayout_CanOverflowEveryDefaultAction()
    {
        var layout = CommandSurfaceLayout.CreateDefault();

        var hideOrder = ToolbarOverflowPlanner.PrimaryHideOrderFor(layout);

        foreach (var id in layout.VisiblePrimaryActionIds())
        {
            Assert.Contains(id, hideOrder);
        }
    }

    [Fact]
    public void FromJson_SimplifiesPreviousDefaultButPreservesCustomLayoutsAndDisplayPreference()
    {
        var previous = new CommandSurfaceLayout
        {
            ToolbarDisplayMode = ToolbarActionCatalog.IconOnlyDisplayMode,
            PrimaryToolbar = [.. new[] { "new", "dual-pane", "copy-to-pane", "move-to-pane", "operation-history", "ftp-sftp-manager", "|", "duplicate-checker", "disk-cleanup", "|", "profiles", "view-options", "settings" }
                .Select(id => id == "|" ? CommandSurfaceItem.Separator() : CommandSurfaceItem.Command(id))],
        };
        var migrated = CommandSurfaceLayout.FromJson(previous.ToJson());
        Assert.Equal(CommandSurfaceLayout.DefaultPrimaryActionIds, migrated.VisiblePrimaryActionIds());
        Assert.Equal(ToolbarActionCatalog.IconOnlyDisplayMode, migrated.ToolbarDisplayMode);
        previous.PrimaryToolbar.Reverse();
        var custom = CommandSurfaceLayout.FromJson(previous.ToJson());
        Assert.Equal(previous.VisiblePrimaryActionIds(), custom.VisiblePrimaryActionIds());
    }

    [Fact]
    public void EnsurePrimaryItem_InsertsBeforeSettingsOnce()
    {
        var layout = CommandSurfaceLayout.CreateDefault();

        Assert.True(layout.EnsurePrimaryItem("plugin:com.a:run"));
        Assert.False(layout.EnsurePrimaryItem("plugin:com.a:run"));

        var ids = layout.PrimaryToolbar.Select(item => item.Id).ToList();
        Assert.Single(ids, "plugin:com.a:run");
        Assert.Equal(ToolbarOverflowPlanner.Settings, ids[^1]);
        Assert.Equal("plugin:com.a:run", ids[^2]);

        var noSettings = new CommandSurfaceLayout { PrimaryToolbar = [CommandSurfaceItem.Command("copy")] };
        Assert.True(noSettings.EnsurePrimaryItem("x"));
        Assert.Equal("x", noSettings.PrimaryToolbar[^1].Id);
    }

    [Fact]
    public void ToolbarActionCatalog_SetPluginActions_AddsAndRestores()
    {
        var raised = 0;
        void Handler(object? sender, EventArgs e) => raised++;
        ToolbarActionCatalog.Changed += Handler;
        try
        {
            ToolbarActionCatalog.SetPluginActions([new ToolbarAction("plugin:com.a:run", "Run", "Hello", "\uE768", "plugin:com.a:run")]);
            Assert.Equal(1, raised);
            Assert.NotNull(ToolbarActionCatalog.Find("plugin:com.a:run"));
            Assert.Contains(ToolbarActionCatalog.All, action => action.Id == "plugin:com.a:run");

            ToolbarActionCatalog.SetPluginActions([]);
            Assert.Null(ToolbarActionCatalog.Find("plugin:com.a:run"));
        }
        finally
        {
            ToolbarActionCatalog.Changed -= Handler;
            ToolbarActionCatalog.SetPluginActions([]);
        }
    }
}
