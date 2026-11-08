using System.Linq;
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

    [Fact]
    public void PrimaryToolbarCommandIds_OnlyPrimaryPlacement()
    {
        Assert.Equal(["plugin:com.sumafile.hello:greet"], PluginContributionAdapter.PrimaryToolbarCommandIds([Record()]));
    }
}
