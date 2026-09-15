using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class FolderViewSettingsTests
{
    [Fact]
    public void Resolve_UsesExactThenNearestDescendantThenGlobal()
    {
        var document = new FolderViewSettingsDocument();
        document.Upsert(
            FolderViewScope.Global,
            "",
            [],
            new FolderViewOptions { View = "list", IconSize = 16, SortBy = "name" });
        document.Upsert(
            FolderViewScope.Descendants,
            @"C:\Users\test",
            [],
            new FolderViewOptions { View = "content", IconSize = 48, SortBy = "date", SortAscending = false });
        document.Upsert(
            FolderViewScope.Descendants,
            @"C:\Users\test\Pictures",
            [],
            new FolderViewOptions { View = "tiles", IconSize = 128, SortBy = "extension" });
        document.Upsert(
            FolderViewScope.Folder,
            @"C:\Users\test\Pictures\Raw",
            [],
            new FolderViewOptions { View = "details", IconSize = 32, SortBy = "size" });

        Assert.Equal("details", document.Resolve(@"C:\Users\test\Pictures\Raw", [])?.Options.View);
        Assert.Equal("tiles", document.Resolve(@"C:\Users\test\Pictures\Exports", [])?.Options.View);
        Assert.Equal("content", document.Resolve(@"C:\Users\test\Downloads", [])?.Options.View);
        Assert.Equal("list", document.Resolve(@"D:\Other", [])?.Options.View);
    }

    [Fact]
    public void FromJson_SanitizesMalformedAndDuplicateRules()
    {
        var raw = """
            {
              "version": 99,
              "rules": [
                {
                  "id": "",
                  "scope": "folder-and-descendants",
                  "path": " C:\\Work ",
                  "options": {
                    "view": "tiles",
                    "iconSize": 95,
                    "visibleColumnIds": [ "name", "git", "missing", "name" ],
                    "columnWidths": { "path": 50000, "missing": 42 },
                    "columnPreset": "developer"
                  }
                },
                {
                  "id": "duplicate",
                  "scope": "descendants",
                  "path": "c:\\work",
                  "options": { "view": "content" }
                },
                {
                  "id": "blank",
                  "scope": "folder",
                  "path": " ",
                  "options": { "view": "list" }
                }
              ]
            }
            """;

        var document = FolderViewSettingsDocument.FromJson(raw);
        var rule = Assert.Single(document.Rules);

        Assert.Equal(FolderViewSettingsDocument.CurrentVersion, document.Version);
        Assert.Equal(FolderViewRuleScope.Descendants, rule.Scope);
        Assert.Equal(@"c:\work", rule.Path.ToLowerInvariant());
        Assert.Equal("content", rule.Options.View);
        Assert.NotEmpty(rule.Id);
    }

    [Fact]
    public void StableKeys_MatchMappedNetworkDriveAcrossDriveLetterChanges()
    {
        var document = new FolderViewSettingsDocument();
        document.Upsert(
            FolderViewScope.Descendants,
            @"Z:\Photos",
            [
                new DriveInfo
                {
                    Name = "Media share",
                    Path = @"Z:\",
                    DriveType = "Network",
                    RemotePath = @"\\nas\share",
                    DriveStatus = "available",
                },
            ],
            new FolderViewOptions { View = "tiles", IconSize = 192 });

        var resolved = document.Resolve(
            @"Y:\Photos\2026",
            [
                new DriveInfo
                {
                    Name = "Media share",
                    Path = @"Y:\",
                    DriveType = "Network",
                    RemotePath = @"\\nas\share",
                    DriveStatus = "available",
                },
            ]);

        Assert.Equal("tiles", resolved?.Options.View);
        Assert.StartsWith("unc://nas/share/photos", document.Rules.Single().StableKey);
    }

    [Fact]
    public void StableKeys_MatchRemovableDriveAcrossDriveLetterChanges()
    {
        var document = new FolderViewSettingsDocument();
        document.Upsert(
            FolderViewScope.Descendants,
            @"E:\DCIM",
            [
                new DriveInfo
                {
                    Name = "Camera Card",
                    Path = @"E:\",
                    DriveType = "Removable",
                    FileSystem = "exFAT",
                    TotalSpace = 64000000000,
                    DriveStatus = "available",
                },
            ],
            new FolderViewOptions { View = "tiles", IconSize = 256 });

        var resolved = document.Resolve(
            @"F:\DCIM\100MEDIA",
            [
                new DriveInfo
                {
                    Name = "Camera Card",
                    Path = @"F:\",
                    DriveType = "Removable",
                    FileSystem = "exFAT",
                    TotalSpace = 64000000000,
                    DriveStatus = "available",
                },
            ]);
        var differentDisk = document.Resolve(
            @"E:\DCIM\100MEDIA",
            [
                new DriveInfo
                {
                    Name = "Backup Stick",
                    Path = @"E:\",
                    DriveType = "Removable",
                    FileSystem = "exFAT",
                    TotalSpace = 32000000000,
                    DriveStatus = "available",
                },
            ]);

        Assert.Equal("tiles", resolved?.Options.View);
        Assert.Null(differentDisk);
        Assert.StartsWith("volume:removable:camera%20card:exfat:64000000000/dcim", document.Rules.Single().StableKey);
    }
}
