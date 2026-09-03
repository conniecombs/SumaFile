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

    [Fact]
    public void MediaFolder_DetectsImageAndVideoHeavyDirectory()
    {
        var media = new[]
        {
            new FileEntry { Name = "clip.mp4", Extension = "mp4" },
            new FileEntry { Name = "scene.mov", Extension = "mov" },
            new FileEntry { Name = "notes.txt", Extension = "txt" },
        };

        Assert.True(MediaFolder.IsMediaFolder(media, 60));
        Assert.False(MediaFolder.IsMediaFolder(media, 80));
    }

    [Fact]
    public void MediaFolder_VideoDetectionAcceptsExtensionsAndNames()
    {
        Assert.True(MediaFolder.IsVideo("mp4"));
        Assert.True(MediaFolder.IsVideo("movie.webm"));
        Assert.True(MediaFolder.IsVideo("phone.3gp"));
        Assert.True(MediaFolder.IsVisualMedia("poster.jpg"));
        Assert.False(MediaFolder.IsVideo("archive.zip"));
    }
}
