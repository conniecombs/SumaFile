using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginSdk;
using SimpleFile.Tests.Remote;
using SumaFile.Git.Model;

namespace SimpleFile.Tests.Git;

/// <summary>
/// The installed host's chain, minus WinUI: the staged <c>com.sumafile.git</c> folder (copied by the test project's
/// <c>CopyGitPluginFixture</c> target), trusted through its <c>bundled.json</c>, run by the real
/// <see cref="PluginManager"/> with the real <see cref="ProcessLauncher"/>, against a <see cref="TestRepo"/> the git CLI
/// made. The plugin's entry type is replaced by <see cref="CapturingGitPlugin"/>, which delegates its decorations to
/// <see cref="GitDecorationProvider"/> over the captured host, so <c>SumaFile.Git.dll</c> (WinUI) is never loaded; the
/// tests talk to the backend through that host, as the plugin's UI does.
/// </summary>
/// <remarks>
/// Git is isolated from the developer's config: the harness sets <c>GIT_CONFIG_GLOBAL</c> (an empty file in its temp
/// folder), <c>GIT_CONFIG_NOSYSTEM=1</c> and <c>GIT_CEILING_DIRECTORIES</c> (its temp folder) for the test process,
/// which the launched backend inherits, and loopback in <c>NO_PROXY</c>; dispose puts the previous values back. The
/// variables are process-wide, so the tests run in the <c>Plugin environment</c> collection.
/// </remarks>
internal sealed class GitBackendHarness : IAsyncDisposable
{
    public const string PluginId = "com.sumafile.git";
    public const string DecorationProvider = "status";
    public const string BackendExecutableName = "sumafile-git-backend.exe";
    public const string NotStagedMessage = "The Git plugin is not staged. Run scripts/build-plugins.ps1.";

    private static readonly string[] IsolationVariables =
        ["GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_CEILING_DIRECTORIES", "NO_PROXY", "no_proxy"];

    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);
    private readonly RecordingLauncher _launcher = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly CapturingGitPlugin _plugin = new();
    private PluginManager? _manager;
    private GitBackendClient? _client;

    private GitBackendHarness(string temp)
    {
        Temp = temp;
    }

    public static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "git");

    /// <summary>The plugins root the copy target fills; its <c>bundled.json</c> lists the Git plugin's hash.</summary>
    public static string PluginsRoot => Path.Combine(FixtureRoot, "plugins");

    public static string PluginFolder => Path.Combine(PluginsRoot, PluginId);

    public static string BackendExecutable => Path.Combine(PluginFolder, BackendExecutableName);

    /// <summary>Whether <c>scripts/build-plugins.ps1</c> staged the plugin and the test build copied it here.</summary>
    public static bool IsStaged =>
        File.Exists(Path.Combine(PluginFolder, "plugin.json"))
        && File.Exists(BackendExecutable)
        && File.Exists(Path.Combine(PluginsRoot, "bundled.json"));

    /// <summary>
    /// True on GitHub Actions, whose WinUI job stages the plugin before the tests: there a missing plugin fails the
    /// tests with <see cref="NotStagedMessage"/> instead of skipping them.
    /// </summary>
    public static bool StagingRequired =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    public string Temp { get; }

    public string ApprovalStorePath => Path.Combine(Temp, "plugin-approvals.json");

    public string GitConfigPath => Path.Combine(Temp, "gitconfig");

    /// <summary>The work tree under test: <c>&lt;temp&gt;\repo</c>, on <c>main</c>, with its own identity.</summary>
    public TestRepo Repo { get; private set; } = null!;

    public RecordingProgressRows Rows { get; } = new();

    public FakeSettings Settings { get; } = new();

    public PluginManager Manager => _manager ?? throw new InvalidOperationException("The harness is not started.");

    public PluginRecord Record => Manager.Plugins.Single(record => record.Id == PluginId);

    /// <summary>The host the plugin was activated with; set by <see cref="ActivateAsync"/>.</summary>
    public IPluginHost Host => _plugin.Host.Task.IsCompletedSuccessfully
        ? _plugin.Host.Task.Result
        : throw new InvalidOperationException("The plugin is not activated. Call ActivateAsync first.");

    /// <summary>The plugin UI's own client, over <see cref="Host"/>; set by <see cref="ActivateAsync"/>.</summary>
    public GitBackendClient Client => _client ?? throw new InvalidOperationException("The plugin is not activated. Call ActivateAsync first.");

    /// <summary>Every backend launch, in launch order: the executable the manager asked for and its process.</summary>
    public IReadOnlyList<(string Executable, Process Process)> Launches => _launcher.Launched;

    public IReadOnlyList<Process> BackendProcesses => _launcher.Launched.Select(launch => launch.Process).ToList();

    /// <summary>Every line the manager logged (host and plugin), for failure messages.</summary>
    public IReadOnlyList<string> Log => _log.ToList();

    /// <summary>
    /// Isolates git, makes the test repository and discovers the staged plugin without starting its backend
    /// (<c>startBackends: false</c>, as the App does before <see cref="PluginManager.StartStartupBackends"/>). The
    /// plugin must come out Enabled through <c>bundled.json</c>.
    /// </summary>
    public static async Task<GitBackendHarness> StartAsync()
    {
        if (!IsStaged)
        {
            throw new InvalidOperationException(NotStagedMessage);
        }

        var harness = new GitBackendHarness(
            Path.Combine(Path.GetTempPath(), "sumafile-tests", "git-" + Guid.NewGuid().ToString("N")));
        try
        {
            await harness.InitializeAsync();
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Activates the plugin the way the host first does, through one row decoration request for the repository
    /// root, and returns the host it captured. The backend starts on that first call if it is not running yet.
    /// </summary>
    public async Task<IPluginHost> ActivateAsync()
    {
        if (_client is null)
        {
            await Manager.GetRowDecorationsAsync(
                    PluginId, new PluginDecorationRequest(DecorationProvider, Repo.Root, []), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            var host = await _plugin.Host.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _client = new GitBackendClient(host);
        }

        return Host;
    }

    /// <summary>The approvals file's text, or null when the manager never wrote one.</summary>
    public string? ApprovalsText() => File.Exists(ApprovalStorePath) ? File.ReadAllText(ApprovalStorePath) : null;

    public static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
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

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > timeout)
            {
                return condition();
            }

            await Task.Delay(50);
        }

        return true;
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(Temp);
        File.WriteAllText(GitConfigPath, "");
        // The backend the manager launches inherits these, as an installed backend inherits the host's environment.
        foreach (var name in IsolationVariables)
        {
            _previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", GitConfigPath);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", Temp);
        foreach (var name in new[] { "NO_PROXY", "no_proxy" })
        {
            var previous = _previousEnvironment[name];
            Environment.SetEnvironmentVariable(
                name, string.IsNullOrEmpty(previous) ? "127.0.0.1,localhost" : previous + ",127.0.0.1,localhost");
        }

        Repo = TestRepo.Create(Path.Combine(Temp, "repo"), GitConfigPath, Temp);

        var pipeTag = Guid.NewGuid().ToString("N");
        _manager = new PluginManager(new PluginManagerOptions
        {
            Roots = [PluginsRoot],
            BundledRoot = PluginsRoot,
            ApprovalStorePath = ApprovalStorePath,
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
        _manager.InstanceFactory = _ => _plugin;
        await _manager.DiscoverAsync(null, startBackends: false, CancellationToken.None);
        var record = _manager.Plugins.SingleOrDefault(r => r.Id == PluginId)
            ?? throw new InvalidOperationException($"{PluginsRoot} holds no {PluginId}. {NotStagedMessage}");
        if (record.State != PluginState.Enabled)
        {
            throw new InvalidOperationException(
                $"The staged Git plugin is {record.State}, not Enabled through bundled.json: {record.Error}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_manager is { } manager)
        {
            try { await manager.DisposeAsync(); } catch { }
        }

        foreach (var (_, process) in _launcher.Launched)
        {
            try { process.Kill(true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            process.Dispose();
        }

        foreach (var (name, value) in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        await TestRepo.DeleteTreeAsync(Temp);
    }

    /// <summary>
    /// Captures the host it is activated with and answers row decorations with the plugin's own
    /// <see cref="GitDecorationProvider"/>, as <c>SumaFile.Git.GitPlugin</c> does; no WinUI code runs.
    /// </summary>
    private sealed class CapturingGitPlugin : IPlugin, IRowDecorationProvider
    {
        private GitDecorationProvider? _decorations;

        public TaskCompletionSource<IPluginHost> Host { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ActivateAsync(IPluginHost host, CancellationToken ct)
        {
            _decorations = new GitDecorationProvider(new GitBackendClient(host), host.Settings, host.Panels, host.Logger);
            Host.TrySetResult(host);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct) =>
            _decorations is { } decorations ? decorations.GetAsync(request, ct) : Task.FromResult(PluginFolderDecorations.Empty);
    }

    /// <summary>
    /// The real <see cref="ProcessLauncher"/> (kill-on-close job and all), plus the executable it was asked to start
    /// and a handle of its own on each process, so a test can wait for the exit after the manager disposed its handle.
    /// </summary>
    private sealed class RecordingLauncher : IPluginProcessLauncher
    {
        private readonly ProcessLauncher _inner = new();
        private readonly object _gate = new();
        private readonly List<(string Executable, Process Process)> _launched = [];

        public IReadOnlyList<(string Executable, Process Process)> Launched
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
                lock (_gate) _launched.Add((executable, own));
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
/// A real-git repository for the Git plugin's host tests, made with the git CLI. Every git call runs with
/// <c>GIT_CONFIG_GLOBAL</c> (an empty file), <c>GIT_CONFIG_NOSYSTEM=1</c> and <c>GIT_CEILING_DIRECTORIES</c>, so a global
/// <c>core.hooksPath</c>, <c>commit.gpgsign</c> or <c>init.defaultBranch</c> can never run, prompt or change a result.
/// The repository is made with <c>git init -b main</c> and gets its identity in its own config.
/// </summary>
internal sealed class TestRepo
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    private readonly string _gitConfig;
    private readonly string _ceiling;

    private TestRepo(string root, string gitConfig, string ceiling)
    {
        Root = root;
        _gitConfig = gitConfig;
        _ceiling = ceiling;
    }

    /// <summary>The work tree, in native separators.</summary>
    public string Root { get; }

    public static TestRepo Create(string root, string gitConfig, string ceiling)
    {
        Directory.CreateDirectory(root);
        var repo = new TestRepo(System.IO.Path.GetFullPath(root), gitConfig, ceiling);
        repo.Git("init", "-b", "main");
        repo.Git("config", "user.name", "SumaFile Test");
        repo.Git("config", "user.email", "test@sumafile.invalid");
        return repo;
    }

    /// <summary><see cref="Root"/> joined with <paramref name="relative"/> (with <c>/</c> separators), in native separators.</summary>
    public string Path(string relative) =>
        relative.Split('/').Aggregate(Root, System.IO.Path.Combine);

    /// <summary>Writes <paramref name="relative"/> under the work tree and returns its native full path.</summary>
    public string Write(string relative, string contents)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "-m", message);
    }

    /// <summary>Runs git in the work tree and returns its stdout; throws with git's stderr on failure.</summary>
    public string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-c", "core.autocrlf=false", "-c", "core.quotepath=false", "-C", Root }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["GIT_CONFIG_GLOBAL"] = _gitConfig;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CEILING_DIRECTORIES"] = _ceiling;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"git is not on PATH: {exception.Message}", exception);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout))
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException($"git {string.Join(' ', args)} did not finish within {GitTimeout}.");
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', args)} in {Root} failed with {process.ExitCode}: {stderr.Result}");
            }

            return stdout.Result;
        }
    }

    /// <summary>The lines of a git command's output, without empty ones.</summary>
    public IReadOnlyList<string> GitLines(params string[] args) =>
        Git(args).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Deletes <paramref name="path"/> and everything under it, read-only git objects included.</summary>
    public static async Task DeleteTreeAsync(string path)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, true);
            }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}
