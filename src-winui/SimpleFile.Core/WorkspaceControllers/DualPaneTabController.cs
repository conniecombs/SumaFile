namespace SimpleFile.Core;

internal sealed class DualPaneTabController
{
    public bool CanReopenClosedTab(ExplorerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (workspace.Gate)
        {
            return workspace.ClosedTabsController.CanReopen;
        }
    }

    public async Task ToggleDualPaneAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.DualPaneEnabled)
        {
            workspace.SetDualPaneEnabledCore(false);
            workspace.SetActivePaneCore(PaneId.Primary);
            workspace.NotifyChanged();
            return;
        }

        workspace.SetDualPaneEnabledCore(true);
        if (string.IsNullOrEmpty(workspace.Secondary.Path))
        {
            await workspace.NavigatePaneAsync(
                    PaneId.Secondary,
                    workspace.Primary.Path,
                    HistoryMode.ReplaceCurrent,
                    activate: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            lock (workspace.Gate)
            {
                workspace.Secondary.EnsureActiveTab(workspace.Primary.Path);
            }
        }

        workspace.SetActivePaneCore(PaneId.Primary);
        workspace.NotifyChanged();
    }

    public Task CloseFilePaneAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.DualPaneEnabled)
        {
            return Task.CompletedTask;
        }

        if (workspace.Normalize(pane) == PaneId.Primary)
        {
            SwapFilePanes(workspace);
        }

        workspace.SetDualPaneEnabledCore(false);
        workspace.SetActivePaneCore(PaneId.Primary);
        workspace.NotifyChanged();
        return Task.CompletedTask;
    }

    public void SwapFilePanes(ExplorerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (workspace.Gate)
        {
            workspace.Primary.NextNavigationToken();
            workspace.Secondary.NextNavigationToken();
            workspace.Primary.SwapContents(workspace.Secondary);
            workspace.SelectionController.SwapFilterQueries();
        }
    }

    public void ActivatePane(ExplorerWorkspace workspace, PaneId pane)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var next = workspace.DualPaneEnabled && pane == PaneId.Secondary ? PaneId.Secondary : PaneId.Primary;
        if (workspace.ActivePane == next)
        {
            return;
        }

        workspace.SetActivePaneCore(next);
        workspace.NotifyChanged();
    }

    public void SwitchActivePane(ExplorerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.DualPaneEnabled)
        {
            return;
        }

        ActivatePane(workspace, workspace.ActivePane == PaneId.Primary ? PaneId.Secondary : PaneId.Primary);
    }

    public async Task FocusSecondaryAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.DualPaneEnabled)
        {
            await ToggleDualPaneAsync(workspace, cancellationToken).ConfigureAwait(false);
        }

        ActivatePane(workspace, PaneId.Secondary);
    }

    public async Task OpenNewTabAsync(
        ExplorerWorkspace workspace,
        PaneId? pane = null,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane ?? workspace.ActivePane);
        var state = workspace.Pane(target);
        var targetPath = path ?? state.Path;
        if (string.IsNullOrEmpty(targetPath))
        {
            targetPath = workspace.HomePath;
        }

        if (string.IsNullOrEmpty(targetPath))
        {
            return;
        }

        var tab = ExplorerPane.CreateTab(targetPath);
        lock (workspace.Gate)
        {
            state.Tabs.Add(tab);
            state.ApplyTabHistory(tab);
        }

        await workspace.NavigatePaneAsync(
                target,
                targetPath,
                HistoryMode.ReplaceCurrent,
                activate: workspace.DualPaneEnabled,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SwitchToTabAsync(
        ExplorerWorkspace workspace,
        string tabId,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane);
        FileTab? tab;
        lock (workspace.Gate)
        {
            tab = workspace.Pane(target).Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            if (tab is null)
            {
                return;
            }

            workspace.Pane(target).ApplyTabHistory(tab);
        }

        await workspace.NavigatePaneAsync(
                target,
                tab.Path,
                HistoryMode.None,
                activate: workspace.DualPaneEnabled,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CloseTabAsync(
        ExplorerWorkspace workspace,
        string tabId,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane);
        string? nextId = null;
        string? homeFallback = null;
        PaneId? paneToClose = null;
        lock (workspace.Gate)
        {
            var state = workspace.Pane(target);
            var closingIndex = state.Tabs.FindIndex(tab => tab.Id == tabId);
            if (closingIndex < 0)
            {
                return;
            }

            workspace.ClosedTabsController.Remember(target, state.Tabs[closingIndex], closingIndex);
            state.Tabs.RemoveAt(closingIndex);
            if (state.Tabs.Count == 0)
            {
                state.ActiveTabId = null;
                if (workspace.DualPaneEnabled)
                {
                    paneToClose = target;
                }
                else
                {
                    homeFallback = workspace.HomePath;
                    if (string.IsNullOrEmpty(homeFallback))
                    {
                        homeFallback = state.Path;
                    }

                    if (string.IsNullOrEmpty(homeFallback))
                    {
                        homeFallback = workspace.Primary.Path;
                    }
                }
            }
            else if (state.ActiveTabId == tabId)
            {
                var next = state.Tabs[Math.Min(closingIndex, state.Tabs.Count - 1)];
                nextId = next.Id;
            }
        }

        if (paneToClose is not null)
        {
            await CloseFilePaneAsync(workspace, paneToClose.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (homeFallback is not null)
        {
            await OpenNewTabAsync(workspace, target, homeFallback, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (nextId is not null)
        {
            await SwitchToTabAsync(workspace, nextId, target, cancellationToken).ConfigureAwait(false);
            return;
        }

        workspace.NotifyChanged();
    }

    public async Task ReopenClosedTabAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ClosedTabEntry? closed;
        lock (workspace.Gate)
        {
            if (!workspace.ClosedTabsController.TryPop(out closed) || closed is null)
            {
                return;
            }
        }

        if (closed.Pane == PaneId.Secondary && !workspace.DualPaneEnabled)
        {
            await ToggleDualPaneAsync(workspace, cancellationToken).ConfigureAwait(false);
        }

        var target = workspace.Normalize(closed.Pane);
        string targetPath;
        lock (workspace.Gate)
        {
            var state = workspace.Pane(target);
            var tab = ClosedTabController.RestoreIntoPane(state, closed);
            targetPath = tab.Path;
        }

        await workspace.NavigatePaneAsync(
                target,
                targetPath,
                HistoryMode.None,
                activate: workspace.DualPaneEnabled,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SwitchTabByAsync(
        ExplorerWorkspace workspace,
        int delta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var state = workspace.Active;
        if (state.Tabs.Count == 0)
        {
            return Task.CompletedTask;
        }

        var activeIndex = Math.Max(0, state.Tabs.FindIndex(tab => tab.Id == state.ActiveTabId));
        var next = state.Tabs[(activeIndex + delta % state.Tabs.Count + state.Tabs.Count) % state.Tabs.Count];
        return SwitchToTabAsync(workspace, next.Id, workspace.ActivePane, cancellationToken);
    }

    public Task SwitchToTabAtAsync(
        ExplorerWorkspace workspace,
        int oneBasedIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var state = workspace.Active;
        if (state.Tabs.Count == 0 || oneBasedIndex < 1)
        {
            return Task.CompletedTask;
        }

        var index = oneBasedIndex >= 9
            ? state.Tabs.Count - 1
            : oneBasedIndex - 1;
        if (index < 0 || index >= state.Tabs.Count)
        {
            return Task.CompletedTask;
        }

        return SwitchToTabAsync(workspace, state.Tabs[index].Id, workspace.ActivePane, cancellationToken);
    }

    public async Task OpenInOtherPaneAsync(
        ExplorerWorkspace workspace,
        string path,
        bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!workspace.DualPaneEnabled)
        {
            await ToggleDualPaneAsync(workspace, cancellationToken).ConfigureAwait(false);
        }

        var other = workspace.ActivePane == PaneId.Primary ? PaneId.Secondary : PaneId.Primary;
        var destination = isDirectory ? path : PathRules.GetParentPath(path) ?? path;
        await workspace.NavigatePaneAsync(other, destination, HistoryMode.Push, activate: false, cancellationToken)
            .ConfigureAwait(false);
    }
}
