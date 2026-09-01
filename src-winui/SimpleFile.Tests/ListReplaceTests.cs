using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class ListReplaceTests
{
    [Fact]
    public void Apply_AppendsWhenSourceGrowsAsPrefix()
    {
        var target = new List<string> { "a", "b" };
        var changed = ListReplace.Apply(target, ["a", "b", "c", "d"], string.Equals);
        Assert.True(changed);
        Assert.Equal(["a", "b", "c", "d"], target);
    }

    [Fact]
    public void Apply_ReplacesInPlaceWhenCountMatches()
    {
        var target = new List<string> { "a", "old" };
        var changed = ListReplace.Apply(target, ["a", "new"], string.Equals);
        Assert.True(changed);
        Assert.Equal(["a", "new"], target);
    }

    [Fact]
    public void Apply_ReturnsFalseWhenUnchanged()
    {
        var target = new List<string> { "a", "b" };
        Assert.False(ListReplace.Apply(target, ["a", "b"], string.Equals));
    }
}
