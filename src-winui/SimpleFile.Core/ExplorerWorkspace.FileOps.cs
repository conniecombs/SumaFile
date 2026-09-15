using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{

    public string SuggestedNameForNewItem(NewItemTemplate template, PaneId? pane = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var target = Normalize(pane.GetValueOrDefault(ActivePane));
        List<FileEntry> entries;
        lock (_gate)
        {
            entries = [.. Pane(target).Entries];
        }

        return template.SuggestedName(entries);
    }

    public string SuggestedShortcutNameForTarget(string targetPath, PaneId? pane = null)
    {
        var target = Normalize(pane.GetValueOrDefault(ActivePane));
        List<FileEntry> entries;
        lock (_gate)
        {
            entries = [.. Pane(target).Entries];
        }

        return NewItemTemplate.SuggestedShortcutName(targetPath, entries);
    }

    public Task<string> CreateNewItemInCurrentPaneAsync(
        NewItemTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.IsShortcut)
        {
            throw new InvalidOperationException("Shortcut creation requires a target path.");
        }

        var name = SuggestedNameForNewItem(template, ActivePane);
        return template.IsDirectory
            ? CreateFolderInCurrentPaneAsync(name, cancellationToken)
            : CreateFileInCurrentPaneAsync(name, cancellationToken);
    }

    public async Task<string> CreateFolderInCurrentPaneAsync(string name, CancellationToken cancellationToken = default)
        => await CreateEntryInCurrentPaneAsync(name, isDirectory: true, cancellationToken).ConfigureAwait(false);

    public async Task<string> CreateFileInCurrentPaneAsync(string name, CancellationToken cancellationToken = default)
        => await CreateEntryInCurrentPaneAsync(name, isDirectory: false, cancellationToken).ConfigureAwait(false);

    public async Task<string> CreateShortcutInCurrentPaneAsync(
        string name,
        string targetPath,
        string? arguments = null,
        string? workingDirectory = null,
        string? iconPath = null,
        CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        var target = Normalize(ActivePane);
        var path = Pane(target).Path;
        var result = await ops.CreateShortcutAsync(
            path,
            name,
            targetPath,
            arguments,
            workingDirectory,
            iconPath,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Undo.PushCreateShortcut(path, name, targetPath, arguments, workingDirectory, iconPath, result, ops);
        SelectPathForRefresh(target, result);
        await RefreshAsync(target, cancellationToken).ConfigureAwait(false);
        MarkPathSelectedAfterRefresh(target, result, $"Created {PathRules.Basename(result)}");
        return result;
    }

    private async Task<string> CreateEntryInCurrentPaneAsync(
        string name,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        var ops = RequireFileOps();
        var target = Normalize(ActivePane);
        var path = Pane(target).Path;
        var result = isDirectory
            ? await ops.CreateFolderAsync(path, name, cancellationToken).ConfigureAwait(false)
            : await ops.CreateFileAsync(path, name, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Undo.PushCreate(path, name, result, isDirectory, ops);
        SelectPathForRefresh(target, result);
        await RefreshAsync(target, cancellationToken).ConfigureAwait(false);
        MarkPathSelectedAfterRefresh(target, result, $"Created {PathRules.Basename(result)}");
        return result;
    }

    public async Task TrashSelectedAsync(string[] selectedPaths, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        var recycleBinPaths = await ops.TrashAsync(selectedPaths, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Undo.PushTrash(selectedPaths, recycleBinPaths, ops);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken cancellationToken = default)
    {
        var restored = await RequireFileOps().RestoreRecycleBinAsync(paths, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return restored;
    }

    public async Task EmptyRecycleBinAsync(CancellationToken cancellationToken = default)
    {
        await RequireFileOps().EmptyRecycleBinAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSelectedAsync(string path, CancellationToken cancellationToken = default)
    {
        await RequireFileOps().DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RenameSelectedAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        var target = Normalize(ActivePane);
        var result = await ops.RenameAsync(path, newName, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Undo.PushRename(path, result, ops);
        SelectPathForRefresh(target, result);
        await RefreshAsync(target, cancellationToken).ConfigureAwait(false);
        MarkPathSelectedAfterRefresh(target, result, $"Renamed to {PathRules.Basename(result)}");
        return result;
    }

    public async Task OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await RequireFileOps().OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevealInFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        await RequireFileOps().RevealInFolderAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public void AddBookmark(string path)
    {
        Bookmarks = PlacesStore.AddBookmark(Bookmarks, path);
        RaiseChanged();
    }

    public void RemoveBookmark(string path)
    {
        Bookmarks = PlacesStore.RemoveBookmark(Bookmarks, path);
        RaiseChanged();
    }

    public void ClearRecentHistory()
    {
        if (RecentPaths.Count == 0)
        {
            return;
        }

        RecentPaths = [];
        RaiseChanged();
    }

}

