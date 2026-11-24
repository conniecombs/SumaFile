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

        Assert.False(DropDestination.IsValidDrop([@"C:\Users\test\Desktop"], @"C:\Users\test\Desktop", move: false));
        Assert.False(DropDestination.IsValidDrop([@"C:\Users\test\Desktop"], @"C:\Users\test\Desktop\nested", move: true));
        Assert.True(DropDestination.IsValidDrop([@"C:\Users\test\notes.txt"], @"C:\Users\test\Desktop", move: true));

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

    /// <summary>
    /// UI audit F25: one item pasted into its own folder was refused while two or more were
    /// duplicated. A copy into the items' own folder now makes duplicates whatever the count,
    /// and a move that would leave everything where it is is refused whatever the count.
    /// </summary>
    [Fact]
    public void IntoTheirOwnFolder_ACopyDuplicatesAndAMoveIsRefused_WhateverTheCount()
    {
        string[] one = [@"C:\Users\test\notes.txt"];
        string[] two = [@"C:\Users\test\notes.txt", @"C:\Users\test\todo.txt"];
        Assert.True(DropDestination.IsValidDrop(one, @"C:\Users\test", move: false));
        Assert.True(DropDestination.IsValidDrop(two, @"C:\Users\test", move: false));
        Assert.False(DropDestination.IsValidDrop(one, @"c:\users\TEST\", move: true));
        Assert.False(DropDestination.IsValidDrop(two, @"C:\Users\test", move: true));

        // A move with at least one item from elsewhere has something to do.
        string[] mixed = [@"C:\Users\test\notes.txt", @"D:\inbox\todo.txt"];
        Assert.True(DropDestination.IsValidDrop(mixed, @"C:\Users\test", move: true));
    }

    [Fact]
    public void FolderPairsMergeWhileFilesTypeMismatchesAndDuplicateSourcesStillPrompt()
    {
        string[] sources = [@"C:\src\Photos", @"C:\src\notes.txt", @"C:\src\mismatch", @"C:\one\shared", @"C:\two\shared"];
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            sources[0], @"D:\out\Photos", sources[2], sources[3], sources[4], @"D:\out\shared"
        };

        var conflicts = DropDestination.ProbeConflictingTransferNames(sources, @"D:\out",
            _ => true, isDirectoryNoFollow: directories.Contains);

        Assert.Equal(["notes.txt", "mismatch", "shared"], conflicts);
        Assert.Equal(["Photos"], DropDestination.ProbeConflictingTransferNames([@"D:\out\Photos"],
            @"D:\out", _ => true, isDirectoryNoFollow: directories.Contains));
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
