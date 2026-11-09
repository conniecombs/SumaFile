using SimpleFile.Core;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Loading;
using SimpleFile.PluginHost.Manifest;
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

    /// <summary>
    /// The install root whose <c>bundled.json</c> lists the first-party plugins shipped with SumaFile
    /// (<see cref="PluginDiscovery.InstallRoot"/>); a listed plugin found in this root with the listed manifest
    /// hash is enabled without consent. Null disables bundled trust.
    /// </summary>
    public string? BundledRoot { get; init; }

    /// <summary>
    /// Runs activation, commands and the sidebar and settings-page providers on the UI thread. Null runs them inline
    /// on the calling thread (tests, tools).
    /// </summary>
    public IPluginUiThread? UiThread { get; init; }

    /// <summary>
    /// The window host of a plugin, asked once per runtime (under the manager's lock, so it must not block); the plugin
    /// sees it as <c>IPluginHost.Windows</c>, and teardown closes its windows first. Teardown seals the host
    /// (<see cref="IPluginWindowHost.CloseAllAsync"/>) for good, so every call must return a new host, never one it
    /// returned before. Null gives every plugin a host that ignores <c>Attach</c>.
    /// </summary>
    public Func<string, IPluginWindowHost>? WindowsFor { get; init; }

    /// <summary>
    /// Checks an element a sidebar or settings-page provider returned: null when it is valid, otherwise the reason,
    /// which completes "Plugin '&lt;id&gt;' sidebar section '&lt;sectionId&gt;' " or "Plugin '&lt;id&gt;' settings page ".
    /// Null accepts any non-null object.
    /// </summary>
    public Func<object, string?>? ValidateUiElement { get; init; }
}

/// <summary>
/// What one plugin scan read from disk (<see cref="PluginManager.PrepareScan"/>): the discovered plugins with their
/// manifest hashes, the bundled list and one approvals snapshot. The App prepares it while the file service starts
/// (startup budget fallback F2), so the <c>plugins-discovered</c> span holds only evaluation and rendering.
/// </summary>
public sealed class PreparedScan
{
    internal PreparedScan(
        IReadOnlyList<string> roots,
        string? bundledRoot,
        string approvalStorePath,
        IReadOnlyList<DiscoveredPlugin> plugins,
        IReadOnlyDictionary<string, string> bundled,
        PluginApprovalStore approvals,
        ApprovalSnapshot approvalSnapshot)
    {
        Roots = roots;
        BundledRoot = bundledRoot;
        ApprovalStorePath = approvalStorePath;
        Plugins = plugins;
        Bundled = bundled;
        Approvals = approvals;
        ApprovalSnapshot = approvalSnapshot;
    }

    internal IReadOnlyList<string> Roots { get; }
    internal string? BundledRoot { get; }
    internal string ApprovalStorePath { get; }
    internal IReadOnlyList<DiscoveredPlugin> Plugins { get; }
    internal IReadOnlyDictionary<string, string> Bundled { get; }

    /// <summary>The store that read <see cref="ApprovalSnapshot"/>, so tests can read its <c>LoadCount</c>.</summary>
    internal PluginApprovalStore Approvals { get; }

    internal ApprovalSnapshot ApprovalSnapshot { get; }
}

/// <summary>
/// Owns every discovered plugin: decides its state, activates its UI assembly on first command, sidebar section
/// or settings page, starts its backend lazily or at startup, and tracks faults. <see cref="Changed"/> is raised
/// on the thread that caused the change.
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
    // Cancelled first by DisposeAsync: ends background starts and archive registration attempts and their waits.
    // Never disposed, since background work may still read its token.
    private readonly CancellationTokenSource _disposeCts = new();
    private static readonly TimeSpan InflightStartGrace = TimeSpan.FromSeconds(2);

    private readonly HashSet<Task> _inflightStarts = [];
    private List<PluginRecord> _records = [];
    private bool _disposed;
    // Source of PluginRecord.Epoch values; Interlocked, so it needs no lock.
    private int _epochs;

    public PluginManager(PluginManagerOptions options)
    {
        _options = options;
        _approvals = new PluginApprovalStore(options.ApprovalStorePath);
        _backendHost = new PluginBackendHost(options.Launcher, options.PipeNameFor, options.DataRoot, options.Log);
    }

    public event EventHandler? Changed;

    /// <summary>Creates a plugin's entry instance; a seam so tests can gate activation.</summary>
    internal Func<DiscoveredPlugin, IPlugin> InstanceFactory { get; set; } = PluginActivator.CreateInstance;

    /// <summary>Test seam: awaited by DisableAsync after it sets Disabled and before it tears the plugin down.</summary>
    internal Func<Task>? AfterDisableStateSet { get; set; }

    /// <summary>The approval store, so tests can read <see cref="PluginApprovalStore.LoadCount"/>.</summary>
    internal PluginApprovalStore Approvals => _approvals;

    /// <summary>
    /// How long teardown waits for a plugin's windows to close before it stops the plugin anyway; the same 5 s as the
    /// runtime's retire timeout. A seam so tests can shorten it.
    /// </summary>
    internal TimeSpan WindowCloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private IPluginUiThread UiThread => _options.UiThread ?? InlinePluginUiThread.Instance;

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

    public Task DiscoverAsync(CancellationToken ct) => ScanAsync(null, ct);

    /// <summary>
    /// Discovers from <paramref name="prepared"/> when it was made for this manager's roots, bundled root and
    /// approvals file, so nothing is read again; otherwise (or when it is null) scans as
    /// <see cref="DiscoverAsync(CancellationToken)"/> does.
    /// </summary>
    public Task DiscoverAsync(PreparedScan? prepared, CancellationToken ct) => ScanAsync(prepared, ct);

    public Task RescanAsync(CancellationToken ct) => ScanAsync(null, ct);

    /// <summary>
    /// Everything a scan reads from disk, read now: every manifest (parsed and hashed), <c>bundled.json</c> in
    /// <paramref name="bundledRoot"/> and the approvals file. Thread-safe and free of UI work, so the App runs it on
    /// the thread pool while the file service starts and hands the result to <see cref="DiscoverAsync(PreparedScan?, CancellationToken)"/>.
    /// </summary>
    public static PreparedScan PrepareScan(IReadOnlyList<string> roots, string? bundledRoot, string approvalStorePath) =>
        Prepare(roots.ToArray(), bundledRoot, approvalStorePath, new PluginApprovalStore(approvalStorePath));

    // Read once per scan, so every plugin of a scan is judged against the same list and the same approvals
    // (spec §5.2: discovery stays under 50 ms for ten plugins).
    private static PreparedScan Prepare(
        IReadOnlyList<string> roots, string? bundledRoot, string approvalStorePath, PluginApprovalStore store)
    {
        var bundled = bundledRoot is { } root
            ? BundledPluginList.Read(root)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var approvals = store.Snapshot();
        return new PreparedScan(roots, bundledRoot, approvalStorePath, PluginDiscovery.Scan(roots), bundled, store, approvals);
    }

    private bool IsPreparedForThis(PreparedScan prepared) =>
        prepared.Roots.SequenceEqual(_options.Roots, StringComparer.OrdinalIgnoreCase)
        && string.Equals(prepared.BundledRoot, _options.BundledRoot, StringComparison.OrdinalIgnoreCase)
        && string.Equals(prepared.ApprovalStorePath, _options.ApprovalStorePath, StringComparison.OrdinalIgnoreCase);

    private async Task ScanAsync(PreparedScan? prepared, CancellationToken ct)
    {
        List<PluginRecord> toStart;
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (prepared is not null && !IsPreparedForThis(prepared))
            {
                _options.Log("host", "[warn] Ignoring a prepared plugin scan made for other roots or another approvals file; scanning again.");
                prepared = null;
            }

            // Off the UI thread: Evaluate hashes every file signature.json lists (and a fresh scan reads every manifest).
            var fresh = await Task.Run(() =>
                {
                    var scan = prepared ?? Prepare(_options.Roots, _options.BundledRoot, _options.ApprovalStorePath, _approvals);
                    return scan.Plugins.Select(plugin => Evaluate(plugin, scan.Bundled, scan.ApprovalSnapshot)).ToList();
                }, ct)
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
                        candidate.Epoch = NextEpoch();
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

    private PluginRecord Evaluate(
        DiscoveredPlugin plugin, IReadOnlyDictionary<string, string> bundled, ApprovalSnapshot approvals)
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
            // Discovery hashed the bytes it parsed; hash the file only for a record built without them.
            hash = plugin.ManifestHash ?? PluginPackageHash.ComputeManifestHash(plugin.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PluginRecord(plugin, PluginState.InvalidManifest, unsigned, ex.Message, null);
        }

        var signature = PluginSignatureVerifier.Verify(plugin.Folder, plugin.Manifest!);
        var approval = _approvals.Resolve(plugin.Id, hash, signature.Status, plugin.Folder, approvals);
        if (approval == ApprovalState.NeedsConsent && IsBundled(plugin, hash, bundled))
        {
            // Shipped with SumaFile: trusted without consent, and nothing is written to the store. Only a Disabled
            // the user stored for this folder holds, whatever manifest it was stored with, so it survives upgrades.
            // When the store cannot be read that Disabled cannot be seen, so the plugin stays NeedsConsent (as an
            // unbundled one would) rather than being turned on against the user's choice.
            var stored = _approvals.StoredState(plugin.Id, plugin.Folder, approvals, out var unreadable);
            approval = unreadable ? ApprovalState.NeedsConsent
                : stored == ApprovalState.Disabled ? ApprovalState.Disabled
                : ApprovalState.Enabled;
        }

        var state = approval switch
        {
            ApprovalState.Enabled => PluginState.Enabled,
            ApprovalState.Disabled => PluginState.Disabled,
            _ => PluginState.NeedsConsent,
        };
        return new PluginRecord(plugin, state, signature, signature.Error, hash);
    }

    /// <summary>
    /// The plugin sits in <see cref="PluginManagerOptions.BundledRoot"/> and that root's <c>bundled.json</c> lists
    /// its id with this manifest hash. A user-root copy, or a list in any other root, never qualifies.
    /// </summary>
    private bool IsBundled(DiscoveredPlugin plugin, string hash, IReadOnlyDictionary<string, string> bundled) =>
        _options.BundledRoot is { } bundledRoot
        && bundled.TryGetValue(plugin.Id, out var listedHash)
        && string.Equals(listedHash, hash, StringComparison.Ordinal)
        && SameRoot(plugin.Root, bundledRoot);

    private static bool SameRoot(string a, string b)
    {
        try
        {
            return string.Equals(NormalizeRoot(a), NormalizeRoot(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

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

            if (AfterDisableStateSet is { } afterDisableStateSet) await afterDisableStateSet().ConfigureAwait(false);
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

        var (_, _, instance) = await ActivateAsync(
                pluginId,
                manifest =>
                {
                    if (!manifest.Contributions.Commands.Any(c => c.Id == command))
                    {
                        throw new InvalidOperationException($"Plugin '{pluginId}' has no command '{command}'.");
                    }
                },
                ct)
            .ConfigureAwait(false);

        if (instance is not ICommandHandler handler)
        {
            throw new InvalidOperationException($"Plugin '{pluginId}' does not handle commands.");
        }

        await UiThread.RunAsync(async () =>
            {
                await handler.ExecuteAsync(context, ct);
                return true;
            })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Activates the plugin if needed and asks its <see cref="ISidebarSectionProvider"/> for the body of section
    /// <paramref name="sectionId"/>, on the UI thread. The App's <see cref="PluginManagerOptions.ValidateUiElement"/>
    /// has accepted the result, so there it is a <c>Microsoft.UI.Xaml.FrameworkElement</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not Enabled or declares no such section; nothing changes.</exception>
    /// <exception cref="PluginLoadException">Activation or the provider failed; the plugin is now LoadFailed.</exception>
    public async Task<object> CreateSidebarSectionAsync(string pluginId, string sectionId, CancellationToken ct)
    {
        PluginSidebarSpec? spec = null;
        var (record, runtime, instance) = await ActivateAsync(
                pluginId,
                manifest => spec = manifest.Contributions.Sidebar.FirstOrDefault(s => s.Id == sectionId)
                    ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no sidebar section '{sectionId}'."),
                ct)
            .ConfigureAwait(false);

        if (instance is not ISidebarSectionProvider provider)
        {
            throw await LoadFailedAsync(
                    record,
                    runtime,
                    $"Plugin '{pluginId}' declares a sidebar section but its entry type does not implement ISidebarSectionProvider.",
                    null)
                .ConfigureAwait(false);
        }

        var request = new PluginSidebarSectionRequest(spec!.Id, spec.Provider);
        return await CreateElementAsync(
                record,
                runtime,
                () => provider.CreateSidebarSection(request),
                $"Plugin '{pluginId}' returned no element for sidebar section '{sectionId}'.",
                $"Plugin '{pluginId}' failed to create sidebar section '{sectionId}': ",
                $"Plugin '{pluginId}' sidebar section '{sectionId}' ")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Activates the plugin if needed and asks its <see cref="ISettingsPageProvider"/> for its settings page, on the
    /// UI thread. Never loads code for a plugin that is not Enabled.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not Enabled or declares no settings page; nothing changes.</exception>
    /// <exception cref="PluginLoadException">Activation or the provider failed; the plugin is now LoadFailed.</exception>
    public async Task<object> CreateSettingsPageAsync(string pluginId, CancellationToken ct)
    {
        PluginSettingsPageSpec? spec = null;
        var (record, runtime, instance) = await ActivateAsync(
                pluginId,
                manifest => spec = manifest.Contributions.SettingsPage
                    ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no settings page."),
                ct)
            .ConfigureAwait(false);

        if (instance is not ISettingsPageProvider provider)
        {
            throw await LoadFailedAsync(
                    record,
                    runtime,
                    $"Plugin '{pluginId}' declares a settings page but its entry type does not implement ISettingsPageProvider.",
                    null)
                .ConfigureAwait(false);
        }

        var request = new PluginSettingsPageRequest(spec!.Provider);
        return await CreateElementAsync(
                record,
                runtime,
                () => provider.CreateSettingsPage(request),
                $"Plugin '{pluginId}' returned no element for its settings page.",
                $"Plugin '{pluginId}' failed to create its settings page: ",
                $"Plugin '{pluginId}' settings page ")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The one activation path of commands, sidebar sections and settings pages: the Enabled check, the
    /// <paramref name="requireDeclared"/> check (which throws before any state changes), the runtime, activation on
    /// the UI thread, <see cref="MarkLoadFailedAsync"/> when activation fails, and the retired re-check.
    /// </summary>
    private async Task<(PluginRecord Record, PluginRuntime Runtime, IPlugin Instance)> ActivateAsync(
        string pluginId, Action<PluginManifest> requireDeclared, CancellationToken ct)
    {
        PluginRecord record;
        PluginRuntime runtime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            record = Live(pluginId) is { State: PluginState.Enabled } live
                ? live
                : throw new InvalidOperationException($"Plugin '{pluginId}' is not enabled.");
            requireDeclared(record.Discovered.Manifest!);
            runtime = RuntimeFor(record);
        }

        IPlugin instance;
        try
        {
            instance = await UiThread
                .RunAsync(() => runtime.EnsureActivatedAsync(() => InstanceFactory(record.Discovered), ct))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginRetiredException)
        {
            // Disabled, rescanned away or disposed while this call waited; not a load failure.
            throw;
        }
        catch (Exception ex)
        {
            await MarkLoadFailedAsync(record, runtime, ex).ConfigureAwait(false);
            if (ex is PluginLoadException) throw;
            throw new PluginLoadException($"Plugin '{pluginId}' failed to activate: {ex.Message}", ex);
        }

        ThrowIfRetired(record, runtime);
        return (record, runtime, instance);
    }

    /// <summary>
    /// Runs a provider on the UI thread and checks what it returned. A throw, a null element, or an element that
    /// <see cref="PluginManagerOptions.ValidateUiElement"/> rejects marks the plugin LoadFailed.
    /// </summary>
    private async Task<object> CreateElementAsync(
        PluginRecord record,
        PluginRuntime runtime,
        Func<object?> create,
        string nullMessage,
        string failedPrefix,
        string rejectedPrefix)
    {
        object? element;
        try
        {
            element = await UiThread
                .RunAsync(() =>
                {
                    try
                    {
                        return Task.FromResult(create());
                    }
                    catch (Exception ex)
                    {
                        return Task.FromException<object?>(ex);
                    }
                })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw await LoadFailedAsync(record, runtime, failedPrefix + ex.Message, ex).ConfigureAwait(false);
        }

        if (element is null)
        {
            throw await LoadFailedAsync(record, runtime, nullMessage, null).ConfigureAwait(false);
        }

        if (_options.ValidateUiElement?.Invoke(element) is { } reason)
        {
            throw await LoadFailedAsync(record, runtime, rejectedPrefix + reason, null).ConfigureAwait(false);
        }

        // Disabled or rescanned away while the provider ran: the element belongs to a retired runtime.
        ThrowIfRetired(record, runtime);
        return element;
    }

    private void ThrowIfRetired(PluginRecord record, PluginRuntime runtime)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(record.Runtime, runtime) || record.State != PluginState.Enabled)
            {
                throw new PluginRetiredException(record.Id);
            }
        }
    }

    /// <summary>Marks the plugin LoadFailed with <paramref name="message"/> and returns the exception to throw.</summary>
    private async Task<PluginLoadException> LoadFailedAsync(
        PluginRecord record, PluginRuntime runtime, string message, Exception? inner)
    {
        var error = new PluginLoadException(message, inner);
        await MarkLoadFailedAsync(record, runtime, error).ConfigureAwait(false);
        return error;
    }

    /// <summary>
    /// Sets LoadFailed, which withdraws the plugin's contributions (it leaves <see cref="EnabledPlugins"/>), bumps
    /// <see cref="PluginRecord.Epoch"/> and ends the runtime in teardown order: windows, then the backend, then
    /// deactivation.
    /// </summary>
    private async Task MarkLoadFailedAsync(PluginRecord record, PluginRuntime runtime, Exception ex)
    {
        PluginBackendConnection? connection;
        long unregisterGeneration;
        lock (_gate)
        {
            // Only a live, enabled runtime can fail to load. A Disable that set the state before its teardown
            // detached the runtime keeps Disabled; the teardown it is about to run stops the backend.
            if (!ReferenceEquals(record.Runtime, runtime) || record.State != PluginState.Enabled) return;
            record.State = PluginState.LoadFailed;
            record.Error = ex.Message;
            connection = StopBackendLocked(record, out unregisterGeneration);
            record.Runtime = null;
            record.Epoch = NextEpoch();
        }

        _options.Log(record.Id, $"[error] {ex.Message}");
        // A failed load keeps LoadFailed even when deactivation then times out; RetireAsync logs that.
        await StopRuntimeAsync(record, runtime, connection, unregisterGeneration).ConfigureAwait(false);
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        // First, before waiting for _ops: a registration retry or its delay must not hold up teardown.
        await _disposeCts.CancelAsync().ConfigureAwait(false);
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
            _options.WindowsFor?.Invoke(record.Id) ?? NullPluginWindowHost.Instance,
            _options.Log);
        record.Runtime = runtime;
        return runtime;
    }

    private int NextEpoch() => Interlocked.Increment(ref _epochs);

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
