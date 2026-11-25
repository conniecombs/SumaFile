using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class MarqueeSelectionTests
{
    [Fact]
    public void Marquee_IntersectsVerticalRange()
    {
        Assert.True(MarqueeSelection.Intersects(0, 10, 100, 30, 20, 40));
        Assert.False(MarqueeSelection.Intersects(0, 10, 100, 5, 40, 50));
    }

    [Fact]
    public void Bounds_SpanTheStartAndThePointerWhicheverWayTheDragWent()
    {
        Assert.Equal((10, 20, 30, 40), MarqueeSelection.Bounds(10, 20, 40, 60));
        // Dragged up and to the left.
        Assert.Equal((10, 20, 30, 40), MarqueeSelection.Bounds(40, 60, 10, 20));
        Assert.Equal((5, 5, 0, 0), MarqueeSelection.Bounds(5, 5, 5, 5));
    }

    [Fact]
    public void Intersects_RectanglesThatOverlapOrShareAnEdge()
    {
        // A marquee across the middle of a row.
        Assert.True(MarqueeSelection.Intersects(50, 10, 20, 100, 0, 40, 400, 28));
        // One that ends above the row, and one beside it.
        Assert.False(MarqueeSelection.Intersects(50, 10, 20, 20, 0, 40, 400, 28));
        Assert.False(MarqueeSelection.Intersects(450, 10, 20, 100, 0, 40, 400, 28));
        // Touching the row's top edge counts, as it always has.
        Assert.True(MarqueeSelection.Intersects(50, 10, 20, 30, 0, 40, 400, 28));
        // A marquee that has not been dragged yet still touches the row it started on.
        Assert.True(MarqueeSelection.Intersects(50, 50, 0, 0, 0, 40, 400, 28));
    }

    [Fact]
    public void Resolve_SelectsWhatTheMarqueeTouches()
    {
        Assert.Equal(["b", "c"], MarqueeSelection.Resolve(["a"], ["b", "c"], toggle: false).Order());
        Assert.Empty(MarqueeSelection.Resolve(["a"], Array.Empty<string>(), toggle: false));
    }

    [Fact]
    public void Resolve_WithCtrlReversesTheTouchedRowsInTheSelectionItStartedFrom()
    {
        // a and b were selected; the marquee touches b and c: b is taken out, c is added, a stays.
        Assert.Equal(["a", "c"], MarqueeSelection.Resolve(["a", "b"], ["b", "c"], toggle: true).Order());
        // Dragged back off everything, the selection it started from is restored.
        Assert.Equal(["a", "b"], MarqueeSelection.Resolve(["a", "b"], Array.Empty<string>(), toggle: true).Order());
        // A row the marquee touches is reversed once, however often it is reported.
        Assert.Equal(["a"], MarqueeSelection.Resolve(["a", "b"], ["b", "b"], toggle: true).Order());
    }
}
