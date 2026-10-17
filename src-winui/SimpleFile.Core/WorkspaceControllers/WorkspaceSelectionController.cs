using SimpleFile.Ipc;

namespace SimpleFile.Core;

internal sealed class WorkspaceSelectionController
{
    private string _primaryFilterQuery = "";
    private string _secondaryFilterQuery = "";

    public async Task InitializeAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default,
        bool deferInitialNavigation = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var timer = new StartupTimer("ExplorerWorkspace.Initialize");
        timer.Mark("begin");
        cancellationToken.ThrowIfCancellationRequested();
        workspace.SetHomePathCore(
            workspace.Backend.CachedHomeDir
                ?? await workspace.Backend.GetHomeDirAsync(cancellationToken).ConfigureAwait(false));
        timer.Mark(workspace.Backend.CachedHomeDir is null ? "home-ipc" : "home-cached", workspace.HomePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (workspace.Backend.CachedDrives is { Count: > 0 } cachedDrives)
        {
            workspace.NavigationController.SeedCachedDrives(workspace, cachedDrives, quiet: true);
            timer.Mark("drives-cached", $"count={cachedDrives.Count}");
        }
        else
        {
            await workspace.NavigationController
                .RefreshDrivesAsync(workspace, quiet: true, cancellationToken, lightweight: true)
                .ConfigureAwait(false);
            timer.Mark("drives-light");
        }

        await workspace.PlacesController.LoadSmartFoldersAsync(workspace.FileOps, cancellationToken).ConfigureAwait(false);
        timer.Mark("smart-folders", $"count={workspace.SmartFolders.Count}");
        await workspace.TagsController.LoadTagsAsync(workspace.FileOps, cancellationToken).ConfigureAwait(false);
        timer.Mark("tags", $"count={workspace.AllTags.Count}");
        await workspace.FolderViewsController
            .LoadUiSettingsAsync(workspace, workspace.PlacesController, cancellationToken)
            .ConfigureAwait(false);
        timer.Mark("settings");

        var startMode = UiSettings.NormalizeStartLocation(workspace.Settings.StartLocation);
        if (startMode == "last"
            && deferInitialNavigation
            && await workspace.TryPrimeDeferredWorkspaceLayoutAsync(cancellationToken).ConfigureAwait(false))
        {
            timer.Mark("primed-layout");
            return;
        }

        if (startMode == "last" && await workspace.TryRestoreWorkspaceLayoutAsync(cancellationToken).ConfigureAwait(false))
        {
            timer.Mark("restored-layout");
            return;
        }

        var startPath = workspace.ResolveStartPath();
        if (deferInitialNavigation)
        {
            workspace.LayoutsController.PrimeDeferredStartupNavigation(workspace, startPath);
            timer.Mark("primed-start-navigation", startPath);
            return;
        }

        await workspace.NavigatePaneAsync(PaneId.Primary, startPath, HistoryMode.Push, activate: false, cancellationToken)
            .ConfigureAwait(false);
        timer.Mark("navigated-start", startPath);
    }

    public void SelectPath(ExplorerWorkspace workspace, string? path, PaneId pane)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Pane(pane);
        var nextPane = workspace.DualPaneEnabled ? workspace.Normalize(pane) : PaneId.Primary;
        var paneChanged = workspace.ActivePane != nextPane;
        target.SelectedPath = path;
        workspace.SetActivePaneCore(nextPane);
        if (paneChanged)
        {
            workspace.NotifyChanged();
        }
    }

    public void SetFilterQuery(ExplorerWorkspace workspace, PaneId pane, string query)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane);
        var normalized = query ?? "";
        var existing = FilterQueryFor(workspace, target);
        var state = workspace.Pane(target);
        var selectionCleared = false;
        if (!string.IsNullOrEmpty(state.SelectedPath))
        {
            var visible = state.VisibleEntries(
                workspace.ShowHiddenFiles,
                normalized,
                workspace.Settings.KeepFoldersOnTop);
            if (!visible.Any(entry => PathRules.PathsEqual(entry.Path, state.SelectedPath)))
            {
                state.SelectedPath = null;
                selectionCleared = true;
            }
        }

        if (target == PaneId.Secondary)
        {
            _secondaryFilterQuery = normalized;
        }
        else
        {
            _primaryFilterQuery = normalized;
        }

        if (string.Equals(existing, normalized, StringComparison.Ordinal) && !selectionCleared)
        {
            return;
        }

        workspace.ForegroundScheduler.RecordActivity(ForegroundActivityKind.Typing);
        workspace.NotifyChanged();
    }

    public void SwapFilterQueries()
    {
        (_primaryFilterQuery, _secondaryFilterQuery) = (_secondaryFilterQuery, _primaryFilterQuery);
    }

    public string FilterQueryFor(ExplorerWorkspace workspace, PaneId pane)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Normalize(pane) == PaneId.Secondary ? _secondaryFilterQuery : _primaryFilterQuery;
    }

    public void ClearStatus(ExplorerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.SetStatusMessageCore(null);
        workspace.SetErrorMessageCore(null);
        workspace.SetFileOpenUnsupportedCore(false);
        workspace.NotifyChanged();
    }

    public void SelectPathForRefresh(ExplorerWorkspace workspace, PaneId pane, string path)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (workspace.Gate)
        {
            workspace.Pane(pane).SelectedPath = path;
        }
    }

    public void MarkPathSelectedAfterRefresh(
        ExplorerWorkspace workspace,
        PaneId pane,
        string path,
        string statusMessage)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (workspace.Gate)
        {
            var state = workspace.Pane(pane);
            if (state.Entries.Any(entry => PathRules.PathsEqual(entry.Path, path)))
            {
                state.SelectedPath = path;
            }

            workspace.SetStatusMessageCore(statusMessage);
            workspace.SetErrorMessageCore(null);
        }

        workspace.NotifyChanged();
    }

    public IReadOnlyList<FileEntry> VisibleEntriesFor(ExplorerWorkspace workspace, PaneId pane)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane);
        var state = workspace.Pane(target);
        var keepFoldersOnTop = workspace.Settings.KeepFoldersOnTop;
        var entries = state.VisibleEntries(
            workspace.ShowHiddenFiles,
            FilterQueryFor(workspace, target),
            keepFoldersOnTop);
        return workspace.TagsController.FilterEntries(entries);
    }

    public IReadOnlyList<FileEntry> VisibleViewportEntriesFor(
        ExplorerWorkspace workspace,
        PaneId pane,
        int progressiveViewportLimit = EntryPresentation.DefaultProgressiveViewportCount)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane);
        var state = workspace.Pane(target);
        var keepFoldersOnTop = workspace.Settings.KeepFoldersOnTop;
        var entries = state.VisibleViewportEntries(
            workspace.ShowHiddenFiles,
            FilterQueryFor(workspace, target),
            keepFoldersOnTop,
            progressiveViewportLimit);
        return workspace.TagsController.FilterEntries(entries);
    }

    public FileEntry? MatchTypeAhead(ExplorerWorkspace workspace, char character)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.ForegroundScheduler.RecordActivity(ForegroundActivityKind.Typing);
        var buffer = workspace.TypeAheadBuffer.Append(character, TimeSpan.FromSeconds(1));
        var visible = VisibleEntriesFor(workspace, workspace.ActivePane);
        var index = TypeAhead.MatchIndex(visible, buffer);
        return index >= 0 ? visible[index] : null;
    }
}
