using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class PathRulesTests
{
    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("C:", true)]
    [InlineData("C:/", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData("/", true)]
    [InlineData("recycle-bin:", true)]
    [InlineData("/home", false)]
    public void IsRootPath_MatchesWindowsPathContract(string path, bool expected)
    {
        Assert.Equal(expected, PathRules.IsRootPath(path));
    }

    [Theory]
    [InlineData(@"C:\Users\docs", @"C:\Users")]
    [InlineData(@"C:\Users", @"C:\")]
    [InlineData(@"C:\", null)]
    [InlineData("C:", null)]
    [InlineData(@"\\server\share\folder", @"\\server\share")]
    public void GetParentPath_MatchesWindowsPathContract(string path, string? expected)
    {
        Assert.Equal(expected, PathRules.GetParentPath(path));
    }

    [Fact]
    public void JoinPath_UsesWindowsSeparator()
    {
        Assert.Equal(@"C:\Users\Desktop", PathRules.JoinPath(@"C:\Users", "Desktop"));
        Assert.Equal(@"C:\Users\Desktop", PathRules.JoinPath(@"C:\Users\", "Desktop"));
    }

    [Fact]
    public void RecycleBinPath_IsRootAndHasNoParent()
    {
        Assert.True(PathRules.IsRecycleBinPath("recycle-bin:"));
        Assert.True(PathRules.IsRootPath(PathRules.RecycleBinPath));
        Assert.Null(PathRules.GetParentPath(PathRules.RecycleBinPath));
    }

    [Fact]
    public void Basename_DriveRootIsLetter()
    {
        Assert.Equal("C:", PathRules.Basename(@"C:\"));
        Assert.Equal("docs", PathRules.Basename(@"C:\Users\docs"));
    }

    [Fact]
    public void PathsEqual_IgnoresSlashAndCase()
    {
        Assert.True(PathRules.PathsEqual(@"C:\Users\", @"c:/Users"));
        Assert.False(PathRules.PathsEqual(@"C:\Users", @"C:\Users\docs"));
    }

    [Fact]
    public void IsNetworkFsPath_UncAndMappedLetter()
    {
        DriveInfo[] drives =
        [
            new() { Path = @"Z:\", DriveType = "Network" },
            new() { Path = @"C:\", DriveType = "Fixed" },
        ];

        Assert.True(PathRules.IsNetworkFsPath(@"\\server\share", drives));
        Assert.True(PathRules.IsNetworkFsPath(@"Z:\work", drives));
        Assert.False(PathRules.IsNetworkFsPath(@"C:\Users", drives));
    }

    [Fact]
    public void CreateFallbackDriveForPath_WindowsRoot()
    {
        var drive = PathRules.CreateFallbackDriveForPath(@"D:\Projects\app");
        Assert.NotNull(drive);
        Assert.Equal(@"D:\", drive.Path);
        Assert.Equal("Fixed", drive.DriveType);
        Assert.Equal("Local Disk (D:)", drive.Name);
    }
}
