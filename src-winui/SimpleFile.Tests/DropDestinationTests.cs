using System;
using System.IO;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class DropDestinationTests
{
    private static string FindAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "SimpleFile.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SimpleFile.App.");
    }

    [Fact]
    public void DropDestination_ResolvesFolderHoverAndRejectsSelf()
    {
        var ontoFolder = DropDestination.Resolve(@"C:\Users\test", @"C:\Users\test\Desktop", hoveredIsDirectory: true);
        Assert.Equal(@"C:\Users\test\Desktop", ontoFolder.Destination);
        Assert.True(ontoFolder.OntoFolder);

        var ontoPane = DropDestination.Resolve(@"C:\Users\test", @"C:\Users\test\notes.txt", hoveredIsDirectory: false);
        Assert.Equal(@"C:\Users\test", ontoPane.Destination);

        Assert.False(DropDestination.IsValidDrop([@"C:\Users\test\Desktop"], @"C:\Users\test\Desktop"));
        Assert.False(DropDestination.IsValidDrop([@"C:\Users\test\Desktop"], @"C:\Users\test\Desktop\nested"));
        Assert.False(DropDestination.IsValidDrop([@"C:\Users\test\notes.txt"], @"C:\Users\test"));
        Assert.True(DropDestination.IsValidDrop([@"C:\Users\test\notes.txt"], @"C:\Users\test\Desktop"));

        var conflicts = DropDestination.ConflictingNames(
            [@"C:\src\notes.txt", @"C:\src\other.txt"],
            ["notes.txt", "readme.md"]);
        Assert.Equal(["notes.txt"], conflicts);

        var transferConflicts = DropDestination.ConflictingTransferNames(
            [@"C:\one\notes.txt", @"C:\two\notes.txt", @"C:\src\readme.md"],
            ["readme.md"]);
        Assert.Equal(["notes.txt", "readme.md"], transferConflicts);

        var probed = DropDestination.ProbeConflictingTransferNames(
            [@"C:\one\notes.txt", @"C:\two\notes.txt", @"C:\src\readme.md"],
            @"C:\target",
            path => string.Equals(path, @"C:\target\readme.md", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["notes.txt", "readme.md"], probed);
    }

    [Fact]
    public void NativePath_ExistsNoFollow_DoesNotFollowDanglingSymlink()
    {
        var root = Path.Combine(Path.GetTempPath(), "sumafile-nofollow-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(dest);
        var linkPath = Path.Combine(dest, "notes.txt");
        var missingTarget = Path.Combine(root, "missing-target.txt");

        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, missingTarget);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.True(NativePath.ExistsNoFollow(linkPath));

            var conflicts = DropDestination.ProbeConflictingTransferNames(
                [Path.Combine(root, "src", "notes.txt")],
                dest,
                NativePath.ExistsNoFollow);
            Assert.Equal(["notes.txt"], conflicts);

            Assert.Contains(
                "NativePath.ExistsNoFollow",
                File.ReadAllText(Path.Combine(FindAppRoot(), "MainWindow.Transfer.cs")));
        }
        finally
        {
            try
            {
                if (NativePath.ExistsNoFollow(linkPath))
                {
                    File.Delete(linkPath);
                }
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void NativePath_ExistsNoFollow_DetectsJunctionWithoutFollowing()
    {
        var root = Path.Combine(Path.GetTempPath(), "sumafile-junction-" + Guid.NewGuid().ToString("N"));
        var realDir = Path.Combine(root, "real");
        var dest = Path.Combine(root, "dest");
        var junction = Path.Combine(dest, "folder");
        Directory.CreateDirectory(realDir);
        Directory.CreateDirectory(dest);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(junction, realDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.True(NativePath.ExistsNoFollow(junction));
            var conflicts = DropDestination.ProbeConflictingTransferNames(
                [Path.Combine(root, "src", "folder")],
                dest,
                NativePath.ExistsNoFollow);
            Assert.Equal(["folder"], conflicts);
        }
        finally
        {
            try
            {
                if (NativePath.ExistsNoFollow(junction))
                {
                    Directory.Delete(junction);
                }
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
