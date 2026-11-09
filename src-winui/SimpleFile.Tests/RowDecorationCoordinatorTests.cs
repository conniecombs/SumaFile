using System.Collections.Concurrent;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class RowDecorationCoordinatorTests
{
    // Tests that need the budget to pass first use ShortBudget with a source that is still pending. Tests whose sources
    // answer at once use AmpleBudget: each source call starts through Task.Run, and on a loaded CI runner the thread pool
    // can take longer than 50 ms to start it, which made an instant answer look late (CI run 42713361860).
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan AmpleBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DecorateAsync_BeforeStartupReady_CallsNoSource()
    {
        var source = new FakeRowDecorationSource("com.a");
        var coordinator = new RowDecorationCoordinator(ShortBudget);
        coordinator.SetSource(source);

        var result = await coordinator.DecorateAsync(@"R:\repo", false, [@"R:\repo\a.txt"], _ => { }, CancellationToken.None);

        Assert.Same(PaneDecorations.Empty, result);
        Assert.Empty(source.Calls);
        Assert.False(coordinator.IsStartupReady);
        Assert.Null(coordinator.FirstRequestAt);
    }

    [Fact]
    public async Task DecorateAsync_FastSources_AreMergedBeforeThePublish()
    {
        var source = new FakeRowDecorationSource("com.a", "com.b");
        source.Results["com.a"] = FakeRowDecorationSource.Batch("com.a", [(@"R:\repo\a.txt", "M")], "com.a:inRepository");
        source.Results["com.b"] = FakeRowDecorationSource.Batch("com.b", [(@"R:\repo\b.txt", "tag")], "com.b:tagged");
        var coordinator = Ready(source);
        var late = 0;

        var result = await coordinator.DecorateAsync(
            @"R:\repo",
            false,
            [@"R:\repo\a.txt", @"R:\repo\b.txt"],
            _ => Interlocked.Increment(ref late),
            CancellationToken.None).WaitAsync(Watchdog);

        Assert.Equal(@"R:\repo", result.FolderPath);
        Assert.Equal("M", result.For(@"R:\repo\a.txt")?.Text);
        Assert.Equal("tag", result.For(@"R:\repo\b.txt")?.Text);
        Assert.Equal(new HashSet<string> { "com.a:inRepository", "com.b:tagged" }, result.FolderFlags.ToHashSet());
        Assert.NotNull(coordinator.FirstRequestAt);
        Assert.Equal(0, Volatile.Read(ref late));
    }

    [Fact]
    public async Task DecorateAsync_SlowSource_PublishesAfterTheBudgetAndAppliesTheLateResult()
    {
        var source = new FakeRowDecorationSource("com.a", "com.slow");
        source.Results["com.a"] = FakeRowDecorationSource.Batch("com.a", [(@"R:\repo\a.txt", "M")]);
        var slow = new TaskCompletionSource<FolderDecorationBatch?>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (pluginId, _, _, _) => pluginId == "com.slow" ? slow.Task : null;
        var coordinator = Ready(source, ShortBudget);
        var lates = new ConcurrentQueue<PaneDecorations>();

        var result = await coordinator.DecorateAsync(
            @"R:\repo",
            false,
            [@"R:\repo\a.txt", @"R:\repo\b.txt"],
            lates.Enqueue,
            CancellationToken.None).WaitAsync(Watchdog);

        Assert.False(slow.Task.IsCompleted);
        Assert.Equal("M", result.For(@"R:\repo\a.txt")?.Text);
        Assert.Null(result.For(@"R:\repo\b.txt"));
        Assert.Empty(lates);

        slow.SetResult(FakeRowDecorationSource.Batch("com.slow", [(@"R:\repo\b.txt", "slow")], "com.slow:seen"));
        await WaitUntilAsync(() => !lates.IsEmpty);
        await Task.Delay(100);

        var late = Assert.Single(lates);
        Assert.Equal("M", late.For(@"R:\repo\a.txt")?.Text);
        Assert.Equal("slow", late.For(@"R:\repo\b.txt")?.Text);
        Assert.Contains("com.slow:seen", late.FolderFlags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecorateAsync_LateSourcesThatAnswerNothing_StillDeliverTheFinalMerge(bool throws)
    {
        var source = new FakeRowDecorationSource("com.a", "com.slow");
        source.Results["com.a"] = FakeRowDecorationSource.Batch("com.a", [(@"R:\repo\a.txt", "M")], "com.a:inRepository");
        var slow = new TaskCompletionSource<FolderDecorationBatch?>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (pluginId, _, _, _) => pluginId == "com.slow" ? slow.Task : null;
        var coordinator = Ready(source, ShortBudget);
        var lates = new ConcurrentQueue<PaneDecorations>();

        var result = await coordinator.DecorateAsync(
            @"R:\repo",
            false,
            [@"R:\repo\a.txt", @"R:\repo\b.txt"],
            lates.Enqueue,
            CancellationToken.None).WaitAsync(Watchdog);
        Assert.Equal("M", result.For(@"R:\repo\a.txt")?.Text);
        Assert.Empty(lates);

        if (throws)
        {
            slow.SetException(new InvalidOperationException("git status failed"));
        }
        else
        {
            slow.SetResult(null);
        }

        // The slow plugin has nothing for the folder: the request still converges on a final merge, so a caller that
        // kept older decorations while waiting can drop them.
        await WaitUntilAsync(() => !lates.IsEmpty);
        await Task.Delay(100);
        var late = Assert.Single(lates);
        Assert.Equal("M", late.For(@"R:\repo\a.txt")?.Text);
        Assert.Null(late.For(@"R:\repo\b.txt"));
        Assert.Equal(new HashSet<string> { "com.a:inRepository" }, late.FolderFlags.ToHashSet());
    }

    [Fact]
    public async Task DecorateAsync_CancelledRequest_GetsNoLateMerge()
    {
        using var cancel = new CancellationTokenSource();
        var source = new FakeRowDecorationSource("com.a", "com.slow");
        var slow = new TaskCompletionSource<FolderDecorationBatch?>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (pluginId, _, _, _) => pluginId == "com.slow" ? slow.Task : null;
        var coordinator = Ready(source, ShortBudget);
        var lates = new ConcurrentQueue<PaneDecorations>();

        await coordinator.DecorateAsync(@"R:\repo", false, [@"R:\repo\a.txt"], lates.Enqueue, cancel.Token)
            .WaitAsync(Watchdog);
        await cancel.CancelAsync();
        slow.SetResult(FakeRowDecorationSource.Batch("com.slow", [(@"R:\repo\a.txt", "late")]));
        await Task.Delay(150);

        Assert.Empty(lates);
    }

    [Fact]
    public async Task DecorateAsync_NetworkFolderAndRecycleBin_CallNoSource()
    {
        var source = new FakeRowDecorationSource("com.a");
        var coordinator = Ready(source);

        var network = await coordinator.DecorateAsync(@"\\server\share", true, [@"\\server\share\a"], _ => { }, CancellationToken.None);
        var recycleBin = await coordinator.DecorateAsync(PathRules.RecycleBinPath, false, [], _ => { }, CancellationToken.None);

        Assert.Same(PaneDecorations.Empty, network);
        Assert.Same(PaneDecorations.Empty, recycleBin);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task DecorateAsync_FirstPluginInOrderWinsTheRowText_AndEveryPluginCountsForHasDecoration()
    {
        var source = new FakeRowDecorationSource("com.a", "com.b");
        source.Results["com.a"] = FakeRowDecorationSource.Batch("com.a", [(@"R:\repo\a.txt", "from a")]);
        source.Results["com.b"] = FakeRowDecorationSource.Batch("com.b", [(@"R:\repo\a.txt", "from b"), (@"R:\repo\b.txt", "only b")]);
        var coordinator = Ready(source);

        var result = await coordinator.DecorateAsync(
            @"R:\repo",
            false,
            [@"R:\repo\a.txt", @"R:\repo\b.txt", @"R:\repo\c.txt"],
            _ => { },
            CancellationToken.None).WaitAsync(Watchdog);

        var first = result.For(@"R:\repo\a.txt");
        Assert.Equal("from a", first?.Text);
        Assert.Equal("com.a", first?.PluginId);
        Assert.Equal("only b", result.For(@"R:\repo\b.txt")?.Text);
        Assert.Equal(new HashSet<string> { "com.a", "com.b" }, result.PluginsFor([@"R:\repo\a.txt"]).ToHashSet());
        Assert.Equal(new HashSet<string> { "com.b" }, result.PluginsFor([@"R:\repo\b.txt", @"R:\repo\c.txt"]).ToHashSet());
        Assert.Empty(result.PluginsFor([@"R:\repo\c.txt"]));
    }

    [Fact]
    public async Task DecorateAsync_FailedSource_IsSkippedAndLogged()
    {
        var source = new FakeRowDecorationSource("com.a", "com.bad", "com.null");
        source.Results["com.a"] = FakeRowDecorationSource.Batch("com.a", [(@"R:\repo\a.txt", "M")]);
        source.Handler = (pluginId, _, _, _) => pluginId switch
        {
            "com.bad" => Task.FromException<FolderDecorationBatch?>(new InvalidOperationException("boom")),
            "com.null" => Task.FromResult<FolderDecorationBatch?>(null),
            _ => null,
        };
        var coordinator = Ready(source);
        var log = new ConcurrentQueue<string>();
        coordinator.Log = log.Enqueue;

        var result = await coordinator.DecorateAsync(@"R:\repo", false, [@"R:\repo\a.txt"], _ => { }, CancellationToken.None)
            .WaitAsync(Watchdog);

        Assert.Equal("M", result.For(@"R:\repo\a.txt")?.Text);
        Assert.Contains("[warn] Row decorations from com.bad failed: boom", log);
        Assert.Contains(log, line => line.StartsWith("[warn] Row decorations from com.null failed: ", StringComparison.Ordinal));
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public async Task DecorateAsync_KeysDifferingInCaseOrSeparator_MatchTheListedRow()
    {
        var source = new FakeRowDecorationSource("com.git");
        source.Results["com.git"] = FakeRowDecorationSource.Batch(
            "com.git",
            [("R:/repo/Dir/A.TXT", "M"), (@"R:\repo\untracked\", "?")]);
        var coordinator = Ready(source);

        var result = await coordinator.DecorateAsync(
            @"R:\repo",
            false,
            [@"R:\repo\dir\a.txt", @"R:\repo\untracked"],
            _ => { },
            CancellationToken.None).WaitAsync(Watchdog);

        Assert.Equal("M", result.For(@"R:\repo\dir\a.txt")?.Text);
        Assert.Equal("?", result.For(@"R:\repo\untracked")?.Text);
        Assert.Equal(["com.git"], result.PluginsFor([@"r:\REPO\untracked"]));

        Assert.Equal(@"R:\repo\untracked", RowDecorationKeys.Normalize("R:/repo/untracked/"));
        Assert.Equal(@"R:\", RowDecorationKeys.Normalize("R:/"));
        Assert.Equal(@"R:\", RowDecorationKeys.Normalize(@"R:\"));
        Assert.True(RowDecorationKeys.Comparer.Equals("R:/repo/Dir/A.TXT", @"r:\repo\dir\a.txt\"));
        Assert.Equal(
            RowDecorationKeys.Comparer.GetHashCode("R:/repo/Dir/A.TXT"),
            RowDecorationKeys.Comparer.GetHashCode(@"r:\repo\dir\a.txt"));
    }

    [Fact]
    public async Task DecorateAsync_InvalidFolderFlag_IsDropped()
    {
        var source = new FakeRowDecorationSource("com.a");
        source.Results["com.a"] = FakeRowDecorationSource.Batch(
            "com.a",
            [],
            "com.a:inRepository",
            "com.a:has space",
            "com.a:9starts-with-digit",
            "com.a:",
            "com.other:inRepository",
            "inRepository",
            "com.a:" + new string('x', 65),
            "com.a:ok_flag-2");
        var coordinator = Ready(source);

        var result = await coordinator.DecorateAsync(@"R:\repo", false, [], _ => { }, CancellationToken.None)
            .WaitAsync(Watchdog);

        Assert.Equal(new HashSet<string> { "com.a:inRepository", "com.a:ok_flag-2" }, result.FolderFlags.ToHashSet());
    }

    [Fact]
    public void SetSource_ReportsWhetherThePluginSetChanged()
    {
        var coordinator = new RowDecorationCoordinator(ShortBudget);
        Assert.False(coordinator.HasProviders);

        Assert.True(coordinator.SetSource(new FakeRowDecorationSource("com.a")));
        Assert.True(coordinator.HasProviders);
        Assert.False(coordinator.SetSource(new FakeRowDecorationSource("com.a")));
        Assert.True(coordinator.SetSource(new FakeRowDecorationSource("com.a", "com.b")));
        Assert.True(coordinator.SetSource(new FakeRowDecorationSource()));
        Assert.False(coordinator.HasProviders);
        Assert.False(coordinator.SetSource(null));
        Assert.False(coordinator.HasProviders);
    }

    [Fact]
    public void MarkStartupReady_AndAChangedSourceSet_BumpTheGeneration()
    {
        var coordinator = new RowDecorationCoordinator(ShortBudget);
        var start = coordinator.Generation;

        coordinator.MarkStartupReady();
        Assert.True(coordinator.IsStartupReady);
        Assert.Equal(start + 1, coordinator.Generation);

        coordinator.SetSource(new FakeRowDecorationSource("com.a"));
        Assert.Equal(start + 2, coordinator.Generation);

        coordinator.SetSource(new FakeRowDecorationSource("com.a"));
        Assert.Equal(start + 2, coordinator.Generation);

        coordinator.SetSource(null);
        Assert.Equal(start + 3, coordinator.Generation);
    }

    [Fact]
    public async Task DecorateAsync_NeverRunsTheSourceOnTheCallingThread()
    {
        using var release = new ManualResetEventSlim(false);
        var sourceThread = -1;
        var source = new FakeRowDecorationSource("com.blocking")
        {
            Handler = (_, _, _, _) =>
            {
                Volatile.Write(ref sourceThread, Environment.CurrentManagedThreadId);
                release.Wait(TimeSpan.FromSeconds(10));
                return Task.FromResult<FolderDecorationBatch?>(null);
            },
        };
        var coordinator = Ready(source, ShortBudget);

        try
        {
            // The caller is a dedicated thread, so a source that ran inline would block it and the watchdog would see
            // no returned task.
            Task<PaneDecorations>? decorate = null;
            var callerThread = -1;
            using var returned = new ManualResetEventSlim(false);
            var caller = new Thread(() =>
            {
                callerThread = Environment.CurrentManagedThreadId;
                decorate = coordinator.DecorateAsync(@"R:\repo", false, [@"R:\repo\a"], _ => { }, CancellationToken.None);
                returned.Set();
            })
            {
                IsBackground = true,
            };
            caller.Start();

            Assert.True(returned.Wait(Watchdog), "DecorateAsync blocked its caller.");
            Assert.NotNull(decorate);
            // The budget passed with the source still blocked: the result is empty, and the late merge would follow.
            var result = await decorate!.WaitAsync(Watchdog);
            Assert.Null(result.For(@"R:\repo\a"));
            Assert.Empty(result.FolderFlags);
            await WaitUntilAsync(() => Volatile.Read(ref sourceThread) != -1);
            Assert.NotEqual(callerThread, Volatile.Read(ref sourceThread));
            var call = Assert.Single(source.Calls);
            Assert.NotEqual(callerThread, call.ThreadId);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task DecorateAsync_CancelledSource_IsSkippedWithoutALogLine()
    {
        using var cancel = new CancellationTokenSource();
        var source = new FakeRowDecorationSource("com.a", "com.cancelled", "com.cancelled-null");
        source.Results["com.a"] = FakeRowDecorationSource.Batch("com.a", [(@"R:\repo\a.txt", "M")]);
        source.Handler = (pluginId, _, _, ct) => pluginId switch
        {
            "com.cancelled" => Task.FromCanceled<FolderDecorationBatch?>(ct),
            "com.cancelled-null" => Task.FromResult<FolderDecorationBatch?>(null),
            _ => null,
        };
        var coordinator = Ready(source);
        var log = new ConcurrentQueue<string>();
        coordinator.Log = log.Enqueue;
        cancel.Cancel();

        var result = await coordinator.DecorateAsync(@"R:\repo", false, [@"R:\repo\a.txt"], _ => { }, cancel.Token)
            .WaitAsync(Watchdog);
        await Task.Delay(100);

        Assert.Null(result.For(@"R:\repo\b.txt"));
        Assert.Empty(log);
    }

    private static RowDecorationCoordinator Ready(IRowDecorationSource source, TimeSpan? budget = null)
    {
        var coordinator = new RowDecorationCoordinator(budget ?? AmpleBudget);
        coordinator.SetSource(source);
        coordinator.MarkStartupReady();
        return coordinator;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Watchdog);
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

/// <summary>
/// A decoration source for the coordinator and workspace tests. It answers from <see cref="Results"/>, or from
/// <see cref="Handler"/> when that returns a task, and records each call with the thread it ran on.
/// </summary>
internal sealed class FakeRowDecorationSource : IRowDecorationSource
{
    public FakeRowDecorationSource(params string[] pluginIds)
    {
        PluginIds = pluginIds;
    }

    public IReadOnlyList<string> PluginIds { get; }

    public ConcurrentDictionary<string, FolderDecorationBatch> Results { get; } = new(StringComparer.Ordinal);

    public Func<string, string, IReadOnlyList<string>, CancellationToken, Task<FolderDecorationBatch?>?>? Handler { get; set; }

    public ConcurrentQueue<(string PluginId, string FolderPath, int ThreadId)> Calls { get; } = new();

    public Task<FolderDecorationBatch?> DecorateAsync(
        string pluginId,
        string folderPath,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        Calls.Enqueue((pluginId, folderPath, Environment.CurrentManagedThreadId));
        var handled = Handler?.Invoke(pluginId, folderPath, paths, ct);
        if (handled is not null)
        {
            return handled;
        }

        return Task.FromResult<FolderDecorationBatch?>(
            Results.TryGetValue(pluginId, out var batch) ? batch : Batch(pluginId, []));
    }

    public static FolderDecorationBatch Batch(
        string pluginId,
        IEnumerable<(string Path, string Text)> rows,
        params string[] flags) =>
        new(
            pluginId,
            rows.ToDictionary(row => row.Path, row => new RowDecorationEntry(pluginId, row.Text, null, null), StringComparer.Ordinal),
            flags);
}
