using SimpleFile.Core;

namespace SimpleFile.App;

// The dividers' resize for the keyboard. The sidebar, the preview pane, the two file panes and a docked plugin panel
// are resized by dragging the grips between them, which only a pointer can do; these commands move the same
// dividers one step at a time, through the settings and the layout code the grips use, and save as a drag does.
public sealed partial class MainWindow
{
    private Task ResizeSidebarAsync(int direction)
    {
        if (_workspace is not { } workspace)
        {
            return Task.CompletedTask;
        }

        if (!workspace.Settings.SidebarVisible)
        {
            SetStatusText("The sidebar is hidden");
            return Task.CompletedTask;
        }

        workspace.Settings.SidebarWidth = UiSettings.StepSidebarWidth(workspace.Settings.SidebarWidth, direction);
        ApplySidebarLayout();
        SetStatusText($"Sidebar {workspace.Settings.SidebarWidth:0} px wide");
        return workspace.SaveUiSettingsAsync();
    }

    private Task ResizePreviewAsync(int direction)
    {
        if (_workspace is not { } workspace)
        {
            return Task.CompletedTask;
        }

        if (!workspace.Settings.PreviewVisible)
        {
            SetStatusText("The preview pane is hidden");
            return Task.CompletedTask;
        }

        workspace.Settings.PreviewWidth = UiSettings.StepPreviewWidth(workspace.Settings.PreviewWidth, direction);
        ApplyPreviewVisibility();
        SetStatusText($"Preview pane {workspace.Settings.PreviewWidth:0} px wide");
        return workspace.SaveUiSettingsAsync();
    }

    private Task MovePaneDividerAsync(int direction)
    {
        if (_workspace is not { } workspace)
        {
            return Task.CompletedTask;
        }

        var available = PanesGrid.ActualWidth;
        if (!workspace.DualPaneEnabled || available <= 0)
        {
            SetStatusText("There is one pane: open the second pane to move the divider");
            return Task.CompletedTask;
        }

        // What a drag of the divider stores and applies.
        var current = UiSettings.ResolveDualPanePrimaryWidth(
            workspace.Settings.DualPanePrimaryWidth, workspace.Settings.DualPanePrimaryPercent, available);
        var width = UiSettings.StepDualPanePrimaryWidth(current, direction, available);
        workspace.Settings.DualPanePrimaryWidth = width;
        workspace.Settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(width / available * 100);
        ApplyDualPaneLayout();
        QueuePaneSizeColumnRefresh();
        ApplyFileListViewPresentation();
        ApplyColumnWidths();
        QueueDetailsScrollRefresh();
        SetStatusText($"Left pane {width:0} px wide");
        return workspace.SaveUiSettingsAsync();
    }

    private Task ResizeDockedPanelAsync(int direction)
    {
        if (_workspace is not { } workspace)
        {
            return Task.CompletedTask;
        }

        if (DockedPluginPanel is null)
        {
            SetStatusText("No panel is docked");
            return Task.CompletedTask;
        }

        ResizePluginPanelBy(Math.Sign(direction) * UiSettings.ResizeCommandStep);
        return workspace.SaveUiSettingsAsync();
    }
}
