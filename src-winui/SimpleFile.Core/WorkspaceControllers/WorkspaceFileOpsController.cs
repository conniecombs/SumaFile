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
        if (workspace.FileOps is null || sources.Length == 0 || string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        var created = await workspace.FileOps.CreateFolderAsync(workspace.Active.Path, folderName, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await workspace.FileOps.MoveAsync(sources, created, "keep-both", ct: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await workspace.RefreshAffectedPanesAsync([workspace.Active.Path, created, .. sources], cancellationToken).ConfigureAwait(false);
    }

    public async Task UnpackFolderAsync(
        ExplorerWorkspace workspace,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.FileOps is null || string.IsNullOrWhiteSpace(folderPath))
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

        var children = listing.Entries.Select(entry => entry.Path).ToArray();
        if (children.Length > 0)
        {
            await workspace.FileOps.MoveAsync(children, parent, "keep-both", ct: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await workspace.FileOps.DeleteAsync(folderPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await workspace.RefreshAffectedPanesAsync([folderPath, parent, .. children], cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyOrMoveToOtherPaneAsync(
        ExplorerWorkspace workspace,
        string[] sources,
        bool move,
        string conflictAction = "keep-both",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var destination = workspace.OtherPanePath();
        if (destination is null || sources.Length == 0 || workspace.FileOps is null)
        {
            return;
        }

        if (move)
        {
            var results = await workspace.FileOps.MoveAsync(sources, destination, conflictAction, ct: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            workspace.Undo.PushMove(results, workspace.FileOps);
        }
        else
        {
            var results = await workspace.FileOps.CopyAsync(sources, destination, conflictAction, ct: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            workspace.Undo.PushCopy(results, workspace.FileOps);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await workspace.RefreshAffectedPanesAsync(
            OperationLogController.TransferAffectedPaths(sources, destination, move),
            cancellationToken).ConfigureAwait(false);
        if (workspace.DualPaneEnabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await workspace.NavigatePaneAsync(workspace.OtherPane().Id, destination, HistoryMode.None, activate: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task RetryOperationAsync(
        ExplorerWorkspace workspace,
        OperationRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var affected = await workspace.OperationLogController
            .RetryOperationAsync(workspace.FileOps, record, cancellationToken)
            .ConfigureAwait(false);
        if (affected.Count > 0)
        {
            await workspace.RefreshAffectedPanesAsync(affected, cancellationToken).ConfigureAwait(false);
        }
    }
}
