using SimpleFile.Ipc;
using System.Diagnostics;

namespace SimpleFile.Core;

internal sealed class PaneNavigator
{
    private readonly IExplorerBackend _backend;
    private readonly object _gate;
    private readonly Action _raiseChanged;

    public PaneNavigator(IExplorerBackend backend, object gate, Action raiseChanged)
    {
        _backend = backend;
        _gate = gate;
        _raiseChanged = raiseChanged;
    }

    /// <summary>
    /// Builds the view a pane wants for a folder (path, reload). Set by the workspace; while it is null, or the
    /// backend has no listing views, every folder is listed whole.
    /// </summary>
    public Func<ExplorerPane, string, bool, ListingViewRequest>? BuildViewRequest { get; set; }

    // The service's view of the folder: its first page comes back sorted and filtered; the workspace reads the
    // rest behind it. Null when the folder must be listed whole.
    private (IListingViewBackend Views, ListingViewRequest Request)? ViewFor(ExplorerPane pane, string path, bool reload)
    {
        if (_backend.ListingViews is not { } views || BuildViewRequest is not { } build)
        {
            return null;
        }

        lock (_gate)
        {
            return pane.ListingViewUnavailable ? null : (views, build(pane, path, reload));
        }
    }

    public async Task<PaneNavigationResult> NavigateAsync(
        ExplorerPane pane,
        string path,
        int token,
        CancellationToken cancellationToken)
    {
        List<FileEntry> progressive = [];
        var lastPublished = Stopwatch.GetTimestamp();
        void OnChunk(DirectoryListingChunk chunk)
        {
            if (token != pane.NavigationToken)
            {
                return;
            }

            var publish = ShouldRaiseForChunk(chunk, ref lastPublished);
            lock (_gate)
            {
                WorkspaceNavigation.ApplyListingChunk(pane, chunk, progressive, publish);
            }

            if (publish)
            {
                _raiseChanged();
            }
        }

        if (ViewFor(pane, path, reload: false) is { } view)
        {
            // The preview chunks show a slow folder's first entries while the service is still enumerating it.
            var page = await view.Views.OpenListingViewAsync(view.Request, OnChunk, cancellationToken)
                .ConfigureAwait(false);
            return token == pane.NavigationToken
                ? PaneNavigationResult.Completed(view.Request, page)
                : PaneNavigationResult.Abandoned();
        }

        try
        {
            var listing = await _backend.ListDirectoryAsync(
                    path,
                    OnChunk,
                    cancellationToken,
                    WorkspaceNavigation.BuildStreamedListingOptions(pane))
                .ConfigureAwait(false);

            return token == pane.NavigationToken
                ? PaneNavigationResult.Completed(listing)
                : PaneNavigationResult.Abandoned();
        }
        catch (IpcException exception) when (exception.IsResultTooLarge && progressive.Count > 0)
        {
            lock (_gate)
            {
                if (token == pane.NavigationToken) pane.Entries = [.. progressive];
            }
            return token == pane.NavigationToken
                ? PaneNavigationResult.Partial(exception.Message)
                : PaneNavigationResult.Abandoned();
        }
    }

    public async Task<PaneNavigationResult> RefreshAsync(
        ExplorerPane pane,
        string path,
        int token,
        CancellationToken cancellationToken)
    {
        if (ViewFor(pane, path, reload: true) is { } view)
        {
            // The rows shown stay until the page arrives, so no preview is asked for.
            var page = await view.Views.OpenListingViewAsync(view.Request, onPreview: null, cancellationToken)
                .ConfigureAwait(false);
            return token == pane.NavigationToken && PathRules.PathsEqual(pane.Path, path)
                ? PaneNavigationResult.Completed(view.Request, page)
                : PaneNavigationResult.Abandoned();
        }

        List<FileEntry> progressive = [];
        var lastPublished = Stopwatch.GetTimestamp();
        var listing = await _backend.ListDirectoryAsync(
                path,
                chunk =>
                {
                    if (token != pane.NavigationToken || !PathRules.PathsEqual(pane.Path, path))
                    {
                        return;
                    }

                    var publish = ShouldRaiseForChunk(chunk, ref lastPublished);
                    lock (_gate)
                    {
                        progressive.AddRange(chunk.Entries);
                        if (publish) pane.Entries = [.. progressive];
                    }

                    if (publish)
                    {
                        _raiseChanged();
                    }
                },
                cancellationToken,
                WorkspaceNavigation.BuildStreamedListingOptions(pane))
            .ConfigureAwait(false);

        return token == pane.NavigationToken && PathRules.PathsEqual(pane.Path, path)
            ? PaneNavigationResult.Completed(listing)
            : PaneNavigationResult.Abandoned();
    }

    private static bool ShouldRaiseForChunk(DirectoryListingChunk chunk, ref long lastPublished)
    {
        if (chunk.ChunkIndex != 0 && !chunk.Done && Stopwatch.GetElapsedTime(lastPublished).TotalMilliseconds < 100)
            return false;
        lastPublished = Stopwatch.GetTimestamp();
        return true;
    }
}

internal readonly record struct PaneNavigationResult(
    DirectoryListing? Listing,
    string? PartialResultMessage,
    bool IsAbandoned)
{
    /// <summary>The view the listing is the first page of, or null when the folder was listed whole.</summary>
    public ListingViewRequest? ViewRequest { get; init; }

    public ListingViewPage? ViewPage { get; init; }

    public static PaneNavigationResult Completed(DirectoryListing listing) =>
        new(listing, null, false);

    public static PaneNavigationResult Completed(ListingViewRequest request, ListingViewPage page) =>
        new(
            new DirectoryListing
            {
                Path = page.Path,
                Parent = page.Parent,
                Entries = page.Entries,
                IsNetwork = page.IsNetwork,
            },
            null,
            false)
        {
            ViewRequest = request,
            ViewPage = page,
        };

    public static PaneNavigationResult Partial(string message) =>
        new(null, message, false);

    public static PaneNavigationResult Abandoned() =>
        new(null, null, true);
}
