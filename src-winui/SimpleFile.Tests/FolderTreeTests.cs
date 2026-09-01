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

public class FolderTreeTests
{
    [Fact]
    public void FolderTree_FlattensExpandedChildren()
    {
        var roots = new[]
        {
            new TreeNode
            {
                Name = "Users",
                Path = @"C:\Users",
                HasChildren = true,
                Children = [new TreeNode { Name = "test", Path = @"C:\Users\test" }],
            },
        };
        var flat = FolderTree.Flatten(roots, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users" });
        Assert.Equal(2, flat.Count);
        Assert.Equal(1, flat[1].Depth);
    }
}
