using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Whether anyone still waits for a queued request. A video thumbnail that reaches the front of its queue with
/// nobody waiting (the folder was left, the row scrolled away) is dropped before it starts.
/// </summary>
public class WantedRequestsTests
{
    [Fact]
    public void ARequestIsWantedWhileSomeoneWaits()
    {
        var wanted = new WantedRequests();
        Assert.False(wanted.IsWanted("a"));

        using (wanted.Want("a"))
        {
            Assert.True(wanted.IsWanted("a"));
            Assert.False(wanted.IsWanted("b"));
        }

        Assert.False(wanted.IsWanted("a"));
    }

    [Fact]
    public void TwoWaitersKeepItWantedUntilBothLeave()
    {
        var wanted = new WantedRequests();
        var first = wanted.Want("a");
        var second = wanted.Want("a");

        first.Dispose();
        Assert.True(wanted.IsWanted("a"));
        second.Dispose();
        Assert.False(wanted.IsWanted("a"));
    }

    [Fact]
    public void ANewWaiterMakesItWantedAgain()
    {
        var wanted = new WantedRequests();
        wanted.Want("a").Dispose();
        Assert.False(wanted.IsWanted("a"));

        using var again = wanted.Want("a");

        Assert.True(wanted.IsWanted("a"));
    }

    [Fact]
    public void LeavingTwiceCountsOnce()
    {
        var wanted = new WantedRequests();
        var first = wanted.Want("a");
        using var second = wanted.Want("a");

        first.Dispose();
        first.Dispose();

        Assert.True(wanted.IsWanted("a"));
    }

    /// <summary>The keys are thumbnail keys, which hold a path: case does not tell two apart.</summary>
    [Fact]
    public void KeysIgnoreCase()
    {
        var wanted = new WantedRequests();

        using var waiting = wanted.Want(@"C:\Films\A.mp4|96");

        Assert.True(wanted.IsWanted(@"c:\films\a.MP4|96"));
    }
}
