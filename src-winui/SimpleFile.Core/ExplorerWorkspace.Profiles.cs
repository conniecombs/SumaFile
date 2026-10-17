using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{
    public void ApplyUiSettings(UiSettings settings, bool applyViewDefaultsToPanes = true) =>
        _folderViews.ApplyUiSettings(this, settings, applyViewDefaultsToPanes);

    public void SetShowHidden(bool showHidden) =>
        _folderViews.SetShowHidden(this, showHidden);

    public void SetFileListView(string view) =>
        SetFileListView(ActivePane, view);

    public void SetFileListView(PaneId pane, string view) =>
        _folderViews.SetFileListView(this, pane, view);

    public void SetFileListIconSize(int iconSize) =>
        SetFileListIconSize(ActivePane, iconSize);

    public void SetFileListIconSize(PaneId pane, int iconSize) =>
        _folderViews.SetFileListIconSize(this, pane, iconSize);

    public int NudgeFileListIconSize(PaneId pane, int steps) =>
        _folderViews.NudgeFileListIconSize(this, pane, steps);

    public bool ToggleShowHidden() =>
        _folderViews.ToggleShowHidden(this);

    public void ApplyViewOptionsToBothPanes(PaneId sourcePane) =>
        _folderViews.ApplyViewOptionsToBothPanes(this, sourcePane);

    public string ViewFor(PaneId pane) => Pane(pane).View;
    public int IconSizeFor(PaneId pane) => Pane(pane).IconSize;
    public string SortByFor(PaneId pane) => Pane(pane).SortBy;
    public bool SortAscendingFor(PaneId pane) => Pane(pane).SortAscending;

    public FolderViewRule? EffectiveFolderViewRuleFor(PaneId pane) =>
        _folderViews.EffectiveFolderViewRuleFor(this, pane);

    public Task<FolderViewRule> SaveFolderViewSettingsAsync(
        FolderViewScope scope,
        PaneId? pane = null,
        CancellationToken cancellationToken = default) =>
        _folderViews.SaveFolderViewSettingsAsync(this, scope, pane, cancellationToken);

    public void SetSort(string sortBy) =>
        SetSort(ActivePane, sortBy);

    public void SetSort(PaneId pane, string sortBy) =>
        _folderViews.SetSort(this, pane, sortBy);

    public WorkspaceLayout CaptureLayout() =>
        _layouts.CaptureLayout(DualPaneEnabled, ActivePane, SortBy, SortAscending, Primary, Secondary);

    public WorkspaceChromeLayout CaptureChromeLayout() =>
        _layouts.CaptureChromeLayout(Settings, PrimaryColumns, SecondaryColumns);

    public Task ApplyLayoutAsync(WorkspaceLayout layout, CancellationToken cancellationToken = default) =>
        _layouts.ApplyLayoutAsync(this, layout, cancellationToken);

    public Task<bool> TryPrimeDeferredWorkspaceLayoutAsync(CancellationToken cancellationToken = default) =>
        _layouts.TryPrimeDeferredWorkspaceLayoutAsync(this, cancellationToken);

    public Task SaveWorkspaceLayoutAsync(CancellationToken cancellationToken = default) =>
        _layouts.SaveAsync(FileOps, CaptureLayout(), Settings, Active.Path, cancellationToken);

    public Task<bool> TryRestoreWorkspaceLayoutAsync(CancellationToken cancellationToken = default) =>
        _layouts.TryRestoreWorkspaceLayoutAsync(this, cancellationToken);

    public Task SaveUiSettingsAsync(CancellationToken cancellationToken = default) =>
        _folderViews.SaveUiSettingsAsync(this, cancellationToken);

    public Task<WorkspaceProfile> SaveWorkspaceProfileAsync(
        string name,
        bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        _profiles.SaveAsync(name, overwrite, cancellationToken);

    public Task<WorkspaceProfile> OverwriteWorkspaceProfileAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _profiles.OverwriteAsync(id, cancellationToken);

    public Task<WorkspaceProfile> DuplicateWorkspaceProfileAsync(
        string id,
        string? name = null,
        CancellationToken cancellationToken = default) =>
        _profiles.DuplicateAsync(id, name, cancellationToken);

    public Task<WorkspaceProfile> RenameWorkspaceProfileAsync(
        string id,
        string name,
        CancellationToken cancellationToken = default) =>
        _profiles.RenameAsync(id, name, cancellationToken);

    public Task<WorkspaceProfile> ResetWorkspaceProfileAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _profiles.ResetAsync(id, cancellationToken);

    public Task DeleteWorkspaceProfileAsync(string id, CancellationToken cancellationToken = default) =>
        _profiles.DeleteAsync(id, cancellationToken);

    public Task ApplyWorkspaceProfileAsync(string id, CancellationToken cancellationToken = default) =>
        _layouts.ApplyWorkspaceProfileAsync(this, _profiles, id, cancellationToken);

    public Task<string> ExportWorkspaceProfileAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _profiles.ExportAsync(id, cancellationToken);

    public Task<IReadOnlyList<WorkspaceProfile>> ListWorkspaceProfilesAsync(
        CancellationToken cancellationToken = default) =>
        _profiles.ListAsync(cancellationToken);
}
