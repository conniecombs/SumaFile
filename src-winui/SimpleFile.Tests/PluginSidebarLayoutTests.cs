using System.Linq;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PluginSidebarLayoutTests
{
    private static PluginSidebarSection Section(string pluginId, string sectionId, int epoch) =>
        new(pluginId, sectionId, sectionId + " title", null, null, epoch);

    [Fact]
    public void Key_AndCollapseKey_NameThePluginSectionAndEpoch()
    {
        var section = Section("com.a.b", "s", 7);
        Assert.Equal("com.a.b:s:7", PluginSidebarLayout.Key(section));
        Assert.Equal("com.a.b:s", PluginSidebarLayout.CollapseKey(section));
    }

    [Fact]
    public void Diff_AddsNewSectionsAndRemovesWithdrawnOnes()
    {
        var kept = Section("com.a.b", "kept", 1);
        var added = Section("com.c.d", "added", 2);
        var shown = new[] { PluginSidebarLayout.Key(kept), "com.gone.plugin:old:3" };

        var (add, remove) = PluginSidebarLayout.Diff([kept, added], shown);

        Assert.Equal([added], add);
        Assert.Equal(["com.gone.plugin:old:3"], remove);
    }

    [Fact]
    public void Diff_NothingChanged_AddsAndRemovesNothing()
    {
        var a = Section("com.a.b", "s", 1);
        var b = Section("com.a.b", "t", 1);

        var (add, remove) = PluginSidebarLayout.Diff([a, b], [PluginSidebarLayout.Key(a), PluginSidebarLayout.Key(b)]);

        Assert.Empty(add);
        Assert.Empty(remove);
    }

    [Fact]
    public void Diff_ANewEpochReplacesTheSection()
    {
        var before = Section("com.a.b", "s", 1);
        var after = before with { Epoch = 2 };

        var (add, remove) = PluginSidebarLayout.Diff([after], [PluginSidebarLayout.Key(before)]);

        Assert.Equal(["com.a.b:s:1"], remove);
        Assert.Equal("com.a.b:s:2", PluginSidebarLayout.Key(Assert.Single(add)));
    }

    [Fact]
    public void Diff_AddsInWantedOrder()
    {
        var wanted = new[] { Section("com.z.z", "one", 1), Section("com.a.a", "two", 1), Section("com.m.m", "three", 1) };

        var (add, _) = PluginSidebarLayout.Diff(wanted, []);

        Assert.Equal(wanted.Select(PluginSidebarLayout.Key), add.Select(PluginSidebarLayout.Key));
    }

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public void ShouldActivate_OnlyWhenReadyVisibleExpandedAndNotYetActivated(
        bool ready, bool visible, bool collapsed, bool activated, bool expected) =>
        Assert.Equal(expected, PluginSidebarLayout.ShouldActivate(ready, visible, collapsed, activated));
}
