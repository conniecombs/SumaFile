using SimpleFile.Core;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Loading;
using SimpleFile.PluginHost.Trust;
using SimpleFile.PluginSdk;

namespace SimpleFile.PluginHost;

public sealed class PluginManagerOptions
{
    public required IReadOnlyList<string> Roots { get; init; }
    public required string ApprovalStorePath { get; init; }
    public required string DataRoot { get; init; }
    public required IPluginProcessLauncher Launcher { get; init; }
    public required Func<string, string> PipeNameFor { get; init; }
    public required ISettingsBackend Settings { get; init; }
    public required IPluginProgressSink Progress { get; init; }
    public required IPluginNotifier Notifier { get; init; }
    public required Func<string, IPluginDialogs> DialogsFor { get; init; }
    public required IPluginWorkspace Workspace { get; init; }
    public required Action<string, string> Log { get; init; }
}

/// <summary>
/// Owns every discovered plugin: decides its state, activates its UI assembly on first command,
/// starts its backend lazily or at startup, and tracks faults. <see cref="Changed"/> is raised on the
/// thread that caused the change.
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
    private const string HostCommandPrefix = "plugin:";

    private readonly PluginManagerOptions _options;
    private readonly PluginApprovalStore _approvals;
    private readonly PluginBackendHost _backendHost;
    private readonly object _gate = new();
    // Serializes scans, enable/disable, restart and dispose; commands do not take it.
    private readonly SemaphoreSlim _ops = new(1, 1);
    private static readonly TimeSpan InflightStartGrace = TimeSpan.FromSeconds(2);

    private readonly HashSet<Task> _inflightStarts = [];
    private List<PluginRecord> _records = [];
    private bool _disposed;

    public PluginManager(PluginManagerOptions options)
    {
        _options = options;
        _approvals = new PluginApprovalStore(options.ApprovalStorePath);
        _backendHost = new PluginBackendHost(options.Launcher, options.PipeNameFor, options.DataRoot, options.Log);
    }

    public event EventHandler? Changed;

    /// <summary>Creates a plugin's entry instance; a seam so tests can gate activation.</summary>
    internal Func<DiscoveredPlugin, IPlugin> InstanceFactory { get; set; } = PluginActivator.CreateInstance;

    public IReadOnlyList<PluginRecord> Plugins
    {
        get { lock (_gate) return _records.ToList(); }
    }

    public IReadOnlyList<PluginRecord> EnabledPlugins
    {
        get { lock (_gate) return _records.Where(r => r.State == PluginState.Enabled).ToList(); }
    }

    public static string HostCommandId(string pluginId, string command) => $"plugin:{pluginId}:{command}";

    public static bool TryParseHostCommandId(string id, out string pluginId, out string command)
    {
        pluginId = "";
        command = "";
        if (id is null || !id.StartsWith(HostCommandPrefix, StringComparison.Ordinal)) return false;

        var separator = id.IndexOf(':', HostCommandPrefix.Length);
        if (separator <= HostCommandPrefix.Length || separator == id.Length - 1) return false;

        pluginId = id[HostCommandPrefix.Length..separator];
        command = id[(separator + 1)..];
        return true;
    }

    public Task DiscoverAsync(CancellationToken ct) => ScanAsync(ct);

    public Task RescanAsync(CancellationToken ct) => ScanAsync(ct);

    private async Task ScanAsync(CancellationToken ct)
    {
        List<PluginRecord> toStart;
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var fresh = PluginDiscovery.Scan(_options.Roots).Select(Evaluate).ToList();

            List<PluginRecord> dropped;
            lock (_gate)
            {
                var kept = new HashSet<PluginRecord>();
                var next = new List<PluginRecord>(fresh.Count);
                foreach (var candidate in fresh)
                {
                    var previous = _records.FirstOrDefault(r =>
                        r.Id == candidate.Id
                        && string.Equals(r.Discovered.Folder, candidate.Discovered.Folder, StringComparison.OrdinalIgnoreCase));
                    if (previous is not null && CanKeep(previous, candidate))
                    {
                        // Unchanged and still enabled: keep the activated instance and running backend.
                        previous.Discovered = candidate.Discovered;
                        previous.Signature = candidate.Signature;
                        kept.Add(previous);
                        next.Add(previous);
                    }
                    else
                    {
                        next.Add(candidate);
                    }
                }

                dropped = _records.Where(r => !kept.Contains(r)).ToList();
                _records = next;
                toStart = next.Where(r => !kept.Contains(r) && r.State == PluginState.Enabled && StartsAtStartup(r)).ToList();
            }

            foreach (var record in dropped)
            {
                await TeardownAsync(record).ConfigureAwait(false);
            }
        }
        finally
        {
            _ops.Release();
        }

        RaiseChanged();
        // Fire-and-forget: discovery never waits on a backend (spec: discovery stays under 50 ms at startup).
        foreach (var record in toStart)
        {
            StartInBackground(record);
        }
    }

    private static bool CanKeep(PluginRecord previous, PluginRecord candidate) =>
        previous.State == PluginState.Enabled
        && candidate.State == PluginState.Enabled
        && previous.ManifestHash == candidate.ManifestHash
        && previous.Signature.Status == candidate.Signature.Status;

    private PluginRecord Evaluate(DiscoveredPlugin plugin)
    {
        var unsigned = new SignatureState(SignatureStatus.Unsigned, null, null);
        switch (plugin.State)
        {
            case DiscoveryState.Shadowed:
                return new PluginRecord(plugin, PluginState.Shadowed, unsigned, $"Shadowed by {plugin.ShadowedByFolder}", null);
            case DiscoveryState.IncompatibleSdk:
                return new PluginRecord(plugin, PluginState.IncompatibleSdk, unsigned, string.Join("; ", plugin.Errors), null);
            case DiscoveryState.InvalidManifest:
                return new PluginRecord(plugin, PluginState.InvalidManifest, unsigned, string.Join("; ", plugin.Errors), null);
        }

        string hash;
        try
        {
            hash = PluginPackageHash.ComputeManifestHash(plugin.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PluginRecord(plugin, PluginState.InvalidManifest, unsigned, ex.Message, null);
        }

        var signature = PluginSignatureVerifier.Verify(plugin.Folder, plugin.Manifest!);
        var state = _approvals.Resolve(plugin.Id, hash, signature.Status) switch
        {
            ApprovalState.Enabled => PluginState.Enabled,
            ApprovalState.Disabled => PluginState.Disabled,
            _ => PluginState.NeedsConsent,
        };
        return new PluginRecord(plugin, state, signature, signature.Error, hash);
    }

    public async Task EnableAsync(string id, CancellationToken ct)
    {
        PluginRecord record;
        var changed = false;
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            record = Approvable(id);
            _approvals.Set(id, record.ManifestHash!, record.Signature.Status, ApprovalState.Enabled);
            lock (_gate)
            {
                // LoadFailed and Faulted are already approved; they stay as they are until the next rescan.
                if (record.State is PluginState.NeedsConsent or PluginState.Disabled)
                {
                    record.State = PluginState.Enabled;
                    record.Error = record.Signature.Error;
                    changed = true;
                }
            }
        }
        finally
        {
            _ops.Release();
        }

        if (changed)
        {
            RaiseChanged();
            if (StartsAtStartup(record)) StartInBackground(record);
        }
    }

    public async Task DisableAsync(string id, CancellationToken ct)
    {
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record = Approvable(id);
            lock (_gate)
            {
                // First, so no command can attach a new runtime while the old one is torn down.
                record.State = PluginState.Disabled;
                record.Error = record.Signature.Error;
            }

            await TeardownAsync(record).ConfigureAwait(false);
            _approvals.Set(id, record.ManifestHash!, record.Signature.Status, ApprovalState.Disabled);
        }
        finally
        {
            _ops.Release();
        }

        RaiseChanged();
    }

    public async Task RestartBackendAsync(string id, CancellationToken ct)
    {
        PluginRuntime runtime;
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PluginBackendConnection? previous;
            lock (_gate)
            {
                var record = Live(id) ?? throw new InvalidOperationException($"Plugin '{id}' is not installed.");
                if (record.Discovered.Manifest?.Backend is null)
                {
                    throw new InvalidOperationException($"Plugin '{id}' declares no backend.");
                }

                if (record.State is not (PluginState.Enabled or PluginState.Faulted))
                {
                    throw new InvalidOperationException($"Plugin '{id}' is {record.State}.");
                }

                runtime = RuntimeFor(record);
                previous = runtime.Connection;
                runtime.Connection = null;
                runtime.NewGeneration();
                record.BackendRunning = false;
                record.State = PluginState.Enabled;
                record.Error = null;
            }

            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _ops.Release();
        }

        // Outside _ops so Changed handlers (raised here and by the start) never run under it.
        RaiseChanged();
        await runtime.EnsureBackendAsync(ct).ConfigureAwait(false);
    }

    public async Task RunCommandAsync(string hostCommandId, PluginCommandContext context, CancellationToken ct)
    {
        if (!TryParseHostCommandId(hostCommandId, out var pluginId, out var command))
        {
            throw new ArgumentException($"'{hostCommandId}' is not a plugin command id.", nameof(hostCommandId));
        }

        PluginRecord record;
        PluginRuntime runtime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            record = Live(pluginId) is { State: PluginState.Enabled } live
                ? live
                : throw new InvalidOperationException($"Plugin '{pluginId}' is not enabled.");
            if (!record.Discovered.Manifest!.Contributions.Commands.Any(c => c.Id == command))
            {
                throw new InvalidOperationException($"Plugin '{pluginId}' has no command '{command}'.");
            }

            runtime = RuntimeFor(record);
        }

        IPlugin instance;
        try
        {
            instance = await runtime
                .EnsureActivatedAsync(() => InstanceFactory(record.Discovered), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginRetiredException)
        {
            // Disabled, rescanned away or disposed while this command waited; not a load failure.
            throw;
        }
        catch (Exception ex)
        {
            await MarkLoadFailedAsync(record, runtime, ex).ConfigureAwait(false);
            if (ex is PluginLoadException) throw;
            throw new PluginLoadException($"Plugin '{pluginId}' failed to activate: {ex.Message}", ex);
        }

        lock (_gate)
        {
            if (!ReferenceEquals(record.Runtime, runtime) || record.State != PluginState.Enabled)
            {
                throw new PluginRetiredException(pluginId);
            }
        }

        if (instance is not ICommandHandler handler)
        {
            throw new InvalidOperationException($"Plugin '{pluginId}' does not handle commands.");
        }

        await handler.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    private async Task MarkLoadFailedAsync(PluginRecord record, PluginRuntime runtime, Exception ex)
    {
        PluginBackendConnection? connection = null;
        lock (_gate)
        {
            if (!ReferenceEquals(record.Runtime, runtime)) return;
            record.State = PluginState.LoadFailed;
            record.Error = ex.Message;
            connection = StopBackendLocked(record);
        }

        _options.Log(record.Id, $"[error] {ex.Message}");
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await _ops.WaitAsync().ConfigureAwait(false);
        try
        {
            List<PluginRecord> records;
            Task[] starts;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                records = _records;
            }

            lock (_inflightStarts)
            {
                starts = _inflightStarts.ToArray();
            }

            // A start that finishes now sees _disposed and disposes its own connection; give it a moment so
            // no child process outlives the manager. A start still hanging after that is killed when its
            // connect or handshake timeout ends it.
            try { await Task.WhenAll(starts).WaitAsync(InflightStartGrace).ConfigureAwait(false); }
            catch { }

            foreach (var record in records)
            {
                await TeardownAsync(record).ConfigureAwait(false);
            }
        }
        finally
        {
            _ops.Release();
        }
    }

    // ---- backend lifecycle ----

    /// <summary>
    /// Kicks off an <c>activateOn: startup</c> backend without waiting. The start is the services
    /// generation's cached start, so a later <c>InvokeBackendAsync</c> awaits the same one; its outcome
    /// lands on the record (<see cref="PluginRecord.BackendRunning"/> or <see cref="PluginRecord.Error"/>).
    /// </summary>
    private void StartInBackground(PluginRecord record)
    {
        PluginRuntime runtime;
        lock (_gate)
        {
            if (_disposed || record.State != PluginState.Enabled) return;
            runtime = RuntimeFor(record);
        }

        _ = Observe(runtime.EnsureBackendAsync(CancellationToken.None));

        static async Task Observe(Task task)
        {
            try { await task.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Remembers an in-flight start until it settles so <see cref="DisposeAsync"/> can wait for it.</summary>
    private Task<PluginBackendConnection> TrackStart(Task<PluginBackendConnection> start)
    {
        // Called by PluginHostServices under its own lock, so this uses a leaf lock, never _gate.
        lock (_inflightStarts)
        {
            _inflightStarts.Add(start);
        }

        start.ContinueWith(
            t => { lock (_inflightStarts) _inflightStarts.Remove(t); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return start;
    }

    /// <summary>The backend factory of one services generation; see <see cref="PluginRuntime"/>.</summary>
    private async Task<PluginBackendConnection> StartBackendAsync(
        PluginRecord record,
        PluginRuntime runtime,
        PluginHostServices services,
        Action<PluginBackendConnection> configure,
        CancellationToken ct)
    {
        // PluginHostServices runs this factory under its own lock. Leave it before touching any
        // manager or runtime state, so the lock order is always manager -> runtime -> services.
        await Task.Yield();

        lock (_gate)
        {
            if (!IsLiveGeneration(record, runtime, services))
            {
                throw new PluginBackendException($"The backend of plugin '{record.Id}' is not available.");
            }
        }

        var lostDuringStart = false;
        PluginBackendConnection connection;
        try
        {
            connection = await _backendHost.StartAsync(record.Discovered, ct, c =>
            {
                PluginHostMethods.Register(c, record.Id, _options.Settings, _options.Progress, _options.Notifier, _options.Log);
                c.Lost += (_, ex) =>
                {
                    lock (_gate)
                    {
                        if (!ReferenceEquals(runtime.Connection, c))
                        {
                            lostDuringStart = true;
                            return;
                        }
                    }

                    OnBackendLost(record, runtime, c, ex);
                };
                configure(c);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnBackendStartFailed(record, runtime, services, ex);
            throw;
        }

        bool accepted;
        lock (_gate)
        {
            accepted = !lostDuringStart && IsLiveGeneration(record, runtime, services);
            if (accepted)
            {
                runtime.Connection = connection;
                record.BackendRunning = true;
                record.Error = record.Signature.Error;
            }
        }

        if (!accepted)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            var error = new PluginBackendException(lostDuringStart
                ? $"Backend of plugin '{record.Id}' exited during startup."
                : $"The backend of plugin '{record.Id}' was stopped while starting.");
            if (lostDuringStart) OnBackendStartFailed(record, runtime, services, error);
            throw error;
        }

        RaiseChanged();
        return connection;
    }

    private void OnBackendStartFailed(PluginRecord record, PluginRuntime runtime, PluginHostServices services, Exception ex)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(record.Runtime, runtime) || !runtime.IsCurrent(services)) return;

            // Fresh services so the next call retries instead of replaying the cached failure.
            runtime.NewGeneration();
            record.Error = ex.Message;
            if (runtime.RecordFailure() && record.State == PluginState.Enabled)
            {
                record.State = PluginState.Faulted;
            }
        }

        _options.Log(record.Id, $"[error] Backend start failed: {ex.Message}");
        RaiseChanged();
    }

    private void OnBackendLost(PluginRecord record, PluginRuntime runtime, PluginBackendConnection connection, Exception? ex)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(runtime.Connection, connection)) return;
            runtime.Connection = null;
            runtime.NewGeneration();
            record.BackendRunning = false;
            if (record.State == PluginState.Enabled) record.State = PluginState.Faulted;
            record.Error = ex is null
                ? "The plugin backend stopped unexpectedly."
                : $"The plugin backend stopped unexpectedly: {ex.Message}";
        }

        _options.Log(record.Id, $"[error] {record.Error}");
        // Off this thread: Lost may be raised from the connection's own read loop.
        _ = Task.Run(async () =>
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        });
        RaiseChanged();
    }

    /// <summary>Stops the backend, retires the runtime and deactivates the instance; the record keeps its state.</summary>
    private async Task TeardownAsync(PluginRecord record)
    {
        PluginRuntime? runtime;
        PluginBackendConnection? connection;
        lock (_gate)
        {
            runtime = record.Runtime;
            connection = StopBackendLocked(record);
            record.Runtime = null;
        }

        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        if (runtime is not null) await runtime.RetireAsync().ConfigureAwait(false);
    }

    private PluginBackendConnection? StopBackendLocked(PluginRecord record)
    {
        record.BackendRunning = false;
        if (record.Runtime is not { } runtime) return null;

        var connection = runtime.Connection;
        runtime.Connection = null;
        runtime.NewGeneration();
        return connection;
    }

    // ---- helpers (call under _gate unless noted) ----

    private bool IsLiveGeneration(PluginRecord record, PluginRuntime runtime, PluginHostServices services) =>
        !_disposed
        && ReferenceEquals(record.Runtime, runtime)
        && runtime.IsCurrent(services)
        && record.State == PluginState.Enabled;

    private PluginRuntime RuntimeFor(PluginRecord record)
    {
        if (record.Runtime is { } existing) return existing;

        PluginRuntime runtime = null!;
        runtime = new PluginRuntime(
            record.Id,
            (services, configure, ct) => TrackStart(StartBackendAsync(record, runtime, services, configure, ct)),
            () =>
            {
                lock (_gate) return ReferenceEquals(record.Runtime, runtime) && record.State == PluginState.Enabled;
            },
            _options.Settings,
            _options.Progress,
            _options.DialogsFor(record.Id),
            _options.Workspace,
            _options.Log);
        record.Runtime = runtime;
        return runtime;
    }

    private PluginRecord? Live(string id) =>
        _records.FirstOrDefault(r => r.Id == id && r.State != PluginState.Shadowed);

    // Takes _gate itself.
    private PluginRecord Approvable(string id)
    {
        lock (_gate)
        {
            var record = Live(id) ?? throw new InvalidOperationException($"Plugin '{id}' is not installed.");
            if (record.Discovered.State != DiscoveryState.Valid || record.ManifestHash is null)
            {
                throw new InvalidOperationException($"Plugin '{id}' cannot be enabled or disabled while {record.State}.");
            }

            return record;
        }
    }

    private static bool StartsAtStartup(PluginRecord record) =>
        string.Equals(record.Discovered.Manifest?.Backend?.ActivateOn, "startup", StringComparison.Ordinal);

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _options.Log("host", $"[error] PluginManager.Changed handler threw: {ex}");
        }
    }
}
