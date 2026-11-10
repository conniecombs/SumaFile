using System.Globalization;
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

    /// <summary>
    /// The panel service of a plugin, asked once per runtime (under the manager's lock, so it must not block); the plugin
    /// sees it as <c>IPluginHost.Panels</c>. Null gives every plugin a service without panels: <c>IsVisible</c> is false,
    /// <c>Hide</c> does nothing and <c>ShowAsync</c> throws.
    /// </summary>
    public Func<string, IPluginPanels>? PanelsFor { get; init; }

    /// <summary>
    /// Called with the plugin id when a plugin calls <c>IPluginHost.Decorations.Invalidate()</c>; it must only queue
    /// work, never call a provider inline. Null ignores the call.
    /// </summary>
    public Action<string>? InvalidateDecorations { get; init; }
}

/// <summary>
/// One plugin scan, read and judged (<see cref="PluginManager.PrepareScan"/>): every plugin's state, decided from its
/// manifest (parsed and hashed), its signature, the bundled list and one approvals snapshot. The App prepares it on the
/// thread pool while the file service starts (startup budget fallback F2), so its <c>plugins-*</c> startup span holds
/// no file read, no hashing and no hop to the pool, only the hand-over and the contribution rendering.
/// </summary>
public sealed class PreparedScan
{
    internal PreparedScan(
        IReadOnlyList<string> roots,
        string? bundledRoot,
        string approvalStorePath,
        PluginApprovalStore approvals,
        IReadOnlyList<PluginEvaluation> evaluations)
    {
        Roots = roots;
        BundledRoot = bundledRoot;
        ApprovalStorePath = approvalStorePath;
        Approvals = approvals;
        Evaluations = evaluations;
    }

    internal IReadOnlyList<string> Roots { get; }
    internal string? BundledRoot { get; }
    internal string ApprovalStorePath { get; }

    /// <summary>The store that read the approvals snapshot, so tests can read its <c>LoadCount</c>.</summary>
    internal PluginApprovalStore Approvals { get; }

    /// <summary>What the scan decided for each discovered plugin, in discovery order.</summary>
    internal IReadOnlyList<PluginEvaluation> Evaluations { get; }
}

/// <summary>
/// What a scan decided for one discovered plugin. Immutable, so a prepared scan can be handed over more than once; each
/// discovery makes its own <see cref="PluginRecord"/> from it.
/// </summary>
internal sealed record PluginEvaluation(
    DiscoveredPlugin Discovered,
    PluginState State,
    SignatureState Signature,
    string? Error,
    string? ManifestHash,
    bool IsBundledId)
{
    public PluginRecord ToRecord() => new(Discovered, State, Signature, Error, ManifestHash) { IsBundledId = IsBundledId };
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
    // The teardown a load failure started, by plugin id, under _gate; an entry removes itself when its task ends.
    private readonly Dictionary<string, Task> _loadFailureTeardowns = new(StringComparer.Ordinal);
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

    /// <summary>
    /// How long a scan, enable, disable, restart, dispose or backend start waits for a plugin's load-failure teardown
    /// before it goes on anyway. By default it is the sum of every bounded teardown step:
    /// <see cref="WindowCloseTimeout"/>, the archive engine unregister, the old backend's exit and the runtime's retire
    /// timeouts. A seam so tests can shorten it.
    /// </summary>
    internal TimeSpan LoadFailureTeardownWait
    {
        get => _loadFailureTeardownWait
            ?? WindowCloseTimeout + ArchiveUnregisterTimeout + PluginBackendConnection.ExitWait + PluginRuntime.RetireTimeouts;
        set => _loadFailureTeardownWait = value;
    }

    private TimeSpan? _loadFailureTeardownWait;

    /// <summary>
    /// How long activation waits for the latest <see cref="MigrateLegacySettingsAsync"/>, counted from its start, before
    /// it logs and activates anyway. A seam so tests can shorten it.
    /// </summary>
    internal TimeSpan LegacyMigrationWait { get; set; } = TimeSpan.FromSeconds(5);

    // The latest migration bounded by LegacyMigrationWait; never faults. Volatile.
    private Task _legacyMigration = Task.CompletedTask;

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

    public Task DiscoverAsync(CancellationToken ct) => ScanAsync(null, startBackends: true, ct);

    /// <summary>
    /// Discovers from <paramref name="prepared"/> when it was made for this manager's roots, bundled root and
    /// approvals file, so nothing is read again; otherwise (or when it is null) scans as
    /// <see cref="DiscoverAsync(CancellationToken)"/> does.
    /// </summary>
    public Task DiscoverAsync(PreparedScan? prepared, CancellationToken ct) => ScanAsync(prepared, startBackends: true, ct);

    /// <summary>
    /// <see cref="DiscoverAsync(PreparedScan?, CancellationToken)"/>; with <paramref name="startBackends"/> false it
    /// starts no backend, and <see cref="StartStartupBackends"/> starts the startup ones later. A matching prepared
    /// scan leaves nothing to read, hash or wait for, so the returned task has completed (and <see cref="Changed"/>
    /// has been raised) on the caller's thread by the time it returns, unless another scan, enable or disable holds
    /// the manager.
    /// </summary>
    public Task DiscoverAsync(PreparedScan? prepared, bool startBackends, CancellationToken ct) =>
        ScanAsync(prepared, startBackends, ct);

    public Task RescanAsync(CancellationToken ct) => ScanAsync(null, startBackends: true, ct);

    /// <summary>
    /// Everything a scan reads and decides, done now: every manifest (parsed and hashed), <c>bundled.json</c> in
    /// <paramref name="bundledRoot"/>, the approvals file, and each plugin's signature check and state. Thread-safe and
    /// free of UI work, so the App runs it on the thread pool while the file service starts and hands the result to
    /// <see cref="DiscoverAsync(PreparedScan?, bool, CancellationToken)"/>.
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
        var evaluations = PluginDiscovery.Scan(roots)
            .Select(plugin => Evaluate(plugin, bundled, bundledRoot, store, approvals))
            .ToList();
        return new PreparedScan(roots, bundledRoot, approvalStorePath, store, evaluations);
    }

    private bool IsPreparedForThis(PreparedScan prepared) =>
        prepared.Roots.SequenceEqual(_options.Roots, StringComparer.OrdinalIgnoreCase)
        && string.Equals(prepared.BundledRoot, _options.BundledRoot, StringComparison.OrdinalIgnoreCase)
        && string.Equals(prepared.ApprovalStorePath, _options.ApprovalStorePath, StringComparison.OrdinalIgnoreCase);

    private async Task ScanAsync(PreparedScan? prepared, bool startBackends, CancellationToken ct)
    {
        List<PluginRecord> toStart;
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Completes at once when no load failure is tearing down, so discovery's startup path gains no yield. A
            // load failure during the read below is waited for by the backend start itself (StartBackendAsync).
            await WaitForLoadFailureTeardownAsync(null).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (prepared is not null && !IsPreparedForThis(prepared))
            {
                _options.Log("host", "[warn] Ignoring a prepared plugin scan made for other roots or another approvals file; scanning again.");
                prepared = null;
            }

            // A prepared scan was read and judged on the thread pool already, so it is used here with no hop: at startup
            // discovery then runs on the UI thread from start to end. A fresh scan reads every manifest and hashes every
            // file signature.json lists, so it runs off the UI thread.
            var evaluations = prepared?.Evaluations
                ?? await Task.Run(
                        () => Prepare(_options.Roots, _options.BundledRoot, _options.ApprovalStorePath, _approvals).Evaluations,
                        ct)
                    .ConfigureAwait(false);
            var fresh = evaluations.Select(evaluation => evaluation.ToRecord()).ToList();

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
                        previous.IsBundledId = candidate.IsBundledId;
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
        if (!startBackends)
        {
            return;
        }

        // Fire-and-forget: discovery never waits on a backend (spec: discovery stays under 50 ms at startup).
        foreach (var record in toStart)
        {
            StartInBackground(record);
        }
    }

    /// <summary>
    /// Starts, without waiting, the backend of every enabled plugin that starts at startup (<see cref="StartsAtStartup"/>).
    /// The App calls it after <see cref="DiscoverAsync(PreparedScan?, bool, CancellationToken)"/> with
    /// <c>startBackends: false</c>, once it has rendered the plugins and subscribed to <see cref="Changed"/>: no launch
    /// then competes with its startup span, and no <see cref="Changed"/> a start raises is missed. A backend that is
    /// already running or starting is not started again.
    /// </summary>
    public void StartStartupBackends()
    {
        List<PluginRecord> toStart;
        lock (_gate)
        {
            if (_disposed) return;
            toStart = _records.Where(record => record.State == PluginState.Enabled && StartsAtStartup(record)).ToList();
        }

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

    // Static, so PrepareScan can judge every plugin before any manager exists; the store's snapshot overloads read
    // only the snapshot they are given.
    private static PluginEvaluation Evaluate(
        DiscoveredPlugin plugin,
        IReadOnlyDictionary<string, string> bundled,
        string? bundledRoot,
        PluginApprovalStore store,
        ApprovalSnapshot approvals)
    {
        var unsigned = new SignatureState(SignatureStatus.Unsigned, null, null);
        // Listed by the install root's bundled.json, whatever the hash and whatever the discovery state.
        var isBundledId = bundledRoot is not null && bundled.ContainsKey(plugin.Id);
        switch (plugin.State)
        {
            case DiscoveryState.Shadowed:
                return new PluginEvaluation(
                    plugin, PluginState.Shadowed, unsigned, $"Shadowed by {plugin.ShadowedByFolder}", null, isBundledId);
            case DiscoveryState.IncompatibleSdk:
                return new PluginEvaluation(
                    plugin, PluginState.IncompatibleSdk, unsigned, string.Join("; ", plugin.Errors), null, isBundledId);
            case DiscoveryState.InvalidManifest:
                return new PluginEvaluation(
                    plugin, PluginState.InvalidManifest, unsigned, string.Join("; ", plugin.Errors), null, isBundledId);
        }

        string hash;
        try
        {
            // Discovery hashed the bytes it parsed; hash the file only for a record built without them.
            hash = plugin.ManifestHash ?? PluginPackageHash.ComputeManifestHash(plugin.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PluginEvaluation(plugin, PluginState.InvalidManifest, unsigned, ex.Message, null, isBundledId);
        }

        // The bytes discovery hashed, so plugin.json is read once per scan (spec §5.2).
        var signature = PluginSignatureVerifier.Verify(plugin.Folder, plugin.Manifest!, plugin.ManifestBytes);
        var approval = store.Resolve(plugin.Id, hash, signature.Status, plugin.Folder, approvals);
        if (approval == ApprovalState.NeedsConsent && IsBundled(plugin, hash, bundled, bundledRoot))
        {
            // Shipped with SumaFile: trusted without consent, and nothing is written to the store. Only a Disabled
            // the user stored for this folder holds, whatever manifest it was stored with, so it survives upgrades.
            // When the store cannot be read that Disabled cannot be seen, so the plugin stays NeedsConsent (as an
            // unbundled one would) rather than being turned on against the user's choice.
            var stored = store.StoredState(plugin.Id, plugin.Folder, approvals, out var unreadable);
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
        return new PluginEvaluation(plugin, state, signature, signature.Error, hash, isBundledId);
    }

    /// <summary>
    /// The plugin sits in <paramref name="bundledRoot"/> (<see cref="PluginManagerOptions.BundledRoot"/>) and that
    /// root's <c>bundled.json</c> lists its id with this manifest hash. A copy in another root (only
    /// <c>SUMAFILE_PLUGIN_ROOTS</c> adds one), or a list in any other root, never qualifies.
    /// </summary>
    private static bool IsBundled(
        DiscoveredPlugin plugin, string hash, IReadOnlyDictionary<string, string> bundled, string? bundledRoot) =>
        bundledRoot is not null
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
            // Before a backend can start: the old one may still own the plugin's pipe.
            await WaitForLoadFailureTeardownAsync(id).ConfigureAwait(false);
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

    /// <summary>
    /// Sets Disabled, tears the plugin down and stores Disabled. When the approvals file cannot be written the plugin
    /// still stops and <see cref="Changed"/> is still raised (its contributions go), then the write's exception is
    /// thrown; the next start reads the plugin as enabled again.
    /// </summary>
    public async Task DisableAsync(string id, CancellationToken ct)
    {
        var changed = false;
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                // So Disable returns only once the plugin has really stopped.
                await WaitForLoadFailureTeardownAsync(id).ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_disposed, this);
                var record = Approvable(id);
                lock (_gate)
                {
                    // First, so no command can attach a new runtime while the old one is torn down.
                    record.State = PluginState.Disabled;
                    record.Error = record.Signature.Error;
                    changed = true;
                }

                if (AfterDisableStateSet is { } afterDisableStateSet) await afterDisableStateSet().ConfigureAwait(false);
                await TeardownAsync(record).ConfigureAwait(false);
                _approvals.Set(id, record.ManifestHash!, record.Signature.Status, ApprovalState.Disabled, record.Discovered.Folder);
            }
            finally
            {
                _ops.Release();
            }
        }
        finally
        {
            // Outside _ops, as everywhere; also when the approvals write threw, since the plugin has stopped.
            if (changed) RaiseChanged();
        }
    }

    public async Task RestartBackendAsync(string id, CancellationToken ct)
    {
        PluginRuntime runtime;
        await _ops.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForLoadFailureTeardownAsync(id).ConfigureAwait(false);
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
    /// Activates the plugin if needed and asks its <see cref="IPanelProvider"/> for the body of panel
    /// <paramref name="panelId"/>, on the UI thread. <paramref name="detached"/> says whether the body goes into the
    /// host's panel window rather than a docked region; the provider is asked again for each presentation. The App's
    /// <see cref="PluginManagerOptions.ValidateUiElement"/> has accepted the result.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not Enabled or declares no such panel; nothing changes.</exception>
    /// <exception cref="PluginLoadException">Activation or the provider failed; the plugin is now LoadFailed.</exception>
    public async Task<object> CreatePanelAsync(string pluginId, string panelId, bool detached, CancellationToken ct)
    {
        PluginPanelSpec? spec = null;
        var (record, runtime, instance) = await ActivateAsync(
                pluginId,
                manifest => spec = manifest.Contributions.Panels.FirstOrDefault(p => p.Id == panelId)
                    ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no panel '{panelId}'."),
                ct)
            .ConfigureAwait(false);

        if (instance is not IPanelProvider provider)
        {
            throw await LoadFailedAsync(
                    record,
                    runtime,
                    $"Plugin '{pluginId}' declares a panel but its entry type does not implement IPanelProvider.",
                    null)
                .ConfigureAwait(false);
        }

        var request = new PluginPanelRequest(spec!.Id, spec.Provider, detached);
        return await CreateElementAsync(
                record,
                runtime,
                () => provider.CreatePanel(request),
                $"Plugin '{pluginId}' returned no element for panel '{panelId}'.",
                $"Plugin '{pluginId}' failed to create panel '{panelId}': ",
                $"Plugin '{pluginId}' panel '{panelId}' ")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Activates the plugin if needed, on the UI thread through <see cref="IPluginUiThread.RunAsync{T}"/>, and runs its
    /// <see cref="IRowDecorationProvider"/> on the thread pool. The one caller, the host's decoration coordinator, calls
    /// from the thread pool, so the activation is queued to the UI thread and never runs inside a host pass. A provider
    /// that throws or returns null is logged as <c>[warn] Row decorations failed: …</c> and gives null; the plugin
    /// stays Enabled. A call the caller cancels gives null without a log line, since superseded navigations are
    /// cancelled as a matter of course. For that reason the caller's token never reaches <c>IPlugin.ActivateAsync</c>:
    /// a cancel during activation only stops this call's wait, and the activation runs to its end.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The plugin is not Enabled or declares no row decoration provider named <c>request.Provider</c>; nothing changes.
    /// </exception>
    /// <exception cref="PluginLoadException">Activation failed or the entry type lacks the interface; the plugin is now LoadFailed.</exception>
    public async Task<PluginFolderDecorations?> GetRowDecorationsAsync(
        string pluginId, PluginDecorationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        PluginRecord record;
        PluginRuntime runtime;
        IPlugin instance;
        try
        {
            (record, runtime, instance) = await ActivateAsync(
                    pluginId,
                    manifest =>
                    {
                        if (manifest.Contributions.RowDecorations?.Provider != request.Provider)
                        {
                            throw new InvalidOperationException(
                                $"Plugin '{pluginId}' has no row decoration provider '{request.Provider}'.");
                        }
                    },
                    ct,
                    detachActivation: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }

        if (instance is not IRowDecorationProvider provider)
        {
            throw await LoadFailedAsync(
                    record,
                    runtime,
                    $"Plugin '{pluginId}' declares row decorations but its entry type does not implement IRowDecorationProvider.",
                    null)
                .ConfigureAwait(false);
        }

        Task<PluginFolderDecorations>? call = null;
        try
        {
            // The provider must not touch WinUI, and its first synchronous part must not hold up the caller either.
            // The caller's cancel ends the wait even for a provider that ignores its token.
            call = Task.Run(() => provider.GetDecorationsAsync(request, ct), ct);
            var decorations = await call.WaitAsync(ct).ConfigureAwait(false);
            if (decorations is null && !ct.IsCancellationRequested)
            {
                _options.Log(pluginId, "[warn] Row decorations failed: the provider returned no result.");
            }

            return ct.IsCancellationRequested ? null : decorations;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Whatever the provider throws once its caller has gone (its own error for a stopped git, say) is not
            // logged; nobody awaits an abandoned call, so its later failure is observed here.
            if (call is not null)
            {
                _ = call.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return null;
        }
        catch (Exception ex)
        {
            _options.Log(pluginId, $"[warn] Row decorations failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Copies legacy SumaFile settings into the declared settings of bundled plugins (spec §8.1): for every record whose
    /// id <c>bundled.json</c> lists and every setting that declares <c>migrateFrom</c>, it reads
    /// <c>plugin.&lt;id&gt;.&lt;key&gt;</c> and the legacy key (all of them in one
    /// <see cref="ISettingsBackend.GetSettingsAsync"/>). When the plugin's value is unset or blank and the legacy value
    /// is valid for the setting, it writes that value once and logs <c>[info] Migrated setting &lt;key&gt; from
    /// &lt;legacy&gt;</c>. It never writes or deletes the legacy key. Activation waits for the latest migration, at
    /// most <see cref="LegacyMigrationWait"/>, so no plugin reads its setting before the copy.
    /// </summary>
    /// <returns>The number of settings copied. A failed migration is logged as well as thrown.</returns>
    public Task<int> MigrateLegacySettingsAsync(CancellationToken ct)
    {
        List<(string PluginId, PluginSettingSpec Spec)> settings;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            settings = _records
                .Where(r => r.IsBundledId && r.State != PluginState.Shadowed && r.Discovered.Manifest is not null)
                .SelectMany(r => r.Discovered.Manifest!.Settings
                    .Where(s => s.MigrateFrom is not null)
                    .Select(s => (r.Id, s)))
                .DistinctBy(pair => PluginHostMethods.SettingsKey(pair.Id, pair.s.Key), StringComparer.Ordinal)
                .ToList();
        }

        var migration = settings.Count == 0 ? Task.FromResult(0) : MigrateAsync(settings, ct);
        Volatile.Write(ref _legacyMigration, BoundMigrationAsync(migration, ct));
        return migration;
    }

    private async Task<int> MigrateAsync(IReadOnlyList<(string PluginId, PluginSettingSpec Spec)> settings, CancellationToken ct)
    {
        var keys = settings
            .SelectMany(pair => new[] { PluginHostMethods.SettingsKey(pair.PluginId, pair.Spec.Key), pair.Spec.MigrateFrom! })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var values = await _options.Settings.GetSettingsAsync(keys, ct).ConfigureAwait(false);

        var copied = 0;
        foreach (var (pluginId, spec) in settings)
        {
            var storageKey = PluginHostMethods.SettingsKey(pluginId, spec.Key);
            if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault(storageKey))) continue;
            if (values.GetValueOrDefault(spec.MigrateFrom!) is not { } legacy
                || PluginSettingsSchema.ValidateValue(spec, legacy) is not null)
            {
                continue;
            }

            await _options.Settings.SetSettingAsync(storageKey, legacy, ct).ConfigureAwait(false);
            copied++;
            _options.Log(pluginId, $"[info] Migrated setting {spec.Key} from {spec.MigrateFrom}");
        }

        return copied;
    }

    /// <summary>
    /// Completes when <paramref name="migration"/> ends or <see cref="LegacyMigrationWait"/> passes, whichever is
    /// first, and never faults; the timeout and a failure are logged once here.
    /// </summary>
    private async Task BoundMigrationAsync(Task migration, CancellationToken ct)
    {
        try
        {
            await migration.WaitAsync(LegacyMigrationWait).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!migration.IsCompleted)
        {
            // The bound passed. A TimeoutException of the migration's own (a settings call that timed out) is a
            // failure, logged below.
            var seconds = LegacyMigrationWait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            _options.Log(
                "host",
                $"[warn] The legacy settings migration is still running after {seconds} s; activating plugins anyway.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _options.Log("host", $"[warn] The legacy settings migration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes one declared setting of a plugin (Settings &gt; Plugins), then asks the host to decorate that plugin's
    /// folders again, since a setting such as <c>enabled</c> can change its decorations. Loads no plugin code.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="storageKey"/> is not <c>plugin.&lt;pluginId&gt;.&lt;key&gt;</c> for a key the plugin's manifest
    /// declares, or <paramref name="value"/> is not valid for that setting; nothing is written.
    /// </exception>
    public async Task WriteSettingAsync(string pluginId, string storageKey, string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentNullException.ThrowIfNull(value);

        PluginSettingSpec spec;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            spec = Live(pluginId)?.Discovered.Manifest?.Settings
                    .FirstOrDefault(s => string.Equals(PluginHostMethods.SettingsKey(pluginId, s.Key), storageKey, StringComparison.Ordinal))
                ?? throw new ArgumentException(
                    $"'{storageKey}' is not a setting that plugin '{pluginId}' declares.", nameof(storageKey));
        }

        if (PluginSettingsSchema.ValidateValue(spec, value) is { } invalid)
        {
            throw new ArgumentException(invalid, nameof(value));
        }

        await _options.Settings.SetSettingAsync(storageKey, value, ct).ConfigureAwait(false);
        try
        {
            _options.InvalidateDecorations?.Invoke(pluginId);
        }
        catch (Exception ex)
        {
            _options.Log(pluginId, $"[warn] Decorating again after a setting change failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The one activation path of commands, sidebar sections, settings pages, panels and row
    /// decorations: the wait for the legacy settings migration, the Enabled check, the
    /// <paramref name="requireDeclared"/> check (which throws before any state changes), the runtime, activation on
    /// the UI thread, <see cref="MarkLoadFailedAsync"/> when activation fails, and the retired re-check.
    /// </summary>
    /// <param name="detachActivation">
    /// True for callers whose token is cancelled as a matter of course (row decorations: every superseded navigation).
    /// The plugin's <c>ActivateAsync</c> then runs under <see cref="CancellationToken.None"/> and
    /// <paramref name="ct"/> only stops this caller's wait. A cancel can therefore never abandon a half-activated
    /// instance: the activation runs to its end, the runtime keeps the instance (so the next call reuses it and a
    /// retire deactivates it), and a failure still marks the plugin LoadFailed.
    /// </param>
    private async Task<(PluginRecord Record, PluginRuntime Runtime, IPlugin Instance)> ActivateAsync(
        string pluginId, Action<PluginManifest> requireDeclared, CancellationToken ct, bool detachActivation = false)
    {
        // No plugin activates before the legacy settings it reads are migrated (bounded; see MigrateLegacySettingsAsync).
        var migration = Volatile.Read(ref _legacyMigration);
        if (!migration.IsCompleted)
        {
            await migration.WaitAsync(ct).ConfigureAwait(false);
        }

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
        if (detachActivation)
        {
            var activation = ActivateInstanceAsync(record, runtime, CancellationToken.None);
            try
            {
                instance = await activation.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Nobody awaits the activation any more; its failure is recorded by ActivateInstanceAsync itself.
                _ = activation.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
        }
        else
        {
            instance = await ActivateInstanceAsync(record, runtime, ct).ConfigureAwait(false);
        }

        ThrowIfRetired(record, runtime);
        return (record, runtime, instance);
    }

    /// <summary>
    /// Creates and activates the instance on the UI thread under <paramref name="ct"/>; any failure except a cancel
    /// of <paramref name="ct"/> or a retired runtime marks the plugin LoadFailed and surfaces as a
    /// <see cref="PluginLoadException"/>.
    /// </summary>
    private async Task<IPlugin> ActivateInstanceAsync(PluginRecord record, PluginRuntime runtime, CancellationToken ct)
    {
        try
        {
            return await UiThread
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
            throw new PluginLoadException($"Plugin '{record.Id}' failed to activate: {ex.Message}", ex);
        }
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
    /// <see cref="PluginRecord.Epoch"/> and raises <see cref="Changed"/> at once. It then starts ending the runtime in
    /// teardown order (windows, then the backend, then deactivation) as a tracked task and returns without awaiting
    /// it, so the caller gets its <see cref="PluginLoadException"/> right away. Scans, enable, disable, restart and
    /// dispose wait for that task (<see cref="WaitForLoadFailureTeardownAsync"/>).
    /// </summary>
    private Task MarkLoadFailedAsync(PluginRecord record, PluginRuntime runtime, Exception ex)
    {
        lock (_gate)
        {
            // Only a live, enabled runtime can fail to load. A Disable that set the state before its teardown
            // detached the runtime keeps Disabled; the teardown it is about to run stops the backend.
            if (!ReferenceEquals(record.Runtime, runtime) || record.State != PluginState.Enabled) return Task.CompletedTask;
            record.State = PluginState.LoadFailed;
            record.Error = ex.Message;
            var connection = StopBackendLocked(record, out var unregisterGeneration);
            record.Runtime = null;
            record.Epoch = NextEpoch();
            // A failed load keeps LoadFailed even when deactivation then times out; RetireAsync logs that.
            TrackLoadFailureTeardownLocked(
                record.Id,
                () => StopRuntimeAsync(record, runtime, connection, unregisterGeneration));
        }

        _options.Log(record.Id, $"[error] {ex.Message}");
        RaiseChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs <paramref name="teardown"/> on the thread pool after any earlier load-failure teardown of the same plugin,
    /// and tracks it in <see cref="_loadFailureTeardowns"/> until it ends. Call under <see cref="_gate"/>.
    /// </summary>
    private void TrackLoadFailureTeardownLocked(string pluginId, Func<Task> teardown)
    {
        _loadFailureTeardowns.TryGetValue(pluginId, out var previous);
        var task = RunAfterAsync(previous, teardown);
        _loadFailureTeardowns[pluginId] = task;
        _ = task.ContinueWith(
            ended =>
            {
                lock (_gate)
                {
                    if (_loadFailureTeardowns.TryGetValue(pluginId, out var current) && ReferenceEquals(current, ended))
                    {
                        _loadFailureTeardowns.Remove(pluginId);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        async Task RunAfterAsync(Task? before, Func<Task> run)
        {
            // Off the caller's thread (and out of _gate) before any teardown step runs.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            if (before is not null)
            {
                try { await before.ConfigureAwait(false); } catch { }
            }

            try
            {
                await run().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _options.Log(pluginId, $"[warn] Plugin teardown after its load failure failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Waits, at most <see cref="LoadFailureTeardownWait"/>, for the load-failure teardown of <paramref name="pluginId"/>,
    /// or of every plugin when it is null; past the bound it logs and returns. Returns a completed task when nothing is
    /// tracked. The teardown never takes <c>_ops</c>, so callers may wait under it.
    /// </summary>
    private Task WaitForLoadFailureTeardownAsync(string? pluginId)
    {
        KeyValuePair<string, Task>[] pending;
        lock (_gate)
        {
            pending = pluginId is null
                ? [.. _loadFailureTeardowns]
                : _loadFailureTeardowns.TryGetValue(pluginId, out var task) ? [new(pluginId, task)] : [];
        }

        return pending.Length == 0 ? Task.CompletedTask : WaitBoundedAsync(pending);

        async Task WaitBoundedAsync(KeyValuePair<string, Task>[] teardowns)
        {
            try
            {
                await Task.WhenAll(teardowns.Select(teardown => teardown.Value))
                    .WaitAsync(LoadFailureTeardownWait)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                foreach (var (id, task) in teardowns)
                {
                    if (!task.IsCompleted)
                    {
                        _options.Log(id, $"[warn] Plugin '{id}' is still tearing down after its load failure; continuing.");
                    }
                }
            }
        }
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

            // A load failure's teardown runs outside _ops; the manager is not done until it is.
            await WaitForLoadFailureTeardownAsync(null).ConfigureAwait(false);
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
            _options.PanelsFor?.Invoke(record.Id) ?? NoPluginPanels.Instance,
            new PluginDecorationsFacade(record.Id, _options.InvalidateDecorations),
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
