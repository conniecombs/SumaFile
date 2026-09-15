using SimpleFile.Ipc;

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

    public async Task<PaneNavigationResult> NavigateAsync(
        ExplorerPane pane,
        string path,
        int token,
        CancellationToken cancellationToken)
    {
        List<FileEntry> progressive = [];
        try
        {
            var listing = await _backend.ListDirectoryAsync(
                    path,
                    chunk =>
                    {
                        if (token != pane.NavigationToken)
                        {
                            return;
                        }

                        lock (_gate)
                        {
                            WorkspaceNavigation.ApplyListingChunk(pane, chunk, progressive);
                        }

                        _raiseChanged();
                    },
                    cancellationToken,
                    WorkspaceNavigation.BuildStreamedListingOptions(pane))
                .ConfigureAwait(false);

            return token == pane.NavigationToken
                ? PaneNavigationResult.Completed(listing)
                : PaneNavigationResult.Abandoned();
        }
        catch (IpcException exception) when (exception.IsResultTooLarge && progressive.Count > 0)
        {
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
        List<FileEntry> progressive = [];
        var listing = await _backend.ListDirectoryAsync(
                path,
                chunk =>
                {
                    if (token != pane.NavigationToken || !PathRules.PathsEqual(pane.Path, path))
                    {
                        return;
                    }

                    lock (_gate)
                    {
                        progressive.AddRange(chunk.Entries);
                        pane.Entries = [.. progressive];
                    }

                    _raiseChanged();
                },
                cancellationToken,
                WorkspaceNavigation.BuildStreamedListingOptions(pane))
            .ConfigureAwait(false);

        return token == pane.NavigationToken && PathRules.PathsEqual(pane.Path, path)
            ? PaneNavigationResult.Completed(listing)
            : PaneNavigationResult.Abandoned();
    }
}

internal readonly record struct PaneNavigationResult(
    DirectoryListing? Listing,
    string? PartialResultMessage,
    bool IsAbandoned)
{
    public static PaneNavigationResult Completed(DirectoryListing listing) =>
        new(listing, null, false);

    public static PaneNavigationResult Partial(string message) =>
        new(null, message, false);

    public static PaneNavigationResult Abandoned() =>
        new(null, null, true);
}
