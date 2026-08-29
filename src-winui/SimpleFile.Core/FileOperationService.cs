using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class FileOperationService
{
    private ISimpleFileIpc _ipc;
    private readonly OperationJournal? _journal;

    public FileOperationService(ISimpleFileIpc ipc, OperationJournal? journal = null)
    {
        _ipc = ipc;
        _journal = journal;
    }

    public void ReplaceIpc(ISimpleFileIpc ipc)
    {
        ArgumentNullException.ThrowIfNull(ipc);
        _ipc = ipc;
    }

    // Create folder in the given parent directory.
    // Returns the full path of the created folder.
    public async Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken ct = default)
    {
        return await _ipc.CreateDirectoryAsync(parentPath, name, ct).ConfigureAwait(false);
    }

    // Create file in the given parent directory.
    // Returns the full path of the created file.
    public async Task<string> CreateFileAsync(string parentPath, string name, CancellationToken ct = default)
    {
        return await _ipc.CreateFileAsync(parentPath, name, ct).ConfigureAwait(false);
    }

    // Permanently delete a file or directory.
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await _ipc.DeleteEntryAsync(path, ct).ConfigureAwait(false);
    }

    // Move files to the system trash. Throws FileOperationException with
    // IsTrashUnavailable = true if the trash service is unavailable.
    public async Task<string[]> TrashAsync(string[] paths, CancellationToken ct = default)
    {
        return await _ipc.MoveToTrashAsync(paths, ct).ConfigureAwait(false);
    }

    public Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken ct = default)
    {
        return _ipc.RestoreRecycleBinAsync(paths, ct);
    }

    public Task EmptyRecycleBinAsync(CancellationToken ct = default)
    {
        return _ipc.EmptyRecycleBinAsync(ct);
    }

    // Rename a file or directory. Returns the new full path.
    public async Task<string> RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        return await _ipc.RenameEntryAsync(path, newName, ct).ConfigureAwait(false);
    }

    // Batch rename. Returns the new full paths.
    public async Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default)
    {
        return await _ipc.BatchRenameAsync(entries, ct).ConfigureAwait(false);
    }

    // Copy items to a destination with conflict resolution.
    // conflictAction: "error", "skip", "replace", "rename", "keep-both"
    // Returns transfer results.
    public Task<TransferResult[]> CopyAsync(
        string[] sources,
        string destination,
        string conflictAction,
        IProgress<ProgressUpdate>? progress = null,
        Action<string>? operationStarted = null,
        CancellationToken ct = default)
    {
        return RunTransferAsync(
            "copy",
            sources,
            destination,
            (ipc, operationId, token) => ipc.CopyWithProgressAsync(
                sources, destination, operationId, conflictAction, token),
            progress,
            operationStarted,
            ct);
    }

    // Move items to a destination with conflict resolution.
    public Task<TransferResult[]> MoveAsync(
        string[] sources,
        string destination,
        string conflictAction,
        IProgress<ProgressUpdate>? progress = null,
        Action<string>? operationStarted = null,
        CancellationToken ct = default)
    {
        return RunTransferAsync(
            "move",
            sources,
            destination,
            (ipc, operationId, token) => ipc.MoveWithProgressAsync(
                sources, destination, operationId, conflictAction, token),
            progress,
            operationStarted,
            ct);
    }

    private async Task<TransferResult[]> RunTransferAsync(
        string operationType,
        string[] sources,
        string destination,
        Func<ISimpleFileIpc, string, CancellationToken, Task<TransferResult[]>> invoke,
        IProgress<ProgressUpdate>? progress,
        Action<string>? operationStarted,
        CancellationToken ct)
    {
        var ipc = _ipc;
        var operationId = GenerateOperationId();
        _journal?.Started(operationType, operationId, sources, destination);
        operationStarted?.Invoke(operationId);
        IDisposable? subscription = null;
        IDisposable? cancelRegistration = null;
        if (progress != null)
        {
            subscription = ipc.On<ProgressUpdate>(Protocol.OperationProgressEvent, update =>
            {
                if (update.OperationId == operationId)
                    progress.Report(update);
            });
        }

        // JSON-RPC cancellation does not abort the backend copy/move. Always
        // pair a cancelled wait with cancel_operation.
        if (ct.CanBeCanceled)
        {
            cancelRegistration = ct.Register(() =>
            {
                _ = CancelOperationBestEffortAsync(ipc, operationId);
            });
        }

        try
        {
            var results = await invoke(ipc, operationId, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                _journal?.Cancelled(operationType, operationId);
                throw new OperationCanceledException(ct);
            }

            _journal?.Completed(operationType, operationId);
            return results;
        }
        catch (OperationCanceledException)
        {
            _journal?.Cancelled(operationType, operationId);
            throw;
        }
        catch (Exception exception)
        {
            _journal?.Failed(operationType, operationId, exception);
            throw;
        }
        finally
        {
            cancelRegistration?.Dispose();
            subscription?.Dispose();
        }
    }

    private static async Task CancelOperationBestEffortAsync(ISimpleFileIpc ipc, string operationId)
    {
        try
        {
            await ipc.CancelOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    // Cancel an in-progress operation.
    public async Task CancelOperationAsync(string operationId, CancellationToken ct = default)
    {
        await _ipc.CancelOperationAsync(operationId, ct).ConfigureAwait(false);
    }

    public async Task<SearchResult[]> SearchAsync(
        SearchOptions options,
        Action<SearchResult[]>? onBatch = null,
        Action<int>? onComplete = null,
        CancellationToken ct = default)
    {
        return await _ipc.SearchFilesAsync(options, onBatch, onComplete, ct).ConfigureAwait(false);
    }

    public async Task CancelSearchAsync(string searchId, CancellationToken ct = default)
    {
        await _ipc.CancelSearchAsync(searchId, ct).ConfigureAwait(false);
    }

    public async Task WatchDirectoryAsync(string path, CancellationToken ct = default)
    {
        await _ipc.WatchDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    public async Task UnwatchDirectoryAsync(CancellationToken ct = default)
    {
        await _ipc.UnwatchDirectoryAsync(ct).ConfigureAwait(false);
    }

    public Task<string> CopyEntryResolvedAsync(
        string source,
        string destination,
        string conflictAction,
        CancellationToken ct = default)
    {
        return _ipc.CopyEntryResolvedAsync(source, destination, conflictAction, ct);
    }

    public Task<string> MoveEntryResolvedAsync(
        string source,
        string destination,
        string conflictAction,
        CancellationToken ct = default)
    {
        return _ipc.MoveEntryResolvedAsync(source, destination, conflictAction, ct);
    }

    public Task<FileEntry> GetEntryInfoAsync(string path, CancellationToken ct = default)
    {
        return _ipc.GetEntryInfoAsync(path, ct);
    }

    public Task<TreeNode[]> ListSubdirectoriesAsync(string path, CancellationToken ct = default)
    {
        return _ipc.ListSubdirectoriesAsync(path, ct);
    }

    public async Task<ulong> CalculateFolderSizeAsync(string path, CancellationToken ct = default)
    {
        var ipc = _ipc;
        try
        {
            return await ipc.CalculateFolderSizeAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCancelBestEffortAsync(cancelCt => ipc.CancelFolderSizeAsync(cancelCt)).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ulong> CountFolderItemsAsync(string path, CancellationToken ct = default)
    {
        var ipc = _ipc;
        try
        {
            return await ipc.CountFolderItemsAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCancelBestEffortAsync(cancelCt => ipc.CancelFolderItemCountAsync(cancelCt)).ConfigureAwait(false);
            throw;
        }
    }

    public Task GitPullAsync(string path, CancellationToken ct = default) => _ipc.GitPullAsync(path, ct);

    public Task<FileEntry[]> GetGitFileStatusesAsync(string path, CancellationToken ct = default)
        => _ipc.GetGitFileStatusesAsync(path, ct);

    public Task GitPushAsync(string path, CancellationToken ct = default) => _ipc.GitPushAsync(path, ct);

    public Task<GitStatus> GetGitStatusAsync(string path, CancellationToken ct = default) => _ipc.GetGitStatusAsync(path, ct);

    // Open a file in the default application.
    public async Task OpenFileAsync(string path, CancellationToken ct = default)
    {
        await _ipc.OpenFileAsync(path, ct).ConfigureAwait(false);
    }

    // Reveal a file in Windows Explorer.
    public async Task RevealInFolderAsync(string path, CancellationToken ct = default)
    {
        await _ipc.RevealInFolderAsync(path, ct).ConfigureAwait(false);
    }

    public async Task OpenExternalUrlAsync(string url, CancellationToken ct = default)
    {
        await _ipc.OpenExternalUrlAsync(url, ct).ConfigureAwait(false);
    }

    public Task<ArchiveInfo> ListArchiveAsync(string path, CancellationToken ct = default)
    {
        return _ipc.ListArchiveAsync(path, ct);
    }

    public async Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken ct = default)
    {
        await _ipc.ExtractArchiveAsync(archivePath, destination, ct).ConfigureAwait(false);
    }

    public async Task CreateArchiveAsync(
        string[] paths,
        string archivePath,
        string format,
        CancellationToken ct = default)
    {
        await _ipc.CreateArchiveAsync(paths, archivePath, format, ct).ConfigureAwait(false);
    }

    public Task<FilePreview> ReadFilePreviewAsync(string path, ulong? maxSize = null, CancellationToken ct = default)
    {
        return _ipc.ReadFilePreviewAsync(path, maxSize, ct);
    }

    public Task<string> GenerateThumbnailAsync(string path, uint size = 256, CancellationToken ct = default)
    {
        return _ipc.GenerateThumbnailAsync(path, size, ct);
    }

    public Task<ThumbnailResult[]> GenerateThumbnailsAsync(string[] paths, uint size = 128, CancellationToken ct = default)
    {
        return _ipc.GenerateThumbnailsAsync(paths, size, ct);
    }

    public async Task OpenFileWithAsync(string path, string application, CancellationToken ct = default)
    {
        await _ipc.OpenFileWithAsync(path, application, ct).ConfigureAwait(false);
    }

    public Task<FileComparison> CompareFilesAsync(string pathA, string pathB, CancellationToken ct = default)
    {
        return _ipc.CompareFilesAsync(pathA, pathB, ct);
    }

    public Task<Checksums> ComputeChecksumAsync(string path, CancellationToken ct = default)
    {
        return _ipc.ComputeChecksumAsync(path, ct);
    }

    public Task<ImageMetadata> GetImageMetadataAsync(string path, CancellationToken ct = default)
    {
        return _ipc.GetImageMetadataAsync(path, ct);
    }

    public Task<FileMetadata> GetFileMetadataAsync(string path, CancellationToken ct = default)
    {
        return _ipc.GetFileMetadataAsync(path, ct);
    }

    public Task<Tag[]> GetAllTagsAsync(CancellationToken ct = default) => _ipc.GetAllTagsAsync(ct);
    public Task<Tag> CreateTagAsync(string name, string color, CancellationToken ct = default) => _ipc.CreateTagAsync(name, color, ct);
    public Task UpdateTagAsync(long id, string name, string color, CancellationToken ct = default) => _ipc.UpdateTagAsync(id, name, color, ct);
    public Task DeleteTagAsync(long id, CancellationToken ct = default) => _ipc.DeleteTagAsync(id, ct);
    public Task<Tag[]> GetTagsForPathAsync(string path, CancellationToken ct = default) => _ipc.GetTagsForPathAsync(path, ct);
    public Task SetTagsForPathAsync(string path, long[] tagIds, CancellationToken ct = default) => _ipc.SetTagsForPathAsync(path, tagIds, ct);
    public Task<Dictionary<string, Tag>> GetAllFileTagsAsync(CancellationToken ct = default) => _ipc.GetAllFileTagsAsync(ct);
    public Task<string[]> GetFilesWithTagAsync(long tagId, CancellationToken ct = default) => _ipc.GetFilesWithTagAsync(tagId, ct);

    public Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default) => _ipc.LoadSmartFoldersAsync(ct);
    public Task<SmartFolder[]> SaveSmartFolderAsync(SmartFolder folder, CancellationToken ct = default) => _ipc.SaveSmartFolderAsync(folder, ct);
    public Task<SmartFolder[]> DeleteSmartFolderAsync(string id, CancellationToken ct = default) => _ipc.DeleteSmartFolderAsync(id, ct);

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default) => _ipc.GetDbSettingAsync(key, ct);
    public Task SetSettingAsync(string key, string value, CancellationToken ct = default) => _ipc.SetDbSettingAsync(key, value, ct);
    public Task<string> GetAppVersionAsync(CancellationToken ct = default) => _ipc.GetAppVersionAsync(ct);

    public async Task<CleanupResult> DiskCleanupAsync(
        string directory,
        ulong? sizeThreshold = null,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        var ipc = _ipc;
        var operationId = GenerateOperationId();
        _journal?.Started("cleanup", operationId, [directory]);
        IDisposable? subscription = null;
        if (progress != null)
        {
            subscription = ipc.On<ProgressUpdate>(Protocol.OperationProgressEvent, update =>
            {
                if (update.OperationId == operationId && update.OperationType == "cleanup")
                    progress.Report(update);
            });
        }
        try
        {
            var result = await ipc.DiskCleanupAsync(directory, sizeThreshold, operationId, ct).ConfigureAwait(false);
            _journal?.Completed("cleanup", operationId);
            return result;
        }
        catch (OperationCanceledException)
        {
            _journal?.Cancelled("cleanup", operationId);
            throw;
        }
        catch (Exception exception)
        {
            _journal?.Failed("cleanup", operationId, exception);
            throw;
        }
        finally
        {
            subscription?.Dispose();
        }
    }

    public async Task<DuplicateCheckResult> DuplicateCheckAsync(
        string directory,
        ulong? minSize = null,
        ulong? partialHashBytes = null,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        var ipc = _ipc;
        var operationId = GenerateOperationId();
        _journal?.Started("duplicate-check", operationId, [directory]);
        IDisposable? subscription = null;
        if (progress != null)
        {
            subscription = ipc.On<ProgressUpdate>(Protocol.OperationProgressEvent, update =>
            {
                if (update.OperationId == operationId && update.OperationType == "duplicate-check")
                    progress.Report(update);
            });
        }
        try
        {
            var result = await ipc.DuplicateCheckAsync(directory, minSize, partialHashBytes, operationId, ct).ConfigureAwait(false);
            _journal?.Completed("duplicate-check", operationId);
            return result;
        }
        catch (OperationCanceledException)
        {
            _journal?.Cancelled("duplicate-check", operationId);
            throw;
        }
        catch (Exception exception)
        {
            _journal?.Failed("duplicate-check", operationId, exception);
            throw;
        }
        finally
        {
            subscription?.Dispose();
        }
    }

    public Task CancelDiskCleanupAsync(CancellationToken ct = default) => _ipc.CancelDiskCleanupAsync(ct);
    public Task CancelDuplicateCheckAsync(CancellationToken ct = default) => _ipc.CancelDuplicateCheckAsync(ct);
    public Task CancelFolderSizeAsync(CancellationToken ct = default) => _ipc.CancelFolderSizeAsync(ct);
    public Task CancelFolderItemCountAsync(CancellationToken ct = default) => _ipc.CancelFolderItemCountAsync(ct);

    private static async Task TryCancelBestEffortAsync(Func<CancellationToken, Task> cancel)
    {
        try
        {
            await cancel(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation is opportunistic; preserve the original canceled operation.
        }
    }

    public Task<bool> CheckRarInstalledAsync(CancellationToken ct = default) => _ipc.CheckRarInstalledAsync(ct);
    public Task<RarInstallPlan> PrepareRarInstallAsync(CancellationToken ct = default) => _ipc.PrepareRarInstallAsync(ct);
    public Task DiscardRarInstallAsync(string confirmationToken, CancellationToken ct = default) => _ipc.DiscardRarInstallAsync(confirmationToken, ct);
    public Task<string> InstallRarAsync(string confirmationToken, CancellationToken ct = default) => _ipc.InstallRarAsync(confirmationToken, ct);

    public Task<AppAboutInfo> GetAppAboutInfoAsync(CancellationToken ct = default) => _ipc.GetAppAboutInfoAsync(ct);
    public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) => _ipc.CheckForUpdateAsync(ct);

    public async Task InstallUpdateAsync(IProgress<long[]>? progress = null, CancellationToken ct = default)
    {
        var ipc = _ipc;
        var operationId = GenerateOperationId();
        _journal?.Started("update", operationId);
        IDisposable? subscription = null;
        if (progress != null)
        {
            subscription = ipc.On<long[]>(Protocol.UpdateChunkEvent, update =>
            {
                progress.Report(update);
            });
        }
        try
        {
            await ipc.InstallUpdateAsync(ct).ConfigureAwait(false);
            _journal?.Completed("update", operationId);
        }
        catch (OperationCanceledException)
        {
            _journal?.Cancelled("update", operationId);
            throw;
        }
        catch (Exception exception)
        {
            _journal?.Failed("update", operationId, exception);
            throw;
        }
        finally
        {
            subscription?.Dispose();
        }
    }

    public Task OpenTerminalAsync(string path, CancellationToken ct = default) => _ipc.OpenTerminalAsync(path, ct);
    public Task OpenPowershellAdminAsync(string path, CancellationToken ct = default) => _ipc.OpenPowershellAdminAsync(path, ct);

    // Check if an IpcException represents a conflict.
    public static bool IsConflict(IpcException ex)
        => ex.Message.StartsWith(Protocol.PrefixConflict, StringComparison.Ordinal);

    // Check if an IpcException represents a trash unavailable error.
    public static bool IsTrashUnavailable(IpcException ex)
        => ex.Message.StartsWith(Protocol.PrefixTrashUnavailable, StringComparison.Ordinal);

    public static string TrashUnavailableMessage(IpcException ex)
    {
        var detail = StripPrefix(ex.Message, Protocol.PrefixTrashUnavailable);
        if (Contains(detail, "cannot find the file specified") || Contains(detail, "0x80070002"))
        {
            return "Windows could not move the selection to the Recycle Bin. The item may no longer be available, or this location may not support Recycle Bin operations. Refresh the folder and try again, or use Delete Permanently instead.";
        }

        return "The Recycle Bin is not available for this location. This can happen on network, virtual, or removable drives.";
    }

    private static string StripPrefix(string message, string prefix)
    {
        return message.StartsWith(prefix, StringComparison.Ordinal)
            ? message[prefix.Length..].Trim()
            : message.Trim();
    }

    private static bool Contains(string value, string text)
        => value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

    private static long _operationCounter;

    private static string GenerateOperationId()
    {
        var counter = Interlocked.Increment(ref _operationCounter);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"op_{timestamp}_{counter}";
    }
}

