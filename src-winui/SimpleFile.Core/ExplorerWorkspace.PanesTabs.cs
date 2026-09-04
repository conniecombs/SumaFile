using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{

    public async Task ToggleDualPaneAsync(CancellationToken cancellationToken = default)
    {
        if (DualPaneEnabled)
        {
            DualPaneEnabled = false;
            ActivePane = PaneId.Primary;
            RaiseChanged();
            return;
        }

        DualPaneEnabled = true;
        if (string.IsNullOrEmpty(Secondary.Path))
        {
            await NavigatePaneAsync(
                    PaneId.Secondary,
                    Primary.Path,
                    HistoryMode.ReplaceCurrent,
                    activate: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            lock (_gate)
            {
                Secondary.EnsureActiveTab(Primary.Path);
            }
        }

        ActivePane = PaneId.Primary;
        RaiseChanged();
    }

    public Task CloseFilePaneAsync(PaneId pane, CancellationToken cancellationToken = default)
    {
        if (!DualPaneEnabled)
        {
            return Task.CompletedTask;
        }

        if (Normalize(pane) == PaneId.Primary)
        {
            SwapFilePanes();
        }

        DualPaneEnabled = false;
        ActivePane = PaneId.Primary;
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void SwapFilePanes()
    {
        Primary.SwapContents(Secondary);
        (_primaryFilterQuery, _secondaryFilterQuery) = (_secondaryFilterQuery, _primaryFilterQuery);
    }

    public void ActivatePane(PaneId pane)
    {
        var next = DualPaneEnabled && pane == PaneId.Secondary ? PaneId.Secondary : PaneId.Primary;
        if (ActivePane == next)
        {
            return;
        }

        ActivePane = next;
        RaiseChanged();
    }

    public void SwitchActivePane()
    {
        if (!DualPaneEnabled)
        {
            return;
        }

        ActivatePane(ActivePane == PaneId.Primary ? PaneId.Secondary : PaneId.Primary);
    }

    public async Task FocusSecondaryAsync(CancellationToken cancellationToken = default)
    {
        if (!DualPaneEnabled)
        {
            await ToggleDualPaneAsync(cancellationToken).ConfigureAwait(false);
        }

        ActivatePane(PaneId.Secondary);
    }

    public async Task OpenNewTabAsync(PaneId? pane = null, string? path = null, CancellationToken cancellationToken = default)
    {
        var target = Normalize(pane ?? ActivePane);
        var state = Pane(target);
        var targetPath = path ?? state.Path;
        if (string.IsNullOrEmpty(targetPath))
        {
            targetPath = HomePath;
        }

        if (string.IsNullOrEmpty(targetPath))
        {
            return;
        }

        var tab = ExplorerPane.CreateTab(targetPath);
        lock (_gate)
        {
            state.Tabs.Add(tab);
            state.ApplyTabHistory(tab);
        }

        await NavigatePaneAsync(target, targetPath, HistoryMode.ReplaceCurrent, activate: DualPaneEnabled, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SwitchToTabAsync(string tabId, PaneId pane, CancellationToken cancellationToken = default)
    {
        var target = Normalize(pane);
        FileTab? tab;
        lock (_gate)
        {
            tab = Pane(target).Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            if (tab is null)
            {
                return;
            }

            Pane(target).ApplyTabHistory(tab);
        }

        await NavigatePaneAsync(target, tab.Path, HistoryMode.None, activate: DualPaneEnabled, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CloseTabAsync(string tabId, PaneId pane, CancellationToken cancellationToken = default)
    {
        var target = Normalize(pane);
        string? nextId = null;
        string? homeFallback = null;
        PaneId? paneToClose = null;
        lock (_gate)
        {
            var state = Pane(target);
            var closingIndex = state.Tabs.FindIndex(tab => tab.Id == tabId);
            if (closingIndex < 0)
            {
                return;
            }

            RememberClosedTabLocked(target, state.Tabs[closingIndex], closingIndex);
            state.Tabs.RemoveAt(closingIndex);
            if (state.Tabs.Count == 0)
            {
                state.ActiveTabId = null;
                if (DualPaneEnabled)
                {
                    paneToClose = target;
                }
                else
                {
                    homeFallback = HomePath;
                    if (string.IsNullOrEmpty(homeFallback))
                    {
                        homeFallback = state.Path;
                    }

                    if (string.IsNullOrEmpty(homeFallback))
                    {
                        homeFallback = Primary.Path;
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
            await CloseFilePaneAsync(paneToClose.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (homeFallback is not null)
        {
            await OpenNewTabAsync(target, homeFallback, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (nextId is not null)
        {
            await SwitchToTabAsync(nextId, target, cancellationToken).ConfigureAwait(false);
            return;
        }

        RaiseChanged();
    }

    public async Task ReopenClosedTabAsync(CancellationToken cancellationToken = default)
    {
        ClosedFileTab? closed;
        lock (_gate)
        {
            if (_closedTabs.Count == 0)
            {
                return;
            }

            var lastIndex = _closedTabs.Count - 1;
            closed = _closedTabs[lastIndex];
            _closedTabs.RemoveAt(lastIndex);
        }

        if (closed.Pane == PaneId.Secondary && !DualPaneEnabled)
        {
            await ToggleDualPaneAsync(cancellationToken).ConfigureAwait(false);
        }

        var target = Normalize(closed.Pane);
        string targetPath;
        lock (_gate)
        {
            var state = Pane(target);
            var tab = state.Tabs.FirstOrDefault(candidate =>
                PathRules.PathsEqual(candidate.Path, closed.Tab.Path));
            if (tab is null)
            {
                tab = closed.Tab.Clone();
                if (string.IsNullOrWhiteSpace(tab.Id)
                    || state.Tabs.Any(candidate => string.Equals(candidate.Id, tab.Id, StringComparison.Ordinal)))
                {
                    tab.Id = ExplorerPane.CreateTab(tab.Path).Id;
                }

                if (string.IsNullOrWhiteSpace(tab.Title))
                {
                    tab.Title = PathRules.Basename(tab.Path);
                }

                if (tab.History.Count == 0)
                {
                    tab.History = [tab.Path];
                    tab.HistoryIndex = 0;
                }

                var insertIndex = Math.Clamp(closed.Index, 0, state.Tabs.Count);
                state.Tabs.Insert(insertIndex, tab);
            }

            state.ApplyTabHistory(tab);
            targetPath = tab.Path;
        }

        await NavigatePaneAsync(target, targetPath, HistoryMode.None, activate: DualPaneEnabled, cancellationToken)
            .ConfigureAwait(false);
    }

    private void RememberClosedTabLocked(PaneId pane, FileTab tab, int index)
    {
        if (string.IsNullOrWhiteSpace(tab.Path))
        {
            return;
        }

        _closedTabs.Add(new ClosedFileTab(pane, tab.Clone(), Math.Max(0, index)));
        while (_closedTabs.Count > ClosedTabLimit)
        {
            _closedTabs.RemoveAt(0);
        }
    }

    public Task SwitchTabByAsync(int delta, CancellationToken cancellationToken = default)
    {
        var state = Active;
        if (state.Tabs.Count == 0)
        {
            return Task.CompletedTask;
        }

        var activeIndex = Math.Max(0, state.Tabs.FindIndex(tab => tab.Id == state.ActiveTabId));
        var next = state.Tabs[(activeIndex + delta % state.Tabs.Count + state.Tabs.Count) % state.Tabs.Count];
        return SwitchToTabAsync(next.Id, ActivePane, cancellationToken);
    }

    public Task SwitchToTabAtAsync(int oneBasedIndex, CancellationToken cancellationToken = default)
    {
        var state = Active;
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

        return SwitchToTabAsync(state.Tabs[index].Id, ActivePane, cancellationToken);
    }

    public async Task OpenInOtherPaneAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!DualPaneEnabled)
        {
            await ToggleDualPaneAsync(cancellationToken).ConfigureAwait(false);
        }

        var other = ActivePane == PaneId.Primary ? PaneId.Secondary : PaneId.Primary;
        var destination = isDirectory ? path : PathRules.GetParentPath(path) ?? path;
        await NavigatePaneAsync(other, destination, HistoryMode.Push, activate: false, cancellationToken)
            .ConfigureAwait(false);
    }

}

