using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PluginPanelLayoutTests
{
    private static PluginPanel Panel(string pluginId = "com.sumafile.git", string panelId = "workbench", int epoch = 3,
        string? command = "plugin:com.sumafile.git:toggle-workbench", string placement = "bottom") =>
        new(pluginId, panelId, "Git", placement, true, command, epoch);

    [Fact]
    public void ClampBottomHeight_StaysBetweenMinAndSeventyPercent()
    {
        Assert.Equal(200, PluginPanelLayout.BottomMinHeight);
        Assert.Equal(320, PluginPanelLayout.BottomDefaultHeight);
        Assert.Equal(0.7, PluginPanelLayout.BottomMaxFraction);

        Assert.Equal(320, PluginPanelLayout.ClampBottomHeight(320, 1000));
        Assert.Equal(200, PluginPanelLayout.ClampBottomHeight(50, 1000));
        Assert.Equal(700, PluginPanelLayout.ClampBottomHeight(900, 1000));
        // A window too small for 70% to reach the minimum keeps the minimum.
        Assert.Equal(200, PluginPanelLayout.ClampBottomHeight(320, 250));
        // A size that is not a number falls back to the default.
        Assert.Equal(320, PluginPanelLayout.ClampBottomHeight(double.NaN, 1000));
        // An area not laid out yet only enforces the minimum, so a saved size survives the first pass.
        Assert.Equal(500, PluginPanelLayout.ClampBottomHeight(500, 0));
        Assert.Equal(200, PluginPanelLayout.ClampBottomHeight(100, double.NaN));
    }

    [Fact]
    public void ClampRightWidth_StaysBetweenMinAndSixtyPercent()
    {
        Assert.Equal(260, PluginPanelLayout.RightMinWidth);
        Assert.Equal(360, PluginPanelLayout.RightDefaultWidth);
        Assert.Equal(0.6, PluginPanelLayout.RightMaxFraction);
        Assert.Equal(1180, PluginPanelLayout.WindowWidth);
        Assert.Equal(740, PluginPanelLayout.WindowHeight);

        Assert.Equal(360, PluginPanelLayout.ClampRightWidth(360, 1200));
        Assert.Equal(260, PluginPanelLayout.ClampRightWidth(10, 1200));
        Assert.Equal(720, PluginPanelLayout.ClampRightWidth(5000, 1200));
        Assert.Equal(260, PluginPanelLayout.ClampRightWidth(400, 300));
        Assert.Equal(360, PluginPanelLayout.ClampRightWidth(double.PositiveInfinity, 1200));
        Assert.Equal(900, PluginPanelLayout.ClampRightWidth(900, -1));
    }

    [Fact]
    public void StillWanted_DropsThePanelWhenItsPluginOrEpochLeaves()
    {
        var shown = Panel();
        var key = PluginPanelLayout.Key(shown);
        Assert.Equal("com.sumafile.git:workbench:3", key);
        Assert.Equal("com.sumafile.git:workbench", PluginPanelLayout.StateKey(shown));

        Assert.Same(shown, PluginPanelLayout.StillWanted([Panel("com.other", "x"), shown], key));
        Assert.Null(PluginPanelLayout.StillWanted([Panel(epoch: 4)], key));
        Assert.Null(PluginPanelLayout.StillWanted([Panel("com.other")], key));
        Assert.Null(PluginPanelLayout.StillWanted([], key));
        Assert.Null(PluginPanelLayout.StillWanted([shown], null));
    }

    [Fact]
    public void ForCommand_FindsThePanelItsCommandToggles()
    {
        var git = Panel();
        var hello = Panel("com.hello", "right", 1, "plugin:com.hello:show-right", "right");
        var noCommand = Panel("com.quiet", "p", 1, null);

        Assert.Same(git, PluginPanelLayout.ForCommand([noCommand, hello, git], "plugin:com.sumafile.git:toggle-workbench"));
        Assert.Same(hello, PluginPanelLayout.ForCommand([noCommand, hello, git], "plugin:com.hello:show-right"));
        Assert.Null(PluginPanelLayout.ForCommand([noCommand, hello, git], "plugin:com.hello:SHOW-RIGHT"));
        Assert.Null(PluginPanelLayout.ForCommand([noCommand], "plugin:com.quiet:p"));
    }

    [Fact]
    public void BodyLoadKey_CarriesThePanelSlot()
    {
        var panel = Panel("com.hello", "main", 2);
        var section = new PluginSidebarSection("com.hello", "main", "Hello", null, null, 2);

        Assert.Equal("panel:com.hello:main:2", PluginPanelLayout.BodyLoadKey(panel));
        Assert.Equal("panel:" + PluginPanelLayout.Key(panel), PluginPanelLayout.BodyLoadKey(panel));
        Assert.Equal(PluginSidebarLayout.Key(section), PluginPanelLayout.Key(panel));
        Assert.NotEqual(PluginSidebarLayout.BodyLoadKey(section), PluginPanelLayout.BodyLoadKey(panel));
    }

    [Fact]
    public void NeedsBodyLoad_OncePerPresentation()
    {
        // A shown panel with no body and no load for this presentation asks for one.
        Assert.True(PluginPanelLayout.NeedsBodyLoad(hasBody: false, presentation: 4, loadingPresentation: null));

        // A body already there, or a load for this very presentation still running (it cleared its queue mark before it
        // started), means no second CreatePanel call.
        Assert.False(PluginPanelLayout.NeedsBodyLoad(hasBody: true, presentation: 4, loadingPresentation: null));
        Assert.False(PluginPanelLayout.NeedsBodyLoad(hasBody: false, presentation: 4, loadingPresentation: 4));
        Assert.False(PluginPanelLayout.NeedsBodyLoad(hasBody: true, presentation: 4, loadingPresentation: 4));

        // A load still running for an ended presentation is dropped when it returns, so the new one loads its own.
        Assert.True(PluginPanelLayout.NeedsBodyLoad(hasBody: false, presentation: 5, loadingPresentation: 4));
    }
}
