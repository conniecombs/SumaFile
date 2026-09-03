namespace SimpleFile.Ipc;

public interface ISimpleFileIpc : IAsyncDisposable
{
    bool IsConnected { get; }

    event EventHandler<Exception?>? Disconnected;

    Task<HandshakeResult> HandshakeAsync(string authToken, CancellationToken cancellationToken = default);

    Task<TResult> InvokeAsync<TResult>(
        string method,
        object? args,
        CancellationToken cancellationToken = default);

    Task InvokeAsync(string method, object? args, CancellationToken cancellationToken = default);

    IDisposable On<T>(string eventName, Action<T> handler);

    Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null);

    Task<HealthResult> HealthAsync(CancellationToken cancellationToken = default);

    Task<string> GetAppVersionAsync(CancellationToken cancellationToken = default);

    Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default);

    Task SelectDirectoryAsync(string? defaultPath = null, CancellationToken cancellationToken = default);

    Task ShowMainWindowAsync(CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);

    Task<string?> GetDbSettingAsync(string key, CancellationToken ct = default);
    Task SetDbSettingAsync(string key, string value, CancellationToken ct = default);

    Task<string> CreateDirectoryAsync(string path, string name, CancellationToken ct = default);
    Task<string> CreateFileAsync(string path, string name, CancellationToken ct = default);
    Task DeleteEntryAsync(string path, CancellationToken ct = default);
    Task<string[]> MoveToTrashAsync(string[] paths, CancellationToken ct = default);
    Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken ct = default);
    Task EmptyRecycleBinAsync(CancellationToken ct = default);
    Task<string> RenameEntryAsync(string path, string newName, CancellationToken ct = default);
    Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default);
    Task<string> CopyEntryAsync(string source, string destination, CancellationToken ct = default);
    Task<string> MoveEntryAsync(string source, string destination, CancellationToken ct = default);
    Task<string> CopyEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default);
    Task<string> MoveEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default);
    Task<FileEntry> GetEntryInfoAsync(string path, CancellationToken ct = default);
    Task OpenFileAsync(string path, CancellationToken ct = default);
    Task RevealInFolderAsync(string path, CancellationToken ct = default);
    Task OpenExternalUrlAsync(string url, CancellationToken ct = default);
    Task<ArchiveInfo> ListArchiveAsync(string path, CancellationToken ct = default);
    Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken ct = default);
    Task CreateArchiveAsync(string[] paths, string archivePath, string format, CancellationToken ct = default);
    Task<FilePreview> ReadFilePreviewAsync(string path, ulong? maxSize = null, CancellationToken ct = default);
    Task<string> GenerateThumbnailAsync(string path, uint size, CancellationToken ct = default);
    Task<ThumbnailResult[]> GenerateThumbnailsAsync(string[] paths, uint size, CancellationToken ct = default);
    Task OpenFileWithAsync(string path, string application, CancellationToken ct = default);
    Task<FileComparison> CompareFilesAsync(string pathA, string pathB, CancellationToken ct = default);
    Task<Checksums> ComputeChecksumAsync(string path, CancellationToken ct = default);
    Task<ImageMetadata> GetImageMetadataAsync(string path, CancellationToken ct = default);
    Task<FileMetadata> GetFileMetadataAsync(string path, CancellationToken ct = default);
    Task<TreeNode[]> ListSubdirectoriesAsync(string path, CancellationToken ct = default);
    Task<FolderMetrics> GetFolderMetricsAsync(string path, CancellationToken ct = default);
    Task<ulong> CalculateFolderSizeAsync(string path, CancellationToken ct = default);
    Task<ulong> CountFolderItemsAsync(string path, CancellationToken ct = default);
    Task<TransferResult[]> CopyWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default);
    Task<TransferResult[]> MoveWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default);
    Task CancelOperationAsync(string operationId, CancellationToken ct = default);
    Task<SearchResult[]> SearchFilesAsync(SearchOptions options, Action<SearchResult[]>? onBatch = null, Action<int>? onComplete = null, CancellationToken ct = default);
    Task CancelSearchAsync(string searchId, CancellationToken ct = default);
    Task WatchDirectoryAsync(string path, CancellationToken ct = default);
    Task UnwatchDirectoryAsync(CancellationToken ct = default);
    Task CancelFolderSizeAsync(CancellationToken ct = default);
    Task CancelFolderItemCountAsync(CancellationToken ct = default);
    Task CancelCountItemsAsync(CancellationToken ct = default);
    Task CancelFolderMetricsAsync(CancellationToken ct = default);
    Task<bool> CheckRarInstalledAsync(CancellationToken ct = default);
    Task<RarInstallPlan> PrepareRarInstallAsync(CancellationToken ct = default);
    Task DiscardRarInstallAsync(string confirmationToken, CancellationToken ct = default);
    Task<string> InstallRarAsync(string confirmationToken, CancellationToken ct = default);
    Task<CleanupResult> DiskCleanupAsync(string directory, ulong? sizeThreshold, string? operationId, CancellationToken ct = default);
    Task CancelDiskCleanupAsync(CancellationToken ct = default);
    Task<DuplicateCheckResult> DuplicateCheckAsync(string directory, ulong? minSize, ulong? partialHashBytes, string? operationId, CancellationToken ct = default);
    Task CancelDuplicateCheckAsync(CancellationToken ct = default);
    Task<Tag[]> GetAllTagsAsync(CancellationToken ct = default);
    Task<Tag> CreateTagAsync(string name, string color, CancellationToken ct = default);
    Task<Tag> UpdateTagAsync(long id, string name, string color, CancellationToken ct = default);
    Task DeleteTagAsync(long id, CancellationToken ct = default);
    Task<Tag[]> GetTagsForPathAsync(string path, CancellationToken ct = default);
    Task SetTagsForPathAsync(string path, long[] tagIds, CancellationToken ct = default);
    Task<Dictionary<string, Tag>> GetAllFileTagsAsync(CancellationToken ct = default);
    Task<string[]> GetFilesWithTagAsync(long tagId, CancellationToken ct = default);
    Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default);
    Task<SmartFolder[]> SaveSmartFolderAsync(SmartFolder folder, CancellationToken ct = default);
    Task<SmartFolder[]> DeleteSmartFolderAsync(string id, CancellationToken ct = default);
    Task<AppAboutInfo> GetAppAboutInfoAsync(CancellationToken ct = default);
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);
    Task InstallUpdateAsync(CancellationToken ct = default);
    Task OpenTerminalAsync(string path, CancellationToken ct = default);
    Task OpenPowershellAdminAsync(string path, CancellationToken ct = default);
    Task<GitStatus> GetGitStatusAsync(string path, CancellationToken ct = default);
    Task<FileEntry[]> GetGitFileStatusesAsync(string path, CancellationToken ct = default);
    Task GitPullAsync(string path, CancellationToken ct = default);
    Task GitPushAsync(string path, CancellationToken ct = default);
}
