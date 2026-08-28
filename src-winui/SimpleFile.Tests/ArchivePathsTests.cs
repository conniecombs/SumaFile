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

public class ArchivePathsTests
{
    [Fact]
    public void ArchivePaths_RecognizeCompoundExtensions()
    {
        Assert.True(ArchivePaths.IsArchiveFile(@"C:\pack.tar.gz"));
        Assert.True(ArchivePaths.IsArchiveFile(@"D:\a.tgz"));
        Assert.True(ArchivePaths.IsArchiveFile("bundle.rar"));
        Assert.False(ArchivePaths.IsArchiveFile("notes.txt"));
        Assert.Equal("pack", ArchivePaths.ExtractFolderName("pack.tar.gz"));
        Assert.Equal("pack", ArchivePaths.ExtractFolderName(@"C:\downloads\pack.tar.gz"));
        Assert.Equal("backup", ArchivePaths.ExtractFolderName("backup.tgz"));
        Assert.Equal("report.v1", ArchivePaths.ExtractFolderName("report.v1.txt"));
        Assert.Equal("bundle", ArchivePaths.ExtractFolderName("bundle.zip"));
        Assert.Equal("report.v1.tar.gz", ArchivePaths.WithArchiveExtension("report.v1.zip", "tar.gz"));
        Assert.Equal("report.v1.rar", ArchivePaths.WithArchiveExtension("report.v1", "rar"));
        Assert.Equal("Archive.zip", ArchivePaths.WithArchiveExtension("", "zip"));
    }
}
