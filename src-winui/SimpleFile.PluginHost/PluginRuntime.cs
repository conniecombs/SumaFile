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
    private bool _retired;

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

    /// <summary>True once the file service has this runtime's archive engines; guarded by the manager's lock.</summary>
    public bool ArchiveEnginesRegistered { get; set; }

    /// <summary>How long <see cref="RetireAsync"/> waits for a running activation, and then for DeactivateAsync.</summary>
    private static readonly TimeSpan RetireTimeout = TimeSpan.FromSeconds(5);

    public const string DeactivateTimeoutMessage = "plugin did not deactivate in time";

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
    /// <remarks>Never calls into <see cref="PluginHostServices"/> while holding <c>_gate</c>.</remarks>
    public void NewGeneration()
    {
        var fresh = CreateServices();
        Subscription[] subscriptions;
        lock (_gate)
        {
            _services = fresh;
            subscriptions = _subscriptions.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.AttachTo(fresh);
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
        PluginHostServices current;
        lock (_gate)
        {
            _subscriptions.Add(subscription);
            current = _services;
        }

        subscription.AttachTo(current);
        return subscription;
    }

    /// <summary>Creates and activates the plugin on first use; later calls return the same instance.</summary>
    /// <exception cref="PluginRetiredException">The manager detached this runtime (disable, rescan or dispose).</exception>
    public async Task<IPlugin> EnsureActivatedAsync(Func<IPlugin> create, CancellationToken ct)
    {
        if (!Volatile.Read(ref _retired) && Volatile.Read(ref _instance) is { } existing) return existing;

        await _activation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_retired) throw new PluginRetiredException(PluginId);
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

    /// <summary>Retires the runtime for good and deactivates the instance if one was activated.</summary>
    /// <returns>
    /// False when a running ActivateAsync or the DeactivateAsync call did not finish within
    /// <see cref="RetireTimeout"/>. The instance is then abandoned, so plugin code can never wedge the
    /// manager operation (disable, rescan, dispose) that retires it.
    /// </returns>
    public async Task<bool> RetireAsync()
    {
        // Set first: an activation that finishes late sees it and its command is refused.
        Volatile.Write(ref _retired, true);
        if (!await _activation.WaitAsync(RetireTimeout).ConfigureAwait(false))
        {
            _log(PluginId, $"[error] ActivateAsync did not finish within {RetireTimeout.TotalSeconds:0} s; abandoning the instance.");
            Interlocked.Exchange(ref _instance, null);
            return false;
        }

        try
        {
            var instance = Interlocked.Exchange(ref _instance, null);
            if (instance is null) return true;

            try
            {
                await instance.DeactivateAsync().WaitAsync(RetireTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log(PluginId, $"[error] DeactivateAsync did not finish within {RetireTimeout.TotalSeconds:0} s; abandoning the instance.");
                return false;
            }
            catch (Exception ex)
            {
                _log(PluginId, $"[error] DeactivateAsync failed: {ex}");
            }

            return true;
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

    /// <summary>
    /// One backend event subscription, re-attached to each services generation. Attaching happens with no
    /// runtime lock held; a racing attach to a generation that is no longer current is discarded.
    /// </summary>
    private sealed class Subscription(PluginRuntime owner, Func<PluginHostServices, IDisposable> attach) : IDisposable
    {
        private readonly object _lock = new();
        private PluginHostServices? _attachedTo;
        private IDisposable? _inner;
        private bool _disposed;

        public void AttachTo(PluginHostServices services)
        {
            lock (_lock)
            {
                if (_disposed || ReferenceEquals(_attachedTo, services)) return;
            }

            var inner = attach(services);
            IDisposable? discard;
            lock (_lock)
            {
                if (_disposed || ReferenceEquals(_attachedTo, services) || !owner.IsCurrent(services))
                {
                    discard = inner;
                }
                else
                {
                    discard = _inner;
                    _inner = inner;
                    _attachedTo = services;
                }
            }

            discard?.Dispose();
        }

        public void Dispose()
        {
            IDisposable? inner;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                inner = _inner;
                _inner = null;
            }

            lock (owner._gate)
            {
                owner._subscriptions.Remove(this);
            }

            inner?.Dispose();
        }
    }
}

/// <summary>Thrown when a command reaches a plugin runtime the manager has already detached.</summary>
internal sealed class PluginRetiredException(string pluginId)
    : InvalidOperationException($"Plugin '{pluginId}' is no longer enabled.");
