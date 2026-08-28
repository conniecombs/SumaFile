using CommunityToolkit.Mvvm.ComponentModel;

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
    /// Updates the item count text and optional status message.
    /// </summary>
    public void UpdateCounts(int itemCount, string statusMessage)
    {
        CountText = itemCount == 1 ? "1 item" : $"{itemCount} items";
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            StatusText = statusMessage;
        }
    }
}
