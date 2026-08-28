using System.IO.Pipes;
using System.Text.Json;
using SimpleFile.Ipc;

namespace SimpleFile.Tests;

internal sealed class FakeIpcServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    private FakeIpcServer(string pipeName, NamedPipeServerStream pipe)
    {
        PipeName = pipeName;
        _pipe = pipe;
    }

    public string PipeName { get; }

    public static FakeIpcServer Create()
    {
        var name = "SimpleFile.Test." + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        return new FakeIpcServer(name, pipe);
    }

    public Task WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        return _pipe.WaitForConnectionAsync(cancellationToken);
    }

    public static async Task<(FakeIpcServer Server, NamedPipeJsonClient Client)> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var server = Create();
        try
        {
            var connect = NamedPipeJsonClient.ConnectAsync(server.PipeName, TimeSpan.FromSeconds(5), timeout.Token);
            await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var client = await connect.ConfigureAwait(false);
            return (server, client);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonRpcRequest> ReadRequestAsync(CancellationToken cancellationToken = default)
    {
        var payload = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonRpcRequest>(payload, IpcJson.Options)
            ?? throw new InvalidOperationException("IPC request was empty.");
    }

    public Task SendResultAsync(int id, object? result, CancellationToken cancellationToken = default)
    {
        return WriteAsync(
            new
            {
                jsonrpc = Protocol.JsonRpc,
                id,
                result,
            },
            cancellationToken);
    }

    public Task SendErrorAsync(int id, int code, string message, CancellationToken cancellationToken = default)
    {
        return WriteAsync(
            new
            {
                jsonrpc = Protocol.JsonRpc,
                id,
                error = new { code, message },
            },
            cancellationToken);
    }

    public Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        return WriteAsync(
            new
            {
                jsonrpc = Protocol.JsonRpc,
                method,
                @params,
            },
            cancellationToken);
    }

    public Task SendBinaryFrameAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        return WritePayloadAsync(payload, cancellationToken);
    }

    private async Task WriteAsync<T>(T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, IpcJson.Options);
        await WritePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePayloadAsync(byte[] payload, CancellationToken cancellationToken)
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
                throw new IOException("Fake IPC server pipe closed while reading a frame.");
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

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
