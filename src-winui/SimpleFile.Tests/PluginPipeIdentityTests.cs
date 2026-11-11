using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Manifest;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Final fix wave, item 4: the host checks who serves a plugin's pipe. It connects with
/// <see cref="PipeOptions.CurrentUserOnly"/> (a pipe another user created is refused) and, on Windows, requires
/// <c>GetNamedPipeServerProcessId</c> to name the backend process it launched, so a process that squats on the pipe
/// name first never receives the handshake.
/// </summary>
public sealed class PluginPipeIdentityTests : IAsyncLifetime
{
    private const string PluginId = "com.example.pipeid";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
    private readonly List<FakePluginProcessLauncher> _launchers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var launcher in _launchers)
        {
            await launcher.DisposeAsync();
        }

        try { Directory.Delete(_temp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private DiscoveredPlugin Plugin()
    {
        var manifest = new PluginManifest(
            PluginId, "Pipe", "1.0.0", new PluginPublisher("P", null, null), "1.0.0", null,
            new PluginBackendSpec("backend.exe", [], "first-call", false), new PluginContributions([], [], [], []), [], []);
        return new DiscoveredPlugin(PluginId, Path.Combine(_temp, "plugins", PluginId), Path.Combine(_temp, "plugins"), DiscoveryState.Valid, manifest, [], null);
    }

    private FakePluginProcessLauncher Launcher()
    {
        var launcher = new FakePluginProcessLauncher(async (server, ct) =>
        {
            await server.WaitForConnectionAsync(ct);
            var request = await server.ReadRequestAsync(ct);
            await server.SendResultAsync(request.Id, new { pluginId = PluginId, pluginVersion = "1.0.0", sdkVersion = "1.0.0" }, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }, createServer: true);
        _launchers.Add(launcher);
        return launcher;
    }

    private PluginBackendHost Host(FakePluginProcessLauncher launcher, Func<NamedPipeClientStream, int?> serverProcessId) =>
        new(launcher, _ => "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N"), Path.Combine(_temp, "data"), (_, _) => { })
        {
            ServerProcessId = serverProcessId,
        };

    [Fact]
    public async Task APipeServedByAnotherProcess_FailsTheStartAndStopsTheBackend()
    {
        var launcher = Launcher();
        var host = Host(launcher, _ => 999_999);

        var thrown = await Assert.ThrowsAsync<PluginBackendException>(() => host.StartAsync(Plugin(), CancellationToken.None));

        Assert.Equal(
            $"The pipe of plugin '{PluginId}' is served by process 999999, not by the backend SumaFile started "
            + $"(process {launcher.Process!.Id}); SumaFile did not connect to it.",
            thrown.Message);
        Assert.True(launcher.Process.Killed);
    }

    [Fact]
    public async Task APipeServedByTheLaunchedBackend_Connects()
    {
        var launcher = Launcher();
        int? asked = null;
        var host = Host(launcher, pipe =>
        {
            Assert.True(pipe.IsConnected);
            asked = launcher.Process!.Id;
            return launcher.Process.Id;
        });

        await using var connection = await host.StartAsync(Plugin(), CancellationToken.None);

        Assert.Equal(PluginId, connection.Handshake.PluginId);
        Assert.Equal(launcher.Process!.Id, asked);
    }

    [Fact]
    public async Task NoServerProcessId_SkipsTheCheck()
    {
        // Not Windows: GetNamedPipeServerProcessId does not exist, and CurrentUserOnly alone guards the pipe.
        var launcher = Launcher();
        var host = Host(launcher, _ => null);

        await using var connection = await host.StartAsync(Plugin(), CancellationToken.None);

        Assert.Equal(PluginId, connection.Handshake.PluginId);
    }

    [Fact]
    public async Task TheDefaultCheck_NamesThisProcessForAnInProcessServer()
    {
        await using var server = FakeIpcServer.Create();
        var accept = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        await accept;

        var id = PipeServerIdentity.ServerProcessId(client);

        Assert.Equal(OperatingSystem.IsWindows() ? Environment.ProcessId : null, id);
    }

    [Fact]
    public void TheHost_ConnectsCurrentUserOnlyAndChecksTheServerProcess()
    {
        // Source shape for what only a second user or a squatting process could show.
        var backend = Path.Combine(SourceRoot(), "SimpleFile.PluginHost", "Backend");
        var host = File.ReadAllText(Path.Combine(backend, "PluginBackendHost.cs"));
        var identity = File.ReadAllText(Path.Combine(backend, "PipeServerIdentity.cs"));

        Assert.Contains("PipeOptions.CurrentUserOnly", host, StringComparison.Ordinal);
        Assert.Contains("ServerProcessId(pipe)", host, StringComparison.Ordinal);
        Assert.Contains("GetNamedPipeServerProcessId(pipe.SafePipeHandle", identity, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsWindows()", identity, StringComparison.Ordinal);
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")) && Directory.Exists(Path.Combine(dir.FullName, "src-winui")))
                return Path.Combine(dir.FullName, "src-winui");
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds both Cargo.toml and src-winui.");
    }
}
