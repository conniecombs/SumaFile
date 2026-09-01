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

public class PhotoFolderTests
{
    [Fact]
    public void PhotoFolder_DetectsImageHeavyDirectory()
    {
        var photos = new[]
        {
            new FileEntry { Name = "a.png", Extension = "png" },
            new FileEntry { Name = "b.jpg", Extension = "jpg" },
            new FileEntry { Name = "c.txt", Extension = "txt" },
        };
        Assert.True(PhotoFolder.IsPhotoFolder(photos, 60));
        Assert.False(PhotoFolder.IsPhotoFolder(photos, 80));
    }
}
