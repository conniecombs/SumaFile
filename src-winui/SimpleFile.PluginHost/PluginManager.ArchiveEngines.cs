using System.Collections.Concurrent;
using SimpleFile.PluginHost.Backend;

namespace SimpleFile.PluginHost;

/// <summary>
/// Archive engines (spec section 6.4). A plugin that declares <c>archiveFormats</c> is started at startup
/// whatever its <c>backend.activateOn</c> says (see <see cref="StartsAtStartup"/>). Once its backend has
/// handshaken, the manager registers the formats with the file service, which connects to the backend's pipe as
/// a second client. Registration makes up to <see cref="ArchiveRegisterAttempts"/> attempts, each bounded by
/// <see cref="ArchiveRegisterTimeout"/>. After a service restart, <see cref="ReregisterArchiveEnginesAsync"/>
/// registers the live engines with the new service. When the backend is lost, restarted, disabled, rescanned
/// away or disposed, the manager unregisters them again, best effort. Every change to the service's engine set
/// is followed by <see cref="Changed"/>.
/// </summary>
public sealed partial class PluginManager
{
    private static readonly TimeSpan ArchiveUnregisterTimeout = TimeSpan.FromSeconds(5);

    internal const int ArchiveRegisterAttempts = 3;

    /// <summary>Per registration attempt.</summary>
    internal TimeSpan ArchiveRegisterTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Waits between registration attempts; the argument is the attempt that just failed (1 or 2). The default
    /// waits 1 s after the first and 3 s after the second.
    /// </summary>
    internal Func<int, CancellationToken, Task> ArchiveRetryDelay { get; set; } = DefaultArchiveRetryDelay;

    private readonly ConcurrentDictionary<string, ArchiveRegistration> _archiveRegistrations = new(StringComparer.Ordinal);

    private static Task DefaultArchiveRetryDelay(int failedAttempt, CancellationToken ct) =>
        Task.Delay(failedAttempt == 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3), ct);

    private static bool DeclaresArchiveFormats(PluginRecord record) =>
        record.Discovered.Manifest is { Backend: not null } manifest
        && manifest.Contributions.ArchiveFormats.Count > 0;

    /// <summary>
    /// Registers again every engine the host believes registered (Enabled, live connection,
    /// <c>ArchiveEnginesGeneration != 0</c>) with the service the registrar now reaches, all plugins
    /// concurrently. A plugin whose attempts all fail faults. <see cref="PluginRecord.BackendRunning"/> stays
    /// true meanwhile, and <see cref="Changed"/> is raised once, when every plugin's attempts have ended.
    /// </summary>
    public async Task ReregisterArchiveEnginesAsync(CancellationToken ct)
    {
        // Off the caller's thread (the UI thread on a reconnect) before taking any lock.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (_options.ArchiveEngines is null) return;

        var live = new List<(PluginRecord Record, PluginRuntime Runtime, PluginBackendConnection Connection, long Generation)>();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var record in _records)
            {
                if (record.State == PluginState.Enabled
                    && record.Runtime is { Connection: { } connection, ArchiveEnginesGeneration: var generation } runtime
                    && generation != 0)
                {
                    live.Add((record, runtime, connection, generation));
                }
            }
        }

        try
        {
            await Task.WhenAll(live.Select(Reregister)).ConfigureAwait(false);
        }
        finally
        {
            RaiseChanged();
        }

        async Task Reregister((PluginRecord Record, PluginRuntime Runtime, PluginBackendConnection Connection, long Generation) item)
        {
            try
            {
                await RegisterArchiveEnginesAsync(item.Record, item.Runtime, item.Connection, item.Generation, reregister: true, ct)
                    .ConfigureAwait(false);
            }
            catch (PluginBackendException)
            {
                // The plugin faulted; the failure is logged and on its record.
            }
        }
    }

    /// <summary>
    /// Registers an accepted backend's formats, retrying a failed attempt. When every attempt fails, the
    /// plugin faults, its backend stops, the registration is undone and this throws, so the start that
    /// triggered it (and any command awaiting it) fails too. A backend that is lost, disabled or restarted
    /// meanwhile ends the attempts silently.
    /// </summary>
    /// <param name="reregister">
    /// True when the host already believes this generation registered and registers it with a restarted
    /// service: the stop paths then unregister it themselves, so a stop during the call needs no undo here.
    /// </param>
    private async Task RegisterArchiveEnginesAsync(
        PluginRecord record,
        PluginRuntime runtime,
        PluginBackendConnection connection,
        long generation,
        bool reregister,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var token = linked.Token;
        Exception? failure = null;
        for (var attempt = 1; attempt <= ArchiveRegisterAttempts; attempt++)
        {
            if (attempt > 1)
            {
                // Not under the registrar lock, so a stop path's unregister can run meanwhile.
                await ArchiveRetryDelay(attempt - 1, token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!IsLiveConnection(record, runtime, connection)) return;
                }
            }

            var (outcome, error) = await RegisterOnceAsync(record, runtime, connection, generation, reregister, token)
                .ConfigureAwait(false);
            switch (outcome)
            {
                case RegisterOutcome.Registered:
                    var extensions = string.Join(", ", record.Discovered.Manifest!.Contributions.ArchiveFormats.Select(f => f.Extension));
                    _options.Log(record.Id, $"[info] Archive engines registered: {extensions}");
                    return;
                case RegisterOutcome.Stopped:
                    return;
            }

            failure = error!;
            _options.Log(record.Id, $"[warn] Archive engine registration attempt {attempt} of {ArchiveRegisterAttempts} failed: {failure.Message}");
        }

        await FaultArchiveRegistrationAsync(record, runtime, connection, generation, failure!).ConfigureAwait(false);
    }

    private enum RegisterOutcome
    {
        /// <summary>The service has this generation's engines and the connection is still live.</summary>
        Registered,

        /// <summary>A newer generation registered, or the connection is no longer live; nothing more to do.</summary>
        Stopped,

        Failed,
    }

    /// <summary>One attempt, under the plugin's registrar lock. Only cancellation of <paramref name="token"/> throws.</summary>
    private async Task<(RegisterOutcome Outcome, Exception? Error)> RegisterOnceAsync(
        PluginRecord record,
        PluginRuntime runtime,
        PluginBackendConnection connection,
        long generation,
        bool reregister,
        CancellationToken token)
    {
        var registrar = _options.ArchiveEngines!;
        var registration = RegistrationFor(record.Id);
        await registration.Lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Re-checked inside the lock: while this waited, a newer generation may have registered (so this one
            // must not overwrite it), or this backend may have been lost, disabled or restarted.
            if (registration.Generation > generation)
            {
                _options.Log(record.Id, $"[info] Skipped registering the archive engines of backend generation {generation}: newer generation {registration.Generation} has registered.");
                return (RegisterOutcome.Stopped, null);
            }

            lock (_gate)
            {
                if (!IsLiveConnection(record, runtime, connection)) return (RegisterOutcome.Stopped, null);
            }

            using (var call = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try
                {
                    await registrar
                        .RegisterAsync(record.Id, connection.PipeName, record.Discovered.Manifest!.Contributions.ArchiveFormats, call.Token)
                        .WaitAsync(ArchiveRegisterTimeout, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Disposing (or the caller gave up) mid-call: the service may still apply it, and a first
                    // registration is unknown to every stop path, so undo it here, best effort.
                    if (!reregister)
                    {
                        using var undo = new CancellationTokenSource(ArchiveUnregisterTimeout);
                        await CallUnregisterAsync(registrar, record.Id, undo.Token).ConfigureAwait(false);
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    // Abandon a timed-out call; the service's registration ticket orders any late arrival.
                    await call.CancelAsync().ConfigureAwait(false);
                    return (RegisterOutcome.Failed, ex);
                }
            }

            // The service now holds this generation's registration, live or not.
            registration.Generation = generation;

            bool live;
            lock (_gate)
            {
                live = IsLiveConnection(record, runtime, connection);
                if (live)
                {
                    runtime.ArchiveEnginesGeneration = generation;
                    record.BackendRunning = true;
                }
            }

            // Lost, disabled or restarted while registering: the stop path saw no registration, so undo it here,
            // still under the lock so no newer registration can slip in between.
            if (!live && !reregister)
            {
                using var cts = new CancellationTokenSource(ArchiveUnregisterTimeout);
                await CallUnregisterAsync(registrar, record.Id, cts.Token).ConfigureAwait(false);
            }

            return (live ? RegisterOutcome.Registered : RegisterOutcome.Stopped, null);
        }
        finally
        {
            registration.Lock.Release();
        }
    }

    /// <summary>Every attempt failed: faults the plugin, stops its backend, undoes the registration, then throws.</summary>
    private async Task FaultArchiveRegistrationAsync(
        PluginRecord record,
        PluginRuntime runtime,
        PluginBackendConnection connection,
        long generation,
        Exception failure)
    {
        var message = $"Archive engine registration failed: {failure.Message}";
        bool stopped;
        lock (_gate)
        {
            stopped = ReferenceEquals(runtime.Connection, connection);
            if (stopped)
            {
                runtime.Connection = null;
                runtime.NewGeneration();
                // A re-registration's earlier registration is undone below, so no later stop path repeats it.
                TakeArchiveRegistrationLocked(runtime);
                record.BackendRunning = false;
                if (record.State == PluginState.Enabled) record.State = PluginState.Faulted;
                record.Error = message;
            }
        }

        _options.Log(record.Id, $"[error] {message}");
        // An attempt that timed out may still land on the service, so unregister once more. Under the plugin's
        // registrar lock and generation check: a Restart that registered a newer generation meanwhile is left
        // alone. Before the backend dies and before Changed, so the capability refresh sees the final engine set.
        await UnregisterArchiveEnginesAsync(record, generation).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        if (stopped) RaiseChanged();
        throw new PluginBackendException(message, failure);
    }

    /// <summary>Call under <c>_gate</c>.</summary>
    private static bool IsLiveConnection(PluginRecord record, PluginRuntime runtime, PluginBackendConnection connection) =>
        ReferenceEquals(runtime.Connection, connection) && record.State == PluginState.Enabled;

    /// <summary>
    /// Call under <c>_gate</c>: the generation whose engines the runtime still has registered (0 when none);
    /// clears it so only one stop path unregisters.
    /// </summary>
    private static long TakeArchiveRegistrationLocked(PluginRuntime? runtime)
    {
        if (runtime is null || runtime.ArchiveEnginesGeneration == 0) return 0;
        var generation = runtime.ArchiveEnginesGeneration;
        runtime.ArchiveEnginesGeneration = 0;
        return generation;
    }

    /// <summary>Best effort and bounded: the service may already be gone (session cleanup, service restart).</summary>
    /// <param name="generation">
    /// The backend generation whose registration this undoes. The service keys registrations by plugin id, so the
    /// call is skipped when a newer generation has registered since. Null (teardown) always unregisters.
    /// </param>
    private async Task UnregisterArchiveEnginesAsync(PluginRecord record, long? generation)
    {
        if (_options.ArchiveEngines is not { } registrar) return;
        var pluginId = record.Id;
        var registration = RegistrationFor(pluginId);

        // One budget for waiting on the plugin's registrar lock and for the call itself.
        using var cts = new CancellationTokenSource(ArchiveUnregisterTimeout);
        try
        {
            await registration.Lock.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _options.Log(pluginId, "[error] Archive engine unregistration failed: a registration of the same plugin did not finish in time.");
            return;
        }

        try
        {
            // Checked inside the lock, right before the call, so a newer registration cannot land in between.
            if (generation is { } own && registration.Generation > own)
            {
                _options.Log(pluginId, $"[info] Skipped unregistering the archive engines of backend generation {own}: newer generation {registration.Generation} has registered.");
                return;
            }

            await CallUnregisterAsync(registrar, pluginId, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            registration.Lock.Release();
        }
    }

    /// <summary>Call while holding the plugin's registrar lock; logs instead of throwing.</summary>
    private async Task CallUnregisterAsync(IArchiveEngineRegistrar registrar, string pluginId, CancellationToken ct)
    {
        try
        {
            await registrar.UnregisterAsync(pluginId, ct).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Log(pluginId, $"[error] Archive engine unregistration failed: {ex.Message}");
        }
    }

    private ArchiveRegistration RegistrationFor(string pluginId) =>
        _archiveRegistrations.GetOrAdd(pluginId, _ => new ArchiveRegistration());

    /// <summary>
    /// Per plugin id, since the service keys registrations by id and a rescan can replace the record. Every
    /// registrar call for the plugin runs under <see cref="Lock"/>, so the service sees them in the order the
    /// manager decided them; <see cref="Generation"/> is the backend generation that last registered (0: never).
    /// </summary>
    private sealed class ArchiveRegistration
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        /// <summary>Guarded by <see cref="Lock"/>.</summary>
        public long Generation { get; set; }
    }
}
