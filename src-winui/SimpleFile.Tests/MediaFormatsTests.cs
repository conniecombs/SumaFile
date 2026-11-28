using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class MediaFormatsTests
{
    [Theory]
    [InlineData("psd")]
    [InlineData(".EXR")]
    [InlineData("photo.CR2")]
    [InlineData(@"C:\art\scene.kra")]
    public void KnowsTheFormatsTheMediaProcessDecodes(string pathOrExtension)
    {
        Assert.True(MediaFormats.IsMediaProcessFormat(pathOrExtension));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("heic")]
    [InlineData("mp4")]
    [InlineData("")]
    [InlineData(null)]
    public void LeavesBuiltInAndWindowsFormatsAlone(string? pathOrExtension)
    {
        Assert.False(MediaFormats.IsMediaProcessFormat(pathOrExtension));
    }

    /// <summary>
    /// The video list moved from <see cref="MediaFolder"/> into the schema file both sides are generated from. It
    /// must still be the list the app used.
    /// </summary>
    [Fact]
    public void TheVideoListIsTheOneMediaFolderUsed()
    {
        string[] used =
        [
            "mp4", "m4v", "mov", "qt", "webm", "mkv", "mk3d", "avi", "wmv", "asf",
            "mpg", "mpeg", "mpe", "m2v", "m2ts", "mts", "vob", "flv", "f4v",
            "3gp", "3g2", "ogv", "divx", "mxf", "rm", "rmvb", "h264", "h265", "hevc", "y4m",
        ];

        Assert.Equal(used.Order(StringComparer.Ordinal), MediaFormats.VideoExtensions);
    }

    [Fact]
    public void MediaFolderReadsTheGeneratedVideoList()
    {
        Assert.True(MediaFolder.IsVideo("clip.M2TS"));
        Assert.True(MediaFormats.IsVideoFormat(@"C:\films\a.mkv"));
        Assert.False(MediaFolder.IsVideo("app.ts"));
        Assert.False(MediaFormats.IsVideoFormat("cover.psd"));
        Assert.False(MediaFormats.IsVideoFormat(null));
        Assert.All(MediaFormats.VideoExtensions, extension => Assert.True(MediaFolder.IsVideo(extension)));
    }

    /// <summary>The list is generated from the schema file the Rust side is generated from.</summary>
    [Fact]
    public void TheListIsSortedLowercaseAndWithoutDots()
    {
        var extensions = MediaFormats.Extensions.ToArray();

        Assert.NotEmpty(extensions);
        Assert.Equal(extensions.OrderBy(value => value, StringComparer.Ordinal), extensions);
        Assert.All(extensions, value => Assert.Matches("^[a-z0-9]+$", value));
    }
}
