using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{
    public string FilterQuery => FilterQueryFor(ActivePane);
    public string FileListView => ViewFor(ActivePane);
    public int FileListIconSize => IconSizeFor(ActivePane);

    public Task InitializeAsync(
        CancellationToken cancellationToken = default,
        bool deferInitialNavigation = false) =>
        _selection.InitializeAsync(this, cancellationToken, deferInitialNavigation);

    public Task RunDeferredStartupNavigationAsync(CancellationToken cancellationToken = default) =>
        _layouts.RunDeferredStartupNavigationAsync(this, cancellationToken);

    public void SelectPath(string? path) =>
        SelectPath(path, ActivePane);

    public void SelectPath(string? path, PaneId pane) =>
        _selection.SelectPath(this, path, pane);

    public void SetFilterQuery(string query) =>
        SetFilterQuery(ActivePane, query);

    public void SetFilterQuery(PaneId pane, string query) =>
        _selection.SetFilterQuery(this, pane, query);

    public void ClearStatus() =>
        _selection.ClearStatus(this);

    public ForegroundTaskScheduler ForegroundScheduler { get; } = ForegroundTaskScheduler.Shared;

    public IReadOnlyList<FileEntry> VisibleEntriesFor(PaneId pane) =>
        _selection.VisibleEntriesFor(this, pane);

    public IReadOnlyList<FileEntry> VisibleViewportEntriesFor(
        PaneId pane,
        int progressiveViewportLimit = EntryPresentation.DefaultProgressiveViewportCount) =>
        _selection.VisibleViewportEntriesFor(this, pane, progressiveViewportLimit);

    public string FilterQueryFor(PaneId pane) =>
        _selection.FilterQueryFor(this, pane);

    public FileEntry? MatchTypeAhead(char character) =>
        _selection.MatchTypeAhead(this, character);
}
