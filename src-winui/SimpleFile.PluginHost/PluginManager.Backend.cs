using SimpleFile.PluginHost.Backend;

namespace SimpleFile.PluginHost;

public sealed partial class PluginManager
{
    // ---- backend lifecycle ----

    /// <summary>Test seam: awaited before the queued cleanup of a lost backend runs.</summary>
    internal Func<Task> BeforeLostCleanup { get; set; } = () => Task.CompletedTask;

    /// <summary>Numbers every backend start across all plugins, so later generations compare greater.</summary>
    private long _backendGenerations;

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
        // manager or runtime state, so the lock order is always manager -> runtime -> services. Hop to the
        // thread pool (not Task.Yield, which resumes on the UI context) so Process.Start, job creation and
        // the pipe connect never run on the UI thread.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        lock (_gate)
        {
            if (!IsLiveGeneration(record, runtime, services))
            {
                throw new PluginBackendException($"The backend of plugin '{record.Id}' is not available.");
            }
        }

        var generation = Interlocked.Increment(ref _backendGenerations);
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
        var registerEngines = _options.ArchiveEngines is not null && DeclaresArchiveFormats(record);
        lock (_gate)
        {
            accepted = !lostDuringStart && IsLiveGeneration(record, runtime, services);
            if (accepted)
            {
                runtime.Connection = connection;
                // An archive engine counts as running once the service knows about it.
                record.BackendRunning = !registerEngines;
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

        if (registerEngines) await RegisterArchiveEnginesAsync(record, runtime, connection, generation, ct).ConfigureAwait(false);

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
        long unregisterGeneration;
        lock (_gate)
        {
            if (!ReferenceEquals(runtime.Connection, connection)) return;
            runtime.Connection = null;
            runtime.NewGeneration();
            unregisterGeneration = TakeArchiveRegistrationLocked(runtime);
            record.BackendRunning = false;
            if (record.State == PluginState.Enabled) record.State = PluginState.Faulted;
            record.Error = ex is null
                ? "The plugin backend stopped unexpectedly."
                : $"The plugin backend stopped unexpectedly: {ex.Message}";
        }

        _options.Log(record.Id, $"[error] {record.Error}");
        // Off this thread: Lost may be raised from the connection's own read loop. This runs independently of a
        // restart, so it passes its generation and is skipped if the replacement has registered meanwhile.
        _ = Task.Run(async () =>
        {
            try { await BeforeLostCleanup().ConfigureAwait(false); } catch { }
            if (unregisterGeneration != 0) await UnregisterArchiveEnginesAsync(record, unregisterGeneration).ConfigureAwait(false);
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        });
        RaiseChanged();
    }

    /// <summary>Stops the backend, retires the runtime and deactivates the instance; the record keeps its state.</summary>
    private async Task TeardownAsync(PluginRecord record)
    {
        PluginRuntime? runtime;
        PluginBackendConnection? connection;
        long unregisterGeneration;
        lock (_gate)
        {
            runtime = record.Runtime;
            connection = StopBackendLocked(record, out unregisterGeneration);
            record.Runtime = null;
        }

        // Before the backend dies, so the service stops routing archives to its pipe first. Teardown (disable,
        // rescan, dispose) ends the plugin, so it always unregisters: no generation check.
        if (unregisterGeneration != 0) await UnregisterArchiveEnginesAsync(record, generation: null).ConfigureAwait(false);
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        if (runtime is not null && !await runtime.RetireAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                record.State = PluginState.Faulted;
                record.Error = PluginRuntime.DeactivateTimeoutMessage;
            }
        }
    }

    /// <param name="unregisterGeneration">The generation whose engines need unregistering, or 0.</param>
    private PluginBackendConnection? StopBackendLocked(PluginRecord record, out long unregisterGeneration)
    {
        record.BackendRunning = false;
        unregisterGeneration = 0;
        if (record.Runtime is not { } runtime) return null;

        var connection = runtime.Connection;
        runtime.Connection = null;
        runtime.NewGeneration();
        unregisterGeneration = TakeArchiveRegistrationLocked(runtime);
        return connection;
    }
}
