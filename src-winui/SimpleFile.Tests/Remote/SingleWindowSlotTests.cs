using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

/// <summary>
/// The one-manager-window rule behind every plugin entry point (the command, the settings page button and the
/// sidebar rows). Those entry points run inside WinUI event handlers, where an escaping exception ends the whole App,
/// so <see cref="SingleWindowSlot{TWindow}.TryShow"/> reports failures instead of throwing, and a window that failed
/// to show is closed and never kept.
/// </summary>
public class SingleWindowSlotTests
{
    [Fact]
    public void TryShow_KeepsTheWindowOnlyAfterItShows()
    {
        var slot = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);
        var reported = new List<Exception>();
        var created = new FakeWindow();

        Assert.True(slot.TryShow(_ => throw new InvalidOperationException("no window yet"), () => created, w => w.Shown = true, Abandon, reported.Add));

        Assert.True(created.Shown);
        Assert.Same(created, slot.Open);
        Assert.Empty(reported);
    }

    [Fact]
    public void TryShow_ReusesTheOpenWindow()
    {
        var slot = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);
        var first = new FakeWindow();
        Assert.True(slot.TryShow(_ => { }, () => first, _ => { }, Abandon, _ => { }));

        var reused = new List<FakeWindow>();
        Assert.True(slot.TryShow(reused.Add, () => throw new InvalidOperationException("must not create"), _ => { }, Abandon, _ => { }));

        Assert.Equal([first], reused);
        Assert.Same(first, slot.Open);
    }

    [Fact]
    public void TryShow_ReportsACreateFailureAndKeepsNothing()
    {
        var slot = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);
        var reported = new List<Exception>();
        var failure = new InvalidOperationException("layout did not parse");

        Assert.False(slot.TryShow(_ => { }, () => throw failure, _ => { }, Abandon, reported.Add));

        Assert.Equal([failure], reported);
        Assert.Null(slot.Open);
    }

    [Fact]
    public void TryShow_ClosesAWindowThatFailedToShowAndCreatesAFreshOneNextTime()
    {
        var slot = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);
        var reported = new List<Exception>();
        var halfBuilt = new FakeWindow();

        Assert.False(slot.TryShow(_ => { }, () => halfBuilt, _ => throw new InvalidOperationException("attach failed"), Abandon, reported.Add));

        Assert.True(halfBuilt.IsClosed);
        Assert.Null(slot.Open);
        Assert.Equal("attach failed", Assert.Single(reported).Message);

        // The next open must not activate the half-built window: it builds a new one.
        var reused = new List<FakeWindow>();
        var fresh = new FakeWindow();
        Assert.True(slot.TryShow(reused.Add, () => fresh, _ => { }, Abandon, reported.Add));
        Assert.Empty(reused);
        Assert.Same(fresh, slot.Open);
    }

    [Fact]
    public void TryShow_ReportsAReuseFailureAndAnAbandonFailureWithoutThrowing()
    {
        var slot = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);
        var reported = new List<Exception>();
        var open = new FakeWindow();
        Assert.True(slot.TryShow(_ => { }, () => open, _ => { }, Abandon, reported.Add));

        Assert.False(slot.TryShow(_ => throw new InvalidOperationException("activate failed"), () => new FakeWindow(), _ => { }, Abandon, reported.Add));
        Assert.Equal("activate failed", Assert.Single(reported).Message);

        reported.Clear();
        var other = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);
        Assert.False(other.TryShow(
            _ => { },
            () => new FakeWindow(),
            _ => throw new InvalidOperationException("show failed"),
            _ => throw new InvalidOperationException("close failed"),
            reported.Add));
        Assert.Equal("show failed", Assert.Single(reported).Message);
        Assert.Null(other.Open);
    }

    [Fact]
    public void TryShow_SwallowsAReporterThatThrows()
    {
        var slot = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);

        Assert.False(slot.TryShow(_ => { }, () => throw new InvalidOperationException("create"), _ => { }, Abandon, _ => throw new InvalidOperationException("report")));
    }

    [Fact]
    public void TryShow_DoesNotKeepAWindowTheHostClosedWhileShowing()
    {
        // A host that is already stopping the plugin closes an attached window at once.
        var slot = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);
        var closedByHost = new FakeWindow();

        Assert.True(slot.TryShow(_ => { }, () => closedByHost, w => w.IsClosed = true, Abandon, _ => { }));

        Assert.Null(slot.Open);
    }

    [Fact]
    public void Forget_ClearsOnlyTheSameWindow_AndTakeEmptiesTheSlot()
    {
        var slot = new SingleWindowSlot<FakeWindow>(window => !window.IsClosed);
        var open = new FakeWindow();
        slot.TryShow(_ => { }, () => open, _ => { }, Abandon, _ => { });

        slot.Forget(new FakeWindow());
        Assert.Same(open, slot.Open);

        slot.Forget(open);
        Assert.Null(slot.Open);

        slot.TryShow(_ => { }, () => open, _ => { }, Abandon, _ => { });
        Assert.Same(open, slot.Take());
        Assert.Null(slot.Open);
        Assert.Null(slot.Take());
    }

    private static void Abandon(FakeWindow window) => window.IsClosed = true;

    private sealed class FakeWindow
    {
        public bool Shown { get; set; }
        public bool IsClosed { get; set; }
    }
}
