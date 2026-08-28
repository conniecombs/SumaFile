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

public class ToolbarOverflowPlannerTests
{
    [Fact]
    public void ToolbarOverflowPlanner_HidesLowestPriorityFirstAndStaysStable()
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ToolbarOverflowPlanner.Filter] = ToolbarOverflowPlanner.FilterOverflowWidthFor(1200),
            [ToolbarOverflowPlanner.Search] = ToolbarOverflowPlanner.SearchOverflowWidthFor(1200),
            [ToolbarOverflowPlanner.Settings] = 32,
            [ToolbarOverflowPlanner.DualPane] = 32,
            [ToolbarOverflowPlanner.ViewOptions] = 32,
            [ToolbarOverflowPlanner.NewFile] = 32,
            [ToolbarOverflowPlanner.NewFolder] = 32,
        };
        var reserved = 360;

        Assert.Equal(
            [
                ToolbarOverflowPlanner.Filter,
                ToolbarOverflowPlanner.Search,
                ToolbarOverflowPlanner.Settings,
                ToolbarOverflowPlanner.DualPane,
                ToolbarOverflowPlanner.ViewOptions,
                ToolbarOverflowPlanner.NewFile,
                ToolbarOverflowPlanner.NewFolder,
            ],
            ToolbarOverflowPlanner.PrimaryHideOrder);

        var wide = ToolbarOverflowPlanner.OverflowIds(1200, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.Empty(wide);

        var medium = ToolbarOverflowPlanner.OverflowIds(700, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.Contains(ToolbarOverflowPlanner.Filter, medium);
        Assert.Contains(ToolbarOverflowPlanner.Search, medium);
        Assert.DoesNotContain(ToolbarOverflowPlanner.NewFolder, medium);

        var narrow = ToolbarOverflowPlanner.OverflowIds(340, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.True(narrow.IsSupersetOf(medium));
        Assert.Contains(ToolbarOverflowPlanner.NewFolder, narrow);

        var again = ToolbarOverflowPlanner.OverflowIds(340, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.True(narrow.SetEquals(again));
    }
    [Theory]
    [InlineData(700, 260, 128)]
    [InlineData(1600, 384, 160)]
    [InlineData(2400, 480, 200)]
    public void ToolbarOverflowPlanner_ScalesSearchAndFilterWithinCaps(
        double availableWidth,
        double expectedSearchWidth,
        double expectedFilterWidth)
    {
        Assert.Equal(expectedSearchWidth, ToolbarOverflowPlanner.SearchWidthFor(availableWidth), 3);
        Assert.Equal(expectedFilterWidth, ToolbarOverflowPlanner.FilterWidthFor(availableWidth), 3);
    }
}
