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
