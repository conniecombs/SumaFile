using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{
    public ClipboardHistory ClipboardHistory => _operationLog.ClipboardHistory;
    public List<OperationRecord> OperationLog => _operationLog.Entries;

    public string SuggestedNameForNewItem(NewItemTemplate template, PaneId? pane = null) =>
        _fileOpsController.SuggestedNameForNewItem(this, template, pane);

    public string SuggestedShortcutNameForTarget(string targetPath, PaneId? pane = null) =>
        _fileOpsController.SuggestedShortcutNameForTarget(this, targetPath, pane);

    public Task<string> CreateNewItemInCurrentPaneAsync(
        NewItemTemplate template,
        CancellationToken cancellationToken = default) =>
        _fileOpsController.CreateNewItemInCurrentPaneAsync(this, template, cancellationToken);

    public Task<string> CreateFolderInCurrentPaneAsync(string name, CancellationToken cancellationToken = default) =>
        _fileOpsController.CreateEntryInCurrentPaneAsync(this, name, isDirectory: true, cancellationToken);

    public Task<string> CreateFileInCurrentPaneAsync(string name, CancellationToken cancellationToken = default) =>
        _fileOpsController.CreateEntryInCurrentPaneAsync(this, name, isDirectory: false, cancellationToken);

    public Task<string> CreateShortcutInCurrentPaneAsync(
        string name,
        string targetPath,
        string? arguments = null,
        string? workingDirectory = null,
        string? iconPath = null,
        CancellationToken cancellationToken = default) =>
        _fileOpsController.CreateShortcutInCurrentPaneAsync(
            this,
            name,
            targetPath,
            arguments,
            workingDirectory,
            iconPath,
            cancellationToken);

    public Task TrashSelectedAsync(string[] selectedPaths, CancellationToken cancellationToken = default) =>
        _fileOpsController.TrashSelectedAsync(this, selectedPaths, cancellationToken);

    public Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken cancellationToken = default) =>
        _fileOpsController.RestoreRecycleBinAsync(this, paths, cancellationToken);

    public Task EmptyRecycleBinAsync(CancellationToken cancellationToken = default) =>
        _fileOpsController.EmptyRecycleBinAsync(this, cancellationToken);

    public Task DeleteSelectedAsync(string path, CancellationToken cancellationToken = default) =>
        _fileOpsController.DeleteSelectedAsync(this, path, cancellationToken);

    public Task<string> RenameSelectedAsync(string path, string newName, CancellationToken cancellationToken = default) =>
        _fileOpsController.RenameSelectedAsync(this, path, newName, cancellationToken);

    public Task OpenFileAsync(string path, CancellationToken cancellationToken = default) =>
        RequireFileOps().OpenFileAsync(path, cancellationToken);

    public Task RevealInFolderAsync(string path, CancellationToken cancellationToken = default) =>
        RequireFileOps().RevealInFolderAsync(path, cancellationToken);

    public Task PackIntoFolderAsync(string[] sources, string folderName, CancellationToken cancellationToken = default) =>
        _fileOpsController.PackIntoFolderAsync(this, sources, folderName, cancellationToken);

    public Task UnpackFolderAsync(string folderPath, CancellationToken cancellationToken = default) =>
        _fileOpsController.UnpackFolderAsync(this, folderPath, cancellationToken);

    public Task CopyOrMoveToOtherPaneAsync(
        string[] sources,
        bool move,
        string conflictAction = "keep-both",
        CancellationToken cancellationToken = default) =>
        _fileOpsController.CopyOrMoveToOtherPaneAsync(this, sources, move, conflictAction, cancellationToken);

    public void RememberClipboard() =>
        _operationLog.RememberClipboard(Clipboard);

    public void RememberOperation(
        string kind,
        string description,
        string[] sources,
        string destination,
        bool move,
        string status = "completed") =>
        _operationLog.RememberOperation(kind, description, sources, destination, move, status);

    public Task RetryOperationAsync(OperationRecord record, CancellationToken cancellationToken = default) =>
        _fileOpsController.RetryOperationAsync(this, record, cancellationToken);

    public void AddBookmark(string path) =>
        _places.AddBookmark(this, path);

    public void RemoveBookmark(string path) =>
        _places.RemoveBookmark(this, path);

    public void ClearRecentHistory() =>
        _places.ClearRecentHistory(this);

    public Task SaveSmartFolderAsync(SmartFolder folder, CancellationToken cancellationToken = default) =>
        _places.SaveSmartFolderAsync(this, folder, cancellationToken);

    public Task DeleteSmartFolderAsync(string id, CancellationToken cancellationToken = default) =>
        _places.DeleteSmartFolderAsync(this, id, cancellationToken);

    public Task SaveCurrentSearchAsSmartFolderAsync(
        string name,
        SearchOptions options,
        CancellationToken cancellationToken = default) =>
        _places.SaveSearchAsSmartFolderAsync(this, name, options, cancellationToken);

    public Task SetColorLabelAsync(string[] paths, long tagId, CancellationToken cancellationToken = default) =>
        _tags.SetColorLabelAsync(this, paths, tagId, cancellationToken);

    public Task RemoveColorLabelAsync(string[] paths, CancellationToken cancellationToken = default) =>
        _tags.RemoveColorLabelAsync(this, paths, cancellationToken);

    public void SetTagFilter(long? tagId) =>
        _tags.SetTagFilter(this, tagId);

    public Task UpdateTagAsync(long id, string name, string color) =>
        _tags.UpdateTagAsync(this, id, name, color);

    public Task DeleteTagDefinitionAsync(long id) =>
        _tags.DeleteTagDefinitionAsync(this, id);

    public IReadOnlyList<string> FilesWithTag(long tagId) =>
        _tags.FilesWithTag(tagId);

    public Task LoadTreeChildrenAsync(string path, CancellationToken cancellationToken = default) =>
        _folderTree.LoadChildrenAsync(this, path, cancellationToken);

    public void ToggleTreeExpanded(string path) =>
        _folderTree.ToggleExpanded(this, path);

    public Task ApplyGitStatusesAsync(CancellationToken cancellationToken = default) =>
        ApplyGitStatusesAsync(ActivePane, cancellationToken);

    public Task ApplyGitStatusesAsync(PaneId pane, CancellationToken cancellationToken = default) =>
        _enrichment.ApplyGitStatusesAsync(this, pane, cancellationToken);

    public void ClearGitStatuses() =>
        _enrichment.ClearGitStatuses(this);

    public Task FillFolderSizesAsync(CancellationToken cancellationToken = default) =>
        FillFolderMetricsAsync(ActivePane, includeSizes: Settings.ShowFolderSizes, includeItemCounts: false, cancellationToken);

    public Task FillFolderMetricsAsync(
        PaneId pane,
        bool includeSizes,
        bool includeItemCounts,
        CancellationToken cancellationToken = default) =>
        _enrichment.FillFolderMetricsAsync(this, pane, includeSizes, includeItemCounts, cancellationToken);
}
