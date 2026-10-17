using System.Text.Json;

namespace SimpleFile.Core;

internal sealed class WorkspaceLayoutController
{
    private string? _deferredStartupNavigationPath;
    private WorkspaceLayout? _deferredStartupLayout;

    public WorkspaceLayout CaptureLayout(ExplorerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return CaptureLayout(
            workspace.DualPaneEnabled,
            workspace.ActivePane,
            workspace.SortBy,
            workspace.SortAscending,
            workspace.Primary,
            workspace.Secondary);
    }

    public WorkspaceLayout CaptureLayout(
        bool dualPaneEnabled,
        PaneId activePane,
        string sortBy,
        bool sortAscending,
        ExplorerPane primary,
        ExplorerPane secondary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        return new WorkspaceLayout
        {
            DualPaneEnabled = dualPaneEnabled,
            ActivePane = activePane,
            SortBy = sortBy,
            SortAscending = sortAscending,
            Primary = CapturePane(primary),
            Secondary = CapturePane(secondary),
        };
    }

    public WorkspaceChromeLayout CaptureChromeLayout(ExplorerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return CaptureChromeLayout(workspace.Settings, workspace.PrimaryColumns, workspace.SecondaryColumns);
    }

    public WorkspaceChromeLayout CaptureChromeLayout(
        UiSettings settings,
        ColumnLayout primaryColumns,
        ColumnLayout secondaryColumns)
    {
        return WorkspaceChromeLayout.Capture(settings, primaryColumns, secondaryColumns);
    }

    public async Task ApplyLayoutAsync(
        ExplorerWorkspace workspace,
        WorkspaceLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(layout);

        workspace.SetDualPaneEnabledCore(layout.DualPaneEnabled);
        RestorePaneState(layout, workspace.Primary, workspace.Secondary, workspace.Settings);

        var primaryPath = string.IsNullOrWhiteSpace(layout.Primary.Path) ? workspace.HomePath : layout.Primary.Path;
        if (!string.IsNullOrWhiteSpace(primaryPath))
        {
            await workspace.NavigatePaneAsync(
                    PaneId.Primary,
                    primaryPath,
                    HistoryMode.ReplaceCurrent,
                    activate: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (workspace.DualPaneEnabled && !string.IsNullOrWhiteSpace(layout.Secondary.Path))
        {
            await workspace.NavigatePaneAsync(
                    PaneId.Secondary,
                    layout.Secondary.Path,
                    HistoryMode.ReplaceCurrent,
                    activate: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        workspace.ActivatePane(workspace.DualPaneEnabled ? layout.ActivePane : PaneId.Primary);
    }

    public async Task<bool> TryPrimeDeferredWorkspaceLayoutAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var layout = await LoadAsync(workspace.FileOps, cancellationToken).ConfigureAwait(false);
        if (layout is null)
        {
            return false;
        }

        PrimeDeferredWorkspaceLayout(workspace, layout);
        return true;
    }

    public void PrimeDeferredWorkspaceLayout(ExplorerWorkspace workspace, WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(layout);

        _deferredStartupNavigationPath = null;
        _deferredStartupLayout = layout;
        workspace.SetDualPaneEnabledCore(layout.DualPaneEnabled);
        RestorePaneState(layout, workspace.Primary, workspace.Secondary, workspace.Settings);

        var primaryPath = string.IsNullOrWhiteSpace(layout.Primary.Path) ? workspace.HomePath : layout.Primary.Path;
        lock (workspace.Gate)
        {
            if (!string.IsNullOrWhiteSpace(primaryPath))
            {
                PrimePaneForDeferredNavigationLocked(workspace, PaneId.Primary, workspace.Primary, primaryPath);
            }

            if (workspace.DualPaneEnabled && !string.IsNullOrWhiteSpace(layout.Secondary.Path))
            {
                PrimePaneForDeferredNavigationLocked(workspace, PaneId.Secondary, workspace.Secondary, layout.Secondary.Path);
            }
            else
            {
                workspace.Secondary.IsNavigating = false;
                workspace.Secondary.ListingInProgress = false;
            }

            workspace.SetActivePaneCore(
                workspace.DualPaneEnabled && layout.ActivePane == PaneId.Secondary
                    ? PaneId.Secondary
                    : PaneId.Primary);
        }

        workspace.NotifyChanged();
    }

    public void PrimeDeferredStartupNavigation(ExplorerWorkspace workspace, string startPath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _deferredStartupNavigationPath = startPath;
        lock (workspace.Gate)
        {
            var state = workspace.Primary;
            state.IsNavigating = true;
            state.ListingInProgress = true;
            state.Path = startPath;
            state.Entries = [];
            state.SelectedPath = null;
            workspace.SetErrorMessageCore(null);
            workspace.SetFileOpenUnsupportedCore(false);
            workspace.DrivesController.ClearPendingReconnect();
            state.PathIsNetwork = workspace.DrivesController.IsNetworkFsPath(startPath);
            workspace.ApplyFolderViewSettingsForPathLocked(PaneId.Primary, startPath);
        }

        workspace.NotifyChanged();
    }

    public Task RunDeferredStartupNavigationAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var layout = _deferredStartupLayout;
        if (layout is not null)
        {
            _deferredStartupLayout = null;
            return RunDeferredWorkspaceLayoutNavigationAsync(workspace, layout, cancellationToken);
        }

        var path = _deferredStartupNavigationPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        _deferredStartupNavigationPath = null;
        return workspace.NavigatePaneAsync(PaneId.Primary, path, HistoryMode.Push, activate: false, cancellationToken);
    }

    private static void PrimePaneForDeferredNavigationLocked(
        ExplorerWorkspace workspace,
        PaneId pane,
        ExplorerPane state,
        string path)
    {
        state.NextNavigationToken();
        state.IsNavigating = true;
        state.ListingInProgress = true;
        state.Path = path;
        state.Entries = [];
        state.SelectedPath = null;
        state.PathIsNetwork = workspace.DrivesController.IsNetworkFsPath(path);
        workspace.ApplyFolderViewSettingsForPathLocked(pane, path);
        state.EnsureActiveTab(path);
    }

    private static async Task RunDeferredWorkspaceLayoutNavigationAsync(
        ExplorerWorkspace workspace,
        WorkspaceLayout layout,
        CancellationToken cancellationToken)
    {
        var primaryPath = string.IsNullOrWhiteSpace(layout.Primary.Path) ? workspace.HomePath : layout.Primary.Path;
        if (!string.IsNullOrWhiteSpace(primaryPath))
        {
            await workspace.NavigatePaneAsync(
                    PaneId.Primary,
                    primaryPath,
                    HistoryMode.ReplaceCurrent,
                    activate: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (workspace.DualPaneEnabled && !string.IsNullOrWhiteSpace(layout.Secondary.Path))
        {
            await workspace.NavigatePaneAsync(
                    PaneId.Secondary,
                    layout.Secondary.Path,
                    HistoryMode.ReplaceCurrent,
                    activate: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        workspace.ActivatePane(workspace.DualPaneEnabled ? layout.ActivePane : PaneId.Primary);
    }

    public async Task<bool> TryRestoreWorkspaceLayoutAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var layout = await LoadAsync(workspace.FileOps, cancellationToken).ConfigureAwait(false);
        if (layout is null)
        {
            return false;
        }

        await ApplyLayoutAsync(workspace, layout, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task ApplyWorkspaceProfileAsync(
        ExplorerWorkspace workspace,
        WorkspaceProfileService profiles,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(profiles);
        var profile = await profiles.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Profile was not found.");
        var clone = profile.Clone();
        (clone.Chrome ?? new WorkspaceChromeLayout()).Apply(
            workspace.Settings,
            workspace.PrimaryColumns,
            workspace.SecondaryColumns);
        await ApplyLayoutAsync(workspace, clone.Layout, cancellationToken).ConfigureAwait(false);
        await profiles.SetActiveAsync(clone.Id, cancellationToken).ConfigureAwait(false);
        await SaveAsync(workspace.FileOps, CaptureLayout(workspace), workspace.Settings, workspace.Active.Path, cancellationToken)
            .ConfigureAwait(false);
        await workspace.SaveUiSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public void RestorePaneState(
        WorkspaceLayout layout,
        ExplorerPane primary,
        ExplorerPane secondary,
        UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(settings);

        var legacySortBy = string.IsNullOrWhiteSpace(layout.SortBy) ? "name" : layout.SortBy;
        RestorePaneViewOptions(primary, layout.Primary, settings, legacySortBy, layout.SortAscending);
        RestorePaneViewOptions(secondary, layout.Secondary, settings, legacySortBy, layout.SortAscending);
        RestorePaneTabs(primary, layout.Primary);
        RestorePaneTabs(secondary, layout.Secondary);
    }

    public static WorkspacePaneLayout CapturePane(ExplorerPane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return new WorkspacePaneLayout
        {
            Path = pane.Path,
            ActiveTabId = pane.ActiveTabId,
            View = pane.View,
            IconSize = pane.IconSize,
            SortBy = pane.SortBy,
            SortAscending = pane.SortAscending,
            Tabs = pane.Tabs.Select(tab => new WorkspaceTabLayout
            {
                Id = tab.Id,
                Path = tab.Path,
                Title = tab.Title,
                History = [.. tab.History],
                HistoryIndex = tab.HistoryIndex,
            }).ToList(),
        };
    }

    public static void RestorePaneViewOptions(
        ExplorerPane pane,
        WorkspacePaneLayout layout,
        UiSettings settings,
        string fallbackSortBy,
        bool fallbackSortAscending)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(settings);

        pane.View = UiSettings.NormalizeDefaultView(
            string.IsNullOrWhiteSpace(layout.View) ? settings.DefaultView : layout.View);
        pane.IconSize = UiSettings.NormalizeIconSize(layout.IconSize ?? settings.DefaultIconSize);
        pane.SortBy = string.IsNullOrWhiteSpace(layout.SortBy) ? fallbackSortBy : layout.SortBy;
        pane.SortAscending = layout.SortAscending ?? fallbackSortAscending;
    }

    public static void RestorePaneTabs(ExplorerPane pane, WorkspacePaneLayout layout)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(layout);

        pane.Tabs.Clear();
        foreach (var tab in layout.Tabs)
        {
            pane.Tabs.Add(new FileTab
            {
                Id = string.IsNullOrEmpty(tab.Id) ? ExplorerPane.CreateTab(tab.Path).Id : tab.Id,
                Path = tab.Path,
                Title = string.IsNullOrEmpty(tab.Title) ? PathRules.Basename(tab.Path) : tab.Title,
                History = tab.History.Count > 0 ? [.. tab.History] : [tab.Path],
                HistoryIndex = tab.HistoryIndex,
            });
        }

        pane.ActiveTabId = layout.ActiveTabId;
        if (pane.ActiveTabId is not null)
        {
            var active = pane.Tabs.FirstOrDefault(tab => tab.Id == pane.ActiveTabId);
            if (active is not null)
            {
                pane.ApplyTabHistory(active);
            }
        }
    }

    public async Task SaveAsync(
        ISettingsBackend? fileOps,
        WorkspaceLayout layout,
        UiSettings settings,
        string activePath,
        CancellationToken cancellationToken = default)
    {
        if (fileOps is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(layout);
        await fileOps.SetSettingAsync(WorkspaceLayout.SettingsKey, json, cancellationToken).ConfigureAwait(false);
        settings.LastPath = activePath;
        await fileOps.SetSettingAsync("lastPath", settings.LastPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceLayout?> LoadAsync(
        ISettingsBackend? fileOps,
        CancellationToken cancellationToken = default)
    {
        if (fileOps is null)
        {
            return null;
        }

        try
        {
            var json = await fileOps.GetSettingAsync(WorkspaceLayout.SettingsKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var layout = JsonSerializer.Deserialize<WorkspaceLayout>(json);
            return layout is null || string.IsNullOrWhiteSpace(layout.Primary.Path)
                ? null
                : layout;
        }
        catch
        {
            return null;
        }
    }
}
