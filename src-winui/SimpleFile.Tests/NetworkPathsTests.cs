using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Which paths are on a network drive. Video thumbnails from such a drive load one at a time, in a slot of
/// their own, so a slow share never holds up videos on local drives.
/// </summary>
public class NetworkPathsTests
{
    private static NetworkPaths With(DriveType type, List<string>? asked = null) =>
        new(root =>
        {
            asked?.Add(root);
            return type;
        });

    [Theory]
    [InlineData(@"\\server\share\films\a.mp4")]
    [InlineData(@"\\server\share")]
    [InlineData("//server/share/a.mp4")]
    [InlineData(@"\\?\UNC\server\share\a.mp4")]
    public void AShareNamedByItsServerIsOnTheNetwork(string path)
    {
        var asked = new List<string>();

        Assert.True(With(DriveType.Fixed, asked).IsNetwork(path));
        Assert.Empty(asked);
    }

    [Fact]
    public void ADriveLetterIsWhatWindowsSaysItIs()
    {
        Assert.True(With(DriveType.Network).IsNetwork(@"Z:\films\a.mp4"));
        Assert.False(With(DriveType.Fixed).IsNetwork(@"C:\films\a.mp4"));
        Assert.False(With(DriveType.Removable).IsNetwork(@"E:\a.mp4"));
        Assert.True(With(DriveType.Network).IsNetwork(@"\\?\Z:\films\a.mp4"));
    }

    [Fact]
    public void ADriveIsAskedAboutOnce()
    {
        var asked = new List<string>();
        var paths = With(DriveType.Network, asked);

        Assert.True(paths.IsNetwork(@"Z:\films\a.mp4"));
        Assert.True(paths.IsNetwork(@"z:\other\b.mkv"));
        Assert.True(paths.IsNetwork(@"Z:\"));

        Assert.Equal([@"Z:\"], asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("clip.mp4")]
    [InlineData(@"films\clip.mp4")]
    public void APathWithoutADriveIsNotOnTheNetwork(string? path)
    {
        Assert.False(With(DriveType.Network).IsNetwork(path));
    }

    /// <summary>A drive Windows cannot describe (unplugged, a bad letter) is treated as local.</summary>
    [Fact]
    public void ADriveThatCannotBeAskedAboutIsNotOnTheNetwork()
    {
        var paths = new NetworkPaths(_ => throw new IOException("The device is not ready."));

        Assert.False(paths.IsNetwork(@"Q:\a.mp4"));
    }
}
