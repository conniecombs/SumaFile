using SimpleFile.Ipc;

namespace SimpleFile.Core;

internal sealed class WorkspaceFileOpsController
{
    public string SuggestedNameForNewItem(
        ExplorerWorkspace workspace,
        NewItemTemplate template,
        PaneId? pane = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(template);
        var target = workspace.Normalize(pane.GetValueOrDefault(workspace.ActivePane));
        List<FileEntry> entries;
        lock (workspace.Gate)
        {
            entries = [.. workspace.Pane(target).Entries];
        }

        return template.SuggestedName(entries);
    }

    public string SuggestedShortcutNameForTarget(
        ExplorerWorkspace workspace,
        string targetPath,
        PaneId? pane = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane.GetValueOrDefault(workspace.ActivePane));
        List<FileEntry> entries;
        lock (workspace.Gate)
        {
            entries = [.. workspace.Pane(target).Entries];
        }

        return NewItemTemplate.SuggestedShortcutName(targetPath, entries);
    }

    public Task<string> CreateNewItemInCurrentPaneAsync(
        ExplorerWorkspace workspace,
        NewItemTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(template);
        if (template.IsShortcut)
        {
            throw new InvalidOperationException("Shortcut creation requires a target path.");
        }

        var name = SuggestedNameForNewItem(workspace, template, workspace.ActivePane);
        return template.IsDirectory
            ? CreateEntryInCurrentPaneAsync(workspace, name, isDirectory: true, cancellationToken)
            : CreateEntryInCurrentPaneAsync(workspace, name, isDirectory: false, cancellationToken);
    }

    public async Task<string> CreateShortcutInCurrentPaneAsync(
        ExplorerWorkspace workspace,
        string name,
        string targetPath,
        string? arguments = null,
        string? workingDirectory = null,
        string? iconPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var ops = workspace.RequireFileOps();
        var target = workspace.Normalize(workspace.ActivePane);
        var path = workspace.Pane(target).Path;
        var result = await ops.CreateShortcutAsync(
            path,
            name,
            targetPath,
            arguments,
            workingDirectory,
            iconPath,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        workspace.Undo.PushCreateShortcut(path, name, targetPath, arguments, workingDirectory, iconPath, result, ops);
        workspace.SelectionController.SelectPathForRefresh(workspace, target, result);
        await workspace.RefreshAffectedPanesAsync([path, result], cancellationToken).ConfigureAwait(false);
        workspace.SelectionController.MarkPathSelectedAfterRefresh(workspace, target, result, $"Created {PathRules.Basename(result)}");
        return result;
    }

    public async Task<string> CreateEntryInCurrentPaneAsync(
        ExplorerWorkspace workspace,
        string name,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var ops = workspace.RequireFileOps();
        var target = workspace.Normalize(workspace.ActivePane);
        var path = workspace.Pane(target).Path;
        var result = isDirectory
            ? await ops.CreateFolderAsync(path, name, cancellationToken).ConfigureAwait(false)
            : await ops.CreateFileAsync(path, name, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        workspace.Undo.PushCreate(path, name, result, isDirectory, ops);
        workspace.SelectionController.SelectPathForRefresh(workspace, target, result);
        await workspace.RefreshAffectedPanesAsync([path, result], cancellationToken).ConfigureAwait(false);
        workspace.SelectionController.MarkPathSelectedAfterRefresh(workspace, target, result, $"Created {PathRules.Basename(result)}");
        return result;
    }

    public async Task TrashSelectedAsync(
        ExplorerWorkspace workspace,
        string[] selectedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var ops = workspace.RequireFileOps();
        var recycleBinPaths = await ops.TrashAsync(selectedPaths, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        workspace.Undo.PushTrash(selectedPaths, recycleBinPaths, ops);
        await workspace.RefreshAffectedPanesAsync([.. selectedPaths, .. recycleBinPaths], cancellationToken).ConfigureAwait(false);
    }

    public async Task<string[]> RestoreRecycleBinAsync(
        ExplorerWorkspace workspace,
        string[] paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var restored = await workspace.RequireFileOps().RestoreRecycleBinAsync(paths, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await workspace.RefreshAffectedPanesAsync([.. paths, .. restored], cancellationToken).ConfigureAwait(false);
        return restored;
    }

    public async Task EmptyRecycleBinAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await workspace.RequireFileOps().EmptyRecycleBinAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await workspace.RefreshAffectedPanesAsync([PathRules.RecycleBinPath], cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSelectedAsync(
        ExplorerWorkspace workspace,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await workspace.RequireFileOps().DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await workspace.RefreshAffectedPanesAsync([path], cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RenameSelectedAsync(
        ExplorerWorkspace workspace,
        string path,
        string newName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var ops = workspace.RequireFileOps();
        var target = workspace.Normalize(workspace.ActivePane);
        var result = await ops.RenameAsync(path, newName, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        workspace.Undo.PushRename(path, result, ops);
        workspace.SelectionController.SelectPathForRefresh(workspace, target, result);
        await workspace.RefreshAffectedPanesAsync([path, result], cancellationToken).ConfigureAwait(false);
        workspace.SelectionController.MarkPathSelectedAfterRefresh(workspace, target, result, $"Renamed to {PathRules.Basename(result)}");
        return result;
    }

    public async Task PackIntoFolderAsync(
        ExplorerWorkspace workspace,
        string[] sources,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.FileOps is not { } ops || sources.Length == 0 || string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        var parentDir = workspace.Active.Path;
        var created = await ops.CreateFolderAsync(parentDir, folderName, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var moved = await ops.MoveAsync(sources, created, "keep-both", ct: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var currentFolder = created;
        var currentSources = sources.ToArray();
        var currentMoved = moved.ToArray();
        workspace.Undo.Push(new UndoEntry
        {
            Description = "Pack into folder",
            Undo = async ct =>
            {
                for (var i = currentMoved.Length - 1; i >= 0; i--)
                {
                    var item = currentMoved[i];
                    var sourceParent = PathRules.GetParentPath(item.Source);
                    if (sourceParent is null) continue;
                    var movedBack = await ops.MoveEntryResolvedAsync(item.Destination, sourceParent, "rename", ct).ConfigureAwait(false);
                    var desiredName = PathRules.Basename(item.Source);
                    if (!string.IsNullOrEmpty(movedBack)
                        && !string.Equals(PathRules.Basename(movedBack), desiredName, StringComparison.Ordinal))
                    {
                        try
                        {
                            var renamed = await ops.RenameAsync(movedBack, desiredName, ct).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(renamed)) movedBack = renamed;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { }
                    }

                    if (!string.IsNullOrEmpty(movedBack) && i < currentSources.Length)
                    {
                        currentSources[i] = movedBack;
                    }
                }

                await ops.DeleteAsync(currentFolder, ct).ConfigureAwait(false);
            },
            Redo = async ct =>
            {
                currentFolder = await ops.CreateFolderAsync(parentDir, folderName, ct).ConfigureAwait(false);
                currentMoved = await ops.MoveAsync(currentSources, currentFolder, "keep-both", ct: ct).ConfigureAwait(false);
            },
        });

        await workspace.RefreshAffectedPanesAsync([parentDir, created, .. sources], cancellationToken).ConfigureAwait(false);
    }

    public async Task UnpackFolderAsync(
        ExplorerWorkspace workspace,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.FileOps is not { } ops || string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var listing = await workspace.Backend.ListDirectoryAsync(folderPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var parent = PathRules.GetParentPath(folderPath);
        if (parent is null)
        {
            return;
        }

        var folderName = PathRules.Basename(folderPath);
        var children = listing.Entries.Select(entry => entry.Path).ToArray();
        var moved = Array.Empty<TransferResult>();
        if (children.Length > 0)
        {
            moved = await ops.MoveAsync(children, parent, "keep-both", ct: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await ops.DeleteAsync(folderPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var currentFolder = folderPath;
        var currentMoved = moved.ToArray();
        workspace.Undo.Push(new UndoEntry
        {
            Description = "Flatten folder",
            Undo = async ct =>
            {
                currentFolder = await ops.CreateFolderAsync(parent, folderName, ct).ConfigureAwait(false);
                for (var i = currentMoved.Length - 1; i >= 0; i--)
                {
                    var item = currentMoved[i];
                    var movedBack = await ops.MoveEntryResolvedAsync(item.Destination, currentFolder, "rename", ct).ConfigureAwait(false);
                    var desiredName = PathRules.Basename(item.Source);
                    if (!string.IsNullOrEmpty(movedBack)
                        && !string.Equals(PathRules.Basename(movedBack), desiredName, StringComparison.Ordinal))
                    {
                        try
                        {
                            await ops.RenameAsync(movedBack, desiredName, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { }
                    }
                }
            },
            Redo = async ct =>
            {
                var relisting = await workspace.Backend.ListDirectoryAsync(currentFolder, cancellationToken: ct).ConfigureAwait(false);
                var nextChildren = relisting.Entries.Select(entry => entry.Path).ToArray();
                currentMoved = nextChildren.Length > 0
                    ? await ops.MoveAsync(nextChildren, parent, "keep-both", ct: ct).ConfigureAwait(false)
                    : [];
                await ops.DeleteAsync(currentFolder, ct).ConfigureAwait(false);
            },
        });

        await workspace.RefreshAffectedPanesAsync([folderPath, parent, .. children], cancellationToken).ConfigureAwait(false);
    }

}
