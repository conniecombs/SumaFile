using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class DropDestinationTests
{
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
}
