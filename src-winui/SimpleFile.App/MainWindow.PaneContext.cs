using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private void InitializePaneContextBars()
    {
        PrimaryPane.ContextBar.ClearFilter += (_, _) => ClearPaneFilter(PaneId.Primary);
        SecondaryPane.ContextBar.ClearFilter += (_, _) => ClearPaneFilter(PaneId.Secondary);
        PrimaryPane.ContextBar.ReturnToFolder += async (_, _) => await ReturnToFolderAsync(PaneId.Primary);
        SecondaryPane.ContextBar.ReturnToFolder += async (_, _) => await ReturnToFolderAsync(PaneId.Secondary);
    }

    private void UpdatePaneContextBars()
    {
        if (_workspace is null) return;
        UpdatePaneContextBar(PrimaryPane.ContextBar, PaneId.Primary, PrimaryFiles.Count);
        UpdatePaneContextBar(SecondaryPane.ContextBar, PaneId.Secondary, SecondaryFiles.Count);
    }

    private void UpdatePaneContextBar(PaneContextBar bar, PaneId pane, int count)
    {
        var searching = _search?.IsActiveForPane(pane) == true;
        bar.Update(_workspace!.FilterQueryFor(pane), count, _workspace.Pane(pane).Entries.Count,
            searching, _search?.ExecutedQuery ?? "", _search?.Root ?? "", searching && _search!.CanCancel);
    }

    private void ClearPaneFilter(PaneId pane)
    {
        _workspace?.SetFilterQuery(pane, "");
        SyncFromWorkspace();
    }

    private async Task ReturnToFolderAsync(PaneId pane)
    {
        await RunUiActionAsync("Return to folder", async () =>
        {
            if (_search?.IsActiveForPane(pane) != true) return;
            await _search.CancelActiveAsync();
            _search.ClearState();
            SyncFromWorkspace();
        });
    }
}
