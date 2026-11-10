using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Contributions;
using SimpleFile.PluginSdk;
using SumaFile.Git.Model;
using Xunit;

namespace SimpleFile.Tests.Git;

/// <summary>
/// A test that needs the staged Git plugin. It skips with <see cref="GitBackendHarness.NotStagedMessage"/> when the
/// plugin is not staged (a machine without <c>scripts/build-plugins.ps1</c>'s output, such as a Linux build), except on
/// GitHub Actions, whose WinUI job stages it first: there the test runs and fails with that message.
/// </summary>
public sealed class GitPluginFactAttribute : FactAttribute
{
    public GitPluginFactAttribute()
    {
        if (!GitBackendHarness.IsStaged && !GitBackendHarness.StagingRequired)
        {
            Skip = GitBackendHarness.NotStagedMessage;
        }
    }
}

/// <summary>
/// The real host drives the real Git backend: the staged <c>com.sumafile.git</c> folder, trusted through its
/// <c>bundled.json</c>, launched by the real <see cref="PluginManager"/> and its <c>ProcessLauncher</c>, against a
/// repository the git CLI made. The harness sets process-wide git variables, so these tests run in the
/// <c>Plugin environment</c> collection, alone. Every wait is bounded.
/// </summary>
[Collection(PluginEnvironmentCollection.Name)]
public class GitPluginBackendTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GitWait = TimeSpan.FromSeconds(30);
    private const string UnicodeFolder = "café 資料";

    [GitPluginFact]
    public async Task GitPlugin_IsTrustedThroughBundledJsonAndStartsAtStartup()
    {
        await using var harness = await GitBackendHarness.StartAsync();

        // Enabled without a consent dialog: the install root's bundled.json lists it, and nothing is stored.
        Assert.Equal(PluginState.Enabled, harness.Record.State);
        Assert.True(harness.Record.IsBundledId);
        Assert.False(harness.Record.BackendRunning);
        Assert.Empty(harness.Launches);
        Assert.DoesNotContain(GitBackendHarness.PluginId, harness.ApprovalsText() ?? "", StringComparison.Ordinal);

        // activateOn: startup. The App starts it after its plugins-discovered span.
        harness.Manager.StartStartupBackends();

        Assert.True(
            await GitBackendHarness.WaitUntilAsync(() => harness.Record.BackendRunning, Wait),
            $"The Git backend did not start: {string.Join(" | ", harness.Log)}");
        var (executable, process) = Assert.Single(harness.Launches);
        Assert.Equal(
            Path.GetFullPath(GitBackendHarness.BackendExecutable),
            Path.GetFullPath(executable),
            StringComparer.OrdinalIgnoreCase);
        Assert.False(process.HasExited);
        Assert.DoesNotContain(GitBackendHarness.PluginId, harness.ApprovalsText() ?? "", StringComparison.Ordinal);
    }

    [GitPluginFact]
    public async Task Decorations_ThroughTheRealHost_MarkTheChangedChildren()
    {
        await using var harness = await GitBackendHarness.StartAsync();
        var repo = harness.Repo;
        repo.Write("tracked.txt", "one\n");
        repo.Write("staged.txt", "one\n");
        repo.Write("clean.txt", "one\n");
        repo.Write(UnicodeFolder + "/hello.txt", "one\n");
        repo.CommitAll("initial");
        repo.Write("tracked.txt", "two\n");
        repo.Write("new.txt", "new\n");
        repo.Write("staged.txt", "two\n");
        repo.Git("add", "--", "staged.txt");
        repo.Write(UnicodeFolder + "/hello.txt", "two\n");
        string[] topPaths =
        [
            repo.Path("clean.txt"), repo.Path("new.txt"), repo.Path("staged.txt"), repo.Path("tracked.txt"),
            repo.Path(UnicodeFolder),
        ];

        // The manager's own call: the provider's answer as the host receives it.
        var top = await harness.Manager.GetRowDecorationsAsync(
                GitBackendHarness.PluginId,
                new PluginDecorationRequest(GitBackendHarness.DecorationProvider, repo.Root, topPaths),
                CancellationToken.None)
            .WaitAsync(GitWait);

        Assert.NotNull(top);
        Assert.Equal([GitDecorationProvider.InRepositoryFlag], top.FolderFlags);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [repo.Path("new.txt")] = "untracked",
            [repo.Path("staged.txt")] = "staged",
            [repo.Path("tracked.txt")] = "modified",
        };
        // Keyed by native full paths in the folder's own spelling. Only direct children with a change of their own:
        // the committed folder whose file changed and the clean file are not decorated here.
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), top.Rows.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, status) in expected)
        {
            var row = top.Rows[path];
            Assert.Equal(status, row.Text);
            Assert.Equal(GitText.DecorationBrushKey(status), row.BrushKey);
            Assert.Equal("Git: " + status, row.Tooltip);
            Assert.False(path.Contains('/', StringComparison.Ordinal), path);
            Assert.False(Path.EndsInDirectorySeparator(path), path);
        }

        // The App's chain: the Core coordinator over PluginRowDecorationSource, on the Unicode folder and on the root.
        var source = new PluginRowDecorationSource(
            harness.Manager, PluginContributionAdapter.RowDecorationProviders(harness.Manager.EnabledPlugins));
        Assert.Equal([GitBackendHarness.PluginId], source.PluginIds);
        var coordinator = new RowDecorationCoordinator(GitWait);
        coordinator.SetSource(source);
        coordinator.MarkStartupReady();
        var hello = repo.Path(UnicodeFolder + "/hello.txt");

        var unicode = await coordinator.DecorateAsync(repo.Path(UnicodeFolder), false, [hello], _ => { }, CancellationToken.None)
            .WaitAsync(GitWait + Wait);

        Assert.Contains($"{GitBackendHarness.PluginId}:{GitDecorationProvider.InRepositoryFlag}", unicode.FolderFlags);
        var helloEntry = unicode.For(hello);
        Assert.NotNull(helloEntry);
        Assert.Equal(new RowDecorationEntry(GitBackendHarness.PluginId, "modified", GitText.DecorationBrushKey("modified"), "Git: modified"), helloEntry);

        var root = await coordinator.DecorateAsync(repo.Root, false, topPaths, _ => { }, CancellationToken.None)
            .WaitAsync(GitWait + Wait);

        Assert.Contains($"{GitBackendHarness.PluginId}:{GitDecorationProvider.InRepositoryFlag}", root.FolderFlags);
        Assert.Null(root.For(repo.Path("clean.txt")));
        Assert.Null(root.For(repo.Path(UnicodeFolder)));
        foreach (var (path, status) in expected)
        {
            Assert.Equal(status, root.For(path)?.Text);
        }
    }

    [GitPluginFact]
    public async Task Decorations_OutsideARepository_AreEmpty()
    {
        await using var harness = await GitBackendHarness.StartAsync();
        var plain = Directory.CreateDirectory(Path.Combine(harness.Temp, "plain")).FullName;
        var file = Path.Combine(plain, "file.txt");
        File.WriteAllText(file, "not in a repository\n");

        var decorations = await harness.Manager.GetRowDecorationsAsync(
                GitBackendHarness.PluginId,
                new PluginDecorationRequest(GitBackendHarness.DecorationProvider, plain, [file]),
                CancellationToken.None)
            .WaitAsync(GitWait);

        Assert.NotNull(decorations);
        Assert.Empty(decorations.Rows);
        Assert.Empty(decorations.FolderFlags);

        var coordinator = new RowDecorationCoordinator(GitWait);
        coordinator.SetSource(new PluginRowDecorationSource(
            harness.Manager, PluginContributionAdapter.RowDecorationProviders(harness.Manager.EnabledPlugins)));
        coordinator.MarkStartupReady();
        var pane = await coordinator.DecorateAsync(plain, false, [file], _ => { }, CancellationToken.None)
            .WaitAsync(GitWait + Wait);

        Assert.Empty(pane.FolderFlags);
        Assert.Null(pane.For(file));
    }

    [GitPluginFact]
    public async Task StageCommitAndDiff_ThroughTheRealHost_ChangeTheRepository()
    {
        await using var harness = await GitBackendHarness.StartAsync();
        var repo = harness.Repo;
        repo.Write("a.txt", "one\n");
        repo.CommitAll("initial");
        repo.Write("a.txt", "two\n");
        repo.Write("b.txt", "new\n");
        await harness.ActivateAsync();
        var client = harness.Client;

        var status = await client.StatusAsync(repo.Root, CancellationToken.None).WaitAsync(GitWait);
        Assert.True(status.IsRepo);
        Assert.Equal("main", status.Branch);
        Assert.Equal(1, status.Unstaged);
        Assert.Equal(1, status.Untracked);

        var staged = await client.StageAsync(repo.Root, [repo.Path("a.txt"), repo.Path("b.txt")], CancellationToken.None)
            .WaitAsync(GitWait);
        Assert.Equal(0, staged.ExitCode);
        Assert.Equal(["a.txt", "b.txt"], repo.GitLines("diff", "--cached", "--name-only").Order(StringComparer.Ordinal));

        var diff = await client.DiffAsync(repo.Root, repo.Path("a.txt"), CancellationToken.None).WaitAsync(GitWait);
        Assert.StartsWith("Staged changes", diff, StringComparison.Ordinal);
        Assert.Contains("+two", diff, StringComparison.Ordinal);

        var unstaged = await client.UnstageAsync(repo.Root, [repo.Path("b.txt")], CancellationToken.None).WaitAsync(GitWait);
        Assert.Equal(0, unstaged.ExitCode);
        Assert.Equal(["a.txt"], repo.GitLines("diff", "--cached", "--name-only"));

        var committed = await client.CommitAsync(repo.Root, "second", CancellationToken.None).WaitAsync(GitWait);
        Assert.Equal(0, committed.ExitCode);
        Assert.Equal(["second"], repo.GitLines("log", "-1", "--format=%s"));
        Assert.Equal(["?? b.txt"], repo.GitLines("status", "--porcelain"));
        Assert.Equal("two\n", repo.Git("show", "HEAD:a.txt"));
        // A commit opens no Transfers row.
        Assert.Empty(harness.Rows.All);
    }

    [GitPluginFact]
    public async Task FetchCancelledFromItsRow_StopsGitAndFailsTheRow()
    {
        await using var harness = await GitBackendHarness.StartAsync();
        var repo = harness.Repo;
        repo.Write("a.txt", "one\n");
        repo.CommitAll("initial");
        // A remote that accepts the connection and never answers, so git fetch hangs until it is stopped.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverStop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(serverStop.Token);
            connected.TrySetResult();
            var stream = socket.GetStream();
            var buffer = new byte[4096];
            // Ends when git closes its side: it was stopped.
            while (await stream.ReadAsync(buffer, serverStop.Token) > 0)
            {
            }
        });
        repo.Git("remote", "add", "origin", $"http://127.0.0.1:{port}/repo.git");
        await harness.ActivateAsync();

        var fetch = harness.Client.FetchAsync(repo.Root, CancellationToken.None);
        var row = await harness.Rows.WaitForRowAsync(0, GitWait);
        Assert.Equal("Fetching repo", row.Title);
        Assert.Equal(GitBackendHarness.PluginId, row.PluginId);
        await connected.Task.WaitAsync(GitWait);
        Assert.False(fetch.IsCompleted);
        var cancelled = Stopwatch.StartNew();

        // The Transfers row's Cancel: the host sends the row's id, not the request's operation id.
        row.Cancel();

        await row.Ended.WaitAsync(Wait);
        Assert.Equal(GitErrors.Cancelled, row.FailMessage);
        Assert.True(cancelled.Elapsed < Wait, $"The row failed {cancelled.Elapsed} after its cancel.");
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => fetch.WaitAsync(Wait));
        Assert.IsNotType<TimeoutException>(failure);
        Assert.Equal(GitErrors.Cancelled, failure.Message);
        // git and its remote helper are gone: their connection to the remote is closed.
        await server.WaitAsync(Wait);
        Assert.Equal(1, row.Ends);
    }

    [GitPluginFact]
    public async Task DisablingThePlugin_StopsTheBackendProcess()
    {
        await using var harness = await GitBackendHarness.StartAsync();
        harness.Repo.Write("a.txt", "one\n");
        harness.Repo.CommitAll("initial");
        await harness.ActivateAsync();
        Assert.True((await harness.Client.StatusAsync(harness.Repo.Root, CancellationToken.None).WaitAsync(GitWait)).IsRepo);
        var backend = Assert.Single(harness.BackendProcesses);
        Assert.True(harness.Record.BackendRunning);

        await harness.Manager.DisableAsync(GitBackendHarness.PluginId, CancellationToken.None).WaitAsync(Wait);

        Assert.True(await GitBackendHarness.WaitForExitAsync(backend, TimeSpan.FromSeconds(5)), "The Git backend process is still running.");
        Assert.Equal(PluginState.Disabled, harness.Record.State);
        Assert.False(harness.Record.BackendRunning);
        Assert.DoesNotContain(harness.Manager.EnabledPlugins, record => record.Id == GitBackendHarness.PluginId);
    }
}
