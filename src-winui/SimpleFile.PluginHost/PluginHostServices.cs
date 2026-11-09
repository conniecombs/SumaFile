using SimpleFile.Core;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginSdk;

namespace SimpleFile.PluginHost;

/// <summary>The <see cref="IPluginHost"/> a plugin's C# half receives.</summary>
public sealed class PluginHostServices : IPluginHost
{
    private readonly Func<Action<PluginBackendConnection>, CancellationToken, Task<PluginBackendConnection>> _backendFactory;
    private readonly List<Func<PluginBackendConnection, IDisposable>> _pending = [];
    private PluginBackendConnection? _connection;
    private readonly object _gate = new();
    private Task<PluginBackendConnection>? _backend;

    public PluginHostServices(
        string pluginId,
        Func<Action<PluginBackendConnection>, CancellationToken, Task<PluginBackendConnection>> backend,
        ISettingsBackend settings,
        IPluginProgressSink progress,
        IPluginDialogs dialogs,
        IPluginWorkspace workspace,
        IPluginWindows windows,
        IPluginPanels panels,
        IPluginDecorations decorations,
        Action<string, string> log)
    {
        PluginId = pluginId;
        _backendFactory = backend;
        Settings = new SettingsFacade(pluginId, settings);
        Progress = new ProgressFacade(pluginId, progress);
        Dialogs = dialogs;
        Workspace = workspace;
        Windows = windows;
        Panels = panels;
        Decorations = decorations;
        Logger = new LoggerFacade(pluginId, log);
    }

    public string PluginId { get; }
    public IPluginSettings Settings { get; }
    public IPluginProgress Progress { get; }
    public IPluginDialogs Dialogs { get; }
    public IPluginWorkspace Workspace { get; }
    public IPluginLogger Logger { get; }
    public IPluginWindows Windows { get; }
    public IPluginPanels Panels { get; }
    public IPluginDecorations Decorations { get; }

    private Task<PluginBackendConnection> GetBackend()
    {
        lock (_gate)
        {
            // Started once with no caller token so one caller's cancellation cannot poison the shared start.
            return _backend ??= _backendFactory(Configure, CancellationToken.None);
        }
    }

    /// <summary>Starts the backend without calling a method; used by the manager for <c>activateOn: startup</c> and restarts.</summary>
    internal Task EnsureBackendAsync(CancellationToken ct) => GetBackend().WaitAsync(ct);

    public async Task<T> InvokeBackendAsync<T>(string method, object? args, CancellationToken ct)
    {
        var connection = await GetBackend().WaitAsync(ct).ConfigureAwait(false);
        return await connection.InvokeAsync<T>(method, args, ct).ConfigureAwait(false);
    }

    // Called by the backend host before the handshake, so subscriptions are in place before any event can arrive.
    private void Configure(PluginBackendConnection connection)
    {
        lock (_gate)
        {
            _connection = connection;
            foreach (var attach in _pending)
            {
                attach(connection);
            }

            _pending.Clear();
        }
    }

    public IDisposable OnBackendEvent<T>(string eventName, Action<T> handler)
    {
        var subscription = new DeferredSubscription();
        Func<PluginBackendConnection, IDisposable> attach = c =>
        {
            subscription.Attach(c.On(eventName, handler));
            return subscription;
        };
        lock (_gate)
        {
            if (_connection is { } connection)
            {
                attach(connection);
            }
            else
            {
                _pending.Add(attach);
            }
        }

        return subscription;
    }

    private sealed class DeferredSubscription : IDisposable
    {
        private readonly object _lock = new();
        private IDisposable? _inner;
        private bool _disposed;

        public void Attach(IDisposable inner)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _inner = inner;
                    return;
                }
            }

            inner.Dispose();
        }

        public void Dispose()
        {
            IDisposable? inner;
            lock (_lock)
            {
                _disposed = true;
                inner = _inner;
                _inner = null;
            }

            inner?.Dispose();
        }
    }

    private sealed class SettingsFacade(string pluginId, ISettingsBackend backend) : IPluginSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct) =>
            backend.GetSettingAsync(Prefixed(key), ct);

        public Task SetAsync(string key, string value, CancellationToken ct) =>
            backend.SetSettingAsync(Prefixed(key), value, ct);

        private string Prefixed(string key)
        {
            if (!PluginHostMethods.IsValidSettingKey(key))
            {
                throw new ArgumentException($"Invalid settings key '{key}'.", nameof(key));
            }

            return PluginHostMethods.SettingsKey(pluginId, key);
        }
    }

    private sealed class ProgressFacade(string pluginId, IPluginProgressSink sink) : IPluginProgress
    {
        public IPluginOperation Begin(string title)
        {
            var cts = new CancellationTokenSource();
            var id = sink.Begin(pluginId, title, () =>
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            });
            return new Operation(sink, id, cts);
        }
    }

    private sealed class Operation(IPluginProgressSink sink, string id, CancellationTokenSource cts) : IPluginOperation
    {
        private int _finished;

        public CancellationToken CancellationToken => cts.Token;

        public void Report(long current, long total, string? detail)
        {
            if (Volatile.Read(ref _finished) == 0)
            {
                sink.Report(id, current, total, detail);
            }
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
            {
                sink.Complete(id);
            }
        }

        public void Fail(string message)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
            {
                sink.Fail(id, message);
            }
        }

        public void Dispose()
        {
            Complete();
            cts.Dispose();
        }
    }

    private sealed class LoggerFacade(string pluginId, Action<string, string> log) : IPluginLogger
    {
        public void Info(string message) => log(pluginId, $"[info] {message}");

        public void Warn(string message) => log(pluginId, $"[warn] {message}");

        public void Error(string message, Exception? exception = null) =>
            log(pluginId, exception is null ? $"[error] {message}" : $"[error] {message}: {exception}");
    }
}

/// <summary>
/// The panel service of a host without panels (<see cref="PluginManagerOptions.PanelsFor"/> is null): no panel is ever
/// visible, <c>Hide</c> does nothing and <c>ShowAsync</c> throws, so a provider that only asks whether a panel is shown
/// works on such a host.
/// </summary>
internal sealed class NoPluginPanels : IPluginPanels
{
    public const string NoPanelsMessage = "This host has no panels.";

    public static NoPluginPanels Instance { get; } = new();

    private NoPluginPanels()
    {
    }

    public Task ShowAsync(string panelId, CancellationToken ct) =>
        Task.FromException(new InvalidOperationException(NoPanelsMessage));

    public void Hide(string panelId)
    {
    }

    public bool IsVisible(string panelId) => false;
}

/// <summary>
/// A plugin's <see cref="IPluginDecorations"/>: <c>Invalidate</c> hands the plugin id to
/// <see cref="PluginManagerOptions.InvalidateDecorations"/>, which only queues work.
/// </summary>
internal sealed class PluginDecorationsFacade(string pluginId, Action<string>? invalidate) : IPluginDecorations
{
    public void Invalidate() => invalidate?.Invoke(pluginId);
}
