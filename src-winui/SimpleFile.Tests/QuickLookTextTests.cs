using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>The Quick Look lightbox's counter and image zoom (audit U7).</summary>
public class QuickLookTextTests
{
    [Fact]
    public void Position_SaysWhereTheItemIsAmongThePanesRows()
    {
        Assert.Equal("1 of 12", QuickLookText.Position(0, 12));
        Assert.Equal("12 of 12", QuickLookText.Position(11, 12));
        // One row has no place to tell, and an item that left the list has none.
        Assert.Equal("", QuickLookText.Position(0, 1));
        Assert.Equal("", QuickLookText.Position(-1, 12));
        Assert.Equal("", QuickLookText.Position(12, 12));
        Assert.Equal("", QuickLookText.Position(0, 0));
    }

    [Fact]
    public void Zoom_StepsInAndOutBetweenFittedAndEightTimes()
    {
        Assert.Equal(1.25, QuickLookZoom.Step(1, 1));
        Assert.Equal(1.5, QuickLookZoom.Step(1.25, 1));
        Assert.Equal(8, QuickLookZoom.Step(6, 1));
        Assert.Equal(8, QuickLookZoom.Step(8, 1));
        Assert.Equal(6, QuickLookZoom.Step(8, -1));
        Assert.Equal(1, QuickLookZoom.Step(1.25, -1));
        Assert.Equal(1, QuickLookZoom.Step(1, -1));
        // 0 fits the image again, from anywhere.
        Assert.Equal(QuickLookZoom.Fit, QuickLookZoom.Step(4, 0));
        Assert.Equal(QuickLookZoom.Fit, QuickLookZoom.Step(double.NaN, 1));
    }

    [Fact]
    public void Zoom_FromBetweenTwoStepsGoesToTheNextOneThatWay()
    {
        // A pinch or Ctrl+wheel leaves the zoom anywhere.
        Assert.Equal(2, QuickLookZoom.Step(1.7, 1));
        Assert.Equal(1.5, QuickLookZoom.Step(1.7, -1));
        // What the view reports for a step is not exactly the step.
        Assert.Equal(1.5, QuickLookZoom.Step(1.2500001, 1));
        Assert.Equal(1, QuickLookZoom.Step(1.2500001, -1));
    }
}
