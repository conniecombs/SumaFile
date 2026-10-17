namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{
    public bool CanReopenClosedTab => _panesTabs.CanReopenClosedTab(this);

    public Task ToggleDualPaneAsync(CancellationToken cancellationToken = default) =>
        _panesTabs.ToggleDualPaneAsync(this, cancellationToken);

    public Task CloseFilePaneAsync(PaneId pane, CancellationToken cancellationToken = default) =>
        _panesTabs.CloseFilePaneAsync(this, pane, cancellationToken);

    public void SwapFilePanes() =>
        _panesTabs.SwapFilePanes(this);

    public void ActivatePane(PaneId pane) =>
        _panesTabs.ActivatePane(this, pane);

    public void SwitchActivePane() =>
        _panesTabs.SwitchActivePane(this);

    public Task FocusSecondaryAsync(CancellationToken cancellationToken = default) =>
        _panesTabs.FocusSecondaryAsync(this, cancellationToken);

    public Task OpenNewTabAsync(PaneId? pane = null, string? path = null, CancellationToken cancellationToken = default) =>
        _panesTabs.OpenNewTabAsync(this, pane, path, cancellationToken);

    public Task SwitchToTabAsync(string tabId, PaneId pane, CancellationToken cancellationToken = default) =>
        _panesTabs.SwitchToTabAsync(this, tabId, pane, cancellationToken);

    public Task CloseTabAsync(string tabId, PaneId pane, CancellationToken cancellationToken = default) =>
        _panesTabs.CloseTabAsync(this, tabId, pane, cancellationToken);

    public Task ReopenClosedTabAsync(CancellationToken cancellationToken = default) =>
        _panesTabs.ReopenClosedTabAsync(this, cancellationToken);

    public Task SwitchTabByAsync(int delta, CancellationToken cancellationToken = default) =>
        _panesTabs.SwitchTabByAsync(this, delta, cancellationToken);

    public Task SwitchToTabAtAsync(int oneBasedIndex, CancellationToken cancellationToken = default) =>
        _panesTabs.SwitchToTabAtAsync(this, oneBasedIndex, cancellationToken);

    public Task OpenInOtherPaneAsync(string path, bool isDirectory, CancellationToken cancellationToken = default) =>
        _panesTabs.OpenInOtherPaneAsync(this, path, isDirectory, cancellationToken);
}
