using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{
    public IReadOnlyList<DriveInfo> Drives => _drives.Drives;
    public DriveInfo? PendingReconnect => _drives.PendingReconnect;
    public PaneId PendingReconnectPane => _drives.PendingReconnectPane;

    public string ResolveStartPath() =>
        WorkspaceNavigation.ResolveStartPath(Settings, HomePath, Primary.Path);

    public Task RefreshDrivesAsync(bool quiet = false, CancellationToken cancellationToken = default) =>
        _navigation.RefreshDrivesAsync(this, quiet, cancellationToken, lightweight: false);

    public Task RefreshDriveAsync(
        string path,
        bool quiet = false,
        CancellationToken cancellationToken = default) =>
        _navigation.RefreshDriveAsync(this, path, quiet, cancellationToken);

    public Task NavigateToAsync(
        string path,
        HistoryMode historyMode = HistoryMode.Push,
        CancellationToken cancellationToken = default) =>
        NavigatePaneAsync(ActivePane, path, historyMode, activate: false, cancellationToken);

    public Task NavigatePaneAsync(
        PaneId pane,
        string path,
        HistoryMode historyMode = HistoryMode.Push,
        bool activate = true,
        CancellationToken cancellationToken = default) =>
        _navigation.NavigatePaneAsync(this, pane, path, historyMode, activate, cancellationToken);

    public Task GoBackAsync(CancellationToken cancellationToken = default) =>
        GoBackAsync(ActivePane, cancellationToken);

    public Task GoForwardAsync(CancellationToken cancellationToken = default) =>
        GoForwardAsync(ActivePane, cancellationToken);

    public Task GoUpAsync(CancellationToken cancellationToken = default) =>
        GoUpAsync(ActivePane, cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(ActivePane, cancellationToken);

    public Task RefreshAffectedPanesAsync(
        IEnumerable<string?> affectedPaths,
        CancellationToken cancellationToken = default) =>
        _navigation.RefreshAffectedPanesAsync(this, affectedPaths, cancellationToken);

    /// <summary>
    /// Re-lists the visible panes whose path is, or is inside, a file ending with one of the extensions: called
    /// when plugin archive engines for those extensions were registered or unregistered.
    /// </summary>
    public Task RefreshArchivePanesAsync(
        IReadOnlyCollection<string> extensions,
        CancellationToken cancellationToken = default) =>
        _navigation.RefreshArchivePanesAsync(this, extensions, cancellationToken);

    public IReadOnlyList<PaneId> AffectedVisiblePanes(IEnumerable<string?> affectedPaths) =>
        _navigation.AffectedVisiblePanes(this, affectedPaths);

    public Task NavigateSpecialAsync(string command, CancellationToken cancellationToken = default) =>
        NavigateSpecialAsync(command, SidebarTarget, cancellationToken);

    public Task OpenEntryAsync(FileEntry entry, CancellationToken cancellationToken = default) =>
        OpenPathAsync(entry.Path, entry.IsDir, ActivePane, cancellationToken);

    public Task OpenPathAsync(
        string path,
        bool? isDirectory = null,
        CancellationToken cancellationToken = default) =>
        OpenPathAsync(path, isDirectory, ActivePane, cancellationToken);

    public Task RetryPendingDriveAsync(CancellationToken cancellationToken = default) =>
        _navigation.RetryPendingDriveAsync(this, cancellationToken);

    public void CancelPendingReconnect() =>
        _navigation.CancelPendingReconnect(this);

    public Task GoBackAsync(PaneId pane, CancellationToken cancellationToken = default) =>
        _navigation.GoBackAsync(this, pane, cancellationToken);

    public Task GoForwardAsync(PaneId pane, CancellationToken cancellationToken = default) =>
        _navigation.GoForwardAsync(this, pane, cancellationToken);

    public Task GoUpAsync(PaneId pane, CancellationToken cancellationToken = default) =>
        _navigation.GoUpAsync(this, pane, cancellationToken);

    public Task RefreshAsync(PaneId pane, CancellationToken cancellationToken = default) =>
        _navigation.RefreshAsync(this, pane, cancellationToken);

    public Task NavigateSpecialAsync(string command, PaneId pane, CancellationToken cancellationToken = default) =>
        _navigation.NavigateSpecialAsync(this, command, pane, cancellationToken);

    public Task OpenEntryAsync(FileEntry entry, PaneId pane, CancellationToken cancellationToken = default) =>
        OpenPathAsync(entry.Path, entry.IsDir, pane, cancellationToken);

    public Task OpenPathAsync(
        string path,
        bool? isDirectory,
        PaneId pane,
        CancellationToken cancellationToken = default) =>
        _navigation.OpenPathAsync(this, path, isDirectory, pane, cancellationToken);

    /// <summary>
    /// Decorates every shown pane whose listing is published again, without listing it again: after startup becomes
    /// ready, after the decorating plugins change, and when a plugin asks (<c>IPluginDecorations.Invalidate</c>, a
    /// setting write). A pane still listing is skipped, because its publish checks
    /// <see cref="RowDecorationCoordinator.Generation"/> itself. The provider calls start on the thread pool, so the
    /// caller is never blocked and never runs plugin code.
    /// </summary>
    public Task InvalidateDecorationsAsync() =>
        DualPaneEnabled
            ? Task.WhenAll(InvalidateDecorationsAsync(PaneId.Primary), InvalidateDecorationsAsync(PaneId.Secondary))
            : InvalidateDecorationsAsync(PaneId.Primary);

    /// <summary>
    /// Decorates one pane again under a new request number. The result within the budget applies at once: a plugin
    /// that answered (or answered nothing) replaces its part, a plugin that left the set loses its part, and a plugin
    /// still pending keeps its shown part, so a slow provider never blanks the badges and folder flags in between. The
    /// final merge follows once the pending plugins settle, whatever they answer.
    /// </summary>
    internal async Task InvalidateDecorationsAsync(PaneId pane)
    {
        var state = PaneFor(pane);
        string path;
        bool isNetwork;
        List<string> paths;
        int navigationToken;
        long request;
        CancellationToken cancellation;
        lock (_gate)
        {
            if (state.ListingInProgress || string.IsNullOrEmpty(state.Path))
            {
                return;
            }

            path = state.Path;
            isNetwork = state.PathIsNetwork;
            paths = state.Entries.Select(entry => entry.Path).ToList();
            navigationToken = state.NavigationToken;
            request = state.NextDecorationRequest();
            cancellation = state.DecorationCancellation;
        }

        var decorations = await RowDecorations.DecorateAsync(
                path,
                isNetwork,
                paths,
                late => ApplyLateDecorations(pane, navigationToken, request, path, late),
                cancellation)
            .ConfigureAwait(false);
        ApplyDecorations(pane, navigationToken, request, path, decorations, late: false);
    }

    /// <summary>
    /// Applies a decoration result that arrived after its listing was published. It applies only while the pane still
    /// shows that navigation's folder and no newer request's result was applied, whatever order results arrive in.
    /// </summary>
    internal void ApplyLateDecorations(
        PaneId pane,
        int navigationToken,
        long request,
        string folderPath,
        PaneDecorations decorations) =>
        ApplyDecorations(pane, navigationToken, request, folderPath, decorations, late: true);

    private void ApplyDecorations(
        PaneId pane,
        int navigationToken,
        long request,
        string folderPath,
        PaneDecorations decorations,
        bool late)
    {
        var state = PaneFor(pane);
        lock (_gate)
        {
            if (state.NavigationToken != navigationToken
                || !PathRules.PathsEqual(state.Path, folderPath)
                || !ShouldApplyDecorationsLocked(state, request, late))
            {
                return;
            }

            // A budget-cut result, and a late merge the late limit cut, keep each pending plugin's shown part.
            decorations = decorations.KeepingPendingFrom(state.Decorations);

            var unchanged = (decorations.HasNoDecorations && state.Decorations.HasNoDecorations)
                || decorations.HasSameContentAs(state.Decorations);
            state.AppliedDecorationRequest = request;
            state.AppliedDecorationLate = late;
            state.Decorations = decorations;
            if (unchanged)
            {
                return;
            }

            state.BumpContentVersion();
        }

        RaiseChanged();
    }

    // Results compare by (request, late): a newer request wins, and a request's late merge beats its own budget cut.
    internal static bool ShouldApplyDecorationsLocked(ExplorerPane state, long request, bool late) =>
        request > state.AppliedDecorationRequest
        || (request == state.AppliedDecorationRequest && late && !state.AppliedDecorationLate);

    /// <summary>
    /// Starts <see cref="InvalidateDecorationsAsync(PaneId)"/> without awaiting it, logging a failure through
    /// <see cref="RowDecorationCoordinator.Log"/>.
    /// </summary>
    internal void StartDecorationRefresh(PaneId pane)
    {
        _ = InvalidateDecorationsAsync(pane).ContinueWith(
            task => RowDecorations.Log?.Invoke($"[warn] Decorating the folder again failed: {task.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // The pane itself, never normalized: a result for the secondary pane must not land on the primary one while dual
    // pane is off.
    private ExplorerPane PaneFor(PaneId pane) => pane == PaneId.Secondary ? Secondary : Primary;
}
