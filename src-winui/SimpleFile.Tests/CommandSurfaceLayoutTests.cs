using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

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
    public void CreateDefault_PrioritizesTransferHistoryAndInspectionActions()
    {
        var layout = CommandSurfaceLayout.CreateDefault();

        Assert.Equal(
            [
                ToolbarOverflowPlanner.New,
                ToolbarOverflowPlanner.DualPane,
                "copy-to-pane",
                "move-to-pane",
                "operation-history",
                "ftp-sftp-manager",
                "duplicate-checker",
                "disk-cleanup",
                ToolbarOverflowPlanner.Profiles,
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
            item => Assert.Equal("operation-history", item.Id),
            item => Assert.Equal("ftp-sftp-manager", item.Id),
            item => Assert.True(item.IsSeparator),
            item => Assert.Equal("duplicate-checker", item.Id),
            item => Assert.Equal("disk-cleanup", item.Id),
            item => Assert.True(item.IsSeparator),
            item => Assert.Equal(ToolbarOverflowPlanner.Profiles, item.Id),
            item => Assert.Equal(ToolbarOverflowPlanner.ViewOptions, item.Id),
            item => Assert.Equal(ToolbarOverflowPlanner.Settings, item.Id));
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
}
