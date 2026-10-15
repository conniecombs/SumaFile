using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public sealed class BoundedLruCacheTests
{
    [Fact]
    public void ScrollingBeyondCapacityRetainsRecentlyViewedImages()
    {
        var cache = new BoundedLruCache<string, string>(3, 100);
        cache.Set("a", "A", 20);
        cache.Set("b", "B", 20);
        cache.Set("c", "C", 20);
        Assert.True(cache.TryGetValue("a", out _));
        cache.Set("d", "D", 20);
        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("a", out _));
        Assert.True(cache.TryGetValue("c", out _));
        Assert.Equal(60, cache.EstimatedBytes);
    }

    [Fact]
    public void MemoryBudgetEvictsOnlyWhatInsertionNeedsAndBypassesOversizeImages()
    {
        var cache = new BoundedLruCache<int, int>(100, 100);
        cache.Set(1, 1, 40);
        cache.Set(2, 2, 40);
        cache.Set(3, 3, 30);
        Assert.False(cache.TryGetValue(1, out _));
        cache.Set(4, 4, 101);
        Assert.Equal(2, cache.Count);
        cache.Set(2, 20, 60);
        Assert.Equal(90, cache.EstimatedBytes);
        Assert.True(cache.TryGetValue(2, out var value));
        Assert.Equal(20, value);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.EstimatedBytes);
    }

    [Fact]
    public void ConcurrentRequestsStayWithinBothBudgets()
    {
        var cache = new BoundedLruCache<int, int>(20, 100);
        Parallel.For(0, 10000, i => { cache.Set(i, i, 10); cache.TryGetValue(i, out _); });
        Assert.InRange(cache.Count, 1, 10);
        Assert.InRange(cache.EstimatedBytes, 1, 100);
    }
}
