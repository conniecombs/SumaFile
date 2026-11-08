using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Loading;
using SimpleFile.PluginHost.Trust;
using SimpleFile.PluginSdk;
using Xunit;

namespace SimpleFile.Tests;

[Collection(HelloFixture.Collection)]
public class PluginManagerTests : IAsyncLifetime
{
    private const string BackendId = "com.example.backend";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
    private readonly List<FakePluginProcessLauncher> _launchers = [];
    private readonly List<PluginManager> _managers = [];
    private readonly HelloFixture.MarkerScope _markers = new();

    private string Root => Path.Combine(_temp, "plugins");
    private string StorePath => Path.Combine(_temp, "approvals.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }

        foreach (var launcher in _launchers)
        {
            await launcher.DisposeAsync();
        }

        _markers.Dispose();
        try { Directory.Delete(_temp, true); } catch { }
    }

    private string _pipe = "";

    private PluginManager Manager(IPluginProcessLauncher? launcher = null, IArchiveEngineRegistrar? archiveEngines = null)
    {
        var pipe = "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N");
        _pipe = pipe;
        var manager = new PluginManager(new PluginManagerOptions
        {
            Roots = [Root],
            ApprovalStorePath = StorePath,
            DataRoot = Path.Combine(_temp, "data"),
            Launcher = launcher ?? new ThrowingLauncher(),
            PipeNameFor = _ => pipe,
            Settings = new FakeSettings(),
            Progress = new RecordingSink(),
            Notifier = new RecordingNotifier(),
            DialogsFor = _ => null!,
            Workspace = null!,
            Log = (_, _) => { },
            ArchiveEngines = archiveEngines,
        });
        _managers.Add(manager);
        return manager;
    }

    private FakePluginProcessLauncher HandshakingLauncher()
    {
        var launcher = new FakePluginProcessLauncher(async (server, ct) =>
        {
            await server.WaitForConnectionAsync(ct);
            var request = await server.ReadRequestAsync(ct);
            await server.SendResultAsync(
                request.Id, new { pluginId = BackendId, pluginVersion = "1.0.0", sdkVersion = "1.0.0" }, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }, createServer: true);
        _launchers.Add(launcher);
        return launcher;
    }

    private string WriteBackendPlugin(string activateOn = "startup")
    {
        var folder = Path.Combine(Root, BackendId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            {
              "id": "{{BackendId}}",
              "name": "Backend",
              "version": "1.0.0",
              "publisher": { "name": "P" },
              "sdkVersion": "1.0.0",
              "backend": { "executable": "backend.exe", "args": [], "activateOn": "{{activateOn}}", "acceptsMultipleClients": false },
              "contributions": { "commands": [] }
            }
            """);
        return folder;
    }

    private void Approve(string id, string folder, ApprovalState state = ApprovalState.Enabled) =>
        new PluginApprovalStore(StorePath).Set(id, PluginPackageHash.ComputeManifestHash(folder), SignatureStatus.Unsigned, state, folder);

    /// <summary>A backend plugin with one archive format; first-call on purpose, since archive engines always start at startup.</summary>
    private string WriteArchivePlugin()
    {
        var folder = Path.Combine(Root, BackendId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            {
              "id": "{{BackendId}}",
              "name": "Backend",
              "version": "1.0.0",
              "publisher": { "name": "P" },
              "sdkVersion": "1.0.0",
              "backend": { "executable": "backend.exe", "args": [], "activateOn": "first-call", "acceptsMultipleClients": true },
              "contributions": {
                "archiveFormats": [ { "extension": ".bk", "canList": true, "canExtract": true, "canCreate": false, "canModify": false, "engineName": "bk" } ]
              }
            }
            """);
        return folder;
    }

    private static PluginRecord Record(PluginManager manager, string id) => manager.Plugins.Single(p => p.Id == id);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 5 s");
            await Task.Delay(20);
        }
    }

    private static PluginCommandContext Context(string command) =>
        new(PluginManager.HostCommandId(HelloFixture.Id, command), null, []);

    [Fact]
    public async Task Discover_NewPlugin_NeedsConsent()
    {
        HelloFixture.CopyTo(Root);
        var manager = Manager();

        await manager.DiscoverAsync(CancellationToken.None);

        var record = Record(manager, HelloFixture.Id);
        Assert.Equal(PluginState.NeedsConsent, record.State);
        Assert.Equal(SignatureStatus.Unsigned, record.Signature.Status);
        Assert.False(record.BackendRunning);
        Assert.Empty(manager.EnabledPlugins);
    }

    [Fact]
    public async Task Enable_WritesApprovalAndContributes()
    {
        var folder = HelloFixture.CopyTo(Root);
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var changed = 0;
        manager.Changed += (_, _) => changed++;

        await manager.EnableAsync(HelloFixture.Id, CancellationToken.None);

        Assert.Equal(PluginState.Enabled, Record(manager, HelloFixture.Id).State);
        Assert.Equal([HelloFixture.Id], manager.EnabledPlugins.Select(p => p.Id));
        Assert.True(changed > 0);
        var stored = new PluginApprovalStore(StorePath)
            .Resolve(HelloFixture.Id, PluginPackageHash.ComputeManifestHash(folder), SignatureStatus.Unsigned, folder);
        Assert.Equal(ApprovalState.Enabled, stored);
    }

    [Fact]
    public async Task Discover_ApprovedUnchangedPlugin_IsEnabled()
    {
        var folder = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, folder);
        var manager = Manager();

        await manager.DiscoverAsync(CancellationToken.None);

        Assert.Equal(PluginState.Enabled, Record(manager, HelloFixture.Id).State);
        Assert.Single(manager.EnabledPlugins);
    }

    [Fact]
    public async Task Discover_ApprovedButManifestChanged_NeedsConsentAgain()
    {
        var folder = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, folder);
        var manifest = Path.Combine(folder, "plugin.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"Hello fixture\"", "\"Hello fixture v2\""));
        var manager = Manager();

        await manager.DiscoverAsync(CancellationToken.None);

        Assert.Equal(PluginState.NeedsConsent, Record(manager, HelloFixture.Id).State);
        Assert.Empty(manager.EnabledPlugins);
    }

    [Fact]
    public async Task RunCommand_ActivatesOnceThenExecutes()
    {
        var folder = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, folder);
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var id = PluginManager.HostCommandId(HelloFixture.Id, "greet");

        await manager.RunCommandAsync(id, Context("greet"), CancellationToken.None);
        Assert.True(File.Exists(_markers.Activated));
        File.Delete(_markers.Activated);
        await manager.RunCommandAsync(id, Context("greet"), CancellationToken.None);

        Assert.False(File.Exists(_markers.Activated), "the plugin was activated a second time");
        Assert.Equal(2, File.ReadAllLines(_markers.CommandsLog).Length);
        Assert.Equal(PluginState.Enabled, Record(manager, HelloFixture.Id).State);
        Assert.False(Record(manager, HelloFixture.Id).BackendRunning);
    }

    [Fact]
    public async Task RunCommand_LoadFailure_SetsLoadFailedWithError()
    {
        var folder = HelloFixture.CopyTo(Root);
        var manifest = Path.Combine(folder, "plugin.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest)
            .Replace("\"SimpleFile.Tests.Fixtures.HelloPlugin.dll\"", "\"Missing.dll\""));
        Approve(HelloFixture.Id, folder);
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var changed = 0;
        manager.Changed += (_, _) => changed++;

        await Assert.ThrowsAsync<PluginLoadException>(() => manager.RunCommandAsync(
            PluginManager.HostCommandId(HelloFixture.Id, "greet"), Context("greet"), CancellationToken.None));

        var record = Record(manager, HelloFixture.Id);
        Assert.Equal(PluginState.LoadFailed, record.State);
        Assert.Contains("Missing.dll", record.Error);
        Assert.Empty(manager.EnabledPlugins);
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task Backend_StartupActivate_StartsDuringDiscover()
    {
        var folder = WriteBackendPlugin("startup");
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var manager = Manager(launcher);

        await manager.DiscoverAsync(CancellationToken.None);

        // Discovery does not wait for the backend; the launch follows shortly after.
        var executable = await launcher.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Path.Combine(folder, "backend.exe"), executable);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
    }

    [Fact]
    public async Task Backend_ThreeFailures_SetsFaulted()
    {
        var folder = WriteBackendPlugin("startup");
        Approve(BackendId, folder);
        var launcher = new ThrowingLauncher();
        var manager = Manager(launcher);

        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => launcher.Launches == 1 && Record(manager, BackendId).Error is not null);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);

        await Assert.ThrowsAsync<PluginBackendException>(() => manager.RestartBackendAsync(BackendId, CancellationToken.None));
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
        await Assert.ThrowsAsync<PluginBackendException>(() => manager.RestartBackendAsync(BackendId, CancellationToken.None));

        Assert.Equal(3, launcher.Launches);
        var record = Record(manager, BackendId);
        Assert.Equal(PluginState.Faulted, record.State);
        Assert.NotNull(record.Error);
        Assert.False(record.BackendRunning);
        Assert.Empty(manager.EnabledPlugins);
    }

    [Fact]
    public async Task Backend_Lost_SetsFaultedAndRaisesChanged()
    {
        var folder = WriteBackendPlugin("startup");
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var manager = Manager(launcher);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += (_, _) =>
        {
            if (Record(manager, BackendId).State == PluginState.Faulted) changed.TrySetResult();
        };

        launcher.Process!.SimulateExit();

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var record = Record(manager, BackendId);
        Assert.Equal(PluginState.Faulted, record.State);
        Assert.False(record.BackendRunning);
        Assert.NotNull(record.Error);
    }

    [Fact]
    public async Task Disable_StopsBackendAndPersists()
    {
        var folder = WriteBackendPlugin("startup");
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var manager = Manager(launcher);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);

        await manager.DisableAsync(BackendId, CancellationToken.None);

        Assert.True(launcher.Process!.Killed);
        var record = Record(manager, BackendId);
        Assert.Equal(PluginState.Disabled, record.State);
        Assert.False(record.BackendRunning);
        Assert.Empty(manager.EnabledPlugins);
        var stored = new PluginApprovalStore(StorePath)
            .Resolve(BackendId, PluginPackageHash.ComputeManifestHash(folder), SignatureStatus.Unsigned, folder);
        Assert.Equal(ApprovalState.Disabled, stored);

        var fresh = Manager();
        await fresh.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.Disabled, Record(fresh, BackendId).State);
    }

    [Fact]
    public async Task Rescan_RemovedFolder_DropsRecordAndContributions()
    {
        var hello = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, hello);
        var backend = WriteBackendPlugin("first-call");
        Approve(BackendId, backend);
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        Assert.Equal(2, manager.EnabledPlugins.Count);
        var kept = Record(manager, BackendId);

        Directory.Delete(hello, true);
        await manager.RescanAsync(CancellationToken.None);

        Assert.DoesNotContain(manager.Plugins, p => p.Id == HelloFixture.Id);
        Assert.Equal([BackendId], manager.EnabledPlugins.Select(p => p.Id));
        Assert.Same(kept, Record(manager, BackendId));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.RunCommandAsync(
            PluginManager.HostCommandId(HelloFixture.Id, "greet"), Context("greet"), CancellationToken.None));
    }

    [Fact]
    public async Task RunCommand_RacingDisable_DoesNotReactivate()
    {
        var folder = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, folder);
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new GatedPlugin();
        manager.InstanceFactory = _ => plugin;
        var id = PluginManager.HostCommandId(HelloFixture.Id, "greet");

        var first = manager.RunCommandAsync(id, Context("greet"), CancellationToken.None);
        await plugin.ActivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = manager.RunCommandAsync(id, Context("greet"), CancellationToken.None);
        var disable = manager.DisableAsync(HelloFixture.Id, CancellationToken.None);
        Assert.False(disable.IsCompleted);

        plugin.ReleaseActivation.SetResult();
        await disable.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            manager.RunCommandAsync(id, Context("greet"), CancellationToken.None));
        Assert.Equal(1, plugin.Activations);
        Assert.Equal(1, plugin.Deactivations);
        Assert.Equal(0, plugin.Executions);
        Assert.Equal(PluginState.Disabled, Record(manager, HelloFixture.Id).State);
    }

    [Fact]
    public async Task Backend_SubscribeWhileStartupLaunchBlocked_DoesNotDeadlock()
    {
        // Formerly: the blocked launch held the services lock, the subscribing plugin held the runtime
        // lock and waited for it, and the launch failure then needed the runtime lock.
        var folder = HelloFixture.CopyTo(Root);
        var manifest = Path.Combine(folder, "plugin.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"first-call\"", "\"startup\""));
        Approve(HelloFixture.Id, folder);
        using var launcher = new BlockingThrowingLauncher();
        var manager = Manager(launcher);
        var plugin = new SubscribingPlugin();
        manager.InstanceFactory = _ => plugin;

        // Discovery returns while the startup launch is still blocked.
        await Task.Run(() => manager.DiscoverAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(launcher.Entered.Wait(TimeSpan.FromSeconds(5)), "startup launch never began");

        await Task.Run(() => manager.RunCommandAsync(
                PluginManager.HostCommandId(HelloFixture.Id, "greet"), Context("greet"), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(plugin.Subscribed);

        launcher.Release.Set();
        await WaitUntil(() => Record(manager, HelloFixture.Id).Error is not null);
        Assert.Equal(PluginState.Enabled, Record(manager, HelloFixture.Id).State);
    }

    [Fact]
    public async Task Dispose_WithStartupBackendInFlight_KillsIt()
    {
        var folder = WriteBackendPlugin("startup");
        Approve(BackendId, folder);
        var launcher = new FakePluginProcessLauncher(async (server, ct) =>
        {
            await server.WaitForConnectionAsync(ct);
            var request = await server.ReadRequestAsync(ct);
            await Task.Delay(300, ct);
            await server.SendResultAsync(
                request.Id, new { pluginId = BackendId, pluginVersion = "1.0.0", sdkVersion = "1.0.0" }, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }, createServer: true);
        _launchers.Add(launcher);
        var manager = Manager(launcher);
        await manager.DiscoverAsync(CancellationToken.None);
        await launcher.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Record(manager, BackendId).BackendRunning);

        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(launcher.Process!.Killed);
        Assert.False(Record(manager, BackendId).BackendRunning);
    }

    [Fact]
    public async Task Disable_PluginWhoseDeactivateNeverCompletes_FaultsAndFinishes()
    {
        var folder = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, folder);
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new HangingDeactivatePlugin();
        manager.InstanceFactory = _ => plugin;
        await manager.RunCommandAsync(
            PluginManager.HostCommandId(HelloFixture.Id, "greet"), Context("greet"), CancellationToken.None);

        await manager.DisableAsync(HelloFixture.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(plugin.DeactivateEntered);
        var record = Record(manager, HelloFixture.Id);
        Assert.Equal(PluginState.Faulted, record.State);
        Assert.Equal("plugin did not deactivate in time", record.Error);
        // The manager is not wedged: later operations still run.
        await manager.RescanAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ArchivePlugin_RegistersAfterHandshake_AtStartupEvenWhenFirstCall()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var manager = Manager(launcher, registrar);

        await manager.DiscoverAsync(CancellationToken.None);

        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        var registration = Assert.Single(registrar.Registered);
        Assert.Equal(BackendId, registration.PluginId);
        Assert.Equal(_pipe, registration.PipeName);
        var format = Assert.Single(registration.Formats);
        Assert.Equal((".bk", true, true, false, false, "bk"),
            (format.Extension, format.CanList, format.CanExtract, format.CanCreate, format.CanModify, format.EngineName));
        Assert.Empty(registrar.Unregistered);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
    }

    [Fact]
    public async Task ArchivePlugin_Lost_Unregisters()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var manager = Manager(launcher, registrar);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);

        launcher.Process!.SimulateExit();

        await WaitUntil(() => registrar.Unregistered.Contains(BackendId));
        Assert.Equal(PluginState.Faulted, Record(manager, BackendId).State);
        Assert.Single(registrar.Unregistered);
    }

    [Fact]
    public async Task ArchivePlugin_Disable_Unregisters()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var manager = Manager(launcher, registrar);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);

        await manager.DisableAsync(BackendId, CancellationToken.None);

        Assert.Equal([BackendId], registrar.Unregistered);
        Assert.True(launcher.Process!.Killed);
    }

    [Fact]
    public async Task ArchivePlugin_Dispose_Unregisters()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var manager = Manager(launcher, registrar);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);

        await manager.DisposeAsync();

        Assert.Equal([BackendId], registrar.Unregistered);
        Assert.True(launcher.Process!.Killed);
    }

    [Fact]
    public async Task ArchivePlugin_RegistrationFails_SetsFaultedAndStopsBackend()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar { Failure = new InvalidOperationException("service refused") };
        var manager = Manager(launcher, registrar);

        await manager.DiscoverAsync(CancellationToken.None);

        await WaitUntil(() => Record(manager, BackendId).State == PluginState.Faulted);
        var record = Record(manager, BackendId);
        Assert.Contains("service refused", record.Error);
        Assert.False(record.BackendRunning);
        await WaitUntil(() => launcher.Process!.Killed);
        Assert.Empty(registrar.Unregistered);
    }

    [Fact]
    public void HostCommandId_RoundTrips()
    {
        var id = PluginManager.HostCommandId("com.example.a", "do-thing");

        Assert.Equal("plugin:com.example.a:do-thing", id);
        Assert.True(PluginManager.TryParseHostCommandId(id, out var pluginId, out var command));
        Assert.Equal("com.example.a", pluginId);
        Assert.Equal("do-thing", command);

        Assert.False(PluginManager.TryParseHostCommandId("open-folder", out _, out _));
        Assert.False(PluginManager.TryParseHostCommandId("plugin:com.example.a", out _, out _));
        Assert.False(PluginManager.TryParseHostCommandId("plugin::cmd", out _, out _));
        Assert.False(PluginManager.TryParseHostCommandId("plugin:com.example.a:", out _, out _));
    }

    private sealed class GatedPlugin : IPlugin, ICommandHandler
    {
        private int _activations;
        private int _deactivations;
        private int _executions;

        public TaskCompletionSource ActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseActivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Activations => Volatile.Read(ref _activations);
        public int Deactivations => Volatile.Read(ref _deactivations);
        public int Executions => Volatile.Read(ref _executions);

        public async Task ActivateAsync(IPluginHost host, CancellationToken ct)
        {
            Interlocked.Increment(ref _activations);
            ActivationEntered.TrySetResult();
            await ReleaseActivation.Task;
        }

        public Task DeactivateAsync()
        {
            Interlocked.Increment(ref _deactivations);
            return Task.CompletedTask;
        }

        public Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _executions);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRegistrar : IArchiveEngineRegistrar
    {
        private readonly object _lock = new();
        private readonly List<(string PluginId, string PipeName, IReadOnlyList<SimpleFile.PluginHost.Manifest.PluginArchiveFormatSpec> Formats)> _registered = [];
        private readonly List<string> _unregistered = [];

        public Exception? Failure { get; init; }

        public IReadOnlyList<(string PluginId, string PipeName, IReadOnlyList<SimpleFile.PluginHost.Manifest.PluginArchiveFormatSpec> Formats)> Registered
        {
            get { lock (_lock) return _registered.ToList(); }
        }

        public IReadOnlyList<string> Unregistered
        {
            get { lock (_lock) return _unregistered.ToList(); }
        }

        public Task RegisterAsync(
            string pluginId,
            string pipeName,
            IReadOnlyList<SimpleFile.PluginHost.Manifest.PluginArchiveFormatSpec> formats,
            CancellationToken ct)
        {
            if (Failure is not null) return Task.FromException(Failure);
            lock (_lock) _registered.Add((pluginId, pipeName, formats));
            return Task.CompletedTask;
        }

        public Task UnregisterAsync(string pluginId, CancellationToken ct)
        {
            lock (_lock) _unregistered.Add(pluginId);
            return Task.CompletedTask;
        }
    }

    private sealed class HangingDeactivatePlugin : IPlugin, ICommandHandler
    {
        private volatile bool _deactivateEntered;

        public bool DeactivateEntered => _deactivateEntered;

        public Task ActivateAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task DeactivateAsync()
        {
            _deactivateEntered = true;
            return new TaskCompletionSource().Task;
        }

        public Task ExecuteAsync(PluginCommandContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SubscribingPlugin : IPlugin, ICommandHandler
    {
        public bool Subscribed { get; private set; }

        public Task ActivateAsync(IPluginHost host, CancellationToken ct)
        {
            host.OnBackendEvent<string>("tick", _ => { });
            Subscribed = true;
            return Task.CompletedTask;
        }

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task ExecuteAsync(PluginCommandContext context, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Blocks inside Launch until released, then fails the start.</summary>
    private sealed class BlockingThrowingLauncher : IPluginProcessLauncher, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public IPluginProcess Launch(
            string executable, IReadOnlyList<string> args, string workingDirectory, Action<string> onStderrLine)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            throw new IOException("backend executable not found");
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
        }
    }

    private sealed class ThrowingLauncher : IPluginProcessLauncher
    {
        private int _launches;

        public int Launches => Volatile.Read(ref _launches);

        public IPluginProcess Launch(
            string executable, IReadOnlyList<string> args, string workingDirectory, Action<string> onStderrLine)
        {
            Interlocked.Increment(ref _launches);
            throw new IOException("backend executable not found");
        }
    }
}
