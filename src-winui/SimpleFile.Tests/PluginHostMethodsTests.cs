using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginSdk;
using Xunit;

namespace SimpleFile.Tests;

public class PluginHostMethodsTests : IAsyncLifetime
{
    private const string PluginId = "com.example.a";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
    private readonly List<FakePluginProcessLauncher> _launchers = [];
    private readonly FakeSettings _settings = new();
    private readonly RecordingSink _sink = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly List<(string Plugin, string Message)> _logs = [];

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
            PluginId, "A", "1.0.0", new PluginPublisher("P", null, null), "1.0.0", null,
            new PluginBackendSpec("backend.exe", [], "startup", false),
            new PluginContributions([], [], [], []), [], []);
        return new DiscoveredPlugin(PluginId, folder, Path.Combine(_temp, "plugins"), DiscoveryState.Valid, manifest, [], null);
    }

    private PluginBackendHost Host(Func<FakeIpcServer, CancellationToken, Task> serve)
    {
        var launcher = new FakePluginProcessLauncher(serve, true);
        _launchers.Add(launcher);
        var name = "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N");
        return new PluginBackendHost(launcher, _ => name, Path.Combine(_temp, "data"), (_, _) => { });
    }

    private static async Task Handshake(FakeIpcServer server, CancellationToken ct)
    {
        await server.WaitForConnectionAsync(ct);
        var request = await server.ReadRequestAsync(ct);
        await server.SendResultAsync(
            request.Id, new { pluginId = PluginId, pluginVersion = "1.0.0", sdkVersion = "1.0.0" }, ct);
    }

    /// <summary>Starts a backend that sends each call as a host request and collects the replies in order.</summary>
    private async Task<List<JsonElement>> Call(params (string Method, object? Params)[] calls)
    {
        var replies = new TaskCompletionSource<List<JsonElement>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Host(async (s, ct) =>
        {
            await Handshake(s, ct);
            var list = new List<JsonElement>();
            for (var i = 0; i < calls.Length; i++)
            {
                await s.SendRequestAsync(100 + i, calls[i].Method, calls[i].Params, ct);
                list.Add(await s.ReadResponseAsync(ct));
            }

            replies.TrySetResult(list);
            await Task.Delay(Timeout.Infinite, ct);
        });

        await using var connection = await host.StartAsync(
            Plugin(),
            CancellationToken.None,
            c => PluginHostMethods.Register(c, PluginId, _settings, _sink, _notifier, (p, m) => _logs.Add((p, m))));
        return await replies.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static int ErrorCode(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

    [Fact]
    public async Task GetSettings_PrefixesKeysWithPluginId()
    {
        _settings.Values["plugin.com.example.a.theme"] = "dark";

        var replies = await Call((PluginHostMethods.GetSettings, new { keys = new[] { "theme", "missing" } }));

        Assert.Equal(["plugin.com.example.a.theme", "plugin.com.example.a.missing"], _settings.Requested);
        var values = replies[0].GetProperty("result").GetProperty("values");
        Assert.Equal("dark", values.GetProperty("theme").GetString());
        Assert.Equal(JsonValueKind.Null, values.GetProperty("missing").ValueKind);
    }

    [Fact]
    public async Task SetSetting_InvalidKey_IsInvalidParams()
    {
        var replies = await Call(
            (PluginHostMethods.SetSetting, new { key = "a.b", value = "v" }),
            (PluginHostMethods.SetSetting, new { key = "", value = "v" }),
            (PluginHostMethods.SetSetting, new { key = new string('a', 65), value = "v" }),
            (PluginHostMethods.SetSetting, new { key = "../x", value = "v" }),
            (PluginHostMethods.SetSetting, new { key = "theme\n", value = "v" }));

        Assert.All(replies, r => Assert.Equal(Protocol.ErrInvalidParams, ErrorCode(r)));
        Assert.Empty(_settings.Written);

        var max = new string('a', 64);
        var ok = await Call(
            (PluginHostMethods.SetSetting, new { key = "theme", value = "dark" }),
            (PluginHostMethods.SetSetting, new { key = max, value = "v" }));
        Assert.Equal("v", _settings.Written["plugin.com.example.a." + max]);
        Assert.Equal(JsonValueKind.Null, ok[0].GetProperty("result").ValueKind);
        Assert.Equal("dark", _settings.Written["plugin.com.example.a.theme"]);
    }

    [Fact]
    public async Task Progress_BeginReportComplete_FlowsToSink()
    {
        var replies = await Call(
            (PluginHostMethods.ProgressBegin, new { title = "Working" }),
            (PluginHostMethods.ProgressReport, new { operationId = "op-1", current = 3, total = 10, detail = "file" }),
            (PluginHostMethods.ProgressComplete, new { operationId = "op-1" }));

        Assert.Equal("op-1", replies[0].GetProperty("result").GetProperty("operationId").GetString());
        Assert.Equal(
            ["begin:com.example.a:Working", "report:op-1:3:10:file", "complete:op-1"],
            _sink.Events);
    }

    [Fact]
    public async Task Progress_UnknownOperation_IsInvalidParams()
    {
        var replies = await Call(
            (PluginHostMethods.ProgressReport, new { operationId = "nope", current = 1, total = 2 }),
            (PluginHostMethods.ProgressComplete, new { operationId = "nope" }),
            (PluginHostMethods.ProgressFail, new { operationId = "nope", message = "x" }));

        Assert.All(replies, r => Assert.Equal(Protocol.ErrInvalidParams, ErrorCode(r)));
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public async Task Log_WritesWithPluginId()
    {
        await Call((PluginHostMethods.Log, new { level = "warn", message = "careful" }));

        Assert.Equal([(PluginId, "[warn] careful")], _logs);
    }

    [Fact]
    public async Task Notify_ReachesNotifier()
    {
        await Call((PluginHostMethods.Notify, new { message = "done", isError = true }));

        Assert.Equal([$"{PluginId}:done:True"], _notifier.Events);
    }

    [Fact]
    public async Task Services_Settings_UsesPrefixedKey()
    {
        var services = NewServices((_, _) => throw new InvalidOperationException());

        await services.Settings.SetAsync("theme", "dark", CancellationToken.None);
        _settings.Values["plugin.com.example.a.other"] = "x";

        Assert.Equal("dark", _settings.Written["plugin.com.example.a.theme"]);
        Assert.Equal("x", await services.Settings.GetAsync("other", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => services.Settings.GetAsync("a.b", CancellationToken.None));
    }

    [Fact]
    public async Task Services_InvokeBackend_StartsBackendOnce()
    {
        var host = Host(async (s, ct) =>
        {
            await Handshake(s, ct);
            for (var i = 0; i < 2; i++)
            {
                var request = await s.ReadRequestAsync(ct);
                await s.SendResultAsync(request.Id, 42, ct);
            }

            await Task.Delay(Timeout.Infinite, ct);
        });
        var starts = 0;
        var services = NewServices((configure, ct) =>
        {
            Interlocked.Increment(ref starts);
            return host.StartAsync(Plugin(), ct, configure);
        });

        var results = await Task.WhenAll(
            services.InvokeBackendAsync<int>("echo", null, CancellationToken.None),
            services.InvokeBackendAsync<int>("echo", null, CancellationToken.None));

        Assert.Equal([42, 42], results);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Services_OnBackendEvent_ReceivesEventSentRightAfterHandshake()
    {
        var host = Host(async (s, ct) =>
        {
            await Handshake(s, ct);
            await s.SendNotificationAsync("tick", 5, ct);
            var request = await s.ReadRequestAsync(ct);
            await s.SendResultAsync(request.Id, 1, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var services = NewServices((configure, ct) => host.StartAsync(Plugin(), ct, configure));
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = services.OnBackendEvent<int>("tick", v => received.TrySetResult(v));

        await services.InvokeBackendAsync<int>("echo", null, CancellationToken.None);

        Assert.Equal(5, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Progress_Cancel_IsSentToBackend()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new TaskCompletionSource<JsonRpcRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Host(async (s, ct) =>
        {
            await Handshake(s, ct);
            await s.SendRequestAsync(1, PluginHostMethods.ProgressBegin, new { title = "T" }, ct);
            await s.ReadResponseAsync(ct);
            began.TrySetResult();
            var request = await s.ReadRequestAsync(ct);
            cancel.TrySetResult(request);
            await s.SendResultAsync(request.Id, null, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var connection = await host.StartAsync(
            Plugin(),
            CancellationToken.None,
            c => PluginHostMethods.Register(c, PluginId, _settings, _sink, _notifier, (_, _) => { }));
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _sink.CancelLast();

        var request = await cancel.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancel", request.Method);
        Assert.Equal("op-1", ((JsonElement)request.Params!).GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task Progress_BackendLost_FailsOpenOperations()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Host(async (s, ct) =>
        {
            await Handshake(s, ct);
            await s.SendRequestAsync(1, PluginHostMethods.ProgressBegin, new { title = "T" }, ct);
            await s.ReadResponseAsync(ct);
            began.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var connection = await host.StartAsync(
            Plugin(),
            CancellationToken.None,
            c => PluginHostMethods.Register(c, PluginId, _settings, _sink, _notifier, (_, _) => { }));
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _launchers.Last().Process!.SimulateExit();

        for (var i = 0; i < 100 && !_sink.Events.Contains("fail:op-1:Plugin backend stopped."); i++)
        {
            await Task.Delay(50);
        }

        Assert.Contains("fail:op-1:Plugin backend stopped.", _sink.Events);
    }

    [Fact]
    public async Task Progress_ConnectionDisposedIntentionally_FailsOpenOperations()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Host(async (s, ct) =>
        {
            await Handshake(s, ct);
            await s.SendRequestAsync(1, PluginHostMethods.ProgressBegin, new { title = "T" }, ct);
            await s.ReadResponseAsync(ct);
            began.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        var connection = await host.StartAsync(
            Plugin(),
            CancellationToken.None,
            c => PluginHostMethods.Register(c, PluginId, _settings, _sink, _notifier, (_, _) => { }));
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A disable or restart stops the backend on purpose: Lost stays quiet, but the row must still end.
        await connection.DisposeAsync();

        Assert.Contains("fail:op-1:Plugin backend stopped.", _sink.Events);
        Assert.Single(_sink.Events, e => e.StartsWith("fail:", StringComparison.Ordinal));
    }

    [Fact]
    public void Services_ProgressOperation_CancelFromSinkCancelsToken()
    {
        var services = NewServices((_, _) => throw new InvalidOperationException());

        using var operation = services.Progress.Begin("Copy");
        Assert.False(operation.CancellationToken.IsCancellationRequested);

        _sink.CancelLast();

        Assert.True(operation.CancellationToken.IsCancellationRequested);
    }

    private PluginHostServices NewServices(
        Func<Action<PluginBackendConnection>, CancellationToken, Task<PluginBackendConnection>> backend) =>
        new(PluginId, backend, _settings, _sink, null!, null!, null!, (_, _) => { });
}

internal sealed class FakeSettings : ISettingsBackend
{
    public Dictionary<string, string?> Values { get; } = [];
    public Dictionary<string, string> Written { get; } = [];
    public List<string> Requested { get; } = [];

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.GetValueOrDefault(key));

    public Task<IReadOnlyDictionary<string, string?>> GetSettingsAsync(
        IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        Requested.AddRange(keys);
        IReadOnlyDictionary<string, string?> result = keys
            .Where(Values.ContainsKey)
            .ToDictionary(k => k, k => Values[k]);
        return Task.FromResult(result);
    }

    public Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        Written[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingSink : IPluginProgressSink
{
    private Action? _cancel;
    private int _next;

    public List<string> Events { get; } = [];

    public string Begin(string pluginId, string title, Action cancel)
    {
        _cancel = cancel;
        Events.Add($"begin:{pluginId}:{title}");
        return $"op-{++_next}";
    }

    public void Report(string operationId, long current, long total, string? detail) =>
        Events.Add($"report:{operationId}:{current}:{total}:{detail}");

    public void Complete(string operationId) => Events.Add($"complete:{operationId}");

    public void Fail(string operationId, string message) => Events.Add($"fail:{operationId}:{message}");

    public void CancelLast() => _cancel!();
}

internal sealed class RecordingNotifier : IPluginNotifier
{
    public List<string> Events { get; } = [];

    public void Notify(string pluginId, string message, bool isError) =>
        Events.Add($"{pluginId}:{message}:{isError}");
}
