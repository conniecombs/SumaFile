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
        try
        {
            await registrar
                .RegisterAsync(record.Id, connection.PipeName, record.Discovered.Manifest!.Contributions.ArchiveFormats, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = $"Archive engine registration failed: {ex.Message}";
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
            throw new PluginBackendException(message, ex);
        }

        bool live;
        lock (_gate)
        {
            // Lost, disabled or restarted while registering: the stop path saw no registration, so undo it here.
            // The service now holds this generation's registration, live or not.
            record.ArchiveRegistrationGeneration = generation;
            live = ReferenceEquals(runtime.Connection, connection) && record.State == PluginState.Enabled;
            if (live)
            {
                runtime.ArchiveEnginesGeneration = generation;
                record.BackendRunning = true;
            }
        }

        if (!live) await UnregisterArchiveEnginesAsync(record, generation).ConfigureAwait(false);
    }

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
        if (generation is { } own)
        {
            long registered;
            lock (_gate) registered = record.ArchiveRegistrationGeneration;
            if (registered > own)
            {
                _options.Log(pluginId, $"[info] Skipped unregistering the archive engines of backend generation {own}: newer generation {registered} has registered.");
                return;
            }
        }

        try
        {
            using var cts = new CancellationTokenSource(ArchiveUnregisterTimeout);
            await registrar.UnregisterAsync(pluginId, cts.Token).WaitAsync(ArchiveUnregisterTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Log(pluginId, $"[error] Archive engine unregistration failed: {ex.Message}");
        }
    }
}
