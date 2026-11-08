using System.Collections.Concurrent;
using SimpleFile.PluginHost.Backend;

namespace SimpleFile.PluginHost;

/// <summary>
/// Archive engines (spec section 6.4). A plugin that declares <c>archiveFormats</c> is started at startup
/// whatever its <c>backend.activateOn</c> says (see <see cref="StartsAtStartup"/>). Once its backend has
/// handshaken, the manager registers the formats with the file service, which connects to the backend's pipe as
/// a second client. When the backend is lost, restarted, disabled, rescanned away or disposed, the manager
/// unregisters them again, best effort.
/// </summary>
public sealed partial class PluginManager
{
    private static readonly TimeSpan ArchiveUnregisterTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, ArchiveRegistration> _archiveRegistrations = new(StringComparer.Ordinal);

    private static bool DeclaresArchiveFormats(PluginRecord record) =>
        record.Discovered.Manifest is { Backend: not null } manifest
        && manifest.Contributions.ArchiveFormats.Count > 0;

    /// <summary>
    /// Registers an accepted backend's formats. A failure faults the plugin, stops its backend and throws, so the
    /// start that triggered it (and any command awaiting it) fails too.
    /// </summary>
    private async Task RegisterArchiveEnginesAsync(
        PluginRecord record,
        PluginRuntime runtime,
        PluginBackendConnection connection,
        long generation,
        CancellationToken ct)
    {
        var registrar = _options.ArchiveEngines!;
        var registration = RegistrationFor(record.Id);
        Exception? failure = null;
        var acquired = false;
        try
        {
            await registration.Lock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            // Re-checked inside the lock: while this waited, a newer generation may have registered (so this one
            // must not overwrite it), or this backend may have been lost, disabled or restarted.
            if (registration.Generation > generation)
            {
                _options.Log(record.Id, $"[info] Skipped registering the archive engines of backend generation {generation}: newer generation {registration.Generation} has registered.");
                return;
            }

            lock (_gate)
            {
                if (!IsLiveConnection(record, runtime, connection)) return;
            }

            await registrar
                .RegisterAsync(record.Id, connection.PipeName, record.Discovered.Manifest!.Contributions.ArchiveFormats, ct)
                .ConfigureAwait(false);
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
            if (!live)
            {
                using var cts = new CancellationTokenSource(ArchiveUnregisterTimeout);
                await CallUnregisterAsync(registrar, record.Id, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (acquired) registration.Lock.Release();
        }

        if (failure is null) return;

        var message = $"Archive engine registration failed: {failure.Message}";
        bool stopped;
        lock (_gate)
        {
            stopped = ReferenceEquals(runtime.Connection, connection);
            if (stopped)
            {
                runtime.Connection = null;
                runtime.NewGeneration();
                record.BackendRunning = false;
                if (record.State == PluginState.Enabled) record.State = PluginState.Faulted;
                record.Error = message;
            }
        }

        _options.Log(record.Id, $"[error] {message}");
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
