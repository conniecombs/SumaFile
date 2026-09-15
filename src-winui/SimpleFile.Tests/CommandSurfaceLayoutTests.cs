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
}
