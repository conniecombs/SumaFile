using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.PluginHost;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The seal every <see cref="IPluginWindowHost"/> keeps: once <see cref="IPluginWindowHost.CloseAllAsync"/> starts,
/// a window attached later is refused (and closed at once by the host), so none outlives the plugin's teardown.
/// </summary>
public class PluginWindowSetTests
{
    private sealed class FakeWindow(string name)
    {
        public override string ToString() => name;
    }

    [Fact]
    public void TryTrack_BeforeTheSeal_TracksTheWindowOnce()
    {
        var set = new PluginWindowSet<FakeWindow>();
        var window = new FakeWindow("a");

        Assert.True(set.TryTrack(window));
        Assert.True(set.TryTrack(window));

        Assert.False(set.IsSealed);
        Assert.Equal([window], set.Open);
    }

    [Fact]
    public void Remove_ForgetsAClosedWindow()
    {
        var set = new PluginWindowSet<FakeWindow>();
        var a = new FakeWindow("a");
        var b = new FakeWindow("b");
        set.TryTrack(a);
        set.TryTrack(b);

        Assert.True(set.Remove(a));
        Assert.False(set.Remove(a));

        Assert.Equal([b], set.Open);
    }

    [Fact]
    public void Seal_ReturnsEveryTrackedWindowAndStaysSealed()
    {
        var set = new PluginWindowSet<FakeWindow>();
        var a = new FakeWindow("a");
        var b = new FakeWindow("b");
        set.TryTrack(a);
        set.TryTrack(b);

        Assert.Equal([a, b], set.Seal());
        Assert.True(set.IsSealed);

        // The windows stay tracked until their Closed event removes them, so a second close finds what is left.
        set.Remove(a);
        Assert.Equal([b], set.Seal());
        set.Remove(b);
        Assert.Empty(set.Seal());
        Assert.True(set.IsSealed);
    }

    [Fact]
    public void TryTrack_AfterTheSeal_IsRefusedAndLeavesNoTrackedWindowOpen()
    {
        var set = new PluginWindowSet<FakeWindow>();
        var early = new FakeWindow("early");
        set.TryTrack(early);
        set.Seal();
        set.Remove(early);

        var late = new FakeWindow("late");
        Assert.False(set.TryTrack(late));

        Assert.Empty(set.Open);
        Assert.Empty(set.Seal());
    }

    [Fact]
    public void TryTrack_NullWindow_Throws()
    {
        var set = new PluginWindowSet<FakeWindow>();

        Assert.Throws<ArgumentNullException>(() => set.TryTrack(null!));
    }

    [Fact]
    public async Task TryTrack_RacingTheSeal_EveryAcceptedWindowIsInTheSealResult()
    {
        for (var round = 0; round < 200; round++)
        {
            var set = new PluginWindowSet<FakeWindow>();
            var windows = Enumerable.Range(0, 8).Select(i => new FakeWindow($"w{i}")).ToArray();
            var accepted = new bool[windows.Length];
            using var start = new Barrier(windows.Length + 1);

            var attaches = windows.Select((window, i) => Task.Run(() =>
            {
                start.SignalAndWait();
                accepted[i] = set.TryTrack(window);
            })).ToArray();
            var seal = Task.Run(() =>
            {
                start.SignalAndWait();
                return set.Seal();
            });

            await Task.WhenAll(attaches);
            var sealedWith = await seal;

            // A window is either refused (the host closes it at once) or handed back by the seal (the host closes
            // it with the rest); none is tracked yet missed.
            for (var i = 0; i < windows.Length; i++)
            {
                Assert.Equal(accepted[i], sealedWith.Contains(windows[i]));
            }

            Assert.True(set.Open.ToHashSet().SetEquals(windows.Where((_, i) => accepted[i])));
        }
    }
}
