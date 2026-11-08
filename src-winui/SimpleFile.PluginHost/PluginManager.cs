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

    /// <summary>Registers plugin archive engines with the file service; null skips registration (tests, tools).</summary>
    public IArchiveEngineRegistrar? ArchiveEngines { get; init; }
}

/// <summary>
/// Owns every discovered plugin: decides its state, activates its UI assembly on first command,
/// starts its backend lazily or at startup, and tracks faults. <see cref="Changed"/> is raised on the
/// thread that caused the change.
/// </summary>
public sealed partial class PluginManager : IAsyncDisposable
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
            // Off the UI thread: Evaluate hashes the manifest and every file signature.json lists.
            var fresh = await Task.Run(() => PluginDiscovery.Scan(_options.Roots).Select(Evaluate).ToList(), ct)
                .ConfigureAwait(false);

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
        var state = _approvals.Resolve(plugin.Id, hash, signature.Status, plugin.Folder) switch
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
            _approvals.Set(id, record.ManifestHash!, record.Signature.Status, ApprovalState.Enabled, record.Discovered.Folder);
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
            _approvals.Set(id, record.ManifestHash!, record.Signature.Status, ApprovalState.Disabled, record.Discovered.Folder);
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
            PluginRecord record;
            long unregisterGeneration;
            lock (_gate)
            {
                record = Live(id) ?? throw new InvalidOperationException($"Plugin '{id}' is not installed.");
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
                unregisterGeneration = TakeArchiveRegistrationLocked(runtime);
                record.BackendRunning = false;
                record.State = PluginState.Enabled;
                record.Error = null;
            }

            if (unregisterGeneration != 0) await UnregisterArchiveEnginesAsync(record, unregisterGeneration).ConfigureAwait(false);
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
        long unregisterGeneration;
        lock (_gate)
        {
            if (!ReferenceEquals(record.Runtime, runtime)) return;
            record.State = PluginState.LoadFailed;
            record.Error = ex.Message;
            connection = StopBackendLocked(record, out unregisterGeneration);
        }

        _options.Log(record.Id, $"[error] {ex.Message}");
        if (unregisterGeneration != 0) await UnregisterArchiveEnginesAsync(record, unregisterGeneration).ConfigureAwait(false);
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

    /// <summary>
    /// <c>activateOn: startup</c>, and every plugin that declares <c>archiveFormats</c> whatever its
    /// <c>activateOn</c>: the file service can only reach an archive engine whose backend is already running.
    /// </summary>
    private static bool StartsAtStartup(PluginRecord record) =>
        string.Equals(record.Discovered.Manifest?.Backend?.ActivateOn, "startup", StringComparison.Ordinal)
        || DeclaresArchiveFormats(record);

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
