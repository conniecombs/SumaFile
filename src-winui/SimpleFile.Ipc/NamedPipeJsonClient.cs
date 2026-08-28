using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace SimpleFile.Ipc;

internal readonly record struct IpcResultPayload(string? Json, object? TypedValue, bool HasTypedValue)
{
    public static IpcResultPayload FromJson(string json) => new(json, null, false);

    public static IpcResultPayload FromTyped(object? value) => new(null, value, true);
}

public sealed class NamedPipeJsonClient : ISimpleFileIpc
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcResultPayload>> _pending = new();
    private readonly object _handlersLock = new();
    private readonly Dictionary<string, List<Subscription>> _handlers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _loopCts = new();
    private readonly Task _receiveLoop;

    private int _nextId;
    private int _disposed;

    private NamedPipeJsonClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        IsConnected = true;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public bool IsConnected { get; private set; }

    public int InFlightCount => _pending.Count;

    public event EventHandler<Exception?>? Disconnected;

    public static async Task<NamedPipeJsonClient> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var name = pipeName.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
            ? pipeName["\\\\.\\pipe\\".Length..]
            : pipeName;

        var pipe = new NamedPipeClientStream(
            ".",
            name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return new NamedPipeJsonClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<HandshakeResult> HandshakeAsync(string authToken, CancellationToken cancellationToken = default)
    {
        return InvokeAsync<HandshakeResult>(
            Protocol.HandshakeMethod,
            new HandshakeParams { AuthToken = authToken },
            cancellationToken);
    }

    public Task<HealthResult> HealthAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync<HealthResult>(Protocol.HealthMethod, new { }, cancellationToken);
    }

    public Task<string> GetAppVersionAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync<string>(Protocol.GetAppVersionMethod, new { }, cancellationToken);
    }

    public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync<string>(Protocol.GetHomeDirMethod, new { }, cancellationToken);
    }

    public async Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default)
    {
        var drives = await InvokeAsync<DriveInfo[]>(Protocol.ListDrivesMethod, new { }, cancellationToken)
            .ConfigureAwait(false);
        return drives;
    }

    public Task SelectDirectoryAsync(string? defaultPath = null, CancellationToken cancellationToken = default)
    {
        return InvokeAsync(
            Protocol.SelectDirectoryMethod,
            new SelectDirectoryParams { DefaultPath = defaultPath },
            cancellationToken);
    }

    public Task ShowMainWindowAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync(Protocol.ShowMainWindowMethod, new { }, cancellationToken);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync(Protocol.ShutdownMethod, new { }, cancellationToken);
    }

    public Task<string?> GetDbSettingAsync(string key, CancellationToken ct = default)
        => InvokeAsync<string?>(Protocol.GetDbSettingMethod, new { key }, ct);

    public Task SetDbSettingAsync(string key, string value, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.SetDbSettingMethod, new { key, value }, ct);

    public Task<string> CreateDirectoryAsync(string path, string name, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.CreateDirectoryMethod, new { path, name }, ct);

    public Task<string> CreateFileAsync(string path, string name, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.CreateFileMethod, new { path, name }, ct);

    public Task DeleteEntryAsync(string path, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.DeleteEntryMethod, new { path }, ct);

    public Task MoveToTrashAsync(string[] paths, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.MoveToTrashMethod, new { paths }, ct);

    public Task<string> RenameEntryAsync(string path, string newName, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.RenameEntryMethod, new { path, newName }, ct);

    public Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default)
        => InvokeAsync<string[]>(Protocol.BatchRenameMethod, new { entries }, ct);

    public Task<string> CopyEntryAsync(string source, string destination, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.CopyEntryMethod, new { source, destination }, ct);

    public Task<string> MoveEntryAsync(string source, string destination, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.MoveEntryMethod, new { source, destination }, ct);

    public Task<string> CopyEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.CopyEntryResolvedMethod, new { source, destination, conflictAction }, ct);

    public Task<string> MoveEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.MoveEntryResolvedMethod, new { source, destination, conflictAction }, ct);

    public Task<FileEntry> GetEntryInfoAsync(string path, CancellationToken ct = default)
        => InvokeAsync<FileEntry>(Protocol.GetEntryInfoMethod, new { path }, ct);

    public Task OpenFileAsync(string path, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.OpenFileMethod, new { path }, ct);

    public Task RevealInFolderAsync(string path, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.RevealInFolderMethod, new { path }, ct);

    public Task OpenExternalUrlAsync(string url, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.OpenExternalUrlMethod, new { url }, ct);

    public Task<ArchiveInfo> ListArchiveAsync(string path, CancellationToken ct = default)
        => InvokeAsync<ArchiveInfo>(Protocol.ListArchiveMethod, new { path }, ct);

    public Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.ExtractArchiveMethod, new { archivePath, destination }, ct);

    public Task CreateArchiveAsync(string[] paths, string archivePath, string format, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.CreateArchiveMethod, new { paths, archivePath, format }, ct);

    public Task<FilePreview> ReadFilePreviewAsync(string path, ulong? maxSize = null, CancellationToken ct = default)
        => InvokeAsync<FilePreview>(Protocol.ReadFilePreviewMethod, new { path, maxSize }, ct);

    public Task<string> GenerateThumbnailAsync(string path, uint size, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.GenerateThumbnailMethod, new { path, size }, ct);

    public Task<ThumbnailResult[]> GenerateThumbnailsAsync(string[] paths, uint size, CancellationToken ct = default)
        => InvokeAsync<ThumbnailResult[]>(Protocol.GenerateThumbnailsMethod, new { paths, size }, ct);

    public Task OpenFileWithAsync(string path, string application, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.OpenFileWithMethod, new { path, application }, ct);

    public Task<FileComparison> CompareFilesAsync(string pathA, string pathB, CancellationToken ct = default)
        => InvokeAsync<FileComparison>(Protocol.CompareFilesMethod, new { pathA, pathB }, ct);

    public Task<Checksums> ComputeChecksumAsync(string path, CancellationToken ct = default)
        => InvokeAsync<Checksums>(Protocol.ComputeChecksumMethod, new { path }, ct);

    public Task<ImageMetadata> GetImageMetadataAsync(string path, CancellationToken ct = default)
        => InvokeAsync<ImageMetadata>(Protocol.GetImageMetadataMethod, new { path }, ct);

    public Task<FileMetadata> GetFileMetadataAsync(string path, CancellationToken ct = default)
        => InvokeAsync<FileMetadata>(Protocol.GetFileMetadataMethod, new { path }, ct);

    public Task<TreeNode[]> ListSubdirectoriesAsync(string path, CancellationToken ct = default)
        => InvokeAsync<TreeNode[]>(Protocol.ListSubdirectoriesMethod, new { path }, ct);

    public Task<ulong> CalculateFolderSizeAsync(string path, CancellationToken ct = default)
        => InvokeAsync<ulong>(Protocol.CalculateFolderSizeMethod, new { path }, ct);

    public Task<ulong> CountFolderItemsAsync(string path, CancellationToken ct = default)
        => InvokeAsync<ulong>(Protocol.CountFolderItemsMethod, new { path }, ct);

    public Task<TransferResult[]> CopyWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default)
        => InvokeAsync<TransferResult[]>(Protocol.CopyWithProgressMethod, new { sources, destination, operationId, conflictAction }, ct);

    public Task<TransferResult[]> MoveWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default)
        => InvokeAsync<TransferResult[]>(Protocol.MoveWithProgressMethod, new { sources, destination, operationId, conflictAction }, ct);

    public Task CancelOperationAsync(string operationId, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.CancelOperationMethod, new { operationId }, ct);

    public async Task<SearchResult[]> SearchFilesAsync(
        SearchOptions options,
        Action<SearchResult[]>? onBatch = null,
        Action<int>? onComplete = null,
        CancellationToken ct = default)
    {
        IDisposable? batchSubscription = null;
        IDisposable? completeSubscription = null;
        if (onBatch is not null)
        {
            batchSubscription = On<SearchResult[]>(Protocol.SearchResultsBatchEvent, onBatch);
        }

        if (onComplete is not null)
        {
            completeSubscription = On<int>(Protocol.SearchCompleteEvent, onComplete);
        }

        try
        {
            return await InvokeAsync<SearchResult[]>(
                    Protocol.SearchFilesMethod,
                    new { options },
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            completeSubscription?.Dispose();
            batchSubscription?.Dispose();
        }
    }

    public Task CancelSearchAsync(string searchId, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.CancelSearchMethod, new { searchId }, ct);

    public Task WatchDirectoryAsync(string path, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.WatchDirectoryMethod, new { path }, ct);

    public Task UnwatchDirectoryAsync(CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.UnwatchDirectoryMethod, new { }, ct);

    public Task CancelFolderSizeAsync(CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.CancelFolderSizeMethod, new { }, ct);

    public Task CancelFolderItemCountAsync(CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.CancelFolderItemCountMethod, new { }, ct);

    public Task CancelCountItemsAsync(CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.CancelCountItemsMethod, new { }, ct);

    public Task<bool> CheckRarInstalledAsync(CancellationToken ct = default)
        => InvokeAsync<bool>(Protocol.CheckRarInstalledMethod, new { }, ct);

    public Task<RarInstallPlan> PrepareRarInstallAsync(CancellationToken ct = default)
        => InvokeAsync<RarInstallPlan>(Protocol.PrepareRarInstallMethod, new { }, ct);

    public Task DiscardRarInstallAsync(string confirmationToken, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.DiscardRarInstallMethod, new { confirmationToken }, ct);

    public Task<string> InstallRarAsync(string confirmationToken, CancellationToken ct = default)
        => InvokeAsync<string>(Protocol.InstallRarMethod, new { confirmationToken }, ct);

    public Task<CleanupResult> DiskCleanupAsync(string directory, ulong? sizeThreshold, string? operationId, CancellationToken ct = default)
        => InvokeAsync<CleanupResult>(Protocol.DiskCleanupMethod, new { directory, sizeThreshold, operationId }, ct);

    public Task CancelDiskCleanupAsync(CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.CancelDiskCleanupMethod, new { }, ct);

    public Task<DuplicateCheckResult> DuplicateCheckAsync(string directory, ulong? minSize, ulong? partialHashBytes, string? operationId, CancellationToken ct = default)
        => InvokeAsync<DuplicateCheckResult>(Protocol.DuplicateCheckMethod, new { directory, minSize, partialHashBytes, operationId }, ct);

    public Task CancelDuplicateCheckAsync(CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.CancelDuplicateCheckMethod, new { }, ct);

    public Task<Tag[]> GetAllTagsAsync(CancellationToken ct = default)
        => InvokeAsync<Tag[]>(Protocol.GetAllTagsMethod, new { }, ct);

    public Task<Tag> CreateTagAsync(string name, string color, CancellationToken ct = default)
        => InvokeAsync<Tag>(Protocol.CreateTagMethod, new { name, color }, ct);

    public Task<Tag> UpdateTagAsync(long id, string name, string color, CancellationToken ct = default)
        => InvokeAsync<Tag>(Protocol.UpdateTagMethod, new { id, name, color }, ct);

    public Task DeleteTagAsync(long id, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.DeleteTagMethod, new { id }, ct);

    public Task<Tag[]> GetTagsForPathAsync(string path, CancellationToken ct = default)
        => InvokeAsync<Tag[]>(Protocol.GetTagsForPathMethod, new { path }, ct);

    public Task SetTagsForPathAsync(string path, long[] tagIds, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.SetTagsForPathMethod, new { path, tagIds }, ct);

    public Task<Dictionary<string, Tag>> GetAllFileTagsAsync(CancellationToken ct = default)
        => InvokeAsync<Dictionary<string, Tag>>(Protocol.GetAllFileTagsMethod, new { }, ct);

    public Task<string[]> GetFilesWithTagAsync(long tagId, CancellationToken ct = default)
        => InvokeAsync<string[]>(Protocol.GetFilesWithTagMethod, new { tagId }, ct);

    public Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default)
        => InvokeAsync<SmartFolder[]>(Protocol.LoadSmartFoldersMethod, new { }, ct);

    public Task<SmartFolder[]> SaveSmartFolderAsync(SmartFolder folder, CancellationToken ct = default)
        => InvokeAsync<SmartFolder[]>(Protocol.SaveSmartFolderMethod, new { folder }, ct);

    public Task<SmartFolder[]> DeleteSmartFolderAsync(string id, CancellationToken ct = default)
        => InvokeAsync<SmartFolder[]>(Protocol.DeleteSmartFolderMethod, new { id }, ct);

    public Task<AppAboutInfo> GetAppAboutInfoAsync(CancellationToken ct = default)
        => InvokeAsync<AppAboutInfo>(Protocol.GetAppAboutInfoMethod, new { }, ct);

    public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
        => InvokeAsync<UpdateInfo?>(Protocol.CheckForUpdateMethod, new { }, ct);

    public Task InstallUpdateAsync(CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.InstallUpdateMethod, new { }, ct);

    public Task OpenTerminalAsync(string path, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.OpenTerminalMethod, new { path }, ct);

    public Task OpenPowershellAdminAsync(string path, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.OpenPowershellAdminMethod, new { path }, ct);

    public Task<GitStatus> GetGitStatusAsync(string path, CancellationToken ct = default)
        => InvokeAsync<GitStatus>(Protocol.GetGitStatusMethod, new { path }, ct);

    public Task<FileEntry[]> GetGitFileStatusesAsync(string path, CancellationToken ct = default)
        => InvokeAsync<FileEntry[]>(Protocol.GetGitFileStatusesMethod, new { path }, ct);

    public Task GitPullAsync(string path, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.GitPullMethod, new { path }, ct);

    public Task GitPushAsync(string path, CancellationToken ct = default)
        => InvokeAsync<object?>(Protocol.GitPushMethod, new { path }, ct);

    public async Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null)
    {
        var requestId = AllocateId();
        var streamedEntries = new List<FileEntry>();
        IDisposable? subscription = null;
        if (onChunk is not null)
        {
            subscription = On<DirectoryListingChunkNotification>(
                Protocol.ListDirectoryChunkEvent,
                notification =>
                {
                    if (notification.RequestId == requestId)
                    {
                        var chunk = NormalizeListingChunk(notification.ToChunk());
                        if (options?.FinalEntries == false)
                        {
                            lock (streamedEntries)
                            {
                                streamedEntries.AddRange(chunk.Entries);
                            }
                        }

                        onChunk(chunk);
                    }
                });
        }

        try
        {
            var listing = await InvokeAllocatedAsync<DirectoryListing>(
                    requestId,
                    Protocol.ListDirectoryMethod,
                    BuildListDirectoryParams(path, options),
                    cancellationToken)
                .ConfigureAwait(false);
            NormalizeListing(listing);
            if (options?.FinalEntries == false && listing.Entries.Count == 0)
            {
                lock (streamedEntries)
                {
                    listing.Entries = [.. streamedEntries];
                }
            }

            return listing;
        }
        finally
        {
            subscription?.Dispose();
        }
    }

    public async Task InvokeAsync(string method, object? args, CancellationToken cancellationToken = default)
    {
        _ = await InvokeAllocatedAsync<object?>(AllocateId(), method, args, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<TResult> InvokeAsync<TResult>(
        string method,
        object? args,
        CancellationToken cancellationToken = default)
    {
        return InvokeAllocatedAsync<TResult>(AllocateId(), method, args, cancellationToken);
    }

    public IDisposable On<T>(string eventName, Action<T> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(
            this,
            eventName,
            payload =>
            {
                var value = payload.Deserialize<T>(IpcJson.Options);
                if (value is not null)
                {
                    handler(value);
                }
            },
            value =>
            {
                if (value is T typed)
                {
                    handler(typed);
                }
                else if (value is null && default(T) is null)
                {
                    handler(default!);
                }
            });

        lock (_handlersLock)
        {
            if (!_handlers.TryGetValue(eventName, out var list))
            {
                list = [];
                _handlers[eventName] = list;
            }

            list.Add(subscription);
        }

        return subscription;
    }

    private async Task<TResult> InvokeAllocatedAsync<TResult>(
        int id,
        string method,
        object? args,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!IsConnected)
        {
            throw IpcException.Transport("IPC client is not connected.");
        }

        var completion = new TaskCompletionSource<IpcResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new IpcException(Protocol.ErrInternal, $"Duplicate IPC request id {id}.");
        }

        // Cancel only drops this await. It must not write a JSON-RPC cancel;
        // backend cancellation stays named commands when those exist.
        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        if (cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            var request = new JsonRpcRequest
            {
                Id = id,
                Method = method,
                Params = args,
            };
            var requestPayload = JsonSerializer.SerializeToUtf8Bytes(request, IpcJson.Options);
            await WriteFrameAsync(requestPayload, _loopCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw IpcException.Transport($"Failed to write IPC request '{method}'.", exception);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        IpcResultPayload resultPayload;
        try
        {
            resultPayload = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }

            throw;
        }

        return DeserializeResult<TResult>(method, resultPayload);
    }

    private static TResult DeserializeResult<TResult>(string method, IpcResultPayload payload)
    {
        if (payload.HasTypedValue)
        {
            if (payload.TypedValue is TResult typed)
            {
                return typed;
            }

            if (payload.TypedValue is null && default(TResult) is null)
            {
                return default!;
            }

            throw new IpcException(
                Protocol.ErrInternal,
                $"IPC method '{method}' returned binary {payload.TypedValue?.GetType().Name ?? "null"}, not {typeof(TResult).Name}.");
        }

        var raw = payload.Json ?? "null";
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "null" : raw);
        var element = document.RootElement;
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (default(TResult) is null)
            {
                return default!;
            }

            throw new IpcException(Protocol.ErrInternal, $"IPC method '{method}' returned no result.");
        }

        if (typeof(TResult) == typeof(string))
        {
            var text = element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? ""
                : element.GetRawText();
            return (TResult)(object)text;
        }

        return element.Deserialize<TResult>(IpcJson.Options)
            ?? throw new IpcException(Protocol.ErrInternal, $"IPC method '{method}' returned no result.");
    }

    private int AllocateId()
    {
        return Interlocked.Increment(ref _nextId);
    }

    private static object BuildListDirectoryParams(string path, ListDirectoryOptions? options)
    {
        if (options is null)
        {
            return new PathParams { Path = path };
        }

        return new ListDirectoryParams
        {
            Path = path,
            Mode = options.Mode,
            FinalEntries = options.FinalEntries,
            SortBy = options.SortBy,
            SortAscending = options.SortAscending,
            Filter = options.Filter,
            IncludeHidden = options.IncludeHidden,
        };
    }

    private static DirectoryListingChunk NormalizeListingChunk(DirectoryListingChunk chunk)
    {
        NormalizeEntries(chunk.Path, chunk.Entries);
        return chunk;
    }

    private static void NormalizeListing(DirectoryListing listing)
    {
        NormalizeEntries(listing.Path, listing.Entries);
    }

    private static void NormalizeEntries(string basePath, List<FileEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Path))
            {
                entry.Path = JoinEntryPath(basePath, entry.Name);
            }

            if (!entry.IsDir && string.IsNullOrEmpty(entry.Extension))
            {
                entry.Extension = System.IO.Path.GetExtension(entry.Name).TrimStart('.');
            }
        }
    }

    private static string JoinEntryPath(string basePath, string name)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return name;
        }

        var trimmed = basePath.Trim();
        if (trimmed == "/")
        {
            return "/" + name;
        }

        var clean = trimmed.TrimEnd('\\', '/');
        var separator = clean.Contains('/') && !clean.Contains('\\') ? '/' : '\\';
        return $"{clean}{separator}{name}";
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? fault = null;
        try
        {
            while (_disposed == 0 && !_loopCts.IsCancellationRequested)
            {
                var payload = await ReadFrameAsync(_loopCts.Token).ConfigureAwait(false);
                HandlePayload(payload);
            }
        }
        catch (OperationCanceledException) when (_disposed != 0 || _loopCts.IsCancellationRequested)
        {
            // Clean shutdown.
        }
        catch (Exception exception)
        {
            fault = exception;
        }
        finally
        {
            IsConnected = false;
            if (_disposed == 0)
            {
                FailAllPending(IpcException.Transport("IPC pipe closed.", fault));
                try
                {
                    Disconnected?.Invoke(this, fault);
                }
                catch
                {
                    // Subscriber failures must not escape the receive loop.
                }
            }
            else
            {
                FailAllPending(new ObjectDisposedException(nameof(NamedPipeJsonClient)));
            }
        }
    }

    private void HandlePayload(byte[] payload)
    {
        if (BinaryFrameCodec.TryDecode(payload, out var binaryMessage))
        {
            HandleBinaryPayload(binaryMessage!);
            return;
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (IsNotification(root))
        {
            DispatchNotification(root);
            return;
        }

        if (!root.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt32(out var id))
        {
            return;
        }

        if (!_pending.TryRemove(id, out var pending))
        {
            return;
        }

        if (root.TryGetProperty("error", out var errorElement)
            && errorElement.ValueKind == JsonValueKind.Object)
        {
            var code = errorElement.TryGetProperty("code", out var codeElement)
                && codeElement.TryGetInt32(out var parsed)
                    ? parsed
                    : Protocol.ErrInternal;
            var message = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? ""
                : "";
            pending.TrySetException(new IpcException(code, message));
            return;
        }

        var raw = root.TryGetProperty("result", out var resultElement)
            ? resultElement.GetRawText()
            : "null";
        pending.TrySetResult(IpcResultPayload.FromJson(raw));
    }

    private void HandleBinaryPayload(BinaryFrameMessage message)
    {
        if (message.ResponseId is int responseId)
        {
            if (_pending.TryRemove(responseId, out var pending))
            {
                pending.TrySetResult(IpcResultPayload.FromTyped(message.Payload));
            }

            return;
        }

        if (!string.IsNullOrEmpty(message.EventName))
        {
            DispatchTypedNotification(message.EventName, message.Payload);
        }
    }

    private static bool IsNotification(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var method)
            || method.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!root.TryGetProperty("id", out var id) || id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        return !root.TryGetProperty("result", out _) && !root.TryGetProperty("error", out _);
    }

    private void DispatchNotification(JsonElement root)
    {
        var method = root.GetProperty("method").GetString();
        if (string.IsNullOrEmpty(method))
        {
            return;
        }

        if (!root.TryGetProperty("params", out var paramsElement))
        {
            paramsElement = default;
        }

        Subscription[] snapshot;
        lock (_handlersLock)
        {
            if (!_handlers.TryGetValue(method, out var list) || list.Count == 0)
            {
                return;
            }

            snapshot = [.. list];
        }

        foreach (var subscription in snapshot)
        {
            try
            {
                subscription.Invoke(paramsElement);
            }
            catch
            {
                // A handler must not tear down the receive loop.
            }
        }
    }

    private void DispatchTypedNotification(string eventName, object? payload)
    {
        Subscription[] snapshot;
        lock (_handlersLock)
        {
            if (!_handlers.TryGetValue(eventName, out var list) || list.Count == 0)
            {
                return;
            }

            snapshot = [.. list];
        }

        foreach (var subscription in snapshot)
        {
            try
            {
                subscription.Invoke(payload);
            }
            catch
            {
                // A handler must not tear down the receive loop.
            }
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private async Task WriteFrameAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var frame = FrameCodec.Encode(payload);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactAsync(header, cancellationToken).ConfigureAwait(false);
        var length = FrameCodec.DecodeLength(header);
        var payload = new byte[length];
        await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            var read = await _pipe.ReadAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("IPC pipe closed while reading a frame.");
            }

            remaining = remaining[read..];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        FailAllPending(new ObjectDisposedException(nameof(NamedPipeJsonClient)));
        IsConnected = false;
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _loopCts.Dispose();
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_handlersLock)
        {
            if (_handlers.TryGetValue(subscription.EventName, out var list))
            {
                list.Remove(subscription);
                if (list.Count == 0)
                {
                    _handlers.Remove(subscription.EventName);
                }
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly NamedPipeJsonClient _client;
        private readonly Action<JsonElement> _jsonHandler;
        private readonly Action<object?> _typedHandler;
        private int _disposed;

        public Subscription(
            NamedPipeJsonClient client,
            string eventName,
            Action<JsonElement> jsonHandler,
            Action<object?> typedHandler)
        {
            _client = client;
            EventName = eventName;
            _jsonHandler = jsonHandler;
            _typedHandler = typedHandler;
        }

        public string EventName { get; }

        public void Invoke(JsonElement payload)
        {
            if (_disposed != 0)
            {
                return;
            }

            _jsonHandler(payload);
        }

        public void Invoke(object? payload)
        {
            if (_disposed != 0)
            {
                return;
            }

            _typedHandler(payload);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _client.Unsubscribe(this);
        }
    }
}
