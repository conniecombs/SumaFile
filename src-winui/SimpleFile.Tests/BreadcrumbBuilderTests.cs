using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class BreadcrumbBuilderTests
{
    [Fact]
    public void FromPath_SplitsWindowsPathLikeContentShell()
    {
        var segments = BreadcrumbBuilder.FromPath(@"C:\Users\Public");
        Assert.Equal(3, segments.Count);
        Assert.Equal("C:", segments[0].Label);
        Assert.Equal(@"C:\", segments[0].Path);
        Assert.False(segments[0].Current);
        Assert.Equal("Users", segments[1].Label);
        // ContentShell concatenates `C:\` + `\Users` → `C:\\Users` (same quirk).
        Assert.Equal(@"C:\\Users", segments[1].Path);
        Assert.Equal("Public", segments[2].Label);
        Assert.Equal(@"C:\\Users\Public", segments[2].Path);
        Assert.True(segments[2].Current);
    }

    [Fact]
    public void FromPath_EmptyIsEmpty()
    {
        Assert.Empty(BreadcrumbBuilder.FromPath(""));
    }
}
