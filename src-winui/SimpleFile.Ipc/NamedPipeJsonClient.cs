using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace SimpleFile.Ipc;

internal readonly record struct IpcResultPayload(string? Json, object? TypedValue, bool HasTypedValue)
{
    public static IpcResultPayload FromJson(string json) => new(json, null, false);

    public static IpcResultPayload FromTyped(object? value) => new(null, value, true);
}

public sealed partial class NamedPipeJsonClient : ISimpleFileIpc
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
