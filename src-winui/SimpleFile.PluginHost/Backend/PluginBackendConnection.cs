using SimpleFile.Ipc;

namespace SimpleFile.PluginHost.Backend;

public sealed class PluginBackendConnection : IAsyncDisposable
{
    private readonly NamedPipeJsonClient _client;
    private readonly IPluginProcess _process;
    private int _lost;
    private int _disposed;

    internal PluginBackendConnection(
        NamedPipeJsonClient client,
        IPluginProcess process,
        PluginHandshakeResult handshake)
    {
        _client = client;
        _process = process;
        Handshake = handshake;
        _client.Disconnected += (_, ex) => RaiseLost(ex);
        _process.Exited += (_, _) => RaiseLost(null);
    }

    public PluginHandshakeResult Handshake { get; }

    public event EventHandler<Exception?>? Lost;

    public Task<T> InvokeAsync<T>(string method, object? args, CancellationToken ct) =>
        _client.InvokeAsync<T>(method, args, ct);

    public IDisposable On<T>(string eventName, Action<T> handler) => _client.On(eventName, handler);

    public IDisposable OnHostRequest(string method, InboundRequestHandler handler) =>
        _client.OnRequest(method, handler);

    private void RaiseLost(Exception? ex)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _lost, 1) == 1)
        {
            return;
        }

        try { Lost?.Invoke(this, ex); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _process.Kill();
        try { await _client.DisposeAsync().ConfigureAwait(false); } catch { }
        _process.Dispose();
    }
}
