using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>The rules behind the gestures on the window's chrome (audit U4) that need no window.</summary>
public class PaneMouseGestureTests
{
    [Fact]
    public void SideButtons_GoBackAndForward()
    {
        Assert.Equal(-1, PaneMouseGestures.HistoryStep(xButton1Pressed: true, xButton2Pressed: false));
        Assert.Equal(1, PaneMouseGestures.HistoryStep(xButton1Pressed: false, xButton2Pressed: true));
        Assert.Equal(0, PaneMouseGestures.HistoryStep(false, false));
        // Both at once is neither.
        Assert.Equal(0, PaneMouseGestures.HistoryStep(true, true));
    }

    [Fact]
    public void ADraggedTab_TakesThePlaceOfTheTabUnderThePointer()
    {
        // Three tabs with a gap of 4 between them.
        (double Left, double Right)[] tabs = [(0, 100), (104, 204), (208, 308)];

        Assert.Equal(0, StripReorder.TargetIndex(50, tabs));
        Assert.Equal(1, StripReorder.TargetIndex(150, tabs));
        Assert.Equal(2, StripReorder.TargetIndex(300, tabs));
        // A gap belongs to the tab on its right, and the ends to the first and the last tab.
        Assert.Equal(1, StripReorder.TargetIndex(102, tabs));
        Assert.Equal(0, StripReorder.TargetIndex(-40, tabs));
        Assert.Equal(2, StripReorder.TargetIndex(900, tabs));
        Assert.Equal(0, StripReorder.TargetIndex(10, []));
    }

    [Fact]
    public void TheKeyboard_MovesATabOnePlaceAndStopsAtTheEnds()
    {
        Assert.Equal(1, StripReorder.Step(0, 3, 1));
        Assert.Equal(0, StripReorder.Step(1, 3, -1));
        Assert.Equal(2, StripReorder.Step(2, 3, 1));
        Assert.Equal(0, StripReorder.Step(0, 3, -1));
        Assert.Equal(0, StripReorder.Step(0, 0, 1));
    }

    [Fact]
    public void ResizeCommands_MoveADividerOneStepAndStopAtItsLimits()
    {
        var step = UiSettings.ResizeCommandStep;

        Assert.Equal(UiSettings.SidebarDefaultWidth + step, UiSettings.StepSidebarWidth(UiSettings.SidebarDefaultWidth, 1));
        Assert.Equal(UiSettings.SidebarDefaultWidth - step, UiSettings.StepSidebarWidth(UiSettings.SidebarDefaultWidth, -1));
        Assert.Equal(UiSettings.SidebarMaxWidth, UiSettings.StepSidebarWidth(UiSettings.SidebarMaxWidth, 1));
        Assert.Equal(UiSettings.SidebarMinWidth, UiSettings.StepSidebarWidth(UiSettings.SidebarMinWidth, -1));
        // A width that was never a number starts from the default.
        Assert.Equal(UiSettings.SidebarDefaultWidth + step, UiSettings.StepSidebarWidth(double.NaN, 1));

        Assert.Equal(UiSettings.PreviewDefaultWidth + step, UiSettings.StepPreviewWidth(UiSettings.PreviewDefaultWidth, 1));
        Assert.Equal(UiSettings.PreviewMaxWidth, UiSettings.StepPreviewWidth(UiSettings.PreviewMaxWidth, 1));
        Assert.Equal(UiSettings.PreviewMinWidth, UiSettings.StepPreviewWidth(UiSettings.PreviewMinWidth, -1));

        // The pane divider: both panes keep their minimum width.
        const double available = 1200;
        Assert.Equal(600 + step, UiSettings.StepDualPanePrimaryWidth(600, 1, available));
        Assert.Equal(600 - step, UiSettings.StepDualPanePrimaryWidth(600, -1, available));
        var widest = available - UiSettings.FilePaneMinWidth - UiSettings.DualPaneDividerWidth;
        Assert.Equal(widest, UiSettings.StepDualPanePrimaryWidth(widest, 1, available));
        Assert.Equal(UiSettings.FilePaneMinWidth, UiSettings.StepDualPanePrimaryWidth(UiSettings.FilePaneMinWidth, -1, available));
    }

    [Fact]
    public void EveryDivider_HasAResizeCommandEachWay()
    {
        string[] ids =
        [
            "sidebar-wider", "sidebar-narrower", "preview-wider", "preview-narrower",
            "pane-divider-left", "pane-divider-right", "panel-larger", "panel-smaller",
        ];

        foreach (var id in ids)
        {
            var command = AppCommandCatalog.Find(id);
            Assert.True(command is not null, $"{id} is not in the command catalog.");
            Assert.Equal("View", command!.Group);
        }

        // Found by what a user would type.
        Assert.Contains(AppCommandCatalog.Filter("sidebar wider"), command => command.Id == "sidebar-wider");
        Assert.Contains(OmnibarSuggestions.CommandSuggestions("divider"), suggestion => suggestion.Value == "pane-divider-left");
    }

    [Fact]
    public void DropDestination_ADragFromAPaneMovesUnlessCtrlIsHeldAndOneFromOutsideCopies()
    {
        Assert.True(DropDestination.IsMove(internalDrag: true, controlHeld: false));
        Assert.False(DropDestination.IsMove(internalDrag: true, controlHeld: true));
        Assert.False(DropDestination.IsMove(internalDrag: false, controlHeld: false));
        Assert.False(DropDestination.IsMove(internalDrag: false, controlHeld: true));
    }

    [Fact]
    public void DropDestination_ATabOrSidebarEntryThatIsNotAFolderTakesNoFiles()
    {
        Assert.True(DropDestination.CanReceive(@"C:\Users\test\Desktop"));
        Assert.True(DropDestination.CanReceive(@"\\server\share\folder"));
        Assert.False(DropDestination.CanReceive(PathRules.RecycleBinPath));
        Assert.False(DropDestination.CanReceive(""));
        Assert.False(DropDestination.CanReceive("  "));
        Assert.False(DropDestination.CanReceive(null));
    }
}
