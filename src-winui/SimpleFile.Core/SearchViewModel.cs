using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// Current search results. Read-only snapshot for the host to render.
    /// </summary>
    public IReadOnlyList<SearchResult> Results => _results;

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
    public void CheckSearchRootDrift()
    {
        if (!IsActive || _searchRoot is null)
        {
            return;
        }

        var currentPath = _workspace.Pane(Pane).Path;
        if (!string.Equals(currentPath, _searchRoot, StringComparison.OrdinalIgnoreCase))
        {
            ClearState();
        }
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
        if (_workspace.FileOps is null)
        {
            return;
        }

        var pane = _workspace.Normalize(requestedPane ?? _workspace.ActivePane);
        _workspace.ActivatePane(pane);

        if (string.IsNullOrWhiteSpace(Query))
        {
            await CancelActiveAsync();
            ClearState();
            Cleared?.Invoke(this, EventArgs.Empty);
            return;
        }

        await CancelActiveAsync();

        var root = _workspace.Pane(pane).Path;
        var searchId = $"search_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _searchCounter)}";
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _activeSearchId = searchId;
        IsActive = true;
        Pane = pane;
        _searchRoot = root;
        _results.Clear();
        CanCancel = true;
        StatusText = $"Searching {root}...";
        RaiseResultsChanged();

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

        try
        {
            var results = await _workspace.FileOps.SearchAsync(
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

    /// <summary>
    /// Runs a smart folder search with pre-built options.
    /// </summary>
    public async Task StartSmartFolderAsync(
        SearchOptions? template,
        PaneId pane,
        Action<Action> dispatchToUi)
    {
        if (_workspace.FileOps is null)
        {
            return;
        }

        await CancelActiveAsync();

        var root = template?.SearchPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = _workspace.Active.Path;
        }

        var searchId = $"search_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _searchCounter)}";
        var options = SearchOptionsFactory.ForRun(template, searchId, root);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _activeSearchId = searchId;
        IsActive = true;
        Pane = pane;
        _searchRoot = options.SearchPath;
        _results.Clear();
        CanCancel = true;
        StatusText = "Searching smart folder...";
        RaiseResultsChanged();

        try
        {
            var results = await _workspace.FileOps.SearchAsync(
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
        catch
        {
            // Swallow cancellation failures silently; the search is already being torn down.
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
    public void ClearState()
    {
        IsActive = false;
        _searchRoot = null;
        _searchCts?.Cancel();
        _searchCts = null;
        _activeSearchId = null;
        _results.Clear();
        CanCancel = false;
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private void FinishRun(string searchId)
    {
        if (!string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
        {
            return;
        }

        _activeSearchId = null;
        CanCancel = IsActive && !string.IsNullOrEmpty(_activeSearchId);
    }

    private void RaiseResultsChanged()
    {
        ResultsChanged?.Invoke(this, new SearchResultsChangedEventArgs(Pane, _results.ToArray()));
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
