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

    public async Task<PaneNavigationResult> NavigateAsync(
        ExplorerPane pane,
        string path,
        int token,
        CancellationToken cancellationToken)
    {
        List<FileEntry> progressive = [];
        var lastPublished = Stopwatch.GetTimestamp();
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

                        var publish = ShouldRaiseForChunk(chunk, ref lastPublished);
                        lock (_gate)
                        {
                            WorkspaceNavigation.ApplyListingChunk(pane, chunk, progressive, publish);
                        }

                        if (publish)
                        {
                            _raiseChanged();
                        }
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
    public static PaneNavigationResult Completed(DirectoryListing listing) =>
        new(listing, null, false);

    public static PaneNavigationResult Partial(string message) =>
        new(null, message, false);

    public static PaneNavigationResult Abandoned() =>
        new(null, null, true);
}
