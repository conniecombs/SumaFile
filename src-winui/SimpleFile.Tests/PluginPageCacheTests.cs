using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PluginPageCacheTests
{
    private sealed class Page;

    [Fact]
    public void Cache_ReturnsTheStoredPageForTheSameEpoch()
    {
        var cache = new PluginPageCache<Page>();
        var page = new Page();

        Assert.False(cache.TryGet("com.a.b", 3, out var missing));
        Assert.Null(missing);
        cache.Store("com.a.b", 3, page);

        Assert.True(cache.TryGet("com.a.b", 3, out var hit));
        Assert.Same(page, hit);
        Assert.False(cache.TryGet("com.a.c", 3, out _));
    }

    [Fact]
    public void Cache_AnotherEpochMisses()
    {
        var cache = new PluginPageCache<Page>();
        var first = new Page();
        var second = new Page();
        cache.Store("com.a.b", 1, first);

        Assert.False(cache.TryGet("com.a.b", 2, out var missing));
        Assert.Null(missing);

        // Storing another epoch replaces the plugin's page.
        cache.Store("com.a.b", 2, second);
        Assert.False(cache.TryGet("com.a.b", 1, out _));
        Assert.True(cache.TryGet("com.a.b", 2, out var hit));
        Assert.Same(second, hit);
    }

    [Fact]
    public void Cache_RemoveForgetsThePlugin()
    {
        var cache = new PluginPageCache<Page>();
        cache.Store("com.a.b", 1, new Page());
        var other = new Page();
        cache.Store("com.a.c", 1, other);

        cache.Remove("com.a.b");
        cache.Remove("com.never.stored");

        Assert.False(cache.TryGet("com.a.b", 1, out _));
        Assert.True(cache.TryGet("com.a.c", 1, out var kept));
        Assert.Same(other, kept);
    }
}
