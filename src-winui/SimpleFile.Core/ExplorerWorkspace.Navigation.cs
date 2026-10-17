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
}
