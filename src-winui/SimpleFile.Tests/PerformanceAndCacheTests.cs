using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class PerformanceAndCacheTests
{
    [Fact]
    public void BoundedLruCache_EvictsLeastRecentlyUsedWhenByteBudgetExceeded()
    {
        var cache = new BoundedLruCache<string, byte[]>(maxEntries: 16, maxBytes: 100);

        cache.Set("a", new byte[40], estimatedBytes: 40);
        cache.Set("b", new byte[40], estimatedBytes: 40);

        Assert.True(cache.TryGetValue("a", out _)); // Touch "a" so "b" becomes LRU

        cache.Set("c", new byte[40], estimatedBytes: 40);

        Assert.True(cache.TryGetValue("a", out _));
        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("c", out _));

        var stats = cache.SnapshotStats();
        Assert.Equal(2, stats.Count);
        Assert.Equal(80, stats.EstimatedBytes);
        Assert.Equal(1, stats.Evictions);
        Assert.True(stats.Hits >= 3);
        Assert.Equal(1, stats.Misses);
    }

    [Fact]
    public void BoundedLruCache_BypassesOversizedSingleItemAndSupportsSelectiveRemoval()
    {
        var cache = new BoundedLruCache<string, string>(maxEntries: 8, maxBytes: 64);

        cache.Set("oversized", "too-big", estimatedBytes: 128);
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, cache.Bypasses);

        cache.Set(@"C:\Work\A\one.png", "one", estimatedBytes: 20);
        cache.Set(@"C:\Work\A\two.png", "two", estimatedBytes: 20);
        cache.Set(@"C:\Work\B\three.png", "three", estimatedBytes: 20);

        var removed = cache.RemoveWhere(key => ThumbnailCacheKey.MatchesPathOrSubtree(key, @"C:\Work\A"));
        Assert.Equal(2, removed);
        Assert.Equal(1, cache.Count);
        Assert.Equal(20, cache.EstimatedBytes);

        cache.TrimToBytes(0);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.EstimatedBytes);
    }

    [Fact]
    public void ThumbnailCacheKey_IncludesVersionTokenDisplayPixelSizeAndMatchesSubtrees()
    {
        var keyV1 = ThumbnailCacheKey.Create(
            @"C:/Photos/Vacation/img01.jpg",
            size: 1024,
            modified: "2026-10-17T10:00:00Z",
            displayPixelSize: 96);
        var keyV2 = ThumbnailCacheKey.Create(
            @"C:\Photos\Vacation\img01.jpg",
            size: 2048,
            modified: "2026-10-17T10:05:00Z",
            displayPixelSize: 96);
        var keySize128 = ThumbnailCacheKey.Create(
            @"C:\Photos\Vacation\img01.jpg",
            size: 1024,
            modified: "2026-10-17T10:00:00Z",
            displayPixelSize: 128);

        Assert.NotEqual(keyV1, keyV2);
        Assert.NotEqual(keyV1, keySize128);

        Assert.True(ThumbnailCacheKey.MatchesPathOrSubtree(keyV1, @"C:\Photos\Vacation\img01.jpg"));
        Assert.True(ThumbnailCacheKey.MatchesPathOrSubtree(keyV1, @"C:\Photos\Vacation"));
        Assert.True(ThumbnailCacheKey.MatchesPathOrSubtree(keyV1, @"C:\Photos"));
        Assert.False(ThumbnailCacheKey.MatchesPathOrSubtree(keyV1, @"C:\Photos\Vacation2"));

        var estimated96 = ThumbnailCacheKey.EstimateDecodedBytes(96);
        Assert.Equal(96L * 96L * 4L, estimated96);
    }

    [Fact]
    public async Task ForegroundTaskScheduler_PreemptsBackgroundTasksAndThrottlesDuringUserActivity()
    {
        var scheduler = new ForegroundTaskScheduler(pollSlice: TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource();
        using var registration = scheduler.RegisterPreemptibleBackground(
            cts,
            preemptOnNavigation: true,
            preemptOnScrolling: true,
            preemptOnTyping: true);

        Assert.False(cts.IsCancellationRequested);
        scheduler.RecordActivity(ForegroundActivityKind.Scrolling, quietPeriod: TimeSpan.FromMilliseconds(60));

        Assert.True(cts.IsCancellationRequested);
        Assert.True(scheduler.IsForegroundActive);
        Assert.Equal(1, scheduler.PreemptionCount);

        await scheduler.YieldOrThrottleAsync();
        Assert.True(scheduler.YieldCount >= 1);

        using (scheduler.BeginForegroundScope(ForegroundActivityKind.Navigation, postScopeQuietPeriod: TimeSpan.FromMilliseconds(50)))
        {
            Assert.True(scheduler.IsForegroundActive);
        }

        await Task.Delay(100);
        Assert.False(scheduler.IsForegroundActive);
    }

    [Fact]
    public async Task ProgressiveDirectoryEnumeration_StreamsInitialViewportWithoutResortingEntire10kDirectory()
    {
        var entries = Enumerable.Range(0, 10_500)
            .Select(index => new FileEntry
            {
                Name = $"file-{index:D5}.txt",
                Path = $@"C:\Large\file-{index:D5}.txt",
                Extension = "txt",
                Size = (ulong)index,
                Modified = "2026-10-17T00:00:00Z",
            })
            .ToList();

        var pane = new ExplorerPane(PaneId.Primary);
        pane.Entries.AddRange(entries);
        pane.ListingInProgress = true;
        pane.BumpContentVersion();

        var viewport = EntryPresentation.VisibleViewportEntries(
            pane.Entries,
            filterQuery: string.Empty,
            showHidden: false,
            sortBy: "name",
            sortAsc: true,
            keepFoldersOnTop: true,
            listingInProgress: true,
            progressiveViewportLimit: EntryPresentation.DefaultProgressiveViewportCount);

        Assert.Equal(EntryPresentation.DefaultProgressiveViewportCount, viewport.Count);
        Assert.Equal("file-00000.txt", viewport[0].Name);
        Assert.Equal("file-00255.txt", viewport[^1].Name);

        // Memoized VisibleEntries should return the exact same list instance until BumpContentVersion is called
        var firstVisible = pane.VisibleEntries(showHidden: false, filterQuery: string.Empty);
        var secondVisible = pane.VisibleEntries(showHidden: false, filterQuery: string.Empty);
        Assert.Same(firstVisible, secondVisible);

        pane.BumpContentVersion();
        var thirdVisible = pane.VisibleEntries(showHidden: false, filterQuery: string.Empty);
        Assert.NotSame(firstVisible, thirdVisible);

        // Workspace progressive viewport helper caps to initial viewport while ListingInProgress is true
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        var activePane = workspace.Pane(workspace.ActivePane);
        activePane.Entries.Clear();
        activePane.Entries.AddRange(entries);
        activePane.ListingInProgress = true;
        activePane.BumpContentVersion();

        var progressiveRows = workspace.VisibleViewportEntriesFor(workspace.ActivePane);
        Assert.Equal(EntryPresentation.DefaultProgressiveViewportCount, progressiveRows.Count);

        activePane.ListingInProgress = false;
        var fullRows = workspace.VisibleViewportEntriesFor(workspace.ActivePane);
        Assert.Equal(10_500, fullRows.Count);
    }
}
