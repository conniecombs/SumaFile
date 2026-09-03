using SimpleFile.Ipc;

namespace SimpleFile.Tests;

internal sealed class ConfigurableIpc : NullIpc
{
    private readonly Dictionary<string, List<object>> _handlers = new(StringComparer.Ordinal);

    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FileEntry> EntryInfo { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileEntry[]> GitFileStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ulong> FolderSizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ulong> FolderItemCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FolderMetrics> FolderMetrics { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> OpenedFiles { get; } = [];

    public Func<string, string, CancellationToken, Task<string>>? CreateDirectoryHandler { get; set; }
    public Func<string, string, CancellationToken, Task<string>>? CreateFileHandler { get; set; }
    public Func<string, string, CancellationToken, Task<string>>? RenameEntryHandler { get; set; }
    public Func<RenameRequest[], CancellationToken, Task<string[]>>? BatchRenameHandler { get; set; }
    public Func<string, CancellationToken, Task<string?>>? GetDbSettingHandler { get; set; }
    public Func<string, string, CancellationToken, Task>? SetDbSettingHandler { get; set; }
    public Func<string[], CancellationToken, Task<string[]>>? MoveToTrashHandler { get; set; }
    public Func<string[], CancellationToken, Task<string[]>>? RestoreRecycleBinHandler { get; set; }
    public Func<CancellationToken, Task>? EmptyRecycleBinHandler { get; set; }
    public Func<string[], string, string?, string, CancellationToken, Task<TransferResult[]>>? CopyWithProgressHandler { get; set; }
    public Func<string[], string, string?, string, CancellationToken, Task<TransferResult[]>>? MoveWithProgressHandler { get; set; }
    public Func<string, CancellationToken, Task>? CancelOperationHandler { get; set; }
    public Func<SearchOptions, Action<SearchResult[]>?, Action<int>?, CancellationToken, Task<SearchResult[]>>? SearchFilesHandler { get; set; }
    public Func<string, CancellationToken, Task>? CancelSearchHandler { get; set; }
    public Func<string, CancellationToken, Task>? WatchDirectoryHandler { get; set; }
    public Func<CancellationToken, Task>? UnwatchDirectoryHandler { get; set; }
    public Func<string, ulong?, CancellationToken, Task<FilePreview>>? ReadFilePreviewHandler { get; set; }
    public Func<string, CancellationToken, Task<Checksums>>? ComputeChecksumHandler { get; set; }
    public Func<string, CancellationToken, Task<FileMetadata>>? GetFileMetadataHandler { get; set; }
    public Func<string, string, CancellationToken, Task<FileComparison>>? CompareFilesHandler { get; set; }
    public Func<string, CancellationToken, Task<ArchiveInfo>>? ListArchiveHandler { get; set; }
    public Func<string, string, CancellationToken, Task>? ExtractArchiveHandler { get; set; }
    public Func<string[], string, string, CancellationToken, Task>? CreateArchiveHandler { get; set; }
    public Func<string, ulong?, string?, CancellationToken, Task<CleanupResult>>? DiskCleanupHandler { get; set; }
    public Func<string, ulong?, ulong?, string?, CancellationToken, Task<DuplicateCheckResult>>? DuplicateCheckHandler { get; set; }
    public Func<CancellationToken, Task>? InstallUpdateHandler { get; set; }
    public Func<string, CancellationToken, Task<FileEntry>>? GetEntryInfoHandler { get; set; }
    public Func<string, CancellationToken, Task>? OpenFileHandler { get; set; }
    public Func<string, CancellationToken, Task<ulong>>? CalculateFolderSizeHandler { get; set; }
    public Func<string, CancellationToken, Task<ulong>>? CountFolderItemsHandler { get; set; }
    public Func<string, CancellationToken, Task<FolderMetrics>>? GetFolderMetricsHandler { get; set; }
    public Func<CancellationToken, Task>? CancelFolderSizeHandler { get; set; }
    public Func<CancellationToken, Task>? CancelFolderItemCountHandler { get; set; }
    public Func<CancellationToken, Task>? CancelFolderMetricsHandler { get; set; }
    public Func<CancellationToken, Task<SmartFolder[]>>? LoadSmartFoldersHandler { get; set; }

    public int GitStatusCalls { get; private set; }
    public int MoveWithProgressCalls { get; private set; }
    public int CancelFolderSizeCalls { get; private set; }
    public int CancelFolderItemCountCalls { get; private set; }
    public int CancelFolderMetricsCalls { get; private set; }
    public SearchOptions? LastSearchOptions { get; private set; }
    public string? LastCancelledOperationId { get; private set; }
    public string? LastCancelledSearchId { get; private set; }

    public override bool IsConnected => true;

    public int SubscriptionCount(string eventName)
        => _handlers.TryGetValue(eventName, out var handlers) ? handlers.Count : 0;

    public void Emit<T>(string eventName, T payload)
    {
        if (!_handlers.TryGetValue(eventName, out var handlers))
        {
            return;
        }

        foreach (var handler in handlers.OfType<Action<T>>().ToArray())
        {
            handler(payload);
        }
    }

    public override IDisposable On<T>(string eventName, Action<T> handler)
    {
        if (!_handlers.TryGetValue(eventName, out var handlers))
        {
            handlers = [];
            _handlers[eventName] = handlers;
        }

        handlers.Add(handler);
        return new ActionSubscription(() => handlers.Remove(handler));
    }

    public override Task<string?> GetDbSettingAsync(string key, CancellationToken ct = default)
    {
        if (GetDbSettingHandler is not null)
        {
            return GetDbSettingHandler(key, ct);
        }

        Settings.TryGetValue(key, out var value);
        return Task.FromResult<string?>(value);
    }

    public override Task SetDbSettingAsync(string key, string value, CancellationToken ct = default)
    {
        if (SetDbSettingHandler is not null)
        {
            return SetDbSettingHandler(key, value, ct);
        }

        Settings[key] = value;
        return Task.CompletedTask;
    }

    public override Task<string> CreateDirectoryAsync(string path, string name, CancellationToken ct = default)
        => CreateDirectoryHandler?.Invoke(path, name, ct) ?? throw NotConfigured();

    public override Task<string> CreateFileAsync(string path, string name, CancellationToken ct = default)
        => CreateFileHandler?.Invoke(path, name, ct) ?? throw NotConfigured();

    public override Task<string[]> MoveToTrashAsync(string[] paths, CancellationToken ct = default)
        => MoveToTrashHandler?.Invoke(paths, ct) ?? throw NotConfigured();

    public override Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken ct = default)
        => RestoreRecycleBinHandler?.Invoke(paths, ct) ?? throw NotConfigured();

    public override Task EmptyRecycleBinAsync(CancellationToken ct = default)
        => EmptyRecycleBinHandler?.Invoke(ct) ?? throw NotConfigured();

    public override Task<string> RenameEntryAsync(string path, string newName, CancellationToken ct = default)
        => RenameEntryHandler?.Invoke(path, newName, ct) ?? throw NotConfigured();

    public override Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default)
        => BatchRenameHandler?.Invoke(entries, ct) ?? throw NotConfigured();

    public override Task<TransferResult[]> CopyWithProgressAsync(
        string[] sources,
        string destination,
        string? operationId,
        string conflictAction,
        CancellationToken ct = default)
        => CopyWithProgressHandler?.Invoke(sources, destination, operationId, conflictAction, ct) ?? throw NotConfigured();

    public override Task<TransferResult[]> MoveWithProgressAsync(
        string[] sources,
        string destination,
        string? operationId,
        string conflictAction,
        CancellationToken ct = default)
    {
        MoveWithProgressCalls += 1;
        return MoveWithProgressHandler?.Invoke(sources, destination, operationId, conflictAction, ct) ?? throw NotConfigured();
    }

    public override Task CancelOperationAsync(string operationId, CancellationToken ct = default)
    {
        LastCancelledOperationId = operationId;
        return CancelOperationHandler?.Invoke(operationId, ct) ?? Task.CompletedTask;
    }

    public override Task<SearchResult[]> SearchFilesAsync(
        SearchOptions options,
        Action<SearchResult[]>? onBatch = null,
        Action<int>? onComplete = null,
        CancellationToken ct = default)
    {
        LastSearchOptions = options;
        return SearchFilesHandler?.Invoke(options, onBatch, onComplete, ct)
            ?? Task.FromResult(Array.Empty<SearchResult>());
    }

    public override Task CancelSearchAsync(string searchId, CancellationToken ct = default)
    {
        LastCancelledSearchId = searchId;
        return CancelSearchHandler?.Invoke(searchId, ct) ?? Task.CompletedTask;
    }

    public override Task WatchDirectoryAsync(string path, CancellationToken ct = default)
        => WatchDirectoryHandler?.Invoke(path, ct) ?? throw NotConfigured();

    public override Task UnwatchDirectoryAsync(CancellationToken ct = default)
        => UnwatchDirectoryHandler?.Invoke(ct) ?? throw NotConfigured();

    public override Task<FilePreview> ReadFilePreviewAsync(string path, ulong? maxSize = null, CancellationToken ct = default)
        => ReadFilePreviewHandler?.Invoke(path, maxSize, ct) ?? throw NotConfigured();

    public override Task<Checksums> ComputeChecksumAsync(string path, CancellationToken ct = default)
        => ComputeChecksumHandler?.Invoke(path, ct) ?? throw NotConfigured();

    public override Task<FileMetadata> GetFileMetadataAsync(string path, CancellationToken ct = default)
        => GetFileMetadataHandler?.Invoke(path, ct) ?? throw NotConfigured();

    public override Task<FileComparison> CompareFilesAsync(string pathA, string pathB, CancellationToken ct = default)
        => CompareFilesHandler?.Invoke(pathA, pathB, ct) ?? throw NotConfigured();

    public override Task<ArchiveInfo> ListArchiveAsync(string path, CancellationToken ct = default)
        => ListArchiveHandler?.Invoke(path, ct) ?? throw NotConfigured();

    public override Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken ct = default)
        => ExtractArchiveHandler?.Invoke(archivePath, destination, ct) ?? throw NotConfigured();

    public override Task CreateArchiveAsync(
        string[] paths,
        string archivePath,
        string format,
        CancellationToken ct = default)
        => CreateArchiveHandler?.Invoke(paths, archivePath, format, ct) ?? throw NotConfigured();

    public override Task<CleanupResult> DiskCleanupAsync(
        string directory,
        ulong? sizeThreshold,
        string? operationId,
        CancellationToken ct = default)
        => DiskCleanupHandler?.Invoke(directory, sizeThreshold, operationId, ct) ?? throw NotConfigured();

    public override Task<DuplicateCheckResult> DuplicateCheckAsync(
        string directory,
        ulong? minSize,
        ulong? partialHashBytes,
        string? operationId,
        CancellationToken ct = default)
        => DuplicateCheckHandler?.Invoke(directory, minSize, partialHashBytes, operationId, ct) ?? throw NotConfigured();

    public override Task InstallUpdateAsync(CancellationToken ct = default)
        => InstallUpdateHandler?.Invoke(ct) ?? throw NotConfigured();

    public override Task<FileEntry> GetEntryInfoAsync(string path, CancellationToken ct = default)
    {
        if (GetEntryInfoHandler is not null)
        {
            return GetEntryInfoHandler(path, ct);
        }

        if (EntryInfo.TryGetValue(path, out var entry))
        {
            return Task.FromResult(entry);
        }

        throw new IpcException(Protocol.ErrApplication, $"Path does not exist: {path}");
    }

    public override Task OpenFileAsync(string path, CancellationToken ct = default)
    {
        if (OpenFileHandler is not null)
        {
            return OpenFileHandler(path, ct);
        }

        OpenedFiles.Add(path);
        return Task.CompletedTask;
    }

    public override Task<FileEntry[]> GetGitFileStatusesAsync(string path, CancellationToken ct = default)
    {
        GitStatusCalls += 1;
        return Task.FromResult(GitFileStatuses.TryGetValue(path, out var statuses) ? statuses : []);
    }

    public override Task<ulong> CalculateFolderSizeAsync(string path, CancellationToken ct = default)
        => CalculateFolderSizeHandler?.Invoke(path, ct)
            ?? Task.FromResult(FolderSizes.TryGetValue(path, out var size) ? size : 0UL);

    public override Task<ulong> CountFolderItemsAsync(string path, CancellationToken ct = default)
        => CountFolderItemsHandler?.Invoke(path, ct)
            ?? Task.FromResult(FolderItemCounts.TryGetValue(path, out var count) ? count : 0UL);

    public override Task<FolderMetrics> GetFolderMetricsAsync(string path, CancellationToken ct = default)
        => GetFolderMetricsHandler?.Invoke(path, ct)
            ?? Task.FromResult(FolderMetrics.TryGetValue(path, out var metrics)
                ? metrics
                : new FolderMetrics
                {
                    Size = FolderSizes.TryGetValue(path, out var size) ? size : 0UL,
                    ItemCount = FolderItemCounts.TryGetValue(path, out var count) ? count : 0UL,
                });

    public override Task CancelFolderSizeAsync(CancellationToken ct = default)
    {
        CancelFolderSizeCalls += 1;
        return CancelFolderSizeHandler?.Invoke(ct) ?? Task.CompletedTask;
    }

    public override Task CancelFolderItemCountAsync(CancellationToken ct = default)
    {
        CancelFolderItemCountCalls += 1;
        return CancelFolderItemCountHandler?.Invoke(ct) ?? Task.CompletedTask;
    }

    public override Task CancelFolderMetricsAsync(CancellationToken ct = default)
    {
        CancelFolderMetricsCalls += 1;
        return CancelFolderMetricsHandler?.Invoke(ct) ?? Task.CompletedTask;
    }

    public override Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default)
        => LoadSmartFoldersHandler?.Invoke(ct) ?? throw NotConfigured();

    private sealed class ActionSubscription : IDisposable
    {
        private readonly Action _dispose;
        private bool _disposed;

        public ActionSubscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dispose();
        }
    }
}
