using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// ViewModel encapsulating all search-related state and logic previously scattered
/// across MainWindow.xaml.cs, MainWindow.Commands.cs, and the workspace sync loop.
/// Exposes observable properties that can be data-bound from XAML via {x:Bind}.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly ExplorerWorkspace _workspace;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private PaneId _pane = PaneId.Primary;

    private string? _activeSearchId;
    private string? _searchRoot;
    private CancellationTokenSource? _searchCts;
    private int _searchCounter;
    private readonly List<SearchResult> _results = [];

    /// <summary>
    /// Raised when search results change and the host should update its file list.
    /// The event provides the current results snapshot for the active search pane.
    /// </summary>
    public event EventHandler<SearchResultsChangedEventArgs>? ResultsChanged;

    /// <summary>
    /// Raised when the search mode is cleared and the host should restore normal
    /// directory listing for the affected pane.
    /// </summary>
    public event EventHandler? Cleared;

    /// <summary>
    /// Raised when search work needs the host to surface a user-visible message.
    /// </summary>
    public event EventHandler<ViewModelMessageEventArgs>? MessageRequested;

    /// <summary>
    /// Current search results. Read-only snapshot for the host to render.
    /// </summary>
    public IReadOnlyList<SearchResult> Results => _results;

    public string? Root => _searchRoot;

    public int ResultCount => _results.Count;

    public SearchViewModel(ExplorerWorkspace workspace)
    {
        _workspace = workspace;
    }

    /// <summary>
    /// Returns true if the search is active on the given pane.
    /// Used by the sync loop to decide whether to show search results or directory entries.
    /// </summary>
    public bool IsActiveForPane(PaneId pane) => IsActive && Pane == pane;

    /// <summary>
    /// Checks whether the search root has drifted from the current pane path,
    /// and clears search state if so. Called during workspace sync.
    /// </summary>
    public bool CheckSearchRootDrift()
    {
        if (!IsActive || _searchRoot is null)
        {
            return false;
        }

        var currentPath = _workspace.Pane(Pane).Path;
        if (!string.Equals(currentPath, _searchRoot, StringComparison.OrdinalIgnoreCase))
        {
            ClearState();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Starts a search with the current query text. If the query is empty,
    /// cancels any active search and clears state.
    /// </summary>
    /// <param name="requestedPane">Which pane to search in; defaults to active pane.</param>
    /// <param name="dispatchToUi">Callback to dispatch batch updates to the UI thread.</param>
    public async Task StartAsync(
        PaneId? requestedPane,
        Action<Action> dispatchToUi)
    {
        var workspace = _workspace;
        if (workspace.FileOps is null)
        {
            return;
        }

        var pane = workspace.Normalize(requestedPane ?? workspace.ActivePane);
        workspace.ActivatePane(pane);

        if (string.IsNullOrWhiteSpace(Query))
        {
            await CancelActiveAsync();
            ClearState();
            return;
        }

        var root = workspace.Pane(pane).Path;
        var searchId = NextSearchId("search");
        var options = new SearchOptions
        {
            Query = Query.Trim(),
            SearchPath = root,
            CaseSensitive = false,
            IncludeHidden = false,
            MaxResults = 1000,
            MaxDepth = 10,
            SearchId = searchId,
            ContentSearch = false,
        };

        await RunAsync(
            workspace,
            pane,
            searchId,
            root,
            options,
            "Search",
            $"Searching {root}...",
            dispatchToUi);
    }

    /// <summary>
    /// Runs a smart folder search with pre-built options.
    /// </summary>
    public Task StartSmartFolderAsync(
        SmartFolder folder,
        Action<Action> dispatchToUi)
    {
        var pane = _workspace.ActivePane;
        return StartSmartFolderAsync(folder.SearchOptions, pane, dispatchToUi);
    }

    /// <summary>
    /// Runs a smart folder search with pre-built options.
    /// </summary>
    public async Task StartSmartFolderAsync(
        SearchOptions? template,
        PaneId pane,
        Action<Action> dispatchToUi)
    {
        var workspace = _workspace;
        if (workspace.FileOps is null)
        {
            return;
        }

        var root = template?.SearchPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = workspace.Active.Path;
        }

        var searchId = NextSearchId("search");
        var options = SearchOptionsFactory.ForRun(template, searchId, root);
        await RunAsync(
            workspace,
            pane,
            searchId,
            options.SearchPath,
            options,
            "Smart folder",
            "Searching smart folder...",
            dispatchToUi);
    }

    /// <summary>
    /// Cancels any running search by its backend search ID.
    /// </summary>
    public async Task CancelActiveAsync()
    {
        var searchId = _activeSearchId;
        _searchCts?.Cancel();
        if (string.IsNullOrEmpty(searchId) || _workspace.FileOps is null)
        {
            CanCancel = false;
            return;
        }

        try
        {
            await _workspace.FileOps.CancelSearchAsync(searchId);
            StatusText = "Search cancelled";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            MessageRequested?.Invoke(this, new ViewModelMessageEventArgs("Cancel search", exception.Message));
        }
        finally
        {
            _activeSearchId = null;
            CanCancel = false;
        }
    }

    /// <summary>
    /// Resets all search state without notifying the backend.
    /// </summary>
    public void ClearState(bool notifyHost = true)
    {
        IsActive = false;
        _searchRoot = null;
        _searchCts?.Cancel();
        _searchCts = null;
        _activeSearchId = null;
        _results.Clear();
        CanCancel = false;
        if (notifyHost)
        {
            Cleared?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RunAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        string searchId,
        string root,
        SearchOptions options,
        string errorTitle,
        string initialStatus,
        Action<Action> dispatchToUi)
    {
        await CancelActiveAsync();
        if (!ReferenceEquals(_workspace, workspace) || workspace.FileOps is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _activeSearchId = searchId;
        IsActive = true;
        Pane = pane;
        _searchRoot = root;
        _results.Clear();
        CanCancel = true;
        StatusText = initialStatus;
        RaiseResultsChanged();

        try
        {
            var results = await workspace.FileOps.SearchAsync(
                options,
                batch => dispatchToUi(() =>
                {
                    if (!string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _results.AddRange(batch);
                    StatusText = $"Searching... {_results.Count} result(s)";
                    RaiseResultsChanged();
                }),
                count => dispatchToUi(() =>
                {
                    if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
                    {
                        StatusText = $"Search complete: {count} result(s)";
                    }
                }),
                cts.Token);

            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                _results.Clear();
                _results.AddRange(results);
                StatusText = $"Search complete: {results.Length} result(s)";
                RaiseResultsChanged();
            }
        }
        catch (OperationCanceledException)
        {
            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                StatusText = "Search cancelled";
            }
        }
        catch (Exception exception)
        {
            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                MessageRequested?.Invoke(this, new ViewModelMessageEventArgs(errorTitle, exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
            }

            cts.Dispose();
            FinishRun(searchId);
        }
    }

    private void FinishRun(string searchId)
    {
        if (!string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
        {
            return;
        }

        _activeSearchId = null;
        CanCancel = false;
    }

    private string NextSearchId(string prefix) =>
        $"{prefix}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _searchCounter)}";

    private void RaiseResultsChanged()
    {
        ResultsChanged?.Invoke(this, new SearchResultsChangedEventArgs(Pane, _results.ToArray()));
    }
}

public sealed class ViewModelMessageEventArgs : EventArgs
{
    public string Title { get; }

    public string Message { get; }

    public ViewModelMessageEventArgs(string title, string message)
    {
        Title = title;
        Message = message;
    }
}

/// <summary>
/// Event args carrying updated search results and the pane they belong to.
/// </summary>
public sealed class SearchResultsChangedEventArgs : EventArgs
{
    public PaneId Pane { get; }
    public IReadOnlyList<SearchResult> Results { get; }

    public SearchResultsChangedEventArgs(PaneId pane, IReadOnlyList<SearchResult> results)
    {
        Pane = pane;
        Results = results;
    }
}
