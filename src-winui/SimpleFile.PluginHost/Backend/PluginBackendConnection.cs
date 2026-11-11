using SimpleFile.Ipc;

namespace SimpleFile.PluginHost.Backend;

public sealed class PluginBackendConnection : IAsyncDisposable
{
    /// <summary>How long <see cref="DisposeAsync"/> waits for the killed backend to exit.</summary>
    internal static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(2);

    private readonly NamedPipeJsonClient _client;
    private readonly IPluginProcess _process;
    private int _lost;
    private int _disposed;
    private int _closed;

    internal PluginBackendConnection(NamedPipeJsonClient client, IPluginProcess process)
    {
        _client = client;
        _process = process;
        _client.Disconnected += (_, ex) => RaiseLost(ex);
        _process.Exited += (_, _) => RaiseLost(null);
    }

    /// <summary>Set by the host once the handshake completes; null-forgiving until then (callers only see started connections).</summary>
    public PluginHandshakeResult Handshake { get; internal set; } = null!;

    /// <summary>The bare pipe name the backend serves on; the file service connects to it for archive engines.</summary>
    public string PipeName { get; internal set; } = "";

    /// <summary>Raised once when the backend dies or disconnects on its own; never for <see cref="DisposeAsync"/>.</summary>
    public event EventHandler<Exception?>? Lost;

    /// <summary>
    /// Raised exactly once when the connection ends for any reason: after <see cref="Lost"/> on an unexpected
    /// loss, or from <see cref="DisposeAsync"/> on an intentional stop (disable, restart, teardown).
    /// </summary>
    public event EventHandler? Closed;

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
        RaiseClosed();
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        try { Closed?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Wait (bounded) for the backend to exit, so a restart's new backend never races the old process for
        // its pipe (first_pipe_instance). Subscribed before Kill, which may raise Exited synchronously.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onExited = (_, _) => exited.TrySetResult();
        _process.Exited += onExited;
        try
        {
            _process.Kill();
            if (!_process.HasExited)
            {
                try { await exited.Task.WaitAsync(ExitWait).ConfigureAwait(false); } catch (TimeoutException) { }
            }
        }
        finally
        {
            _process.Exited -= onExited;
        }

        try { await _client.DisposeAsync().ConfigureAwait(false); } catch { }
        _process.Dispose();
        RaiseClosed();
    }
}
