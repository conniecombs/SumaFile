using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{

    public string FilterQuery => FilterQueryFor(ActivePane);
    public string FileListView => ViewFor(ActivePane);
    public int FileListIconSize => IconSizeFor(ActivePane);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        HomePath = await _backend.GetHomeDirAsync(cancellationToken).ConfigureAwait(false);
        await RefreshDrivesAsync(quiet: true, cancellationToken).ConfigureAwait(false);

        await LoadSmartFoldersAsync(cancellationToken).ConfigureAwait(false);
        await LoadTagsAsync(cancellationToken).ConfigureAwait(false);
        await LoadUiSettingsAsync(cancellationToken).ConfigureAwait(false);

        var startMode = UiSettings.NormalizeStartLocation(Settings.StartLocation);
        if (startMode == "last" && await TryRestoreWorkspaceLayoutAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var startPath = ResolveStartPath();
        await NavigatePaneAsync(PaneId.Primary, startPath, HistoryMode.Push, activate: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public void SelectPath(string? path)
    {
        SelectPath(path, ActivePane);
    }

    public void SelectPath(string? path, PaneId pane)
    {
        var target = Pane(pane);
        var nextPane = DualPaneEnabled ? Normalize(pane) : PaneId.Primary;
        var paneChanged = ActivePane != nextPane;
        target.SelectedPath = path;
        ActivePane = nextPane;
        if (paneChanged)
        {
            RaiseChanged();
        }
    }

    public void SetFilterQuery(string query)
    {
        SetFilterQuery(ActivePane, query);
    }

    public void SetFilterQuery(PaneId pane, string query)
    {
        var target = Normalize(pane);
        var normalized = query ?? "";
        var existing = FilterQueryFor(target);
        var state = Pane(target);
        var selectionCleared = false;
        if (!string.IsNullOrEmpty(state.SelectedPath))
        {
            var visible = state.VisibleEntries(ShowHiddenFiles, normalized, Settings.KeepFoldersOnTop);
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

        RaiseChanged();
    }

    public void ClearStatus()
    {
        StatusMessage = null;
        ErrorMessage = null;
        FileOpenUnsupported = false;
        RaiseChanged();
    }

    private void SelectPathForRefresh(PaneId pane, string path)
    {
        lock (_gate)
        {
            Pane(pane).SelectedPath = path;
        }
    }

    private void MarkPathSelectedAfterRefresh(PaneId pane, string path, string statusMessage)
    {
        lock (_gate)
        {
            var state = Pane(pane);
            if (state.Entries.Any(entry => PathRules.PathsEqual(entry.Path, path)))
            {
                state.SelectedPath = path;
            }

            StatusMessage = statusMessage;
            ErrorMessage = null;
        }

        RaiseChanged();
    }

    public IReadOnlyList<FileEntry> VisibleEntriesFor(PaneId pane)
    {
        var target = Normalize(pane);
        var state = Pane(target);
        var keepFoldersOnTop = Settings.KeepFoldersOnTop;
        var canUsePresorted = WorkspaceNavigation.CanUsePresortedEntries(state, keepFoldersOnTop);
        var entries = canUsePresorted
            ? EntryPresentation.VisibleEntriesPreSorted(state.Entries, FilterQueryFor(target), ShowHiddenFiles)
            : state.VisibleEntries(ShowHiddenFiles, FilterQueryFor(target), keepFoldersOnTop);

        if (ActiveTagFilter is long tagId)
        {
            var tagged = FileTags
                .Where(pair => pair.Value.Id == tagId)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries = entries.Where(entry => tagged.Contains(entry.Path)).ToList();
        }

        return entries;
    }

    public string FilterQueryFor(PaneId pane)
    {
        return Normalize(pane) == PaneId.Secondary ? _secondaryFilterQuery : _primaryFilterQuery;
    }

    public FileEntry? MatchTypeAhead(char character)
    {
        var buffer = TypeAheadBuffer.Append(character, TimeSpan.FromSeconds(1));
        var index = TypeAhead.MatchIndex(VisibleEntriesFor(ActivePane), buffer);
        return index >= 0 ? VisibleEntriesFor(ActivePane)[index] : null;
    }

}

