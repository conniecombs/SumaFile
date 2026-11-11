using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginSdk;
using SumaFile.Remote.Model;

namespace SimpleFile.Tests.Remote;

/// <summary>
/// The installed host's chain, minus WinUI: the staged <c>com.sumafile.remote</c> folder (copied by the test project's
/// <c>CopyRemotePluginFixture</c> target), trusted through its <c>bundled.json</c>, run by the real
/// <see cref="PluginManager"/> with the real <see cref="ProcessLauncher"/>, against a <c>sumafile-remote-test-server</c>
/// process. The plugin's entry type is replaced by <see cref="CapturingPlugin"/>, so <c>SumaFile.Remote.dll</c> (WinUI)
/// is never loaded; the tests talk to the backend through the captured <see cref="IPluginHost"/>, as the plugin's UI does.
/// </summary>
internal sealed class RemoteBackendHarness : IAsyncDisposable
{
    public const string PluginId = "com.sumafile.remote";
    public const string NotStagedMessage =
        "The remote plugin is not staged. Run scripts/build-fixture-plugin.ps1 and scripts/build-plugins.ps1.";

    private const string MetadataDbVariable = "SIMPLEFILE_METADATA_DB";
    private const string ManagerCommand = "ftp-sftp-manager";
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What the harness seeds in the server's root, so a listing has something to show.</summary>
    public static readonly IReadOnlyList<string> ServerFixtureNames = ["readme.txt", "docs"];

    private readonly string? _previousMetadataDb;
    private readonly RecordingLauncher _launcher = new();
    private readonly ConcurrentQueue<string> _log = new();
    private Process? _server;
    private JobObject? _serverJob;
    private PluginManager? _manager;
    private IPluginHost? _host;
    private RemoteBackendClient? _client;
    private TestServerInfo? _info;

    private RemoteBackendHarness(string temp)
    {
        Temp = temp;
        _previousMetadataDb = Environment.GetEnvironmentVariable(MetadataDbVariable);
    }

    public static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "remote");

    /// <summary>The plugins root the copy target fills; its <c>bundled.json</c> lists the remote plugin's hash.</summary>
    public static string PluginsRoot => Path.Combine(FixtureRoot, "plugins");

    public static string PluginFolder => Path.Combine(PluginsRoot, PluginId);

    public static string ServerExecutable => Path.Combine(FixtureRoot, "sumafile-remote-test-server.exe");

    public string Temp { get; }

    /// <summary>The folder the test server serves as <c>/</c>.</summary>
    public string ServerRoot => Path.Combine(Temp, "server");

    /// <summary>An empty local folder for uploads' sources and downloads' destinations.</summary>
    public string LocalFolder => Path.Combine(Temp, "local");

    public RemoteBackendClient Client => _client ?? throw new InvalidOperationException("The harness is not started.");

    /// <summary>The host the plugin was activated with: what <see cref="Client"/> calls through.</summary>
    public IPluginHost Host => _host ?? throw new InvalidOperationException("The harness is not started.");

    public TestServerInfo Server => _info ?? throw new InvalidOperationException("The harness is not started.");

    public RecordingProgressRows Rows { get; } = new();

    public FakeSettings Settings { get; } = new();

    public PluginManager Manager => _manager ?? throw new InvalidOperationException("The harness is not started.");

    /// <summary>Every backend process the manager launched, in launch order.</summary>
    public IReadOnlyList<Process> BackendProcesses => _launcher.Launched;

    /// <summary>Every line the manager logged (host and plugin), for failure messages.</summary>
    public IReadOnlyList<string> Log => _log.ToList();

    public static string SettingKey(string key) => PluginHostMethods.SettingsKey(PluginId, key);

    /// <summary>
    /// Starts the test server, points <c>SIMPLEFILE_METADATA_DB</c> at a disposable path (with
    /// <paramref name="legacyDatabase"/>, the 1.0.4 database the server wrote), discovers the staged plugin and runs
    /// its manager command once, which activates it. The backend starts on the first call, as installed.
    /// </summary>
    public static async Task<RemoteBackendHarness> StartAsync(bool legacyDatabase = false)
    {
        if (!File.Exists(Path.Combine(PluginFolder, "plugin.json"))
            || !File.Exists(Path.Combine(PluginFolder, "sumafile-remote-backend.exe"))
            || !File.Exists(Path.Combine(PluginsRoot, "bundled.json"))
            || !File.Exists(ServerExecutable))
        {
            throw new InvalidOperationException(NotStagedMessage);
        }

        var harness = new RemoteBackendHarness(
            Path.Combine(Path.GetTempPath(), "sumafile-tests", "remote-" + Guid.NewGuid().ToString("N")));
        try
        {
            await harness.InitializeAsync(legacyDatabase);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    /// <summary>A password SFTP profile for the test server whose fingerprint is already trusted.</summary>
    public RemoteProfileInput SftpProfile(string name) => new()
    {
        Name = name,
        Protocol = "sftp",
        Host = "127.0.0.1",
        Port = Server.Port,
        Username = Server.User,
        RootPath = "/",
        AuthKind = "password",
        TrustedHostFingerprint = Server.Fingerprint,
    };

    /// <summary>Saves <see cref="SftpProfile"/> without a secret and connects with the password in params.</summary>
    public async Task<RemoteSession> SaveAndConnectAsync()
    {
        var profile = await Client.SaveProfileAsync(SftpProfile("Test server"), null, CancellationToken.None);
        return await Client.ConnectAsync(profile.Id, Server.Password, CancellationToken.None);
    }

    public async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private async Task InitializeAsync(bool legacyDatabase)
    {
        Directory.CreateDirectory(ServerRoot);
        Directory.CreateDirectory(LocalFolder);
        File.WriteAllText(Path.Combine(ServerRoot, "readme.txt"), "served by the SFTP test server");
        Directory.CreateDirectory(Path.Combine(ServerRoot, "docs"));

        var info = Path.Combine(Temp, "info.json");
        List<string> args = ["--root", ServerRoot, "--info", info];
        string metadataDb;
        if (legacyDatabase)
        {
            metadataDb = Path.Combine(Temp, "legacy", "metadata.db");
            args.AddRange(["--legacy-db", metadataDb]);
        }
        else
        {
            metadataDb = Path.Combine(Temp, "none.db");
        }

        // The backend the manager launches inherits this, as an installed backend inherits the host's environment.
        Environment.SetEnvironmentVariable(MetadataDbVariable, metadataDb);
        StartServer(args);
        _info = await ReadServerInfoAsync(info);

        var pipeTag = Guid.NewGuid().ToString("N");
        _manager = new PluginManager(new PluginManagerOptions
        {
            Roots = [PluginsRoot],
            BundledRoot = PluginsRoot,
            ApprovalStorePath = Path.Combine(Temp, "plugin-approvals.json"),
            DataRoot = Path.Combine(Temp, "data"),
            Launcher = _launcher,
            PipeNameFor = id => $"SumaFile.Plugin.Test.{pipeTag}.{Guid.NewGuid():N}.{id}",
            Settings = Settings,
            Progress = Rows,
            Notifier = new RecordingNotifier(),
            DialogsFor = _ => null!,
            Workspace = null!,
            Log = (source, line) => _log.Enqueue($"[{source}] {line}"),
        });
        var plugin = new CapturingPlugin();
        _manager.InstanceFactory = _ => plugin;
        await _manager.DiscoverAsync(CancellationToken.None);
        var record = _manager.Plugins.SingleOrDefault(r => r.Id == PluginId)
            ?? throw new InvalidOperationException($"{PluginsRoot} holds no {PluginId}. {NotStagedMessage}");
        if (record.State != PluginState.Enabled)
        {
            throw new InvalidOperationException(
                $"The staged remote plugin is {record.State}, not Enabled through bundled.json: {record.Error}");
        }

        await _manager.RunCommandAsync(
            PluginManager.HostCommandId(PluginId, ManagerCommand),
            new PluginCommandContext(ManagerCommand, null, []),
            CancellationToken.None);
        _host = await plugin.Host.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _client = new RemoteBackendClient(_host);
    }

    private void StartServer(IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = ServerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // Kill-on-close: a crashed test run never leaves the server serving.
        _serverJob = JobObject.CreateKillOnCloseOnly();
        _server = new Process { StartInfo = start, EnableRaisingEvents = true };
        _server.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line) _log.Enqueue($"[test-server] {line}");
        };
        _server.Start();
        _serverJob.Assign(_server);
        _server.BeginErrorReadLine();
    }

    private async Task<TestServerInfo> ReadServerInfoAsync(string path)
    {
        // The server writes the file beside its final name and renames it, so it is complete once it exists.
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (_server!.HasExited)
            {
                throw new InvalidOperationException(
                    $"The SFTP test server exited with code {_server.ExitCode}: {string.Join(" | ", Log)}");
            }

            if (deadline.Elapsed > ServerStartTimeout)
            {
                throw new TimeoutException($"The SFTP test server wrote no {path} within {ServerStartTimeout}.");
            }

            await Task.Delay(50);
        }

        return JsonSerializer.Deserialize<TestServerInfo>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidOperationException($"{path} holds no server info.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_manager is { } manager)
        {
            try { await manager.DisposeAsync(); } catch { }
        }

        foreach (var process in _launcher.Launched)
        {
            try { process.Kill(true); } catch { }
            process.Dispose();
        }

        if (_server is { } server)
        {
            try { server.Kill(true); } catch { }
            try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            server.Dispose();
        }

        _serverJob?.Dispose();
        Environment.SetEnvironmentVariable(MetadataDbVariable, _previousMetadataDb);
        for (var attempt = 0; attempt < 10 && Directory.Exists(Temp); attempt++)
        {
            try { Directory.Delete(Temp, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    /// <summary>The <c>info.json</c> the test server writes (camelCase).</summary>
    public sealed class TestServerInfo
    {
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
        [JsonPropertyName("user")] public string User { get; set; } = "";
        [JsonPropertyName("password")] public string Password { get; set; } = "";
        [JsonPropertyName("root")] public string Root { get; set; } = "";
        [JsonPropertyName("legacyProfileId")] public string? LegacyProfileId { get; set; }
    }

    /// <summary>Captures the host it is activated with; the manager command is a no-op, so no WinUI code runs.</summary>
    private sealed class CapturingPlugin : IPlugin, ICommandHandler
    {
        public TaskCompletionSource<IPluginHost> Host { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ActivateAsync(IPluginHost host, CancellationToken ct)
        {
            Host.TrySetResult(host);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task ExecuteAsync(PluginCommandContext context, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// The real <see cref="ProcessLauncher"/> (kill-on-close job and all), plus a handle of its own on each process, so a
    /// test can wait for the exit after the manager disposed its handle.
    /// </summary>
    private sealed class RecordingLauncher : IPluginProcessLauncher
    {
        private readonly ProcessLauncher _inner = new();
        private readonly object _gate = new();
        private readonly List<Process> _launched = [];

        public IReadOnlyList<Process> Launched
        {
            get { lock (_gate) return _launched.ToList(); }
        }

        public IPluginProcess Launch(
            string executable,
            IReadOnlyList<string> args,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            Action<string> onStderrLine)
        {
            var process = _inner.Launch(executable, args, workingDirectory, environment, onStderrLine);
            try
            {
                var own = Process.GetProcessById(process.Id);
                lock (_gate) _launched.Add(own);
            }
            catch (ArgumentException)
            {
                // Already gone: nothing to wait for.
            }

            return process;
        }
    }
}

/// <summary>
/// A thread-safe <see cref="IPluginProgressSink"/> that keeps every row: its title, reports, end and cancel callback.
/// </summary>
internal sealed class RecordingProgressRows : IPluginProgressSink
{
    private readonly object _gate = new();
    private readonly List<Row> _rows = [];
    private readonly Dictionary<string, Row> _byId = new(StringComparer.Ordinal);
    private TaskCompletionSource _added = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _next;

    public IReadOnlyList<Row> All
    {
        get { lock (_gate) return _rows.ToList(); }
    }

    public async Task<Row> WaitForRowAsync(int index, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            Task added;
            lock (_gate)
            {
                if (_rows.Count > index) return _rows[index];
                added = _added.Task;
            }

            var left = timeout - deadline.Elapsed;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"No progress row {index} within {timeout}.");
            await added.WaitAsync(left);
        }
    }

    public string Begin(string pluginId, string title, Action cancel)
    {
        TaskCompletionSource added;
        Row row;
        lock (_gate)
        {
            row = new Row($"row-{++_next}", pluginId, title, cancel);
            _rows.Add(row);
            _byId[row.Id] = row;
            added = _added;
            _added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        added.TrySetResult();
        return row.Id;
    }

    public void Report(string operationId, long current, long total, string? detail) =>
        Find(operationId).AddReport(current, total, detail);

    public void Complete(string operationId) => Find(operationId).End(completed: true, null);

    public void Fail(string operationId, string message) => Find(operationId).End(completed: false, message);

    private Row Find(string operationId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(operationId, out var row)
                ? row
                : throw new InvalidOperationException($"Unknown progress row {operationId}.");
        }
    }

    internal sealed class Row(string id, string pluginId, string title, Action cancel)
    {
        private readonly object _gate = new();
        private readonly List<(long Current, long Total, string? Detail)> _reports = [];
        private readonly TaskCompletionSource _firstReport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ends;

        public string Id { get; } = id;
        public string PluginId { get; } = pluginId;
        public string Title { get; } = title;

        public IReadOnlyList<(long Current, long Total, string? Detail)> Reports
        {
            get { lock (_gate) return _reports.ToList(); }
        }

        public Task FirstReport => _firstReport.Task;
        public Task Ended => _ended.Task;
        public bool Completed { get; private set; }
        public string? FailMessage { get; private set; }

        /// <summary>How many times the row was completed or failed; a row ends once.</summary>
        public int Ends => Volatile.Read(ref _ends);

        /// <summary>The Transfers row's Cancel button.</summary>
        public void Cancel() => cancel();

        public string Describe() =>
            $"{Title}: completed={Completed}, failed={FailMessage ?? "<none>"}, reports={Reports.Count}";

        internal void AddReport(long current, long total, string? detail)
        {
            lock (_gate) _reports.Add((current, total, detail));
            _firstReport.TrySetResult();
        }

        internal void End(bool completed, string? message)
        {
            if (Interlocked.Increment(ref _ends) > 1) return;
            Completed = completed;
            FailMessage = message;
            _ended.TrySetResult();
        }
    }
}
