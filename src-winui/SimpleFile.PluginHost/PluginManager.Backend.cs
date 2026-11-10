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

        // The dispose token, so DisposeAsync ends the wait; the start's archive registration attempts observe it too.
        _ = Observe(runtime.EnsureBackendAsync(_disposeCts.Token));

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

        if (registerEngines) await RegisterArchiveEnginesAsync(record, runtime, connection, generation, reregister: false, ct).ConfigureAwait(false);

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
            // A Disable that set the state before its teardown detached the runtime keeps its clean record (as in
            // MarkLoadFailedAsync): the plugin is off, so a start that failed meanwhile is only logged.
            if (record.State != PluginState.Disabled)
            {
                record.Error = ex.Message;
                if (runtime.RecordFailure() && record.State == PluginState.Enabled)
                {
                    record.State = PluginState.Faulted;
                }
            }
        }

        _options.Log(record.Id, $"[error] Backend start failed: {ex.Message}");
        RaiseChanged();
    }

    private void OnBackendLost(PluginRecord record, PluginRuntime runtime, PluginBackendConnection connection, Exception? ex)
    {
        long unregisterGeneration;
        var message = ex is null
            ? "The plugin backend stopped unexpectedly."
            : $"The plugin backend stopped unexpectedly: {ex.Message}";
        lock (_gate)
        {
            if (!ReferenceEquals(runtime.Connection, connection)) return;
            runtime.Connection = null;
            runtime.NewGeneration();
            unregisterGeneration = TakeArchiveRegistrationLocked(runtime);
            record.BackendRunning = false;
            if (record.State == PluginState.Enabled) record.State = PluginState.Faulted;
            // Disabled before its teardown detached the runtime: the record stays as Disable left it.
            if (record.State != PluginState.Disabled) record.Error = message;
        }

        _options.Log(record.Id, $"[error] {message}");
        // Off this thread: Lost may be raised from the connection's own read loop. This runs independently of a
        // restart, so it passes its generation and is skipped if the replacement has registered meanwhile.
        _ = Task.Run(async () =>
        {
            try { await BeforeLostCleanup().ConfigureAwait(false); } catch { }
            if (unregisterGeneration != 0)
            {
                await UnregisterArchiveEnginesAsync(record, unregisterGeneration).ConfigureAwait(false);
                // Again, now that the service's engine set has changed: the Changed below ran before the
                // unregister, so a capability refresh on it may still have seen these engines.
                RaiseChanged();
            }

            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        });
        RaiseChanged();
    }

    /// <summary>
    /// Closes the plugin's windows, stops the backend, then retires the runtime and deactivates the instance, and bumps
    /// <see cref="PluginRecord.Epoch"/>; the record keeps its state.
    /// </summary>
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
            record.Epoch = NextEpoch();
        }

        // Teardown (disable, rescan, dispose) ends the plugin, so it always unregisters: no generation check.
        if (!await StopRuntimeAsync(record, runtime, connection, unregisterGeneration, anyGeneration: true).ConfigureAwait(false))
        {
            lock (_gate)
            {
                record.State = PluginState.Faulted;
                record.Error = PluginRuntime.DeactivateTimeoutMessage;
            }
        }
    }

    /// <summary>
    /// Ends a runtime the caller has already detached from its record, in teardown order: its windows close (bounded
    /// by <see cref="WindowCloseTimeout"/>), then the backend stops, then the instance is deactivated.
    /// </summary>
    /// <param name="unregisterGeneration">The generation whose archive engines need unregistering, or 0.</param>
    /// <param name="anyGeneration">Unregister whatever generation the service holds (the plugin is ending for good).</param>
    /// <returns>False when the runtime did not deactivate within its retire timeout.</returns>
    private async Task<bool> StopRuntimeAsync(
        PluginRecord record,
        PluginRuntime? runtime,
        PluginBackendConnection? connection,
        long unregisterGeneration,
        bool anyGeneration = false)
    {
        if (runtime is not null) await CloseWindowsAsync(record.Id, runtime.WindowHost).ConfigureAwait(false);

        // Before the backend dies, so the service stops routing archives to its pipe first.
        if (unregisterGeneration != 0)
        {
            await UnregisterArchiveEnginesAsync(record, anyGeneration ? null : unregisterGeneration).ConfigureAwait(false);
        }

        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        return runtime is null || await runtime.RetireAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the plugin's windows to close, at most <see cref="WindowCloseTimeout"/>. A dispatcher that is shutting
    /// down may never run the close, so a timeout or a failure is logged and the plugin is stopped anyway.
    /// </summary>
    private async Task CloseWindowsAsync(string pluginId, IPluginWindowHost windows)
    {
        try
        {
            await windows.CloseAllAsync().WaitAsync(WindowCloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _options.Log(pluginId, $"[warn] Plugin windows did not close within {WindowCloseTimeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s; stopping the plugin anyway.");
        }
        catch (Exception ex)
        {
            _options.Log(pluginId, $"[warn] Plugin windows failed to close ({ex.GetType().Name}: {ex.Message}); stopping the plugin anyway.");
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
