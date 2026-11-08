using SimpleFile.Core;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginSdk;

namespace SimpleFile.PluginHost;

/// <summary>
/// Per-plugin runtime owned by <see cref="PluginManager"/>: the activated instance, the backend connection,
/// recent start failures, and the <see cref="IPluginHost"/> the plugin keeps for its lifetime.
/// </summary>
/// <remarks>
/// <see cref="PluginHostServices"/> caches its backend start, including a failed one, so each backend
/// generation gets fresh services. This class is the stable host the plugin sees; it forwards to the
/// current services and re-attaches backend event subscriptions when the generation changes.
/// <see cref="Connection"/> and the failure window are read and written only under the manager's lock.
/// </remarks>
internal sealed class PluginRuntime : IPluginHost
{
    private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);
    private const int FailuresToFault = 3;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _activation = new(1, 1);
    private readonly List<Subscription> _subscriptions = [];
    private readonly Queue<long> _failures = new();
    private readonly Func<PluginHostServices, Action<PluginBackendConnection>, CancellationToken, Task<PluginBackendConnection>> _startBackend;
    private readonly Func<bool> _backendAllowed;
    private readonly ISettingsBackend _settings;
    private readonly IPluginProgressSink _progress;
    private readonly IPluginDialogs _dialogs;
    private readonly IPluginWorkspace _workspace;
    private readonly Action<string, string> _log;
    private PluginHostServices _services;
    private IPlugin? _instance;

    public PluginRuntime(
        string pluginId,
        Func<PluginHostServices, Action<PluginBackendConnection>, CancellationToken, Task<PluginBackendConnection>> startBackend,
        Func<bool> backendAllowed,
        ISettingsBackend settings,
        IPluginProgressSink progress,
        IPluginDialogs dialogs,
        IPluginWorkspace workspace,
        Action<string, string> log)
    {
        PluginId = pluginId;
        _startBackend = startBackend;
        _backendAllowed = backendAllowed;
        _settings = settings;
        _progress = progress;
        _dialogs = dialogs;
        _workspace = workspace;
        _log = log;
        _services = CreateServices();
    }

    public string PluginId { get; }

    /// <summary>The running backend of the current generation; guarded by the manager's lock.</summary>
    public PluginBackendConnection? Connection { get; set; }

    public bool IsActivated => Volatile.Read(ref _instance) is not null;

    private PluginHostServices Current
    {
        get { lock (_gate) return _services; }
    }

    public IPluginSettings Settings => Current.Settings;
    public IPluginProgress Progress => Current.Progress;
    public IPluginDialogs Dialogs => Current.Dialogs;
    public IPluginWorkspace Workspace => Current.Workspace;
    public IPluginLogger Logger => Current.Logger;

    public bool IsCurrent(PluginHostServices services)
    {
        lock (_gate) return ReferenceEquals(_services, services);
    }

    /// <summary>Ends the current backend generation so the next call starts the backend afresh.</summary>
    public void NewGeneration()
    {
        lock (_gate)
        {
            _services = CreateServices();
            foreach (var subscription in _subscriptions)
            {
                subscription.AttachTo(_services);
            }
        }
    }

    /// <summary>Records a failed start and returns true when three fell within the last 60 s.</summary>
    public bool RecordFailure()
    {
        var now = Environment.TickCount64;
        _failures.Enqueue(now);
        while (_failures.Count > 0 && now - _failures.Peek() > FailureWindow.TotalMilliseconds)
        {
            _failures.Dequeue();
        }

        return _failures.Count >= FailuresToFault;
    }

    public Task EnsureBackendAsync(CancellationToken ct)
    {
        ThrowIfBackendUnavailable();
        return Current.EnsureBackendAsync(ct);
    }

    public Task<T> InvokeBackendAsync<T>(string method, object? args, CancellationToken ct)
    {
        ThrowIfBackendUnavailable();
        return Current.InvokeBackendAsync<T>(method, args, ct);
    }

    public IDisposable OnBackendEvent<T>(string eventName, Action<T> handler)
    {
        var subscription = new Subscription(this, services => services.OnBackendEvent(eventName, handler));
        lock (_gate)
        {
            _subscriptions.Add(subscription);
            subscription.AttachTo(_services);
        }

        return subscription;
    }

    /// <summary>Creates and activates the plugin on first use; later calls return the same instance.</summary>
    public async Task<IPlugin> EnsureActivatedAsync(Func<IPlugin> create, CancellationToken ct)
    {
        if (Volatile.Read(ref _instance) is { } existing) return existing;

        await _activation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_instance is { } raced) return raced;

            var instance = create();
            await instance.ActivateAsync(this, ct).ConfigureAwait(false);
            Volatile.Write(ref _instance, instance);
            return instance;
        }
        finally
        {
            _activation.Release();
        }
    }

    public async Task DeactivateAsync()
    {
        await _activation.WaitAsync().ConfigureAwait(false);
        try
        {
            var instance = Interlocked.Exchange(ref _instance, null);
            if (instance is null) return;

            try
            {
                await instance.DeactivateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log(PluginId, $"[error] DeactivateAsync failed: {ex}");
            }
        }
        finally
        {
            _activation.Release();
        }
    }

    private void ThrowIfBackendUnavailable()
    {
        if (!_backendAllowed())
        {
            throw new PluginBackendException($"The backend of plugin '{PluginId}' is not available.");
        }
    }

    private PluginHostServices CreateServices()
    {
        PluginHostServices services = null!;
        services = new PluginHostServices(
            PluginId,
            (configure, ct) => _startBackend(services, configure, ct),
            _settings,
            _progress,
            _dialogs,
            _workspace,
            _log);
        return services;
    }

    private sealed class Subscription(PluginRuntime owner, Func<PluginHostServices, IDisposable> attach) : IDisposable
    {
        private IDisposable? _inner;
        private bool _disposed;

        // Called under owner._gate.
        public void AttachTo(PluginHostServices services)
        {
            if (_disposed) return;
            _inner?.Dispose();
            _inner = attach(services);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                owner._subscriptions.Remove(this);
                _inner?.Dispose();
                _inner = null;
            }
        }
    }
}
