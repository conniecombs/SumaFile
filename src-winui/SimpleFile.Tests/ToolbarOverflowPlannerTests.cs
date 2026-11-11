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
            [ToolbarOverflowPlanner.Settings] = 32,
            [ToolbarOverflowPlanner.Profiles] = 32,
            [ToolbarOverflowPlanner.ViewOptions] = 32,
            [ToolbarOverflowPlanner.New] = 32,
            ["disk-cleanup"] = 32,
            ["duplicate-checker"] = 32,
            ["operation-history"] = 32,
            ["move-to-pane"] = 32,
            ["copy-to-pane"] = 32,
            [ToolbarOverflowPlanner.DualPane] = 32,
        };
        var reserved = 360;

        Assert.Equal(
            [
                ToolbarOverflowPlanner.Settings,
                ToolbarOverflowPlanner.Profiles,
                ToolbarOverflowPlanner.ViewOptions,
                ToolbarOverflowPlanner.New,
                "disk-cleanup",
                "duplicate-checker",
                "operation-history",
                "move-to-pane",
                "copy-to-pane",
                ToolbarOverflowPlanner.DualPane,
            ],
            ToolbarOverflowPlanner.PrimaryHideOrder);

        var wide = ToolbarOverflowPlanner.OverflowIds(1200, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.Empty(wide);

        var medium = ToolbarOverflowPlanner.OverflowIds(660, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.Contains(ToolbarOverflowPlanner.Settings, medium);
        Assert.DoesNotContain(ToolbarOverflowPlanner.New, medium);

        var narrow = ToolbarOverflowPlanner.OverflowIds(340, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.True(narrow.IsSupersetOf(medium));
        Assert.Contains(ToolbarOverflowPlanner.New, narrow);

        var again = ToolbarOverflowPlanner.OverflowIds(340, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.True(narrow.SetEquals(again));
    }
}
