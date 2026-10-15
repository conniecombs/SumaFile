using System.Diagnostics;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using Xunit.Abstractions;

namespace SimpleFile.Tests;

public class LargeDirectoryPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public async Task StreamedNavigation_PreservesRowsWithFewerSnapshotAllocations(int count)
    {
        const string path = @"C:\Large";
        var backend = new FakeExplorerBackend { EmitChunks = true, ChunkSize = 256 };
        backend.Listings[path] = new DirectoryListing
        {
            Path = path,
            Entries = Enumerable.Range(0, count)
                .Select(index => new FileEntry { Name = $"file-{index:D6}.txt", Path = $"{path}\\file-{index:D6}.txt" })
                .ToList(),
        };

        // Previous behavior copied every accumulated row after every chunk.
        var baselinePane = new ExplorerPane(PaneId.Primary);
        var progressive = new List<FileEntry>();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        await backend.ListDirectoryAsync(path,
            chunk => WorkspaceNavigation.ApplyListingChunk(baselinePane, chunk, progressive));
        var baselineTime = timer.Elapsed.TotalMilliseconds;
        var baselineBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var pane = new ExplorerPane(PaneId.Primary);
        var publishedCounts = new List<int>();
        var navigator = new PaneNavigator(backend, new object(), () => publishedCounts.Add(pane.Entries.Count));
        before = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        var result = await navigator.NavigateAsync(pane, path, pane.NextNavigationToken(), CancellationToken.None);
        var elapsed = timer.Elapsed.TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"{count:N0} rows, 256/packet: previous={baselineTime:F2}ms/{baselineBytes:N0}B; current={elapsed:F2}ms/{allocated:N0}B; snapshots={publishedCounts.Count}");
        Assert.False(result.IsAbandoned);
        Assert.Equal(backend.Listings[path].Entries, pane.Entries);
        Assert.Equal(256, publishedCounts[0]);
        Assert.Equal(count, publishedCounts[^1]);
        // Allocation is the stable gate; elapsed time is diagnostic, not a CI threshold.
        Assert.True(allocated < baselineBytes / 2, $"Expected fewer snapshot allocations: {allocated} vs {baselineBytes}");
    }

    [Fact]
    public void ThumbnailLru_RetainsRecentlyViewedImagesAcrossCapacityPressure()
    {
        var cache = new BoundedLruCache<int, int>(512, 64 * 1024 * 1024);
        var previous = new Dictionary<int, int>();
        var previousHits = 0;
        var currentHits = 0;
        for (var round = 0; round < 20; round++)
        {
            // Revisit 400 images, then scroll through 100 newly exposed images.
            foreach (var key in Enumerable.Range(0, 400).Concat(Enumerable.Range(400 + round * 100, 100)))
            {
                if (previous.ContainsKey(key)) previousHits++;
                else
                {
                    if (previous.Count >= 512) previous.Clear();
                    previous[key] = key;
                }
                if (cache.TryGetValue(key, out _)) currentHits++;
                else cache.Set(key, key, 128 * 128 * 4);
            }
        }
        output.WriteLine($"10,000 thumbnail requests: previous hits={previousHits}; LRU hits={currentHits}; entries={cache.Count}; estimated bytes={cache.EstimatedBytes}");
        Assert.True(currentHits > previousHits);
        Assert.InRange(cache.Count, 1, 512);
        Assert.InRange(cache.EstimatedBytes, 1, 64 * 1024 * 1024);
    }
}
