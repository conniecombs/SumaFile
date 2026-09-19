using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PaneRoleFormatterTests
{
    [Fact]
    public void Format_MarksActivePaneAndDestinationPane()
    {
        var left = PaneRoleFormatter.Format(PaneId.Primary, PaneId.Primary, dualPaneEnabled: true, @"C:\Work");
        var right = PaneRoleFormatter.Format(PaneId.Secondary, PaneId.Primary, dualPaneEnabled: true, @"D:\Sorted");

        Assert.Equal("Left - Active", left.Caption);
        Assert.Contains("left pane is active", left.Tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Work", left.Tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Right - Destination", right.Caption);
        Assert.Contains("destination for Copy/Move to other pane", right.Tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"D:\Sorted", right.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_UsesPlainPaneLabelWhenSinglePane()
    {
        var text = PaneRoleFormatter.Format(PaneId.Primary, PaneId.Primary, dualPaneEnabled: false, @"C:\Work");

        Assert.Equal("Left pane", text.Caption);
        Assert.Equal(@"Left pane: C:\Work", text.Tooltip);
    }
}
