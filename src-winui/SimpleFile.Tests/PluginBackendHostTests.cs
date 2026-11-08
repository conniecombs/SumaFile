using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Manifest;
using Xunit;

namespace SimpleFile.Tests;

public class PluginBackendHostTests : IAsyncLifetime
{
    private const string PluginId = "com.example.backend";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
    private readonly List<FakePluginProcessLauncher> _launchers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var launcher in _launchers)
        {
            await launcher.DisposeAsync();
        }

        try { Directory.Delete(_temp, true); } catch { }
    }

    private DiscoveredPlugin Plugin()
    {
        var folder = Path.Combine(_temp, "plugins", PluginId);
        var manifest = new PluginManifest(
            PluginId,
            "Backend",
            "1.2.3",
            new PluginPublisher("P", null, null),
            "1.0.0",
            null,
            new PluginBackendSpec("backend.exe", ["--extra", "x"], "startup", false),
            new PluginContributions([], [], [], []),
            [],
            []);
        return new DiscoveredPlugin(PluginId, folder, Path.Combine(_temp, "plugins"), DiscoveryState.Valid, manifest, [], null);
    }

    private FakePluginProcessLauncher Launcher(Func<FakeIpcServer, CancellationToken, Task>? serve, bool createServer = true)
    {
        var launcher = new FakePluginProcessLauncher(serve, createServer);
        _launchers.Add(launcher);
        return launcher;
    }

    private PluginBackendHost Host(FakePluginProcessLauncher launcher, TimeSpan? timeout = null)
    {
        var name = "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N");
        var dataRoot = Path.Combine(_temp, "data");
        return timeout is null
            ? new PluginBackendHost(launcher, _ => name, dataRoot, (_, _) => { })
            : new PluginBackendHost(launcher, _ => name, dataRoot, (_, _) => { }, timeout.Value);
    }

    private static async Task AnswerHandshake(
        FakeIpcServer server, CancellationToken ct, string pluginId = PluginId, string sdk = "1.0.0")
    {
        await server.WaitForConnectionAsync(ct);
        var request = await server.ReadRequestAsync(ct);
        Assert.Equal("plugin.handshake", request.Method);
        await server.SendResultAsync(
            request.Id,
            new { pluginId, pluginVersion = "1.2.3", sdkVersion = sdk },
            ct);
    }

    [Fact]
    public async Task StartAsync_LaunchesWithPipeAndDataDirArgs()
    {
        var launcher = Launcher(async (s, ct) =>
        {
            await AnswerHandshake(s, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = Host(launcher);
        var plugin = Plugin();

        await using var connection = await host.StartAsync(plugin, CancellationToken.None);

        var args = launcher.LastArgs!;
        var pipeIndex = args.ToList().IndexOf("--pipe-name");
        Assert.True(pipeIndex >= 0);
        Assert.Equal(launcher.PipeName, args[pipeIndex + 1]);
        var dataIndex = args.ToList().IndexOf("--data-dir");
        Assert.True(dataIndex >= 0);
        Assert.EndsWith(PluginId, args[dataIndex + 1]);
        Assert.True(Directory.Exists(args[dataIndex + 1]));
        Assert.Contains("--extra", args);
        Assert.Equal(Path.Combine(plugin.Folder, "backend.exe"), launcher.LastExecutable);
        Assert.Equal(plugin.Folder, launcher.LastWorkingDirectory);
    }

    [Fact]
    public async Task StartAsync_HandshakeSucceeds_ReturnsConnection()
    {
        var launcher = Launcher(async (s, ct) =>
        {
            await AnswerHandshake(s, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = Host(launcher);

        await using var connection = await host.StartAsync(Plugin(), CancellationToken.None);

        Assert.Equal(PluginId, connection.Handshake.PluginId);
        Assert.Equal("1.2.3", connection.Handshake.PluginVersion);
        Assert.Equal("1.0.0", connection.Handshake.SdkVersion);
        Assert.False(launcher.Process!.Killed);
    }

    [Fact]
    public async Task StartAsync_HandshakeNeverAnswered_FailsWithinTimeoutAndKills()
    {
        var launcher = Launcher(async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            await s.ReadRequestAsync(ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = Host(launcher, TimeSpan.FromSeconds(1));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<PluginBackendException>(() => host.StartAsync(Plugin(), CancellationToken.None));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.True(launcher.Process!.Killed);
    }

    [Fact]
    public async Task StartAsync_NoServer_FailsAfterConnectTimeoutAndKills()
    {
        var launcher = Launcher(null, createServer: false);
        var host = Host(launcher, TimeSpan.FromSeconds(1));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<PluginBackendException>(() => host.StartAsync(Plugin(), CancellationToken.None));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
        Assert.True(launcher.Process!.Killed);
    }

    [Fact]
    public async Task StartAsync_WrongPluginId_Fails()
    {
        var launcher = Launcher(async (s, ct) =>
        {
            await AnswerHandshake(s, ct, pluginId: "com.example.other");
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = Host(launcher);

        await Assert.ThrowsAsync<PluginBackendException>(() => host.StartAsync(Plugin(), CancellationToken.None));
        Assert.True(launcher.Process!.Killed);
    }

    [Fact]
    public async Task StartAsync_SdkMajorMismatch_Fails()
    {
        var launcher = Launcher(async (s, ct) =>
        {
            await AnswerHandshake(s, ct, sdk: "2.0.0");
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = Host(launcher);

        await Assert.ThrowsAsync<PluginBackendException>(() => host.StartAsync(Plugin(), CancellationToken.None));
        Assert.True(launcher.Process!.Killed);
    }

    [Fact]
    public async Task Connection_ProcessExit_RaisesLost()
    {
        var launcher = Launcher(async (s, ct) =>
        {
            await AnswerHandshake(s, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = Host(launcher);
        await using var connection = await host.StartAsync(Plugin(), CancellationToken.None);

        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        connection.Lost += (_, _) =>
        {
            Interlocked.Increment(ref count);
            lost.TrySetResult();
        };

        launcher.Process!.SimulateExit();

        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Connection_Closed_RaisedOnceAfterLostAndOnceOnDispose()
    {
        var launcher = Launcher(async (s, ct) =>
        {
            await AnswerHandshake(s, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = Host(launcher);
        var connection = await host.StartAsync(Plugin(), CancellationToken.None);
        var events = new List<string>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Lost += (_, _) => { lock (events) events.Add("lost"); };
        connection.Closed += (_, _) =>
        {
            lock (events) events.Add("closed");
            closed.TrySetResult();
        };

        launcher.Process!.SimulateExit();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.DisposeAsync();

        lock (events) Assert.Equal(["lost", "closed"], events);

        // An intentional stop raises Closed but not Lost.
        var second = await Host(launcher).StartAsync(Plugin(), CancellationToken.None);
        var lost = 0;
        var closedCount = 0;
        second.Lost += (_, _) => Interlocked.Increment(ref lost);
        second.Closed += (_, _) => Interlocked.Increment(ref closedCount);
        await second.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(0, lost);
        Assert.Equal(1, closedCount);
    }

    [Fact]
    public async Task Connection_HostRequestFromBackend_IsAnswered()
    {
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = Launcher(async (s, ct) =>
        {
            await AnswerHandshake(s, ct);
            await s.SendRequestAsync(77, "host.log", new { message = "hi" }, ct);
            reply.TrySetResult(await s.ReadResponseAsync(ct));
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = Host(launcher);
        await using var connection = await host.StartAsync(
            Plugin(),
            CancellationToken.None,
            c => c.OnHostRequest(
                "host.log",
                (p, _) => Task.FromResult<object?>(new { ok = p.GetProperty("message").GetString() })));

        var response = await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(77, response.GetProperty("id").GetInt32());
        Assert.Equal("hi", response.GetProperty("result").GetProperty("ok").GetString());
    }
}

internal sealed class FakePluginProcess : IPluginProcess
{
    private int _exited;

    public int Id => 4242;
    public bool Killed { get; private set; }
    public bool HasExited => Volatile.Read(ref _exited) != 0;
    public event EventHandler? Exited;

    public void Kill()
    {
        Killed = true;
        SimulateExit();
    }

    public void SimulateExit()
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0)
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() { }
}

internal sealed class FakePluginProcessLauncher : IPluginProcessLauncher, IAsyncDisposable
{
    private readonly Func<FakeIpcServer, CancellationToken, Task>? _serve;
    private readonly bool _createServer;
    private readonly CancellationTokenSource _cts = new();
    // Every launch gets its own server; a restart launches again, so all of them are disposed at the end.
    private readonly List<(FakeIpcServer Server, Task Serve)> _servers = [];

    public FakePluginProcessLauncher(Func<FakeIpcServer, CancellationToken, Task>? serve, bool createServer)
    {
        _serve = serve;
        _createServer = createServer;
    }

    public string? LastExecutable { get; private set; }
    public IReadOnlyList<string>? LastArgs { get; private set; }
    public string? LastWorkingDirectory { get; private set; }
    public string? PipeName { get; private set; }
    public FakePluginProcess? Process { get; private set; }

    /// <summary>Completed with the executable path when <see cref="Launch"/> is called.</summary>
    public TaskCompletionSource<string> Launched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IPluginProcess Launch(
        string executable, IReadOnlyList<string> args, string workingDirectory, Action<string> onStderrLine)
    {
        LastExecutable = executable;
        LastArgs = args;
        LastWorkingDirectory = workingDirectory;
        var index = args.ToList().IndexOf("--pipe-name");
        PipeName = args[index + 1];
        Process = new FakePluginProcess();
        Launched.TrySetResult(executable);
        if (_createServer && _serve is not null)
        {
            var server = FakeIpcServer.Create(PipeName);
            var serve = Task.Run(async () =>
            {
                try { await _serve(server, _cts.Token); }
                catch (Exception) { }
            });
            lock (_servers) _servers.Add((server, serve));
        }

        return Process;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        (FakeIpcServer Server, Task Serve)[] servers;
        lock (_servers) servers = _servers.ToArray();
        foreach (var (server, serve) in servers)
        {
            await server.DisposeAsync();
            try { await serve.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _cts.Dispose();
    }
}
