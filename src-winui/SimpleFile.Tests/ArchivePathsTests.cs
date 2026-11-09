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

[Collection(ArchiveFormatsCollection.Name)]
public sealed class ArchivePathsTests : IDisposable
{
    public ArchivePathsTests()
    {
        ArchivePaths.SetEngineFormats([]);
    }

    public void Dispose()
    {
        ArchivePaths.SetEngineFormats([]);
    }

    [Fact]
    public void ArchivePaths_RecognizeCompoundExtensions()
    {
        Assert.True(ArchivePaths.IsArchiveFile(@"C:\pack.tar.gz"));
        Assert.True(ArchivePaths.IsArchiveFile(@"D:\a.tgz"));
        Assert.False(ArchivePaths.IsArchiveFile("bundle.7z"));
        Assert.True(ArchivePaths.IsArchiveFile("bundle.rar"));
        Assert.False(ArchivePaths.IsArchiveFile("notes.txt"));
        Assert.Equal("pack", ArchivePaths.ExtractFolderName("pack.tar.gz"));
        Assert.Equal("pack", ArchivePaths.ExtractFolderName(@"C:\downloads\pack.tar.gz"));
        Assert.Equal("backup", ArchivePaths.ExtractFolderName("backup.tgz"));
        Assert.Equal("bundle", ArchivePaths.ExtractFolderName("bundle.7z"));
        Assert.Equal("report.v1", ArchivePaths.ExtractFolderName("report.v1.txt"));
        Assert.Equal("bundle", ArchivePaths.ExtractFolderName("bundle.zip"));
        Assert.Equal("report.v1.tar.gz", ArchivePaths.WithArchiveExtension("report.v1.zip", "tar.gz"));
        Assert.Equal("report.v1.zip", ArchivePaths.WithArchiveExtension("report.v1.zip", "7z"));
        Assert.Equal("report.v1.rar", ArchivePaths.WithArchiveExtension("report.v1", "rar"));
        Assert.Equal("Archive.zip", ArchivePaths.WithArchiveExtension("", "zip"));
        Assert.True(ArchivePaths.IsBrowsableArchive(@"C:\pack.zip"));
        Assert.True(ArchivePaths.IsBrowsableArchive(@"C:\pack.rar"));
        Assert.False(ArchivePaths.IsBrowsableArchive(@"C:\notes.gz"));
        Assert.True(ArchivePaths.IsArchiveFile(@"C:\notes.gz"));
        Assert.False(ArchivePaths.IsEngineArchive(@"C:\pack.zip"));
    }

    [Fact]
    public void ArchivePaths_EngineFormatsAreRecognizedOnlyWhileRegistered()
    {
        var change = ArchivePaths.SetEngineFormats([SevenZipPluginRow()]);

        Assert.Equal([".7z"], change.Added);
        Assert.Empty(change.Removed);
        Assert.True(ArchivePaths.IsArchiveFile(@"C:\Users\test\pack.7z"));
        Assert.True(ArchivePaths.IsArchiveFile(@"C:\Users\test\PACK.7Z"));
        Assert.True(ArchivePaths.IsBrowsableArchive(@"C:\Users\test\pack.7z"));
        Assert.True(ArchivePaths.IsEngineArchive(@"C:\Users\test\pack.7z"));
        Assert.False(ArchivePaths.IsEngineArchive(@"C:\Users\test\pack.zip"));
        Assert.Equal(".7z", ArchivePaths.ExtensionForFormat("7z"));
        Assert.Equal("pack.zip", ArchivePaths.WithArchiveExtension("pack.7z", "zip"));
        Assert.Equal("report.v1.7z", ArchivePaths.WithArchiveExtension("report.v1.zip", "7z"));
        Assert.Equal("pack", ArchivePaths.ExtractFolderName("pack.7z"));

        // Setting the same set again changes nothing.
        var same = ArchivePaths.SetEngineFormats([SevenZipPluginRow()]);
        Assert.Empty(same.Added);
        Assert.Empty(same.Removed);

        var removed = ArchivePaths.SetEngineFormats([]);

        Assert.Empty(removed.Added);
        Assert.Equal([".7z"], removed.Removed);
        Assert.False(ArchivePaths.IsArchiveFile(@"C:\Users\test\pack.7z"));
        Assert.False(ArchivePaths.IsBrowsableArchive(@"C:\Users\test\pack.7z"));
        Assert.False(ArchivePaths.IsEngineArchive(@"C:\Users\test\pack.7z"));
        Assert.Equal(".zip", ArchivePaths.ExtensionForFormat("7z"));
    }

    [Fact]
    public void ArchivePaths_UnavailableRowsAreNotArchives()
    {
        var change = ArchivePaths.SetEngineFormats(
        [
            new ArchiveFormatCapability
            {
                Format = "zip",
                Extension = ".zip",
                CanList = true,
                CanExtract = true,
                CanCreate = true,
                CanModify = true,
                Engine = "built-in",
            },
            new ArchiveFormatCapability
            {
                Format = "7z",
                Extension = ".7z",
                Engine = "unavailable",
                Note = "7-Zip support requires the SumaFile 7-Zip plugin.",
            },
            new ArchiveFormatCapability
            {
                Format = "cab",
                Extension = ".cab",
                CanList = false,
                CanExtract = true,
                Engine = "plugin:com.example.cab",
            },
            // A built-in extension never reaches an engine, as in the service.
            new ArchiveFormatCapability
            {
                Format = "zip",
                Extension = ".zip",
                CanList = true,
                Engine = "plugin:com.example.zip",
            },
        ]);

        Assert.Empty(change.Added);
        Assert.Empty(change.Removed);
        Assert.False(ArchivePaths.IsArchiveFile("pack.7z"));
        Assert.False(ArchivePaths.IsBrowsableArchive("pack.7z"));
        Assert.False(ArchivePaths.IsEngineArchive("pack.7z"));
        Assert.False(ArchivePaths.IsEngineArchive("pack.cab"));
        Assert.False(ArchivePaths.IsEngineArchive("pack.zip"));
        Assert.Equal(".zip", ArchivePaths.ExtensionForFormat("7z"));
    }

    internal static ArchiveFormatCapability SevenZipPluginRow() => new()
    {
        Format = "7z",
        Extension = ".7z",
        CanList = true,
        CanExtract = true,
        CanCreate = true,
        CanModify = true,
        Engine = "plugin:com.sumafile.sevenzip",
    };
}
