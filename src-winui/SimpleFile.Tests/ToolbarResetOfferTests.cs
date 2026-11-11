using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The Core rule behind re-applying the plugin toolbar placements after a Settings save (spec §7): a toolbar Reset saves
/// the default layout, which has no offers, so the enabled plugins' primary items come back; any other save carries the
/// offers, so an item the user removed stays removed.
/// </summary>
public class ToolbarResetOfferTests
{
    private const string PluginItem = "plugin:com.a:run";

    [Fact]
    public void AfterAToolbarReset_AnEnabledPluginsPrimaryItemComesBackBeforeSettings()
    {
        var live = CommandSurfaceLayout.CreateDefault();
        Assert.True(live.EnsurePrimaryItem(PluginItem));
        live.PrimaryToolbar.RemoveAll(item => item.Id == PluginItem);
        Assert.False(live.EnsurePrimaryItem(PluginItem));

        // Settings' Reset: the saved layout is the default one, without the live layout's offers.
        var reset = CommandSurfaceLayout.CreateDefault();

        // The placements applied after the save offer the item again.
        Assert.True(reset.EnsurePrimaryItem(PluginItem));
        var ids = reset.PrimaryToolbar.Where(item => item.IsCommand).Select(item => item.Id).ToList();
        Assert.Equal(ids.IndexOf(ToolbarOverflowPlanner.Settings) - 1, ids.IndexOf(PluginItem));
        Assert.Contains(PluginItem, reset.OfferedPrimaryItems);
    }

    [Fact]
    public void AfterAnEditedSave_AnItemTheUserRemovedIsNotOfferedAgain()
    {
        var live = CommandSurfaceLayout.CreateDefault();
        Assert.True(live.EnsurePrimaryItem(PluginItem));
        var baseline = CommandSurfaceLayout.FromJson(live.ToJson());

        // The editor removed the item; saving merges the live offers, as SettingsWindow.ApplyTo does without a Reset.
        var edited = CommandSurfaceLayout.FromJson(live.ToJson());
        edited.PrimaryToolbar.RemoveAll(item => item.Id == PluginItem);
        edited.MergeOffersFrom(live, baseline);

        Assert.False(edited.EnsurePrimaryItem(PluginItem));
        Assert.DoesNotContain(edited.PrimaryToolbar, item => item.Id == PluginItem);
    }
}
