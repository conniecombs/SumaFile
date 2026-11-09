using System.Linq;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Contributions;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

public class PluginContributionAdapterTests
{
    private const string Id = "com.sumafile.hello";

    private static PluginRecord Record()
    {
        var contributions = new PluginContributions(
            [new PluginCommandSpec("greet", "Say hello", "Hello", "Ctrl+G")],
            [new PluginContextMenuSpec("Hello", [new PluginContextMenuItemSpec("greet", "Greet", "singleFile")])],
            [
                new PluginToolbarSpec("greet", "Greet", "", "primary"),
                new PluginToolbarSpec("other", "Other", "", "overflow"),
            ],
            []);
        var manifest = new PluginManifest(
            Id, "Hello", "1.0.0", new PluginPublisher("P", null, null), "1.0.0", null, null, contributions, [], []);
        var discovered = new DiscoveredPlugin(Id, "folder", "root", DiscoveryState.Valid, manifest, [], null);
        return new PluginRecord(discovered, PluginState.Enabled, new SignatureState(SignatureStatus.Unsigned, null, null), null, null);
    }

    [Fact]
    public void Commands_PrefixIdsWithPlugin()
    {
        var command = Assert.Single(PluginContributionAdapter.Commands([Record()]));
        Assert.Equal("plugin:com.sumafile.hello:greet", command.Id);
        Assert.Equal("Say hello", command.Label);
        Assert.Equal("Hello", command.Group);
        Assert.Equal("Ctrl+G", command.Shortcut);

        var group = Assert.Single(PluginContributionAdapter.ContextMenus([Record()]));
        Assert.Equal(Id, group.PluginId);
        Assert.Equal("Hello", group.Label);
        var item = Assert.Single(group.Items);
        Assert.Equal("plugin:com.sumafile.hello:greet", item.HostCommandId);
        Assert.Equal("singleFile", item.When);
    }

    [Fact]
    public void ToolbarActions_UseGlyphAndCommandId()
    {
        var actions = PluginContributionAdapter.ToolbarActions([Record()]);
        var greet = actions.First();
        Assert.Equal("plugin:com.sumafile.hello:greet", greet.Id);
        Assert.Equal("plugin:com.sumafile.hello:greet", greet.CommandId);
        Assert.Equal("", greet.IconGlyph);
        Assert.Equal("Greet", greet.Label);
        Assert.Equal(2, actions.Count);
    }

    private static PluginRecord SidebarRecord(string id, string name, params PluginSidebarSpec[] sidebar)
    {
        var contributions = new PluginContributions(
            [new PluginCommandSpec("manage", "Manage things", "G", null)], [], [], [])
        {
            Sidebar = sidebar,
        };
        var manifest = new PluginManifest(
            id, name, "1.0.0", new PluginPublisher("P", null, null), "1.0.0", new PluginUiSpec("A.dll", "A.Plugin"), null, contributions, [], []);
        var discovered = new DiscoveredPlugin(id, "folder", "root", DiscoveryState.Valid, manifest, [], null);
        return new PluginRecord(discovered, PluginState.Enabled, new SignatureState(SignatureStatus.Unsigned, null, null), null, null)
        {
            Epoch = id == "com.example.b" ? 7 : 3,
        };
    }

    [Fact]
    public void SidebarSections_ListEnabledPluginsInManifestOrderWithHostCommandIds()
    {
        var a = SidebarRecord(
            "com.example.a",
            "A",
            new PluginSidebarSpec("first", "First section", "p1", "manage"),
            new PluginSidebarSpec("second", "Second section", "p2", null));
        var b = SidebarRecord("com.example.b", "B", new PluginSidebarSpec("only", "Only section", "p3", null));

        var sections = PluginContributionAdapter.SidebarSections([a, b]);

        Assert.Equal(
            [
                new PluginSidebarSection("com.example.a", "first", "First section", "plugin:com.example.a:manage", "Manage things", 3),
                new PluginSidebarSection("com.example.a", "second", "Second section", null, null, 3),
                new PluginSidebarSection("com.example.b", "only", "Only section", null, null, 7),
            ],
            sections);
    }

    private static PluginRecord SlotRecord(
        string id,
        int epoch,
        IReadOnlyList<PluginPanelSpec> panels,
        PluginRowDecorationSpec? rowDecorations,
        PluginState state = PluginState.Enabled)
    {
        var contributions = new PluginContributions(
            [new PluginCommandSpec("toggle", "Toggle the panel", "G", null)], [], [], [])
        {
            Panels = panels,
            RowDecorations = rowDecorations,
        };
        var manifest = new PluginManifest(
            id, id, "1.0.0", new PluginPublisher("P", null, null), "1.0.0", new PluginUiSpec("A.dll", "A.Plugin"), null, contributions, [], []);
        var discovered = new DiscoveredPlugin(id, "folder", "root", DiscoveryState.Valid, manifest, [], null);
        return new PluginRecord(discovered, state, new SignatureState(SignatureStatus.Unsigned, null, null), null, null)
        {
            Epoch = epoch,
        };
    }

    [Fact]
    public void Panels_CarryTheEpochAndTheHostToggleCommand()
    {
        var b = SlotRecord(
            "com.example.b",
            7,
            [
                new PluginPanelSpec("workbench", "Workbench", "wb", "right", true, "toggle"),
                new PluginPanelSpec("log", "Log", "log", "bottom", false, null),
            ],
            null);
        var a = SlotRecord("com.example.a", 3, [new PluginPanelSpec("only", "Only", "only", "bottom", false, null)], null);

        var panels = PluginContributionAdapter.Panels([b, a]);

        Assert.Equal(
            [
                new PluginPanel("com.example.b", "workbench", "Workbench", "right", true, "plugin:com.example.b:toggle", 7),
                new PluginPanel("com.example.b", "log", "Log", "bottom", false, null, 7),
                new PluginPanel("com.example.a", "only", "Only", "bottom", false, null, 3),
            ],
            panels);
    }

    [Fact]
    public void RowDecorationProviders_ListEnabledPluginsInIdOrder()
    {
        var zed = SlotRecord("com.example.zed", 4, [], new PluginRowDecorationSpec("z-status"));
        var none = SlotRecord("com.example.none", 5, [], null);
        var alpha = SlotRecord("com.example.alpha", 9, [], new PluginRowDecorationSpec("a-status"));
        var upper = SlotRecord("com.example.Beta", 2, [], new PluginRowDecorationSpec("b-status"));

        var providers = PluginContributionAdapter.RowDecorationProviders([zed, none, alpha, upper]);

        Assert.Equal(
            [
                new PluginDecorationProvider("com.example.Beta", "b-status", 2),
                new PluginDecorationProvider("com.example.alpha", "a-status", 9),
                new PluginDecorationProvider("com.example.zed", "z-status", 4),
            ],
            providers);
    }

    [Fact]
    public void ToolbarActions_CarryTheWhenExpressionAndThePluginId()
    {
        var contributions = new PluginContributions(
            [new PluginCommandSpec("toggle", "Toggle", "Git", null)],
            [],
            [
                new PluginToolbarSpec("toggle", "Git", "", "primary") { When = "folderFlag(inRepository)" },
                new PluginToolbarSpec("toggle", "Always", "", "overflow"),
            ],
            []);
        var manifest = new PluginManifest(
            "com.example.git", "Git", "1.0.0", new PluginPublisher("P", null, null), "1.0.0", null, null, contributions, [], []);
        var discovered = new DiscoveredPlugin("com.example.git", "folder", "root", DiscoveryState.Valid, manifest, [], null);
        var record = new PluginRecord(discovered, PluginState.Enabled, new SignatureState(SignatureStatus.Unsigned, null, null), null, null);

        var actions = PluginContributionAdapter.ToolbarActions([record]);

        Assert.Equal(2, actions.Count);
        Assert.Equal("folderFlag(inRepository)", actions[0].When);
        Assert.Equal("com.example.git", actions[0].PluginId);
        Assert.Equal("", actions[1].When);
        Assert.Equal("com.example.git", actions[1].PluginId);

        var builtIn = new ToolbarAction("refresh", "Refresh", "View", "", "refresh");
        Assert.Equal("", builtIn.When);
        Assert.Null(builtIn.PluginId);
    }

    [Fact]
    public void PrimaryToolbarCommandIds_OnlyPrimaryPlacement()
    {
        Assert.Equal(["plugin:com.sumafile.hello:greet"], PluginContributionAdapter.PrimaryToolbarCommandIds([Record()]));
    }
}
