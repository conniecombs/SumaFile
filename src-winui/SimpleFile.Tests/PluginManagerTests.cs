using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.Core;
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
    private readonly List<IDisposable> _denials = [];

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
        // The deny entries first, or the folder could not be deleted.
        foreach (var denial in _denials)
        {
            denial.Dispose();
        }

        try { Directory.Delete(_temp, true); } catch { }
    }

    private string _pipe = "";

    private PluginManager Manager(
        IPluginProcessLauncher? launcher = null,
        IArchiveEngineRegistrar? archiveEngines = null,
        Action<string, string>? log = null,
        bool pipePerStart = false,
        IReadOnlyList<string>? roots = null,
        string? bundledRoot = null,
        IPluginUiThread? uiThread = null,
        Func<string, IPluginWindowHost>? windowsFor = null,
        Func<object, string?>? validateUiElement = null,
        string? approvalStorePath = null,
        ISettingsBackend? settings = null,
        Func<string, IPluginPanels>? panelsFor = null,
        Action<string>? invalidateDecorations = null)
    {
        var pipe = "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N");
        _pipe = pipe;
        var manager = new PluginManager(new PluginManagerOptions
        {
            Roots = roots ?? [Root],
            ApprovalStorePath = approvalStorePath ?? StorePath,
            DataRoot = Path.Combine(_temp, "data"),
            Launcher = launcher ?? new ThrowingLauncher(),
            // Per start when a test restarts a backend: the fake server of the old generation may still hold its pipe.
            PipeNameFor = _ => pipePerStart ? "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N") : pipe,
            Settings = settings ?? new FakeSettings(),
            Progress = new RecordingSink(),
            Notifier = new RecordingNotifier(),
            DialogsFor = _ => null!,
            Workspace = null!,
            Log = log ?? ((_, _) => { }),
            ArchiveEngines = archiveEngines,
            BundledRoot = bundledRoot,
            UiThread = uiThread,
            WindowsFor = windowsFor,
            ValidateUiElement = validateUiElement,
            PanelsFor = panelsFor,
            InvalidateDecorations = invalidateDecorations,
        });
        // No real waits between registration attempts unless a test gates them.
        manager.ArchiveRetryDelay = (_, _) => Task.CompletedTask;
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
    private string WriteArchivePlugin(string extension = ".bk")
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
                "archiveFormats": [ { "extension": "{{extension}}", "canList": true, "canExtract": true, "canCreate": false, "canModify": false, "engineName": "bk" } ]
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
    public async Task Backend_StartupActivate_DeferredByDiscovery_StartsOnStartStartupBackends()
    {
        // The App's startup: it discovers from the prepared scan without starting backends, renders the plugins and
        // subscribes to Changed, and only then starts them, so no launch competes with that span and no Changed is missed.
        var folder = WriteBackendPlugin("startup");
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var manager = Manager(launcher);
        var prepared = PluginManager.PrepareScan([Root], null, StorePath);

        await manager.DiscoverAsync(prepared, startBackends: false, CancellationToken.None);

        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
        await Task.Delay(200);
        Assert.False(launcher.Launched.Task.IsCompleted, "a deferred startup backend was launched by discovery");
        Assert.False(Record(manager, BackendId).BackendRunning);

        manager.StartStartupBackends();
        // A backend that is starting (or running) is not started again.
        manager.StartStartupBackends();

        var executable = await launcher.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Path.Combine(folder, "backend.exe"), executable);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        manager.StartStartupBackends();
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
    }

    [Fact]
    public async Task StartStartupBackends_LeavesFirstCallBackendsAlone()
    {
        var folder = WriteBackendPlugin("first-call");
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var manager = Manager(launcher);
        await manager.DiscoverAsync(PluginManager.PrepareScan([Root], null, StorePath), startBackends: false, CancellationToken.None);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);

        manager.StartStartupBackends();

        await Task.Delay(200);
        Assert.False(launcher.Launched.Task.IsCompleted, "a first-call backend was started at startup");
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task StartStartupBackends_LeavesPluginsThatAreNotEnabledAlone()
    {
        // A startup plugin the user has not approved yet.
        WriteBackendPlugin("startup");
        var launcher = HandshakingLauncher();
        var manager = Manager(launcher);
        await manager.DiscoverAsync(PluginManager.PrepareScan([Root], null, StorePath), startBackends: false, CancellationToken.None);
        Assert.Equal(PluginState.NeedsConsent, Record(manager, BackendId).State);

        manager.StartStartupBackends();

        await Task.Delay(200);
        Assert.False(launcher.Launched.Task.IsCompleted, "a backend was started for a plugin that is not enabled");
        Assert.Equal(0, launcher.Launches);
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
    public async Task RunCommand_LoadFailureWhileDisabling_LeavesThePluginDisabled()
    {
        // Formerly: the activation failure landed between Disable setting the state and its teardown, and
        // MarkLoadFailedAsync overwrote Disabled with LoadFailed (the runtime was still attached).
        var folder = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, folder);
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        manager.InstanceFactory = _ =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("activation failed on purpose");
        };
        var id = PluginManager.HostCommandId(HelloFixture.Id, "greet");

        // Task.Run: the factory blocks the thread that calls it.
        var command = Task.Run(() => manager.RunCommandAsync(id, Context("greet"), CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "activation never began");
        manager.AfterDisableStateSet = async () =>
        {
            gate.Set();
            await Assert.ThrowsAsync<PluginLoadException>(() => command.WaitAsync(TimeSpan.FromSeconds(5)));
        };

        await manager.DisableAsync(HelloFixture.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var record = Record(manager, HelloFixture.Id);
        Assert.Equal(PluginState.Disabled, record.State);
        Assert.Equal(record.Signature.Error, record.Error);
        Assert.Equal(ApprovalState.Disabled, new PluginApprovalStore(StorePath)
            .Resolve(HelloFixture.Id, PluginPackageHash.ComputeManifestHash(folder), SignatureStatus.Unsigned, folder));
    }

    [Fact]
    public async Task Disable_BackendStartFailsAfterStateSet_LeavesTheRecordClean()
    {
        // Formerly: the Hello fixture's archive format starts its backend at discovery; a launch that failed between
        // Disable setting the state and its teardown wrote "Could not start the backend" over the Disabled record's Error.
        var folder = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, folder);
        using var launcher = new BlockingThrowingLauncher();
        var startFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = Manager(launcher, log: (_, line) =>
        {
            if (line.Contains("Backend start failed", StringComparison.Ordinal)) startFailed.TrySetResult();
        });
        await Task.Run(() => manager.DiscoverAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(launcher.Entered.Wait(TimeSpan.FromSeconds(5)), "startup launch never began");
        manager.AfterDisableStateSet = async () =>
        {
            launcher.Release.Set();
            await startFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        await manager.DisableAsync(HelloFixture.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var record = Record(manager, HelloFixture.Id);
        Assert.Equal(PluginState.Disabled, record.State);
        Assert.Equal(record.Signature.Error, record.Error);
        Assert.False(record.BackendRunning);
    }

    [Fact]
    public async Task Disable_BackendLostAfterStateSet_LeavesTheRecordClean()
    {
        var folder = WriteBackendPlugin("startup");
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = Manager(launcher, log: (_, line) =>
        {
            if (line.Contains("stopped unexpectedly", StringComparison.Ordinal)) lost.TrySetResult();
        });
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        manager.AfterDisableStateSet = async () =>
        {
            launcher.Process!.SimulateExit();
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        await manager.DisableAsync(BackendId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var record = Record(manager, BackendId);
        Assert.Equal(PluginState.Disabled, record.State);
        Assert.Equal(record.Signature.Error, record.Error);
        Assert.False(record.BackendRunning);
    }

    [Fact]
    public async Task Disable_ArchiveRegistrationFaultsAfterStateSet_LeavesTheRecordClean()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var lastAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLastAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrar = new RecordingRegistrar
        {
            Failure = new InvalidOperationException("service refused"),
            AttemptGate = attempt =>
            {
                if (attempt != 3) return Task.CompletedTask;
                lastAttempt.TrySetResult();
                return releaseLastAttempt.Task;
            },
        };
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = Manager(launcher, registrar, (_, line) =>
        {
            if (line.Contains("[error] Archive engine registration failed", StringComparison.Ordinal)) faulted.TrySetResult();
        });
        await manager.DiscoverAsync(CancellationToken.None);
        await lastAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.AfterDisableStateSet = async () =>
        {
            releaseLastAttempt.SetResult();
            await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        await manager.DisableAsync(BackendId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var record = Record(manager, BackendId);
        Assert.Equal(PluginState.Disabled, record.State);
        Assert.Equal(record.Signature.Error, record.Error);
        Assert.False(record.BackendRunning);
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
    public async Task Disable_PluginWhoseDeactivateBlocksSynchronously_FaultsWithinBound()
    {
        var folder = HelloFixture.CopyTo(Root);
        Approve(HelloFixture.Id, folder);
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        using var release = new ManualResetEventSlim(false);
        var plugin = new BlockingDeactivatePlugin(release);
        manager.InstanceFactory = _ => plugin;
        await manager.RunCommandAsync(
            PluginManager.HostCommandId(HelloFixture.Id, "greet"), Context("greet"), CancellationToken.None);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Task.Run: if deactivation blocked the caller, DisableAsync would not even return a task to bound.
            await Task.Run(() => manager.DisableAsync(HelloFixture.Id, CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(8));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6.5), $"took {sw.Elapsed}");
            Assert.True(plugin.DeactivateEntered);
            var record = Record(manager, HelloFixture.Id);
            Assert.Equal(PluginState.Faulted, record.State);
            Assert.Equal("plugin did not deactivate in time", record.Error);
        }
        finally
        {
            release.Set();
        }
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
    public async Task ArchivePlugin_StaleLostCleanupAfterRestart_KeepsReplacementRegistered()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var logs = new List<string>();
        var manager = Manager(launcher, registrar, (_, m) => { lock (logs) logs.Add(m); }, pipePerStart: true);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        var first = launcher.Process!;
        var holdCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.BeforeLostCleanup = () => holdCleanup.Task;

        // Generation 1 crashes; its cleanup is queued but held.
        first.SimulateExit();
        await WaitUntil(() => Record(manager, BackendId).State == PluginState.Faulted);

        // Generation 2 registers before generation 1's cleanup gets to run.
        await manager.RestartBackendAsync(BackendId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        Assert.Equal(2, registrar.Registered.Count);
        Assert.NotSame(first, launcher.Process);

        holdCleanup.SetResult();
        // The cleanup disposes generation 1's connection last, which kills its process.
        await WaitUntil(() => first.Killed);

        Assert.Empty(registrar.Unregistered);
        Assert.True(Record(manager, BackendId).BackendRunning);
        lock (logs) Assert.Contains(logs, m => m.Contains("newer generation", StringComparison.Ordinal));

        await manager.DisableAsync(BackendId, CancellationToken.None);

        Assert.Equal([BackendId], registrar.Unregistered);
    }

    [Fact]
    public async Task ArchivePlugin_UnregisterInFlightDuringRestart_ReplacementRegistersAfterIt()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var releaseUnregister = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrar = new RecordingRegistrar { UnregisterGate = releaseUnregister.Task };
        var manager = Manager(launcher, registrar, pipePerStart: true);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        var first = launcher.Process!;
        var firstRegister = $"register:{Assert.Single(registrar.Registered).PipeName}";

        // Generation 1 crashes; its unregister reaches the registrar and blocks there.
        first.SimulateExit();
        await registrar.UnregisterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Generation 2 starts and handshakes while that unregister is still in flight.
        var restart = manager.RestartBackendAsync(BackendId, CancellationToken.None);
        await WaitUntil(() => !ReferenceEquals(launcher.Process, first));
        await Task.Delay(300);
        Assert.Equal([firstRegister], registrar.Calls);

        releaseUnregister.SetResult();
        await restart.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);

        // The service saw unregister-1 complete before register-2, so generation 2 stays registered.
        var calls = registrar.Calls;
        Assert.Equal(3, calls.Count);
        Assert.Equal(firstRegister, calls[0]);
        Assert.Equal("unregister", calls[1]);
        Assert.StartsWith("register:", calls[2], StringComparison.Ordinal);
        Assert.NotEqual(firstRegister, calls[2]);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
    }

    [Fact]
    public async Task ArchivePlugin_TransientRegistrationFailure_RetriesAndRegisters()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar { FailuresBeforeSuccess = 1 };
        var logs = new List<string>();
        var manager = Manager(launcher, registrar, (_, m) => { lock (logs) logs.Add(m); });

        await manager.DiscoverAsync(CancellationToken.None);

        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        Assert.Equal(2, registrar.RegisterCalls);
        Assert.Single(registrar.Registered);
        Assert.Empty(registrar.Unregistered);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
        Assert.False(launcher.Process!.Killed);
        lock (logs) Assert.Contains("[warn] Archive engine registration attempt 1 of 3 failed: attempt 1 refused", logs);
    }

    [Fact]
    public async Task ArchivePlugin_RegistrationFailsEveryAttempt_FaultsAfterThreeAttemptsAndUnregisters()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar { Failure = new InvalidOperationException("service refused") };
        var logs = new List<string>();
        var manager = Manager(launcher, registrar, (_, m) => { lock (logs) logs.Add(m); });

        await manager.DiscoverAsync(CancellationToken.None);

        await WaitUntil(() => Record(manager, BackendId).State == PluginState.Faulted);
        var record = Record(manager, BackendId);
        Assert.Equal("Archive engine registration failed: service refused", record.Error);
        Assert.False(record.BackendRunning);
        await WaitUntil(() => launcher.Process!.Killed);
        await WaitUntil(() => registrar.Unregistered.Count == 1);
        Assert.Equal(3, registrar.RegisterCalls);
        Assert.Empty(registrar.Registered);
        lock (logs)
        {
            Assert.Contains("[warn] Archive engine registration attempt 3 of 3 failed: service refused", logs);
            Assert.Contains("[error] Archive engine registration failed: service refused", logs);
        }

        // Teardown does not unregister a second time.
        await manager.DisposeAsync();
        Assert.Single(registrar.Unregistered);
    }

    [Fact]
    public async Task ArchivePlugin_RegistrationThatNeverCompletes_TimesOutPerAttempt()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar { NeverComplete = true };
        var manager = Manager(launcher, registrar);
        manager.ArchiveRegisterTimeout = TimeSpan.FromMilliseconds(200);

        await manager.DiscoverAsync(CancellationToken.None);

        await WaitUntil(() => Record(manager, BackendId).State == PluginState.Faulted);
        Assert.Equal(3, registrar.RegisterCalls);
        Assert.StartsWith("Archive engine registration failed:", Record(manager, BackendId).Error, StringComparison.Ordinal);
        await WaitUntil(() => launcher.Process!.Killed);
        await WaitUntil(() => registrar.Unregistered.Count == 1);
    }

    [Fact]
    public async Task ArchivePlugin_LostDuringRetryDelay_StopsRetrying()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar { FailuresBeforeSuccess = 1 };
        var manager = Manager(launcher, registrar);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ArchiveRetryDelay = async (_, _) =>
        {
            delayEntered.TrySetResult();
            await releaseDelay.Task;
        };

        await manager.DiscoverAsync(CancellationToken.None);
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        launcher.Process!.SimulateExit();
        await WaitUntil(() => Record(manager, BackendId).State == PluginState.Faulted);
        releaseDelay.SetResult();
        await Task.Delay(300);

        Assert.Equal(1, registrar.RegisterCalls);
        Assert.Empty(registrar.Registered);
        Assert.Empty(registrar.Unregistered);
        Assert.DoesNotContain("registration failed", Record(manager, BackendId).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivePlugin_DisposeDuringRetryDelay_StopsRetrying()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar { FailuresBeforeSuccess = 1 };
        var manager = Manager(launcher, registrar);
        var delayToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ArchiveRetryDelay = (_, ct) =>
        {
            delayToken.TrySetResult(ct);
            return Task.Delay(Timeout.Infinite, ct);
        };

        await manager.DiscoverAsync(CancellationToken.None);
        var token = await delayToken.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await manager.DisposeAsync();
        sw.Stop();

        Assert.True(token.IsCancellationRequested);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        await Task.Delay(200);
        Assert.Equal(1, registrar.RegisterCalls);
        Assert.Empty(registrar.Registered);
        Assert.True(launcher.Process!.Killed);
    }

    [Fact]
    public async Task ArchivePlugin_SuccessfulRegistration_LogsTheRegisteredFormats()
    {
        var folder = WriteArchivePlugin(".hello");
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var logs = new List<string>();
        var manager = Manager(launcher, registrar, (_, m) => { lock (logs) logs.Add(m); });

        await manager.DiscoverAsync(CancellationToken.None);

        // The log line itself, not BackendRunning: the line is written after the registration that sets the flag, so
        // waiting for the flag raced the write (carry-over g).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            lock (logs)
            {
                if (logs.Contains("[info] Archive engines registered: .hello")) break;
            }

            Assert.True(DateTime.UtcNow < deadline, "the registered formats were not logged within 10 s");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ArchivePlugin_ReregisterAfterServiceReconnect_RegistersLiveEnginesAgain()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrar = new RecordingRegistrar
        {
            AttemptGate = n =>
            {
                if (n != 2) return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            },
        };
        var manager = Manager(launcher, registrar);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);

        var reregister = manager.ReregisterArchiveEnginesAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Record(manager, BackendId).BackendRunning);
        release.SetResult();
        await reregister.WaitAsync(TimeSpan.FromSeconds(5));

        var registered = registrar.Registered;
        Assert.Equal(2, registered.Count);
        Assert.All(registered, r => Assert.Equal(_pipe, r.PipeName));
        Assert.True(Record(manager, BackendId).BackendRunning);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
        Assert.Empty(registrar.Unregistered);
        Assert.False(launcher.Process!.Killed);
    }

    [Fact]
    public async Task ArchivePlugin_ReregisterSkipsDisabledAndFaultedPlugins()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var manager = Manager(launcher, registrar, pipePerStart: true);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);

        // Faulted: the backend was lost.
        launcher.Process!.SimulateExit();
        await WaitUntil(() => registrar.Unregistered.Count == 1);
        await manager.ReregisterArchiveEnginesAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, registrar.RegisterCalls);

        // Disabled.
        await manager.RestartBackendAsync(BackendId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        Assert.Equal(2, registrar.RegisterCalls);
        await manager.DisableAsync(BackendId, CancellationToken.None);
        await manager.ReregisterArchiveEnginesAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, registrar.RegisterCalls);
    }

    [Fact]
    public async Task ArchivePlugin_ReregisterFailingEveryAttempt_FaultsThePlugin()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var manager = Manager(launcher, registrar);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);

        registrar.Failure = new InvalidOperationException("new service refused");
        await manager.ReregisterArchiveEnginesAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var record = Record(manager, BackendId);
        Assert.Equal(PluginState.Faulted, record.State);
        Assert.Equal("Archive engine registration failed: new service refused", record.Error);
        Assert.False(record.BackendRunning);
        Assert.Equal(4, registrar.RegisterCalls);
        await WaitUntil(() => launcher.Process!.Killed);
        Assert.Single(registrar.Unregistered);

        await manager.DisposeAsync();
        Assert.Single(registrar.Unregistered);
    }

    [Fact]
    public async Task ArchivePlugin_ReregisterAfterServiceReconnect_RaisesChangedWhenDone()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrar = new RecordingRegistrar
        {
            AttemptGate = n =>
            {
                if (n != 2) return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            },
        };
        var manager = Manager(launcher, registrar);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        await Task.Delay(100);
        manager.Changed += (_, _) => registrar.Note("changed");

        var reregister = manager.ReregisterArchiveEnginesAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.DoesNotContain("changed", registrar.Calls);

        release.SetResult();
        await reregister.WaitAsync(TimeSpan.FromSeconds(5));

        var calls = registrar.Calls;
        Assert.Equal([$"register:{_pipe}", $"register:{_pipe}", "changed"], calls);
    }

    [Fact]
    public async Task ArchivePlugin_FinalUnregisterAfterFailedAttempts_SkipsWhenANewerGenerationRegistered()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrar = new RecordingRegistrar
        {
            FailuresBeforeSuccess = 3,
            AttemptGate = n =>
            {
                if (n != 3) return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            },
        };
        var logs = new List<string>();
        var manager = Manager(launcher, registrar, (_, m) => { lock (logs) logs.Add(m); }, pipePerStart: true);
        await manager.DiscoverAsync(CancellationToken.None);

        // Generation 1's third attempt is in flight, holding the plugin's registrar lock.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = launcher.Process!;

        // Generation 2 starts, handshakes and queues for the lock behind it.
        var restart = manager.RestartBackendAsync(BackendId, CancellationToken.None);
        await WaitUntil(() => !ReferenceEquals(launcher.Process, first));
        await Task.Delay(500);

        // The third attempt fails: generation 2 registers first, so generation 1's final unregister is skipped.
        release.SetResult();
        await restart.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        await WaitUntil(() => { lock (logs) return logs.Any(m => m.Contains("Skipped unregistering", StringComparison.Ordinal)); });

        Assert.Empty(registrar.Unregistered);
        var registration = Assert.Single(registrar.Registered);
        Assert.Equal(launcher.PipeName, registration.PipeName);
        Assert.Equal(4, registrar.RegisterCalls);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
        Assert.True(first.Killed);
    }

    [Fact]
    public async Task ArchivePlugin_Lost_RaisesChangedAfterTheUnregisterCompletes()
    {
        var folder = WriteArchivePlugin();
        Approve(BackendId, folder);
        var launcher = HandshakingLauncher();
        var registrar = new RecordingRegistrar();
        var manager = Manager(launcher, registrar);
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        manager.Changed += (_, _) => registrar.Note("changed");
        // The queued unregister runs well after the Changed that OnBackendLost raises straight away.
        manager.BeforeLostCleanup = () => Task.Delay(300);

        launcher.Process!.SimulateExit();

        await WaitUntil(() => registrar.Unregistered.Count == 1);
        await WaitUntil(() =>
        {
            var calls = registrar.Calls.ToList();
            var unregister = calls.IndexOf("unregister");
            return unregister >= 0 && calls.Skip(unregister + 1).Contains("changed");
        });
        var ordered = registrar.Calls.ToList();
        Assert.Contains("changed", ordered.Take(ordered.IndexOf("unregister")));
        Assert.Equal(PluginState.Faulted, Record(manager, BackendId).State);
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

    // ---- UI slots: sidebar sections, settings pages, plugin windows ----

    private const string UiId = "com.example.ui";

    /// <summary>
    /// A plugin with a UI assembly (never loaded: tests replace <see cref="PluginManager.InstanceFactory"/>), one
    /// command, one sidebar section, one bottom panel, a row decoration provider and, unless told otherwise, a settings
    /// page. With a backend its id must be <see cref="BackendId"/>, which the handshaking launcher answers for. With
    /// <paramref name="legacySetting"/> it declares the boolean setting <c>enabled</c>, migrated from
    /// <see cref="LegacyKey"/>.
    /// </summary>
    private string WriteUiPlugin(
        string id = UiId, string? backendActivateOn = null, bool settingsPage = true, bool legacySetting = false)
    {
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);
        var backend = backendActivateOn is null
            ? ""
            : $$""" "backend": { "executable": "backend.exe", "args": [], "activateOn": "{{backendActivateOn}}", "acceptsMultipleClients": false }, """;
        var page = settingsPage ? """, "settingsPage": { "provider": "page" }""" : "";
        var settings = legacySetting ? SettingsJson : "";
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "Ui",
              "version": "1.0.0",
              "publisher": { "name": "P" },
              "sdkVersion": "1.0.0",
              "ui": { "assembly": "Ui.dll", "entryType": "Ui.Plugin" },
              {{backend}}
              {{settings}}
              "contributions": {
                "commands": [ { "id": "open", "label": "Open", "group": "G" } ],
                "sidebar": [ { "id": "main", "title": "Main", "provider": "main-section", "command": "open" } ],
                "panels": [ { "id": "bottom", "title": "Bottom", "provider": "bottom-panel", "detachable": true } ],
                "rowDecorations": { "provider": "badges" }{{page}}
              }
            }
            """);
        return folder;
    }

    private static PluginCommandContext OpenContext(string pluginId) =>
        new(PluginManager.HostCommandId(pluginId, "open"), null, []);

    private static void Note(List<string> order, string entry)
    {
        lock (order) order.Add(entry);
    }

    private static List<string> Snapshot(List<string> order)
    {
        lock (order) return order.ToList();
    }

    [Fact]
    public async Task CreateSidebarSection_ActivatesOnceAndRunsTheProviderOnTheUiThread()
    {
        Approve(UiId, WriteUiPlugin());
        using var ui = new SingleThreadUiThread();
        var manager = Manager(uiThread: ui);
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin();
        var created = 0;
        manager.InstanceFactory = _ =>
        {
            Interlocked.Increment(ref created);
            return plugin;
        };

        // Called from the thread pool, as a continuation after ConfigureAwait(false) would be.
        var first = await Task.Run(() => manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None));
        var second = await Task.Run(() => manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None));

        Assert.Equal(1, created);
        Assert.Equal(1, plugin.Activations);
        Assert.NotSame(first, second);
        Assert.Equal([new PluginSidebarSectionRequest("main", "main-section"), new PluginSidebarSectionRequest("main", "main-section")], plugin.SectionRequests);
        Assert.Equal([ui.ThreadId], plugin.ActivationThreads);
        Assert.Equal([ui.ThreadId, ui.ThreadId], plugin.ProviderThreads);
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);

        var page = await Task.Run(() => manager.CreateSettingsPageAsync(UiId, CancellationToken.None));
        Assert.IsType<UiElementStub>(page);
        Assert.Equal([new PluginSettingsPageRequest("page")], plugin.PageRequests);
        Assert.Equal([ui.ThreadId, ui.ThreadId, ui.ThreadId], plugin.ProviderThreads);
        Assert.Equal(1, plugin.Activations);
    }

    [Fact]
    public async Task CreateSidebarSection_ProviderThrows_MarksLoadFailedAndWithdrawsContributions()
    {
        Approve(UiId, WriteUiPlugin());
        var order = new List<string>();
        var windows = new RecordingWindowHost(order);
        var log = new List<string>();
        var manager = Manager(windowsFor: _ => windows, log: (_, line) => Note(log, line));
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin(order) { Section = () => throw new InvalidOperationException("boom") };
        manager.InstanceFactory = _ => plugin;
        var epoch = Record(manager, UiId).Epoch;
        var changed = 0;
        manager.Changed += (_, _) => Interlocked.Increment(ref changed);

        var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
            manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None));

        const string expected = "Plugin 'com.example.ui' failed to create sidebar section 'main': boom";
        Assert.Equal(expected, error.Message);
        var record = Record(manager, UiId);
        Assert.Equal(PluginState.LoadFailed, record.State);
        Assert.Equal(expected, record.Error);
        Assert.True(record.Epoch > epoch);
        Assert.Empty(manager.EnabledPlugins);
        Assert.Empty(SimpleFile.PluginHost.Contributions.PluginContributionAdapter.SidebarSections(manager.EnabledPlugins));
        Assert.True(changed > 0);
        Assert.Contains($"[error] {expected}", Snapshot(log));
        // The teardown runs after the exception is handed back, in teardown order.
        await WaitUntil(() => Snapshot(order).Count == 2);
        Assert.Equal(["windows-closed", "deactivated"], Snapshot(order));
    }

    // ---- load-failure teardown (carry-overs #63, #65, #69) ----

    /// <summary>
    /// A plugin whose sidebar provider throws, behind a window host whose close waits for the returned gate. The
    /// creation is started, not awaited; the helper returns once the plugin is LoadFailed, so its teardown is pending.
    /// </summary>
    private async Task<(PluginManager Manager, TaskCompletionSource Gate, List<string> Order, Task<object> Create)> LoadFailedBehindAGate(
        string id = UiId,
        string? backendActivateOn = null,
        FakePluginProcessLauncher? launcher = null,
        Action<string, string>? log = null)
    {
        Approve(id, WriteUiPlugin(id, backendActivateOn));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var manager = Manager(
            launcher,
            log: log,
            pipePerStart: true,
            windowsFor: _ => new RecordingWindowHost(order, () => gate.Task));
        await manager.DiscoverAsync(CancellationToken.None);
        if (backendActivateOn is not null)
        {
            await WaitUntil(() => Record(manager, id).BackendRunning);
        }

        manager.InstanceFactory = _ => new UiPlugin(order) { Section = () => throw new InvalidOperationException("boom") };
        var create = manager.CreateSidebarSectionAsync(id, "main", CancellationToken.None);
        await WaitUntil(() => Record(manager, id).State == PluginState.LoadFailed);
        return (manager, gate, order, create);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan within, string what)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"{what} within {within.TotalSeconds:0.#} s");
            await Task.Delay(20);
        }
    }

    private static async Task Settle(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
    }

    [Fact]
    public async Task LoadFailure_RaisesChangedBeforeTheTeardownFinishes()
    {
        Approve(UiId, WriteUiPlugin());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var manager = Manager(windowsFor: _ => new RecordingWindowHost(order, () => gate.Task));
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new UiPlugin(order) { Section = () => throw new InvalidOperationException("boom") };
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += (_, _) => changed.TrySetResult();

        var create = manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None);
        try
        {
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(manager.EnabledPlugins);
            Assert.Equal(PluginState.LoadFailed, Record(manager, UiId).State);
            Assert.False(gate.Task.IsCompleted);
            Assert.DoesNotContain("deactivated", Snapshot(order));
        }
        finally
        {
            gate.TrySetResult();
        }

        await Assert.ThrowsAsync<PluginLoadException>(() => create);
        await WaitUntil(() => Snapshot(order).Contains("deactivated"));
    }

    [Fact]
    public async Task CreateSidebarSection_ProviderThrows_ThrowsBeforeTheTeardownFinishes()
    {
        Approve(UiId, WriteUiPlugin());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var manager = Manager(windowsFor: _ => new RecordingWindowHost(order, () => gate.Task));
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new UiPlugin(order) { Section = () => throw new InvalidOperationException("boom") };

        try
        {
            var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
                manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("Plugin 'com.example.ui' failed to create sidebar section 'main': boom", error.Message);
            Assert.False(gate.Task.IsCompleted);
            Assert.DoesNotContain("deactivated", Snapshot(order));
        }
        finally
        {
            gate.TrySetResult();
        }

        await WaitUntil(() => Snapshot(order).Contains("deactivated"));
    }

    [Fact]
    public async Task Dispose_WaitsForALoadFailureTeardown()
    {
        var (manager, gate, order, create) = await LoadFailedBehindAGate();
        await Settle(create);

        var dispose = manager.DisposeAsync().AsTask();
        try
        {
            await Task.Delay(300);
            Assert.False(dispose.IsCompleted, "DisposeAsync returned while the load-failure teardown still ran");
        }
        finally
        {
            gate.TrySetResult();
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("deactivated", Snapshot(order));
    }

    [Fact]
    public async Task Rescan_AfterALoadFailure_WaitsForItsTeardown()
    {
        var launcher = HandshakingLauncher();
        var (manager, gate, _, create) = await LoadFailedBehindAGate(BackendId, "startup", launcher);
        Assert.Equal(1, launcher.Launches);

        var rescan = manager.RescanAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            Assert.Equal(1, launcher.Launches);
            Assert.False(rescan.IsCompleted, "RescanAsync returned while the load-failure teardown still ran");
        }
        finally
        {
            gate.TrySetResult();
        }

        await rescan.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntil(() => launcher.Launches >= 2, TimeSpan.FromSeconds(2), "the backend was not started again");
        await Task.Delay(200);
        Assert.Equal(2, launcher.Launches);
        await Settle(create);
    }

    [Fact]
    public async Task Disable_AfterALoadFailure_ReturnsOnlyAfterItsTeardown()
    {
        var launcher = HandshakingLauncher();
        var (manager, gate, _, create) = await LoadFailedBehindAGate(BackendId, "startup", launcher);

        var disable = manager.DisableAsync(BackendId, CancellationToken.None);
        try
        {
            await Task.Delay(300);
            Assert.False(disable.IsCompleted, "DisableAsync returned while the load-failure teardown still ran");
        }
        finally
        {
            gate.TrySetResult();
        }

        await disable.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PluginState.Disabled, Record(manager, BackendId).State);
        Assert.Equal(1, launcher.Launches);
        await Settle(create);
    }

    [Fact]
    public async Task Enable_AfterALoadFailure_WaitsForItsTeardown()
    {
        var launcher = HandshakingLauncher();
        var (manager, gate, _, create) = await LoadFailedBehindAGate(BackendId, "startup", launcher);

        var disable = manager.DisableAsync(BackendId, CancellationToken.None);
        var enable = manager.EnableAsync(BackendId, CancellationToken.None);
        try
        {
            await Task.Delay(300);
            Assert.Equal(1, launcher.Launches);
            Assert.False(enable.IsCompleted, "EnableAsync returned while the load-failure teardown still ran");
        }
        finally
        {
            gate.TrySetResult();
        }

        await Task.WhenAll(disable, enable).WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntil(() => launcher.Launches >= 2, TimeSpan.FromSeconds(2), "the backend was not started again");
        await Task.Delay(200);
        Assert.Equal(2, launcher.Launches);
        Assert.Equal(PluginState.Enabled, Record(manager, BackendId).State);
        await Settle(create);
    }

    [Fact]
    public async Task LoadFailureTeardownWait_IsBoundedAndLogged()
    {
        var log = new List<string>();
        var (manager, gate, order, create) = await LoadFailedBehindAGate(log: (_, line) => Note(log, line));
        manager.LoadFailureTeardownWait = TimeSpan.FromMilliseconds(200);
        try
        {
            await manager.RescanAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(
                "[warn] Plugin 'com.example.ui' is still tearing down after its load failure; continuing.",
                Snapshot(log));
        }
        finally
        {
            gate.TrySetResult();
        }

        await WaitUntil(() => Snapshot(order).Contains("deactivated"));
        await Settle(create);
    }

    [Fact]
    public async Task CreateSidebarSection_EntryWithoutTheProvider_MarksLoadFailed()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new CommandOnlyPlugin();

        var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
            manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None));

        const string expected = "Plugin 'com.example.ui' declares a sidebar section but its entry type does not implement ISidebarSectionProvider.";
        Assert.Equal(expected, error.Message);
        Assert.Equal(PluginState.LoadFailed, Record(manager, UiId).State);
        Assert.Equal(expected, Record(manager, UiId).Error);
    }

    [Fact]
    public async Task CreateSettingsPage_EntryWithoutTheProvider_MarksLoadFailed()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new CommandOnlyPlugin();

        var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
            manager.CreateSettingsPageAsync(UiId, CancellationToken.None));

        Assert.Equal(
            "Plugin 'com.example.ui' declares a settings page but its entry type does not implement ISettingsPageProvider.",
            error.Message);
        Assert.Equal(PluginState.LoadFailed, Record(manager, UiId).State);
    }

    [Fact]
    public async Task CreateSidebarSection_RejectedElement_MarksLoadFailed()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager(validateUiElement: o => o is string ? "returned System.String" : null);
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new UiPlugin { Section = () => "not an element" };

        var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
            manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None));

        Assert.Equal("Plugin 'com.example.ui' sidebar section 'main' returned System.String", error.Message);
        Assert.Equal(PluginState.LoadFailed, Record(manager, UiId).State);
        Assert.Equal(error.Message, Record(manager, UiId).Error);
    }

    [Fact]
    public async Task CreateSettingsPage_RejectedOrMissingElement_MarksLoadFailed()
    {
        Approve(UiId, WriteUiPlugin());
        var rejecting = Manager(validateUiElement: o => o is string ? "returned System.String" : null);
        await rejecting.DiscoverAsync(CancellationToken.None);
        rejecting.InstanceFactory = _ => new UiPlugin { Page = () => "not an element" };

        var rejected = await Assert.ThrowsAsync<PluginLoadException>(() =>
            rejecting.CreateSettingsPageAsync(UiId, CancellationToken.None));
        Assert.Equal("Plugin 'com.example.ui' settings page returned System.String", rejected.Message);

        var empty = Manager();
        await empty.DiscoverAsync(CancellationToken.None);
        empty.InstanceFactory = _ => new UiPlugin { Page = () => null, Section = () => null };
        var missing = await Assert.ThrowsAsync<PluginLoadException>(() =>
            empty.CreateSettingsPageAsync(UiId, CancellationToken.None));
        Assert.Equal("Plugin 'com.example.ui' returned no element for its settings page.", missing.Message);
        Assert.Equal(PluginState.LoadFailed, Record(empty, UiId).State);

        var throwing = Manager();
        await throwing.DiscoverAsync(CancellationToken.None);
        throwing.InstanceFactory = _ => new UiPlugin { Page = () => throw new InvalidOperationException("page boom") };
        var thrown = await Assert.ThrowsAsync<PluginLoadException>(() =>
            throwing.CreateSettingsPageAsync(UiId, CancellationToken.None));
        Assert.Equal("Plugin 'com.example.ui' failed to create its settings page: page boom", thrown.Message);
    }

    [Fact]
    public async Task CreateSidebarSection_ProviderReturnsNull_MarksLoadFailed()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new UiPlugin { Section = () => null };

        var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
            manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None));

        Assert.Equal("Plugin 'com.example.ui' returned no element for sidebar section 'main'.", error.Message);
        Assert.Equal(PluginState.LoadFailed, Record(manager, UiId).State);
    }

    [Fact]
    public async Task CreateSidebarSection_UndeclaredSection_ThrowsAndChangesNothing()
    {
        Approve(UiId, WriteUiPlugin(settingsPage: false));
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var created = 0;
        manager.InstanceFactory = _ =>
        {
            Interlocked.Increment(ref created);
            return new UiPlugin();
        };
        var record = Record(manager, UiId);
        var epoch = record.Epoch;
        var changed = 0;
        manager.Changed += (_, _) => Interlocked.Increment(ref changed);

        var section = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateSidebarSectionAsync(UiId, "nope", CancellationToken.None));
        var page = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateSettingsPageAsync(UiId, CancellationToken.None));

        Assert.Equal("Plugin 'com.example.ui' has no sidebar section 'nope'.", section.Message);
        Assert.Equal("Plugin 'com.example.ui' has no settings page.", page.Message);
        Assert.Equal(0, created);
        Assert.Equal(0, changed);
        Assert.Same(record, Record(manager, UiId));
        Assert.Equal(PluginState.Enabled, record.State);
        Assert.Null(record.Error);
        Assert.Equal(epoch, record.Epoch);
        Assert.Null(record.Runtime);
    }

    [Fact]
    public async Task CreateSettingsPage_ForANeedsConsentPlugin_LoadsNoCode()
    {
        WriteUiPlugin();
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var created = 0;
        manager.InstanceFactory = _ =>
        {
            Interlocked.Increment(ref created);
            return new UiPlugin();
        };
        Assert.Equal(PluginState.NeedsConsent, Record(manager, UiId).State);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateSettingsPageAsync(UiId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None));

        Assert.Equal(0, created);
        Assert.Equal(PluginState.NeedsConsent, Record(manager, UiId).State);
        Assert.Null(Record(manager, UiId).Runtime);
    }

    [Fact]
    public async Task RunCommand_ExecutesThroughTheUiThread()
    {
        Approve(UiId, WriteUiPlugin());
        using var ui = new SingleThreadUiThread();
        var manager = Manager(uiThread: ui);
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin();
        manager.InstanceFactory = _ => plugin;

        await Task.Run(() => manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None));

        Assert.Equal([ui.ThreadId], plugin.ActivationThreads);
        Assert.Equal([ui.ThreadId], plugin.ExecuteThreads);
        Assert.Equal(2, ui.Calls);
    }

    [Fact]
    public async Task Disable_ClosesPluginWindowsBeforeTheBackendStops()
    {
        var (manager, launcher, order) = await StartedUiBackendPlugin();

        await manager.DisableAsync(BackendId, CancellationToken.None);

        Assert.Equal(["windows-closed", "backend-disposed", "deactivated"], Snapshot(order));
        Assert.True(launcher.Process!.Killed);
        Assert.Equal(PluginState.Disabled, Record(manager, BackendId).State);
    }

    [Fact]
    public async Task Dispose_ClosesPluginWindowsBeforeTheBackendStops()
    {
        var (manager, launcher, order) = await StartedUiBackendPlugin();

        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["windows-closed", "backend-disposed", "deactivated"], Snapshot(order));
        Assert.True(launcher.Process!.Killed);
    }

    [Fact]
    public async Task Teardown_WindowsThatNeverClose_AreAbandonedAfterTheTimeout()
    {
        var log = new List<string>();
        var (manager, launcher, order) = await StartedUiBackendPlugin(
            closeAll: () => new TaskCompletionSource().Task, log: (_, line) => Note(log, line));
        manager.WindowCloseTimeout = TimeSpan.FromMilliseconds(100);

        await manager.DisableAsync(BackendId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(launcher.Process!.Killed);
        Assert.Equal(["windows-closing", "backend-disposed", "deactivated"], Snapshot(order));
        Assert.Contains("[warn] Plugin windows did not close within 0.1 s; stopping the plugin anyway.", Snapshot(log));
        Assert.Equal(PluginState.Disabled, Record(manager, BackendId).State);
    }

    [Fact]
    public async Task Teardown_WindowsThatFailToClose_StopThePluginAnyway()
    {
        var log = new List<string>();
        var (manager, launcher, order) = await StartedUiBackendPlugin(
            closeAll: () => throw new InvalidOperationException("dispatcher gone"), log: (_, line) => Note(log, line));

        await manager.DisableAsync(BackendId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(launcher.Process!.Killed);
        Assert.Equal(["windows-closing", "backend-disposed", "deactivated"], Snapshot(order));
        Assert.Contains(Snapshot(log), line =>
            line.StartsWith("[warn] Plugin windows failed to close", StringComparison.Ordinal)
            && line.Contains("dispatcher gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rescan_DroppedPlugin_ClosesItsWindows()
    {
        var folder = WriteUiPlugin();
        Approve(UiId, folder);
        var order = new List<string>();
        var hosts = 0;
        var manager = Manager(windowsFor: _ =>
        {
            Interlocked.Increment(ref hosts);
            return new RecordingWindowHost(order);
        });
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin(order);
        manager.InstanceFactory = _ => plugin;
        await manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None);
        await manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None);

        Directory.Delete(folder, true);
        await manager.RescanAsync(CancellationToken.None);

        Assert.DoesNotContain(manager.Plugins, p => p.Id == UiId);
        Assert.Equal(1, hosts);
        Assert.Equal(["windows-closed", "deactivated"], Snapshot(order));
    }

    [Fact]
    public async Task Windows_OneHostPerRuntime_IsTheOnePluginsSee()
    {
        Approve(UiId, WriteUiPlugin());
        var order = new List<string>();
        var created = new List<RecordingWindowHost>();
        var manager = Manager(windowsFor: id =>
        {
            Assert.Equal(UiId, id);
            var host = new RecordingWindowHost(order);
            lock (created) created.Add(host);
            return host;
        });
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin(order);
        manager.InstanceFactory = _ => plugin;

        await manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None);
        await manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None);
        plugin.Host!.Windows.Attach("window", new PluginWindowOptions("Title", 480, 320));

        var host = Assert.Single(created);
        Assert.Same(host, plugin.Host.Windows);
        Assert.Equal([("window", new PluginWindowOptions("Title", 480, 320))], host.Attached);
    }

    [Fact]
    public async Task Teardown_SealsTheRuntimesWindowHost_AndTheNextRuntimeGetsAFreshOne()
    {
        Approve(UiId, WriteUiPlugin());
        var order = new List<string>();
        var created = new List<RecordingWindowHost>();
        var manager = Manager(windowsFor: _ =>
        {
            var host = new RecordingWindowHost(order);
            lock (created) created.Add(host);
            return host;
        });
        await manager.DiscoverAsync(CancellationToken.None);
        var instances = new List<UiPlugin>();
        manager.InstanceFactory = _ =>
        {
            var plugin = new UiPlugin(order);
            lock (instances) instances.Add(plugin);
            return plugin;
        };

        await manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None);
        var first = Assert.Single(instances);
        first.Host!.Windows.Attach("before", new PluginWindowOptions("Before", 480, 320));
        var firstHost = Assert.Single(created);
        Assert.Equal(["before"], firstHost.Open);

        await manager.DisableAsync(UiId, CancellationToken.None);
        Assert.Equal(1, firstHost.CloseCalls);
        Assert.Empty(firstHost.Open);

        // The stopped instance still holds its IPluginHost (say, reporting a backend that died): its windows reach
        // only the sealed host, which closes a late window at once.
        first.Host.Windows.Attach("late", new PluginWindowOptions("Late", 480, 320));
        Assert.Same(firstHost, first.Host.Windows);
        Assert.Equal(["late"], firstHost.ClosedOnAttach);
        Assert.Empty(firstHost.Open);

        await manager.EnableAsync(UiId, CancellationToken.None);
        await manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None);
        Assert.Equal(2, instances.Count);
        var second = instances[1];
        Assert.Equal(2, created.Count);
        var secondHost = created[1];
        Assert.NotSame(firstHost, secondHost);
        Assert.Same(secondHost, second.Host!.Windows);

        // A sealed host is never handed to a new runtime, so the re-enabled plugin's windows stay open.
        second.Host.Windows.Attach("again", new PluginWindowOptions("Again", 480, 320));
        Assert.Equal(["again"], secondHost.Open);
        Assert.Empty(secondHost.ClosedOnAttach);
        Assert.Equal(0, secondHost.CloseCalls);
        Assert.Equal(1, firstHost.CloseCalls);
    }

    [Fact]
    public async Task Disable_BumpsTheRecordEpoch()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new UiPlugin();
        var record = Record(manager, UiId);
        var initial = record.Epoch;
        await manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None);
        Assert.Equal(initial, record.Epoch);

        await manager.DisableAsync(UiId, CancellationToken.None);
        var disabled = record.Epoch;
        Assert.True(disabled > initial, $"epoch {initial} -> {disabled}");

        await manager.EnableAsync(UiId, CancellationToken.None);
        await manager.CreateSidebarSectionAsync(UiId, "main", CancellationToken.None);
        await manager.DisableAsync(UiId, CancellationToken.None);
        Assert.True(record.Epoch > disabled, $"epoch {disabled} -> {record.Epoch}");
    }

    private async Task<(PluginManager Manager, FakePluginProcessLauncher Launcher, List<string> Order)> StartedUiBackendPlugin(
        Func<Task>? closeAll = null, Action<string, string>? log = null)
    {
        Approve(BackendId, WriteUiPlugin(BackendId, backendActivateOn: "startup"));
        var launcher = HandshakingLauncher();
        var order = new List<string>();
        var manager = Manager(launcher, log: log, windowsFor: _ => new RecordingWindowHost(order, closeAll));
        await manager.DiscoverAsync(CancellationToken.None);
        await WaitUntil(() => Record(manager, BackendId).BackendRunning);
        launcher.Process!.OnKill = () => Note(order, "backend-disposed");
        manager.InstanceFactory = _ => new UiPlugin(order);
        await manager.RunCommandAsync(OpenContext(BackendId).CommandId, OpenContext(BackendId), CancellationToken.None);
        Assert.Empty(Snapshot(order));
        return (manager, launcher, order);
    }

    /// <summary>
    /// Records what the manager asks of a plugin's windows; <c>closeAll</c> replaces the close. It keeps the
    /// <see cref="IPluginWindowHost"/> seal through <see cref="PluginWindowSet{TWindow}"/>, as the App's host does: a
    /// window attached after <see cref="CloseAllAsync"/> is closed at once (<see cref="ClosedOnAttach"/>).
    /// </summary>
    private sealed class RecordingWindowHost(List<string> order, Func<Task>? closeAll = null) : IPluginWindowHost
    {
        private readonly PluginWindowSet<object> _windows = new();
        private int _closeCalls;

        public List<(object Window, PluginWindowOptions Options)> Attached { get; } = [];
        public List<object> ClosedOnAttach { get; } = [];
        public IReadOnlyList<object> Open => _windows.Open;
        public int CloseCalls => Volatile.Read(ref _closeCalls);

        public void Attach(object window, PluginWindowOptions options)
        {
            lock (Attached) Attached.Add((window, options));
            if (!_windows.TryTrack(window))
            {
                lock (ClosedOnAttach) ClosedOnAttach.Add(window);
            }
        }

        public Task CloseAllAsync()
        {
            Interlocked.Increment(ref _closeCalls);
            foreach (var window in _windows.Seal()) _windows.Remove(window);
            if (closeAll is null)
            {
                Note(order, "windows-closed");
                return Task.CompletedTask;
            }

            Note(order, "windows-closing");
            return closeAll();
        }
    }

    /// <summary>Stands in for a WinUI element; every call returns a new one.</summary>
    private sealed class UiElementStub;

    /// <summary>The entry type of a plugin with every UI contract; records the thread each call runs on.</summary>
    private sealed class UiPlugin(List<string>? order = null)
        : IPlugin, ICommandHandler, ISidebarSectionProvider, ISettingsPageProvider, IPanelProvider, IRowDecorationProvider
    {
        private readonly object _lock = new();
        private int _activations;

        public Func<CancellationToken, Task>? Activate { get; init; }
        public Func<object?> Section { get; init; } = () => new UiElementStub();
        public Func<object?> Page { get; init; } = () => new UiElementStub();
        public Func<object?> Panel { get; init; } = () => new UiElementStub();
        public Func<PluginDecorationRequest, CancellationToken, Task<PluginFolderDecorations>> Decorate { get; init; } =
            (_, _) => Task.FromResult(PluginFolderDecorations.Empty);
        public List<PluginPanelRequest> PanelRequests { get; } = [];
        public List<PluginDecorationRequest> DecorationRequests { get; } = [];
        public List<int> DecorationThreads { get; } = [];
        public IPluginHost? Host { get; private set; }
        public int Activations => Volatile.Read(ref _activations);
        public List<int> ActivationThreads { get; } = [];
        public List<int> ExecuteThreads { get; } = [];
        public List<int> ProviderThreads { get; } = [];
        public List<PluginSidebarSectionRequest> SectionRequests { get; } = [];
        public List<PluginSettingsPageRequest> PageRequests { get; } = [];

        public async Task ActivateAsync(IPluginHost host, CancellationToken ct)
        {
            Interlocked.Increment(ref _activations);
            Host = host;
            if (Activate is not null) await Activate(ct);
            // Resumes on the caller's context: the UI thread when the host runs activation there.
            await Task.Delay(10, ct);
            lock (_lock) ActivationThreads.Add(Environment.CurrentManagedThreadId);
        }

        public Task DeactivateAsync()
        {
            if (order is not null) Note(order, "deactivated");
            return Task.CompletedTask;
        }

        public async Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)
        {
            await Task.Delay(10, ct);
            lock (_lock) ExecuteThreads.Add(Environment.CurrentManagedThreadId);
        }

        public object CreateSidebarSection(PluginSidebarSectionRequest request)
        {
            lock (_lock)
            {
                ProviderThreads.Add(Environment.CurrentManagedThreadId);
                SectionRequests.Add(request);
            }

            return Section()!;
        }

        public object CreateSettingsPage(PluginSettingsPageRequest request)
        {
            lock (_lock)
            {
                ProviderThreads.Add(Environment.CurrentManagedThreadId);
                PageRequests.Add(request);
            }

            return Page()!;
        }

        public object CreatePanel(PluginPanelRequest request)
        {
            lock (_lock)
            {
                ProviderThreads.Add(Environment.CurrentManagedThreadId);
                PanelRequests.Add(request);
            }

            return Panel()!;
        }

        public Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct)
        {
            lock (_lock)
            {
                DecorationThreads.Add(Environment.CurrentManagedThreadId);
                DecorationRequests.Add(request);
            }

            return Decorate(request, ct);
        }
    }

    private sealed class CommandOnlyPlugin : IPlugin, ICommandHandler
    {
        public Task ActivateAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task ExecuteAsync(PluginCommandContext context, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// A UI thread for tests: one dedicated thread with its own synchronization context, so awaits inside the work
    /// resume on it as they do on a WinUI dispatcher.
    /// </summary>
    private sealed class SingleThreadUiThread : IPluginUiThread, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        private int _calls;

        public SingleThreadUiThread()
        {
            _thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new Context(this));
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            })
            {
                IsBackground = true,
                Name = "Fake plugin UI thread",
            };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public int Calls => Volatile.Read(ref _calls);

        public Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            Interlocked.Increment(ref _calls);
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    completion.TrySetResult(await work());
                }
                catch (OperationCanceledException exception)
                {
                    completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }, null);
            return completion.Task;
        }

        private void Post(SendOrPostCallback callback, object? state)
        {
            try { _queue.Add((callback, state)); }
            catch (InvalidOperationException) { }
        }

        public void Dispose() => _queue.CompleteAdding();

        private sealed class Context(SingleThreadUiThread owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) => owner.Post(d, state);

            public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

            public override SynchronizationContext CreateCopy() => this;
        }
    }

    // ---- phase 4 slots: panels and row decorations ----

    private static readonly PluginDecorationRequest Badges =
        new("badges", @"R:\repo", [@"R:\repo\a.txt", @"R:\repo\b.txt"]);

    private static PluginFolderDecorations OneBadge() =>
        new(new Dictionary<string, RowDecoration> { [@"R:\repo\a.txt"] = new("M", "SfDecorationModifiedBrush") }, ["inRepository"]);

    [Fact]
    public async Task CreatePanel_ActivatesOnceAndRunsTheProviderOnTheUiThread()
    {
        Approve(UiId, WriteUiPlugin());
        using var ui = new SingleThreadUiThread();
        var manager = Manager(uiThread: ui);
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin();
        var created = 0;
        manager.InstanceFactory = _ =>
        {
            Interlocked.Increment(ref created);
            return plugin;
        };

        // Called from the thread pool, as the App's panel host would after ConfigureAwait(false).
        var first = await Task.Run(() => manager.CreatePanelAsync(UiId, "bottom", false, CancellationToken.None));
        var second = await Task.Run(() => manager.CreatePanelAsync(UiId, "bottom", false, CancellationToken.None));

        Assert.Equal(1, created);
        Assert.Equal(1, plugin.Activations);
        Assert.IsType<UiElementStub>(first);
        Assert.NotSame(first, second);
        Assert.Equal([ui.ThreadId], plugin.ActivationThreads);
        Assert.Equal([ui.ThreadId, ui.ThreadId], plugin.ProviderThreads);
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);
    }

    [Fact]
    public async Task CreatePanel_PassesDetachedToTheProvider()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin();
        manager.InstanceFactory = _ => plugin;

        await manager.CreatePanelAsync(UiId, "bottom", false, CancellationToken.None);
        await manager.CreatePanelAsync(UiId, "bottom", true, CancellationToken.None);

        Assert.Equal(
            [new PluginPanelRequest("bottom", "bottom-panel", false), new PluginPanelRequest("bottom", "bottom-panel", true)],
            plugin.PanelRequests);
        Assert.Equal(1, plugin.Activations);
    }

    [Fact]
    public async Task CreatePanel_UndeclaredPanel_ThrowsWithoutLoading()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var created = 0;
        manager.InstanceFactory = _ =>
        {
            Interlocked.Increment(ref created);
            return new UiPlugin();
        };
        var record = Record(manager, UiId);
        var epoch = record.Epoch;
        var changed = 0;
        manager.Changed += (_, _) => Interlocked.Increment(ref changed);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreatePanelAsync(UiId, "nope", false, CancellationToken.None));

        Assert.Equal("Plugin 'com.example.ui' has no panel 'nope'.", error.Message);
        Assert.Equal(0, created);
        Assert.Equal(0, changed);
        Assert.Equal(PluginState.Enabled, record.State);
        Assert.Equal(epoch, record.Epoch);
        Assert.Null(record.Runtime);
    }

    [Fact]
    public async Task CreatePanel_ProviderThrows_MarksLoadFailedAndWithdrawsContributions()
    {
        Approve(UiId, WriteUiPlugin());
        var log = new List<string>();
        var manager = Manager(log: (_, line) => Note(log, line));
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new UiPlugin { Panel = () => throw new InvalidOperationException("boom") };
        var epoch = Record(manager, UiId).Epoch;
        Assert.Single(SimpleFile.PluginHost.Contributions.PluginContributionAdapter.Panels(manager.EnabledPlugins));

        var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
            manager.CreatePanelAsync(UiId, "bottom", false, CancellationToken.None));

        const string expected = "Plugin 'com.example.ui' failed to create panel 'bottom': boom";
        Assert.Equal(expected, error.Message);
        var record = Record(manager, UiId);
        Assert.Equal(PluginState.LoadFailed, record.State);
        Assert.Equal(expected, record.Error);
        Assert.True(record.Epoch > epoch);
        Assert.Empty(SimpleFile.PluginHost.Contributions.PluginContributionAdapter.Panels(manager.EnabledPlugins));
        Assert.Empty(SimpleFile.PluginHost.Contributions.PluginContributionAdapter.RowDecorationProviders(manager.EnabledPlugins));
        Assert.Contains($"[error] {expected}", Snapshot(log));

        // A null element and a rejected one fail the same way.
        var empty = Manager();
        await empty.DiscoverAsync(CancellationToken.None);
        empty.InstanceFactory = _ => new UiPlugin { Panel = () => null };
        var missing = await Assert.ThrowsAsync<PluginLoadException>(() =>
            empty.CreatePanelAsync(UiId, "bottom", true, CancellationToken.None));
        Assert.Equal("Plugin 'com.example.ui' returned no element for panel 'bottom'.", missing.Message);
        Assert.Equal(PluginState.LoadFailed, Record(empty, UiId).State);

        var rejecting = Manager(validateUiElement: o => o is string ? "returned System.String" : null);
        await rejecting.DiscoverAsync(CancellationToken.None);
        rejecting.InstanceFactory = _ => new UiPlugin { Panel = () => "not an element" };
        var rejected = await Assert.ThrowsAsync<PluginLoadException>(() =>
            rejecting.CreatePanelAsync(UiId, "bottom", false, CancellationToken.None));
        Assert.Equal("Plugin 'com.example.ui' panel 'bottom' returned System.String", rejected.Message);
        Assert.Equal(PluginState.LoadFailed, Record(rejecting, UiId).State);
    }

    [Fact]
    public async Task CreatePanel_EntryTypeWithoutIPanelProvider_MarksLoadFailed()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new CommandOnlyPlugin();

        var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
            manager.CreatePanelAsync(UiId, "bottom", false, CancellationToken.None));

        const string expected = "Plugin 'com.example.ui' declares a panel but its entry type does not implement IPanelProvider.";
        Assert.Equal(expected, error.Message);
        Assert.Equal(PluginState.LoadFailed, Record(manager, UiId).State);
        Assert.Equal(expected, Record(manager, UiId).Error);
    }

    [Fact]
    public async Task GetRowDecorations_RunsTheProviderOffTheUiThread()
    {
        Approve(UiId, WriteUiPlugin());
        using var ui = new SingleThreadUiThread();
        var manager = Manager(uiThread: ui);
        await manager.DiscoverAsync(CancellationToken.None);
        var expected = OneBadge();
        var plugin = new UiPlugin { Decorate = (_, _) => Task.FromResult(expected) };
        manager.InstanceFactory = _ => plugin;

        // The coordinator calls from the thread pool (Task 9).
        var first = await Task.Run(() => manager.GetRowDecorationsAsync(UiId, Badges, CancellationToken.None));
        var second = await Task.Run(() => manager.GetRowDecorationsAsync(UiId, Badges, CancellationToken.None));

        Assert.Same(expected, first);
        Assert.Same(expected, second);
        Assert.Equal(1, plugin.Activations);
        Assert.Equal([ui.ThreadId], plugin.ActivationThreads);
        Assert.Equal([Badges, Badges], plugin.DecorationRequests);
        Assert.Equal(2, plugin.DecorationThreads.Count);
        Assert.DoesNotContain(ui.ThreadId, plugin.DecorationThreads);
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);
    }

    [Fact]
    public async Task GetRowDecorations_ProviderThrows_ReturnsNullAndKeepsThePluginEnabled()
    {
        Approve(UiId, WriteUiPlugin());
        var log = new List<string>();
        var manager = Manager(log: (_, line) => Note(log, line));
        await manager.DiscoverAsync(CancellationToken.None);
        var calls = 0;
        manager.InstanceFactory = _ => new UiPlugin
        {
            Decorate = (_, _) => Interlocked.Increment(ref calls) switch
            {
                1 => throw new InvalidOperationException("bad repo"),
                2 => Task.FromException<PluginFolderDecorations>(new IOException("status failed")),
                _ => Task.FromResult<PluginFolderDecorations>(null!),
            },
        };
        var epoch = Record(manager, UiId).Epoch;

        Assert.Null(await manager.GetRowDecorationsAsync(UiId, Badges, CancellationToken.None));
        Assert.Null(await manager.GetRowDecorationsAsync(UiId, Badges, CancellationToken.None));
        Assert.Null(await manager.GetRowDecorationsAsync(UiId, Badges, CancellationToken.None));

        var record = Record(manager, UiId);
        Assert.Equal(PluginState.Enabled, record.State);
        Assert.Null(record.Error);
        Assert.Equal(epoch, record.Epoch);
        var warnings = Snapshot(log).Where(line => line.StartsWith("[warn] Row decorations failed:", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [
                "[warn] Row decorations failed: bad repo",
                "[warn] Row decorations failed: status failed",
                "[warn] Row decorations failed: the provider returned no result.",
            ],
            warnings);
        Assert.DoesNotContain(Snapshot(log), line => line.StartsWith("[error]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRowDecorations_CancelledByTheCaller_ReturnsNullWithoutALogLine()
    {
        Approve(UiId, WriteUiPlugin());
        var log = new List<string>();
        var manager = Manager(log: (_, line) => Note(log, line));
        await manager.DiscoverAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.InstanceFactory = _ => new UiPlugin
        {
            Decorate = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return PluginFolderDecorations.Empty;
            },
        };
        using var cts = new CancellationTokenSource();

        var call = manager.GetRowDecorationsAsync(UiId, Badges, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        Assert.Null(await call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);
        Assert.DoesNotContain(Snapshot(log), line => line.StartsWith("[warn]", StringComparison.Ordinal));
        Assert.DoesNotContain(Snapshot(log), line => line.StartsWith("[error]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRowDecorations_CancelledDuringActivation_ActivatesOneInstanceOnce()
    {
        Approve(UiId, WriteUiPlugin());
        var log = new List<string>();
        var order = new List<string>();
        var manager = Manager(log: (_, line) => Note(log, line));
        await manager.DiscoverAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new List<UiPlugin>();
        manager.InstanceFactory = _ =>
        {
            var plugin = new UiPlugin(order)
            {
                // Honours the token it is given, as a plugin that subscribes or loads state in ActivateAsync would.
                Activate = async ct =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                },
            };
            lock (created) created.Add(plugin);
            return plugin;
        };
        using var cts = new CancellationTokenSource();

        // A superseded navigation cancels the first call while the plugin is still activating.
        var first = manager.GetRowDecorationsAsync(UiId, Badges, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));

        // The next navigation waits for that same activation instead of creating a second instance.
        var second = manager.GetRowDecorationsAsync(UiId, Badges, CancellationToken.None);
        release.SetResult();
        Assert.Same(PluginFolderDecorations.Empty, await second.WaitAsync(TimeSpan.FromSeconds(5)));

        UiPlugin activated;
        lock (created) activated = Assert.Single(created);
        Assert.Equal(1, activated.Activations);
        Assert.Single(activated.DecorationRequests);
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);
        Assert.DoesNotContain(Snapshot(log), line => line.StartsWith("[warn]", StringComparison.Ordinal));
        Assert.DoesNotContain(Snapshot(log), line => line.StartsWith("[error]", StringComparison.Ordinal));

        // The one activated instance is the one the manager deactivates; nothing was left half-activated.
        await manager.DisableAsync(UiId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["deactivated"], Snapshot(order));
    }

    [Fact]
    public async Task GetRowDecorations_CancelledDuringAnActivationThatLaterFails_StillMarksLoadFailed()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.InstanceFactory = _ => new UiPlugin
        {
            Activate = async ct =>
            {
                entered.TrySetResult();
                await fail.Task.WaitAsync(ct);
            },
        };
        using var cts = new CancellationTokenSource();

        var call = manager.GetRowDecorationsAsync(UiId, Badges, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        Assert.Null(await call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);

        // The activation the caller stopped waiting for still runs to its end, and its failure is still a load failure.
        fail.SetException(new InvalidOperationException("boom"));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Record(manager, UiId).State != PluginState.LoadFailed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(PluginState.LoadFailed, Record(manager, UiId).State);
        Assert.Equal("boom", Record(manager, UiId).Error);
    }

    [Fact]
    public async Task GetRowDecorations_EntryTypeWithoutTheInterface_MarksLoadFailed()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => new CommandOnlyPlugin();

        var error = await Assert.ThrowsAsync<PluginLoadException>(() =>
            manager.GetRowDecorationsAsync(UiId, Badges, CancellationToken.None));

        const string expected =
            "Plugin 'com.example.ui' declares row decorations but its entry type does not implement IRowDecorationProvider.";
        Assert.Equal(expected, error.Message);
        Assert.Equal(PluginState.LoadFailed, Record(manager, UiId).State);
        Assert.Equal(expected, Record(manager, UiId).Error);
    }

    [Fact]
    public async Task GetRowDecorations_NotEnabled_ThrowsInvalidOperation()
    {
        WriteUiPlugin();
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var created = 0;
        manager.InstanceFactory = _ =>
        {
            Interlocked.Increment(ref created);
            return new UiPlugin();
        };
        Assert.Equal(PluginState.NeedsConsent, Record(manager, UiId).State);

        var notEnabled = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GetRowDecorationsAsync(UiId, Badges, CancellationToken.None));
        Assert.Equal("Plugin 'com.example.ui' is not enabled.", notEnabled.Message);

        // An enabled plugin asked for a provider it does not declare changes nothing either.
        await manager.EnableAsync(UiId, CancellationToken.None);
        var undeclared = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GetRowDecorationsAsync(UiId, Badges with { Provider = "other" }, CancellationToken.None));
        Assert.Equal("Plugin 'com.example.ui' has no row decoration provider 'other'.", undeclared.Message);

        Assert.Equal(0, created);
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);
    }

    // ---- legacy settings migration and setting writes ----

    private const string LegacyKey = "enableGitIntegration";
    private const string EnabledKey = "plugin.com.example.ui.enabled";

    private const string SettingsJson = $$"""
        "settings": [
          { "key": "enabled", "type": "boolean", "label": "Enabled", "default": true, "migrateFrom": "{{LegacyKey}}" },
          { "key": "label", "type": "string", "label": "Label" }
        ],
        """;

    /// <summary><see cref="UiId"/> with <see cref="SettingsJson"/>, listed in the install root's <c>bundled.json</c>.</summary>
    private string WriteBundledSettingsPlugin()
    {
        var folder = WriteUiPlugin(legacySetting: true);
        WriteBundledList(Root, (UiId, PluginPackageHash.ComputeManifestHash(folder)));
        return folder;
    }

    [Fact]
    public async Task MigrateLegacySettings_CopiesAFalseLegacyValueOnce()
    {
        WriteBundledSettingsPlugin();
        var settings = new StoreSettings { [LegacyKey] = "false" };
        var log = new List<string>();
        var manager = Manager(bundledRoot: Root, settings: settings, log: (id, line) => Note(log, $"{id} {line}"));
        await manager.DiscoverAsync(CancellationToken.None);
        Assert.True(Record(manager, UiId).IsBundledId);

        Assert.Equal(1, await manager.MigrateLegacySettingsAsync(CancellationToken.None));

        Assert.Equal("false", settings[EnabledKey]);
        Assert.Equal("false", settings[LegacyKey]);
        Assert.Equal([(EnabledKey, "false")], settings.Writes);
        // Both keys in one read.
        var read = Assert.Single(settings.Reads);
        Assert.Equal([LegacyKey, EnabledKey], read.Order(StringComparer.Ordinal));
        Assert.Contains($"{UiId} [info] Migrated setting enabled from {LegacyKey}", Snapshot(log));

        Assert.Equal(0, await manager.MigrateLegacySettingsAsync(CancellationToken.None));
        Assert.Single(settings.Writes);
        Assert.Single(Snapshot(log), line => line.Contains("Migrated setting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrateLegacySettings_NeverOverwritesAValueTheUserSet()
    {
        WriteBundledSettingsPlugin();
        var settings = new StoreSettings { [LegacyKey] = "false", [EnabledKey] = "true" };
        var manager = Manager(bundledRoot: Root, settings: settings);
        await manager.DiscoverAsync(CancellationToken.None);

        Assert.Equal(0, await manager.MigrateLegacySettingsAsync(CancellationToken.None));

        Assert.Equal("true", settings[EnabledKey]);
        Assert.Empty(settings.Writes);

        // A choice made later (through Settings) is never replaced by a later migration either.
        await manager.WriteSettingAsync(UiId, EnabledKey, "true", CancellationToken.None);
        settings.Writes.Clear();
        Assert.Equal(0, await manager.MigrateLegacySettingsAsync(CancellationToken.None));
        Assert.Empty(settings.Writes);
        Assert.Equal("true", settings[EnabledKey]);
    }

    [Fact]
    public async Task MigrateLegacySettings_IgnoresPluginsNotListedInBundledJson()
    {
        // Approved by the user, so Enabled, but bundled.json lists only another id.
        Approve(UiId, WriteUiPlugin(legacySetting: true));
        WriteBundledList(Root, (BundledId, new string('0', 64)));
        var settings = new StoreSettings { [LegacyKey] = "false" };
        var manager = Manager(bundledRoot: Root, settings: settings);
        await manager.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);
        Assert.False(Record(manager, UiId).IsBundledId);

        Assert.Equal(0, await manager.MigrateLegacySettingsAsync(CancellationToken.None));

        Assert.Empty(settings.Writes);
        Assert.DoesNotContain(LegacyKey, settings.Reads.SelectMany(keys => keys));
    }

    [Fact]
    public async Task MigrateLegacySettings_SkipsAnInvalidLegacyValue()
    {
        WriteBundledSettingsPlugin();
        var settings = new StoreSettings { [LegacyKey] = "maybe" };
        var manager = Manager(bundledRoot: Root, settings: settings);
        await manager.DiscoverAsync(CancellationToken.None);

        Assert.Equal(0, await manager.MigrateLegacySettingsAsync(CancellationToken.None));

        Assert.Empty(settings.Writes);
        Assert.False(settings.Contains(EnabledKey));
        Assert.Equal("maybe", settings[LegacyKey]);
    }

    [Fact]
    public async Task MigrateLegacySettings_ReplacesABlankValueAndIgnoresAMissingLegacyKey()
    {
        WriteBundledSettingsPlugin();
        var blank = new StoreSettings { [LegacyKey] = "false", [EnabledKey] = "  " };
        var manager = Manager(bundledRoot: Root, settings: blank);
        await manager.DiscoverAsync(CancellationToken.None);
        Assert.Equal(1, await manager.MigrateLegacySettingsAsync(CancellationToken.None));
        Assert.Equal("false", blank[EnabledKey]);

        var missing = new StoreSettings();
        var other = Manager(bundledRoot: Root, settings: missing);
        await other.DiscoverAsync(CancellationToken.None);
        Assert.Equal(0, await other.MigrateLegacySettingsAsync(CancellationToken.None));
        Assert.Empty(missing.Writes);
    }

    [Fact]
    public async Task Activate_WaitsForTheLegacySettingsMigration()
    {
        WriteBundledSettingsPlugin();
        var settings = new StoreSettings
        {
            [LegacyKey] = "false",
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var manager = Manager(bundledRoot: Root, settings: settings);
        await manager.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.Enabled, Record(manager, UiId).State);
        var plugin = new UiPlugin();
        var created = 0;
        manager.InstanceFactory = _ =>
        {
            Interlocked.Increment(ref created);
            return plugin;
        };

        var migration = manager.MigrateLegacySettingsAsync(CancellationToken.None);
        await settings.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var run = Task.Run(() => manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None));
        await Task.Delay(200);

        Assert.False(run.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref created));
        Assert.Equal(0, plugin.Activations);

        settings.Gate.SetResult();
        Assert.Equal(1, await migration.WaitAsync(TimeSpan.FromSeconds(5)));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, plugin.Activations);
        // By the time the plugin activated, its setting held the migrated value.
        Assert.Equal("false", settings[EnabledKey]);
    }

    [Fact]
    public async Task Activate_MigrationThatNeverEnds_ActivatesAfterTheBoundAndLogs()
    {
        WriteBundledSettingsPlugin();
        var settings = new StoreSettings
        {
            [LegacyKey] = "false",
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var log = new List<string>();
        var manager = Manager(bundledRoot: Root, settings: settings, log: (id, line) => Note(log, $"{id} {line}"));
        manager.LegacyMigrationWait = TimeSpan.FromMilliseconds(100);
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin();
        manager.InstanceFactory = _ => plugin;

        var migration = manager.MigrateLegacySettingsAsync(CancellationToken.None);
        await manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await manager.CreatePanelAsync(UiId, "bottom", false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, plugin.Activations);
        Assert.False(migration.IsCompleted);
        // Logged once, however many activations waited.
        Assert.Single(
            Snapshot(log),
            line => line == "host [warn] The legacy settings migration is still running after 0.1 s; activating plugins anyway.");

        settings.Gate!.SetResult();
        Assert.Equal(1, await migration.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WriteSetting_WritesTheKeyAndInvalidatesThatPluginsDecorations()
    {
        Approve(UiId, WriteUiPlugin(legacySetting: true));
        var order = new List<string>();
        var settings = new StoreSettings { OnWrite = (key, value) => Note(order, $"write {key}={value}") };
        var manager = Manager(settings: settings, invalidateDecorations: id => Note(order, $"invalidate {id}"));
        await manager.DiscoverAsync(CancellationToken.None);
        var created = 0;
        manager.InstanceFactory = _ =>
        {
            Interlocked.Increment(ref created);
            return new UiPlugin();
        };

        await manager.WriteSettingAsync(UiId, EnabledKey, "false", CancellationToken.None);
        await manager.WriteSettingAsync(UiId, "plugin.com.example.ui.label", "x", CancellationToken.None);

        Assert.Equal(
            [$"write {EnabledKey}=false", $"invalidate {UiId}", "write plugin.com.example.ui.label=x", $"invalidate {UiId}"],
            Snapshot(order));
        Assert.Equal("false", settings[EnabledKey]);
        // Writing a setting loads no plugin code.
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task WriteSetting_KeyOfAnotherPlugin_IsRefused()
    {
        Approve(UiId, WriteUiPlugin(legacySetting: true));
        Approve("com.example.other", WriteUiPlugin("com.example.other", legacySetting: true));
        var invalidated = new List<string>();
        var settings = new StoreSettings();
        var manager = Manager(settings: settings, invalidateDecorations: id => Note(invalidated, id));
        await manager.DiscoverAsync(CancellationToken.None);

        foreach (var key in new[]
                 {
                     "plugin.com.example.other.enabled",
                     "plugin.com.example.ui.undeclared",
                     LegacyKey,
                     "plugin.com.example.ui.",
                     "plugin.com.example.ui.Enabled",
                 })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => manager.WriteSettingAsync(UiId, key, "false", CancellationToken.None));
        }

        // An unknown plugin and a value the setting's type refuses are refused as well.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.WriteSettingAsync("com.example.missing", "plugin.com.example.missing.enabled", "false", CancellationToken.None));
        var invalid = await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.WriteSettingAsync(UiId, EnabledKey, "maybe", CancellationToken.None));
        Assert.StartsWith("Enabled must be true or false.", invalid.Message, StringComparison.Ordinal);

        Assert.Empty(settings.Writes);
        Assert.Empty(Snapshot(invalidated));
    }

    // ---- host services: panels and decorations ----

    [Fact]
    public async Task Services_PanelsAndDecorations_ReachTheHostWithThePluginId()
    {
        Approve(UiId, WriteUiPlugin());
        var panelsAsked = new List<string>();
        var invalidated = new List<string>();
        var panels = new RecordingPanels();
        var manager = Manager(
            panelsFor: id =>
            {
                Note(panelsAsked, id);
                return panels;
            },
            invalidateDecorations: id => Note(invalidated, id));
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin();
        manager.InstanceFactory = _ => plugin;

        await manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None);
        var host = plugin.Host!;
        await host.Panels.ShowAsync("bottom", CancellationToken.None);
        host.Panels.Hide("bottom");
        host.Decorations.Invalidate();

        Assert.Equal([UiId], Snapshot(panelsAsked));
        Assert.Same(panels, host.Panels);
        Assert.Equal(["show bottom", "hide bottom"], Snapshot(panels.Calls));
        Assert.Equal([UiId], Snapshot(invalidated));
    }

    [Fact]
    public async Task Services_WithoutPanelsFor_ReportNoPanelVisibleAndRefuseShow()
    {
        Approve(UiId, WriteUiPlugin());
        var manager = Manager();
        await manager.DiscoverAsync(CancellationToken.None);
        var plugin = new UiPlugin();
        manager.InstanceFactory = _ => plugin;

        await manager.RunCommandAsync(OpenContext(UiId).CommandId, OpenContext(UiId), CancellationToken.None);
        var host = plugin.Host!;

        Assert.False(host.Panels.IsVisible("bottom"));
        host.Panels.Hide("bottom");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Panels.ShowAsync("bottom", CancellationToken.None));
        Assert.Equal("This host has no panels.", error.Message);
        // Without InvalidateDecorations, Invalidate does nothing and does not throw.
        host.Decorations.Invalidate();
    }

    private sealed class RecordingPanels : IPluginPanels
    {
        public List<string> Calls { get; } = [];

        public Task ShowAsync(string panelId, CancellationToken ct)
        {
            Note(Calls, $"show {panelId}");
            return Task.CompletedTask;
        }

        public void Hide(string panelId) => Note(Calls, $"hide {panelId}");

        public bool IsVisible(string panelId) => false;
    }

    /// <summary>
    /// A settings store whose writes are read back, unlike <see cref="FakeSettings"/>. With <see cref="Gate"/> set, every
    /// read waits for it after signalling <see cref="ReadEntered"/>.
    /// </summary>
    private sealed class StoreSettings : ISettingsBackend
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<string, string>? OnWrite { get; init; }
        public List<(string Key, string Value)> Writes { get; } = [];
        public List<IReadOnlyList<string>> Reads { get; } = [];

        public string this[string key]
        {
            get { lock (_values) return _values[key]; }
            set { lock (_values) _values[key] = value; }
        }

        public bool Contains(string key)
        {
            lock (_values) return _values.ContainsKey(key);
        }

        public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default) =>
            (await GetSettingsAsync([key], ct))[key];

        public async Task<IReadOnlyDictionary<string, string?>> GetSettingsAsync(IReadOnlyList<string> keys, CancellationToken ct = default)
        {
            lock (_values) Reads.Add(keys.ToList());
            ReadEntered.TrySetResult();
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            lock (_values) return keys.Distinct().ToDictionary(k => k, k => _values.GetValueOrDefault(k));
        }

        public Task SetSettingAsync(string key, string value, CancellationToken ct = default)
        {
            lock (_values)
            {
                _values[key] = value;
                Writes.Add((key, value));
            }

            OnWrite?.Invoke(key, value);
            return Task.CompletedTask;
        }
    }

    // ---- bundled-plugin trust ----

    private const string BundledId = "com.example.bundled";

    /// <summary>A second root, as only SUMAFILE_PLUGIN_ROOTS can add one.</summary>
    private string OtherRoot => Path.Combine(_temp, "other-plugins");

    /// <summary>A plugin with one command and no backend or UI assembly, so enabling it starts nothing.</summary>
    private static string WritePlainPlugin(string root, string id, string version = "1.0.0")
    {
        var folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "Bundled",
              "version": "{{version}}",
              "publisher": { "name": "SumaFile Team" },
              "sdkVersion": "1.0.0",
              "contributions": { "commands": [] }
            }
            """);
        return folder;
    }

    private static void WriteBundledList(string root, params (string Id, string Hash)[] plugins)
    {
        Directory.CreateDirectory(root);
        var entries = string.Join(", ", plugins.Select(p => $$"""{ "id": "{{p.Id}}", "manifestSha256": "{{p.Hash}}" }"""));
        File.WriteAllText(Path.Combine(root, BundledPluginList.FileName), $$"""{ "plugins": [ {{entries}} ] }""");
    }

    [Fact]
    public async Task BundledPlugin_InInstallRoot_IsEnabledWithoutConsent()
    {
        var folder = WritePlainPlugin(Root, BundledId);
        WriteBundledList(Root, (BundledId, PluginPackageHash.ComputeManifestHash(folder)));
        // Another spelling of the same root (case, trailing separator, a relative segment) still matches.
        var bundledRoot = Path.Combine(_temp, "x", "..", "PLUGINS") + Path.DirectorySeparatorChar;
        var manager = Manager(bundledRoot: bundledRoot);

        await manager.DiscoverAsync(CancellationToken.None);

        Assert.Equal(PluginState.Enabled, Record(manager, BundledId).State);
        Assert.Equal([BundledId], manager.EnabledPlugins.Select(p => p.Id));
        Assert.False(File.Exists(StorePath), "bundled trust must not write the approvals file");
    }

    [Fact]
    public async Task BundledPlugin_ManifestHashNotListed_NeedsConsent()
    {
        var folder = WritePlainPlugin(Root, BundledId);
        WriteBundledList(Root, (BundledId, new string('a', 64)), ("com.example.other", PluginPackageHash.ComputeManifestHash(folder)));
        var manager = Manager(bundledRoot: Root);

        await manager.DiscoverAsync(CancellationToken.None);

        Assert.Equal(PluginState.NeedsConsent, Record(manager, BundledId).State);
        Assert.Empty(manager.EnabledPlugins);
    }

    [Fact]
    public async Task UnlistedPluginInTheInstallRoot_NeedsConsent()
    {
        // One root holds the bundled plugins and the ones the user installs: only bundled.json tells them apart.
        var bundled = WritePlainPlugin(Root, BundledId);
        WritePlainPlugin(Root, "com.example.user");
        WriteBundledList(Root, (BundledId, PluginPackageHash.ComputeManifestHash(bundled)));
        var manager = Manager(bundledRoot: Root);

        await manager.DiscoverAsync(CancellationToken.None);

        var user = Record(manager, "com.example.user");
        Assert.Equal(PluginState.NeedsConsent, user.State);
        Assert.False(user.IsBundledId);
        Assert.Equal(PluginState.Enabled, Record(manager, BundledId).State);
        Assert.True(Record(manager, BundledId).IsBundledId);
        Assert.Equal([BundledId], manager.EnabledPlugins.Select(p => p.Id));
    }

    [Fact]
    public async Task BundledId_WithAModifiedManifest_NeedsConsentAndStaysABundledId()
    {
        var folder = WritePlainPlugin(Root, BundledId, "1.0.0");
        WriteBundledList(Root, (BundledId, PluginPackageHash.ComputeManifestHash(folder)));
        // Edited after the release wrote the list: trust needs the listed hash, so consent is asked again, but the id is
        // still SumaFile's, so Settings never offers to remove it.
        WritePlainPlugin(Root, BundledId, "1.0.1-edited");
        var manager = Manager(bundledRoot: Root);

        await manager.DiscoverAsync(CancellationToken.None);

        var record = Record(manager, BundledId);
        Assert.Equal(PluginState.NeedsConsent, record.State);
        Assert.True(record.IsBundledId);
        Assert.Empty(manager.EnabledPlugins);
    }

    [Fact]
    public async Task Enable_ApprovalsFolderNotWritable_ThrowsAndChangesNothing()
    {
        WriteBackendPlugin(activateOn: "startup");
        var approvalsFolder = Path.Combine(_temp, "locked");
        var launcher = new ThrowingLauncher();
        var manager = Manager(launcher, approvalStorePath: Path.Combine(approvalsFolder, "plugin-approvals.json"));
        await manager.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.NeedsConsent, Record(manager, BackendId).State);
        _denials.Add(DenyAce.Add(
            approvalsFolder,
            System.Security.AccessControl.FileSystemRights.CreateFiles
            | System.Security.AccessControl.FileSystemRights.WriteData
            | System.Security.AccessControl.FileSystemRights.AppendData));
        var changed = 0;
        manager.Changed += (_, _) => changed++;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.EnableAsync(BackendId, CancellationToken.None));

        Assert.Equal(PluginState.NeedsConsent, Record(manager, BackendId).State);
        Assert.Equal(0, changed);
        Assert.Equal(0, launcher.Launches);
        Assert.Empty(Directory.GetFiles(approvalsFolder));
    }

    [Fact]
    public async Task BundledPlugin_UserDisabled_StaysDisabledAfterUpgrade()
    {
        var folder = WritePlainPlugin(Root, BundledId, "1.0.0");
        WriteBundledList(Root, (BundledId, PluginPackageHash.ComputeManifestHash(folder)));
        var manager = Manager(bundledRoot: Root);
        await manager.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.Enabled, Record(manager, BundledId).State);

        // The user turns the bundled plugin off: that is the one record bundled trust writes.
        await manager.DisableAsync(BundledId, CancellationToken.None);
        var storedBeforeUpgrade = File.ReadAllBytes(StorePath);

        // An upgrade ships a new manifest and a bundled list with its new hash.
        WritePlainPlugin(Root, BundledId, "2.0.0");
        WriteBundledList(Root, (BundledId, PluginPackageHash.ComputeManifestHash(folder)));
        await manager.RescanAsync(CancellationToken.None);

        Assert.Equal(PluginState.Disabled, Record(manager, BundledId).State);
        Assert.DoesNotContain(manager.Plugins, p => p.State == PluginState.NeedsConsent);
        Assert.Equal(storedBeforeUpgrade, File.ReadAllBytes(StorePath));

        // A fresh start after the upgrade sees the same.
        var restarted = Manager(bundledRoot: Root);
        await restarted.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.Disabled, Record(restarted, BundledId).State);
    }

    [Fact]
    public async Task BundledPlugin_UserDisabled_ApprovalsFileUnreadable_IsNotEnabled()
    {
        var folder = WritePlainPlugin(Root, BundledId);
        WriteBundledList(Root, (BundledId, PluginPackageHash.ComputeManifestHash(folder)));
        new PluginApprovalStore(StorePath).Set(BundledId, "hash-from-an-older-version", SignatureStatus.Unsigned,
            ApprovalState.Disabled, folder);

        // Another process (the antivirus, another SumaFile mid-save) holds the file through every retry, so the
        // user's Disabled cannot be seen. Bundled trust must fail safe instead of turning the plugin on.
        var manager = Manager(bundledRoot: Root);
        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await manager.DiscoverAsync(CancellationToken.None);
        }

        Assert.Equal(PluginState.NeedsConsent, Record(manager, BundledId).State);
        Assert.Empty(manager.EnabledPlugins);

        // Once the file is readable again the stored Disabled holds.
        await manager.RescanAsync(CancellationToken.None);
        Assert.Equal(PluginState.Disabled, Record(manager, BundledId).State);
    }

    [Fact]
    public async Task BundledPlugin_ListInAnotherRoot_IsIgnored()
    {
        // A list next to a plugin in another root trusts nothing: only the install root's list counts.
        var otherCopy = WritePlainPlugin(OtherRoot, BundledId);
        WriteBundledList(OtherRoot, (BundledId, PluginPackageHash.ComputeManifestHash(otherCopy)));
        var manager = Manager(roots: [Root, OtherRoot], bundledRoot: Root);
        await manager.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.NeedsConsent, Record(manager, BundledId).State);

        // Without a BundledRoot, even the install root's own list is ignored.
        var installed = WritePlainPlugin(Root, "com.example.installed");
        WriteBundledList(Root, ("com.example.installed", PluginPackageHash.ComputeManifestHash(installed)));
        var untrusting = Manager(bundledRoot: null);
        await untrusting.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.NeedsConsent, Record(untrusting, "com.example.installed").State);
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
        private readonly List<string> _calls = [];

        /// <summary>Awaited inside UnregisterAsync, so a test can hold an unregister in flight.</summary>
        public Task UnregisterGate { get; init; } = Task.CompletedTask;

        public TaskCompletionSource UnregisterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Calls in the order they completed: "register:{pipe}" or "unregister".</summary>
        public IReadOnlyList<string> Calls
        {
            get { lock (_lock) return _calls.ToList(); }
        }

        private int _registerCalls;
        private Exception? _failure;

        /// <summary>Every register attempt fails with this while it is set.</summary>
        public Exception? Failure
        {
            get => Volatile.Read(ref _failure);
            set => Volatile.Write(ref _failure, value);
        }

        /// <summary>The first this many register attempts fail with "attempt {n} refused".</summary>
        public int FailuresBeforeSuccess { get; init; }

        /// <summary>Register attempts never complete and ignore their token.</summary>
        public bool NeverComplete { get; init; }

        /// <summary>Awaited at the start of register attempt n (1-based, counted across all calls).</summary>
        public Func<int, Task>? AttemptGate { get; init; }

        /// <summary>Register attempts started, failed ones included.</summary>
        public int RegisterCalls => Volatile.Read(ref _registerCalls);

        public IReadOnlyList<(string PluginId, string PipeName, IReadOnlyList<SimpleFile.PluginHost.Manifest.PluginArchiveFormatSpec> Formats)> Registered
        {
            get { lock (_lock) return _registered.ToList(); }
        }

        public IReadOnlyList<string> Unregistered
        {
            get { lock (_lock) return _unregistered.ToList(); }
        }

        /// <summary>Appends to <see cref="Calls"/>, so a test can log its own events in the same order.</summary>
        public void Note(string entry)
        {
            lock (_lock) _calls.Add(entry);
        }

        public async Task RegisterAsync(
            string pluginId,
            string pipeName,
            IReadOnlyList<SimpleFile.PluginHost.Manifest.PluginArchiveFormatSpec> formats,
            CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _registerCalls);
            if (AttemptGate is { } gate) await gate(attempt);
            if (NeverComplete) await new TaskCompletionSource().Task;
            if (Failure is { } failure) throw failure;
            if (attempt <= FailuresBeforeSuccess) throw new InvalidOperationException($"attempt {attempt} refused");
            lock (_lock)
            {
                _registered.Add((pluginId, pipeName, formats));
                _calls.Add($"register:{pipeName}");
            }
        }

        public async Task UnregisterAsync(string pluginId, CancellationToken ct)
        {
            UnregisterEntered.TrySetResult();
            await UnregisterGate.WaitAsync(ct);
            lock (_lock)
            {
                _unregistered.Add(pluginId);
                _calls.Add("unregister");
            }
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

    /// <summary>Blocks the calling thread inside DeactivateAsync, before returning any task.</summary>
    private sealed class BlockingDeactivatePlugin(ManualResetEventSlim release) : IPlugin, ICommandHandler
    {
        private volatile bool _deactivateEntered;

        public bool DeactivateEntered => _deactivateEntered;

        public Task ActivateAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task DeactivateAsync()
        {
            _deactivateEntered = true;
            release.Wait();
            return Task.CompletedTask;
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
            string executable,
            IReadOnlyList<string> args,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            Action<string> onStderrLine)
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
            string executable,
            IReadOnlyList<string> args,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            Action<string> onStderrLine)
        {
            Interlocked.Increment(ref _launches);
            throw new IOException("backend executable not found");
        }
    }
}
