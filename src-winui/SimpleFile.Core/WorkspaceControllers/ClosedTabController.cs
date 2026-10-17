namespace SimpleFile.Core;

internal sealed class ClosedTabController
{
    public const int Limit = 20;
    private readonly List<ClosedTabEntry> _closedTabs = [];

    public int Count => _closedTabs.Count;
    public bool CanReopen => _closedTabs.Count > 0;

    public void Remember(PaneId pane, FileTab tab, int index)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (string.IsNullOrWhiteSpace(tab.Path))
        {
            return;
        }

        _closedTabs.Add(new ClosedTabEntry(pane, tab.Clone(), Math.Max(0, index)));
        while (_closedTabs.Count > Limit)
        {
            _closedTabs.RemoveAt(0);
        }
    }

    public bool TryPop(out ClosedTabEntry? entry)
    {
        if (_closedTabs.Count == 0)
        {
            entry = null;
            return false;
        }

        var lastIndex = _closedTabs.Count - 1;
        entry = _closedTabs[lastIndex];
        _closedTabs.RemoveAt(lastIndex);
        return true;
    }

    public static FileTab RestoreIntoPane(ExplorerPane state, ClosedTabEntry closed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(closed);

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
        return tab;
    }
}

internal sealed record ClosedTabEntry(PaneId Pane, FileTab Tab, int Index);
