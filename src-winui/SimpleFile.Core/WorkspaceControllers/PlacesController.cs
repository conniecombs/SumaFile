using SimpleFile.Ipc;

namespace SimpleFile.Core;

internal sealed class PlacesController
{
    public List<BookmarkItem> Bookmarks { get; private set; } = [];
    public List<string> RecentPaths { get; private set; } = [];
    public List<SmartFolder> SmartFolders { get; private set; } = [];

    public void ApplyLoaded(List<BookmarkItem> bookmarks, List<string> recentPaths)
    {
        Bookmarks = bookmarks ?? [];
        RecentPaths = recentPaths ?? [];
    }

    public void AddBookmark(string path)
    {
        Bookmarks = PlacesStore.AddBookmark(Bookmarks, path);
    }

    public void RemoveBookmark(string path)
    {
        Bookmarks = PlacesStore.RemoveBookmark(Bookmarks, path);
    }

    public void RecordRecent(string path)
    {
        RecentPaths = PlacesStore.RecordRecent(RecentPaths, path);
    }

    public bool ClearRecentHistory()
    {
        if (RecentPaths.Count == 0)
        {
            return false;
        }

        RecentPaths = [];
        return true;
    }

    public async Task LoadSmartFoldersAsync(
        FileOperationService? fileOps,
        CancellationToken cancellationToken = default)
    {
        if (fileOps is null)
        {
            SmartFolders = [];
            return;
        }

        try
        {
            var folders = await fileOps.LoadSmartFoldersAsync(cancellationToken).ConfigureAwait(false);
            SmartFolders = [.. folders];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SmartFolders = [];
        }
    }

    public async Task SaveSmartFolderAsync(
        FileOperationService fileOps,
        SmartFolder folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileOps);
        var updated = await fileOps.SaveSmartFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        SmartFolders = [.. updated];
    }

    public async Task DeleteSmartFolderAsync(
        FileOperationService fileOps,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileOps);
        var updated = await fileOps.DeleteSmartFolderAsync(id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        SmartFolders = [.. updated];
    }

    public Task SaveSearchAsSmartFolderAsync(
        FileOperationService fileOps,
        string name,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var folder = new SmartFolder
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Icon = "search",
            SearchOptions = options,
        };
        return SaveSmartFolderAsync(fileOps, folder, cancellationToken);
    }

    public void AddBookmark(ExplorerWorkspace workspace, string path)
    {
        AddBookmark(path);
        workspace.NotifyChanged();
    }

    public void RemoveBookmark(ExplorerWorkspace workspace, string path)
    {
        RemoveBookmark(path);
        workspace.NotifyChanged();
    }

    public void ClearRecentHistory(ExplorerWorkspace workspace)
    {
        if (ClearRecentHistory())
        {
            workspace.NotifyChanged();
        }
    }

    public async Task SaveSmartFolderAsync(
        ExplorerWorkspace workspace,
        SmartFolder folder,
        CancellationToken cancellationToken = default)
    {
        await SaveSmartFolderAsync(workspace.RequireFileOps(), folder, cancellationToken).ConfigureAwait(false);
        workspace.NotifyChanged();
    }

    public async Task DeleteSmartFolderAsync(
        ExplorerWorkspace workspace,
        string id,
        CancellationToken cancellationToken = default)
    {
        await DeleteSmartFolderAsync(workspace.RequireFileOps(), id, cancellationToken).ConfigureAwait(false);
        workspace.NotifyChanged();
    }

    public async Task SaveSearchAsSmartFolderAsync(
        ExplorerWorkspace workspace,
        string name,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        await SaveSearchAsSmartFolderAsync(workspace.RequireFileOps(), name, options, cancellationToken)
            .ConfigureAwait(false);
        workspace.NotifyChanged();
    }
}
