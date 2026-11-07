using Xunit;

namespace SimpleFile.Tests;

public class PluginSdkSurfaceTests
{
    [Fact]
    public void PublicSurface_MatchesPinnedSnapshot()
    {
        var actual = PluginSdkSurface.Describe(typeof(SimpleFile.PluginSdk.PluginSdk).Assembly);
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PluginSdkSurface.v1.txt"));
        Assert.Equal(expected, actual);
    }
}
