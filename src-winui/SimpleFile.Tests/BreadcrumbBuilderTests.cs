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
        // The old frontend concatenated `C:\` + `\Users` into `C:\\Users`. Navigating to that path made every
        // folder entered from it, and every copy into them, carry the doubled separator; the final rename of a
        // transferred file then failed with "The parameter is incorrect".
        Assert.Equal(@"C:\Users", segments[1].Path);
        Assert.Equal("Public", segments[2].Label);
        Assert.Equal(@"C:\Users\Public", segments[2].Path);
        Assert.True(segments[2].Current);
    }

    [Fact]
    public void FromPath_SegmentPathsRoundTripThroughTheBuilder()
    {
        var segments = BreadcrumbBuilder.FromPath(@"V:\sync\Movies");

        foreach (var segment in segments)
        {
            var again = BreadcrumbBuilder.FromPath(segment.Path);
            Assert.Equal(segment.Path, again[^1].Path);
            Assert.DoesNotContain(@"\\", segment.Path.Substring(Math.Min(1, segment.Path.Length)));
        }
    }

    [Fact]
    public void FromPath_EmptyIsEmpty()
    {
        Assert.Empty(BreadcrumbBuilder.FromPath(""));
    }

    [Fact]
    public void FromPath_RecycleBinIsSingleSegment()
    {
        var segments = BreadcrumbBuilder.FromPath(PathRules.RecycleBinPath);
        var segment = Assert.Single(segments);
        Assert.Equal("Recycle Bin", segment.Label);
        Assert.Equal(PathRules.RecycleBinPath, segment.Path);
        Assert.True(segment.Current);
    }
}
