using System.Runtime.CompilerServices;
using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

internal abstract class NullIpc : ISimpleFileIpc
{
    public virtual bool IsConnected => throw NotConfigured();

#pragma warning disable CS0067
    public virtual event EventHandler<Exception?>? Disconnected;
#pragma warning restore CS0067

    public virtual Task<HandshakeResult> HandshakeAsync(
        string authToken,
        CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task<TResult> InvokeAsync<TResult>(
        string method,
        object? args,
        CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task InvokeAsync(
        string method,
        object? args,
        CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual IDisposable On<T>(string eventName, Action<T> handler) => throw NotConfigured();

    public virtual Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null) => throw NotConfigured();

    public virtual Task<HealthResult> HealthAsync(CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task<string> GetAppVersionAsync(CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task SelectDirectoryAsync(string? defaultPath = null, CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task ShowMainWindowAsync(CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task ShutdownAsync(CancellationToken cancellationToken = default) => throw NotConfigured();

    public virtual Task<string?> GetDbSettingAsync(string key, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task SetDbSettingAsync(string key, string value, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> CreateDirectoryAsync(string path, string name, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> CreateFileAsync(string path, string name, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task DeleteEntryAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task MoveToTrashAsync(string[] paths, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task EmptyRecycleBinAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> RenameEntryAsync(string path, string newName, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> CopyEntryAsync(string source, string destination, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> MoveEntryAsync(string source, string destination, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> CopyEntryResolvedAsync(
        string source,
        string destination,
        string conflictAction,
        CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> MoveEntryResolvedAsync(
        string source,
        string destination,
        string conflictAction,
        CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<FileEntry> GetEntryInfoAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task OpenFileAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task RevealInFolderAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task OpenExternalUrlAsync(string url, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<ArchiveInfo> ListArchiveAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task CreateArchiveAsync(
        string[] paths,
        string archivePath,
        string format,
        CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<FilePreview> ReadFilePreviewAsync(string path, ulong? maxSize = null, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> GenerateThumbnailAsync(string path, uint size, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<ThumbnailResult[]> GenerateThumbnailsAsync(string[] paths, uint size, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task OpenFileWithAsync(string path, string application, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<FileComparison> CompareFilesAsync(string pathA, string pathB, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<Checksums> ComputeChecksumAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<ImageMetadata> GetImageMetadataAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<FileMetadata> GetFileMetadataAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<TreeNode[]> ListSubdirectoriesAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<ulong> CalculateFolderSizeAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<ulong> CountFolderItemsAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<TransferResult[]> CopyWithProgressAsync(
        string[] sources,
        string destination,
        string? operationId,
        string conflictAction,
        CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<TransferResult[]> MoveWithProgressAsync(
        string[] sources,
        string destination,
        string? operationId,
        string conflictAction,
        CancellationToken ct = default) => throw NotConfigured();

    public virtual Task CancelOperationAsync(string operationId, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<SearchResult[]> SearchFilesAsync(
        SearchOptions options,
        Action<SearchResult[]>? onBatch = null,
        Action<int>? onComplete = null,
        CancellationToken ct = default) => throw NotConfigured();

    public virtual Task CancelSearchAsync(string searchId, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task WatchDirectoryAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task UnwatchDirectoryAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task CancelFolderSizeAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task CancelFolderItemCountAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task CancelCountItemsAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<bool> CheckRarInstalledAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<RarInstallPlan> PrepareRarInstallAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task DiscardRarInstallAsync(string confirmationToken, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string> InstallRarAsync(string confirmationToken, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<CleanupResult> DiskCleanupAsync(
        string directory,
        ulong? sizeThreshold,
        string? operationId,
        CancellationToken ct = default) => throw NotConfigured();

    public virtual Task CancelDiskCleanupAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<DuplicateCheckResult> DuplicateCheckAsync(
        string directory,
        ulong? minSize,
        ulong? partialHashBytes,
        string? operationId,
        CancellationToken ct = default) => throw NotConfigured();

    public virtual Task CancelDuplicateCheckAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<Tag[]> GetAllTagsAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<Tag> CreateTagAsync(string name, string color, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<Tag> UpdateTagAsync(long id, string name, string color, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task DeleteTagAsync(long id, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<Tag[]> GetTagsForPathAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task SetTagsForPathAsync(string path, long[] tagIds, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<Dictionary<string, Tag>> GetAllFileTagsAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<string[]> GetFilesWithTagAsync(long tagId, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<SmartFolder[]> SaveSmartFolderAsync(SmartFolder folder, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<SmartFolder[]> DeleteSmartFolderAsync(string id, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<AppAboutInfo> GetAppAboutInfoAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task InstallUpdateAsync(CancellationToken ct = default) => throw NotConfigured();

    public virtual Task OpenTerminalAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task OpenPowershellAdminAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<GitStatus> GetGitStatusAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task<FileEntry[]> GetGitFileStatusesAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task GitPullAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual Task GitPushAsync(string path, CancellationToken ct = default) => throw NotConfigured();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected static NotImplementedException NotConfigured([CallerMemberName] string memberName = "")
        => new($"{memberName} is not configured for this test.");
}
