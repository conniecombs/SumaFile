using CommunityToolkit.Mvvm.ComponentModel;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// ViewModel for the toolbar / navigation bar state. Tracks which toolbar buttons
/// should be enabled/visible based on workspace navigation state.
/// </summary>
public sealed partial class ToolbarViewModel : ObservableObject
{
    private readonly ExplorerWorkspace _workspace;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _canGoUp;

    [ObservableProperty]
    private bool _isDualPaneEnabled;

    [ObservableProperty]
    private string _primaryPath = "";

    [ObservableProperty]
    private string _secondaryPath = "";

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private PaneId _activePane = PaneId.Primary;

    public ToolbarViewModel(ExplorerWorkspace workspace)
    {
        _workspace = workspace;
    }

    /// <summary>
    /// Refreshes toolbar state from the current workspace state.
    /// Called during the SyncFromWorkspace pass.
    /// </summary>
    public void SyncFromWorkspace()
    {
        var activeNavigationPane = _workspace.Pane(_workspace.Normalize(_workspace.ActivePane));
        CanGoBack = activeNavigationPane.CanGoBack;
        CanGoForward = activeNavigationPane.CanGoForward;
        CanGoUp = activeNavigationPane.CanGoUp;
        IsDualPaneEnabled = _workspace.DualPaneEnabled;
        PrimaryPath = _workspace.Primary.Path;
        SecondaryPath = _workspace.Secondary.Path;
        ActivePane = _workspace.Normalize(_workspace.ActivePane);
    }

    /// <summary>
    /// Updates status-bar copy from the active pane and selection snapshot.
    /// </summary>
    public void UpdateStatusBar(
        int visibleCount,
        IReadOnlyList<FileEntry> selectedEntries,
        int? searchResultCount,
        string? searchQuery)
    {
        var active = _workspace.Active;
        var count = searchResultCount ?? visibleCount;
        var snapshot = StatusBarFormatter.Format(
            count,
            selectedEntries,
            active.Path,
            _workspace.ActivePaneLabel,
            listingInProgress: active.ListingInProgress,
            isEmpty: count == 0 && !active.ListingInProgress && searchResultCount is null);

        CountText = searchResultCount is null
            ? snapshot.Combined
            : (count == 1 ? "1 search result" : $"{count} search results");
        if (searchResultCount is not null && !string.IsNullOrEmpty(_workspace.ActivePaneLabel))
        {
            CountText = $"{_workspace.ActivePaneLabel} · {CountText}";
        }

        StatusText = ResolveStatusText(active, count, searchResultCount, searchQuery);
    }

    public void SetCountText(string text)
    {
        CountText = text;
    }

    public void SetStatusText(string text)
    {
        StatusText = text;
    }

    private string ResolveStatusText(
        ExplorerPane active,
        int count,
        int? searchResultCount,
        string? searchQuery)
    {
        if (active.ListingInProgress && count == 0)
        {
            return "Loading…";
        }

        if (!string.IsNullOrEmpty(_workspace.ErrorMessage))
        {
            return _workspace.ErrorMessage;
        }

        if (searchResultCount is not null)
        {
            return string.IsNullOrWhiteSpace(searchQuery)
                ? "Search results"
                : $"Search results for \"{searchQuery.Trim()}\"";
        }

        if (!string.IsNullOrEmpty(_workspace.StatusMessage))
        {
            return _workspace.StatusMessage;
        }

        return active.Path;
    }
}
