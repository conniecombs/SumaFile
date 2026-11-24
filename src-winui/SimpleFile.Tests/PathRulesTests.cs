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
        // A drive root already ends with its separator.
        Assert.Equal(@"C:\Users", PathRules.JoinPath(@"C:\", "Users"));
        Assert.Equal(@"C:\Users", PathRules.JoinPath("C:", "Users"));
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
    public void IsNetworkFsPath_TreatsVirtualFixedDrivesAsRemoteLike()
    {
        DriveInfo[] drives =
        [
            new()
            {
                Name = "Mounted Mirror (S:)",
                Path = @"S:\",
                DriveType = "Fixed",
                FileSystem = "AirLiveDrive-7",
            },
            new()
            {
                Name = "Windows (C:)",
                Path = @"C:\",
                DriveType = "Fixed",
                FileSystem = "NTFS",
            },
        ];

        Assert.True(PathRules.IsNetworkFsPath(@"S:\My Drive [VoX MrZip]\Movies", drives));
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

    [Fact]
    public void AllLieDirectlyIn_IsTrueOnlyWhenEverySourceIsAChildOfTheFolder()
    {
        Assert.True(PathRules.AllLieDirectlyIn([@"C:\Work\a.txt", @"c:\work\B"], @"C:\Work"));
        Assert.True(PathRules.AllLieDirectlyIn([@"C:\Work\a.txt"], @"C:\Work\"));
        Assert.True(PathRules.AllLieDirectlyIn([@"C:\a.txt"], @"C:\"));
        // One item from elsewhere makes the transfer a real one.
        Assert.False(PathRules.AllLieDirectlyIn([@"C:\Work\a.txt", @"C:\Other\b.txt"], @"C:\Work"));
        // A folder dropped on its own parent is not "into itself", and a deeper item is not a direct child.
        Assert.False(PathRules.AllLieDirectlyIn([@"C:\Work\Sub\a.txt"], @"C:\Work"));
        Assert.False(PathRules.AllLieDirectlyIn([@"C:\Work"], @"C:\Work"));
        Assert.False(PathRules.AllLieDirectlyIn([], @"C:\Work"));
        Assert.False(PathRules.AllLieDirectlyIn([@"C:\Work\a.txt"], ""));
    }

    [Theory]
    [InlineData(@"C:/Work/a.txt", @"C:\Work\a.txt")]
    [InlineData(@"C:\Work\Folder\", @"C:\Work\Folder")]
    [InlineData(@"C:\Work\Folder//", @"C:\Work\Folder")]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"C:/", @"C:\")]
    [InlineData(@"c:\", @"c:\")]
    [InlineData(@"\server\share\", @"\server\share")]
    [InlineData(@"/", @"\")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeSeparators_UsesBackslashesAndKeepsOnlyARootsTrailingSeparator(string? path, string expected)
    {
        Assert.Equal(expected, PathRules.NormalizeSeparators(path));
    }

    [Fact]
    public void DecorationAndThumbnailKeys_ShareTheSeparatorRule()
    {
        foreach (var path in new[] { @"R:/repo/Dir/A.TXT", @"R:\repo\untracked\", @"C:", @"C:\Photos\Vacation\" })
        {
            Assert.Equal(PathRules.NormalizeSeparators(path), RowDecorationKeys.Normalize(path));
            Assert.Equal(PathRules.NormalizeSeparators(path), ThumbnailCacheKey.NormalizePath(path));
        }

        Assert.Equal(@"C:\Work\a.txt", ThumbnailCacheKey.NormalizePath("  C:/Work/a.txt  "));
        Assert.Equal("", ThumbnailCacheKey.NormalizePath("   "));
    }
}
