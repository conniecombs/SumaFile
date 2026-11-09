using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Every test that sets or depends on the plugin archive formats in <see cref="ArchivePaths"/>. That state is
/// process-wide, so the collection runs with parallelization disabled: alone, after the parallel collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ArchiveFormatsCollection
{
    public const string Name = "Archive formats";
}

[Collection(ArchiveFormatsCollection.Name)]
public sealed class ArchiveEngineNavigationTests : IDisposable
{
    private const string SevenZipNote = "7-Zip support requires the SumaFile 7-Zip plugin.";

    public ArchiveEngineNavigationTests()
    {
        ArchivePaths.SetEngineFormats([]);
    }

    public void Dispose()
    {
        ArchivePaths.SetEngineFormats([]);
    }

    [Fact]
    public async Task OpenSevenZipFile_WithItsEngine_NavigatesIntoArchive()
    {
        ArchivePaths.SetEngineFormats([ArchivePathsTests.SevenZipPluginRow()]);
        var backend = BackendWithSevenZipArchive(out var archivePath);
        backend.Listings[archivePath] = new DirectoryListing
        {
            Path = archivePath,
            Parent = @"C:\Users\test",
            Entries =
            [
                new FileEntry { Name = "inside.txt", Path = archivePath + @"\inside.txt", Size = 5 },
            ],
        };
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();

        var archive = workspace.VisibleEntries.First(entry => entry.Name == "pack.7z");
        await workspace.OpenEntryAsync(archive);

        Assert.Equal(archivePath, workspace.CurrentPath);
        Assert.Equal("inside.txt", workspace.VisibleEntries.Single().Name);
        Assert.False(workspace.FileOpenUnsupported);
    }

    [Fact]
    public async Task OpenSevenZipFile_WithoutItsEngine_OpensWithTheDefaultApp()
    {
        var backend = BackendWithSevenZipArchive(out var archivePath);
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        await workspace.InitializeAsync();
        var listingsBefore = backend.ListDirectoryCalls;

        var archive = workspace.VisibleEntries.First(entry => entry.Name == "pack.7z");
        await workspace.OpenEntryAsync(archive);

        Assert.Equal(@"C:\Users\test", workspace.CurrentPath);
        Assert.Equal([archivePath], ipc.OpenedFiles);
        Assert.Equal(listingsBefore, backend.ListDirectoryCalls);
    }

    [Fact]
    public async Task RefreshArchivePanes_RelistsPanesInsideArchivesWhoseEngineChanged()
    {
        // Review Focus 2: a pane inside pack.7z shows the plugin note once the engine goes away, and lists again
        // once it is back, without user action. A pane outside any .7z is left alone.
        ArchivePaths.SetEngineFormats([ArchivePathsTests.SevenZipPluginRow()]);
        var backend = BackendWithSevenZipArchive(out var archivePath);
        var docsPath = archivePath + @"\docs";
        var listings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var docsMode = "first";
        backend.ListDirectoryHandler = (path, _) =>
        {
            listings[path] = listings.GetValueOrDefault(path) + 1;
            if (!string.Equals(path, docsPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return docsMode switch
            {
                "missing" => Task.FromException<DirectoryListing>(new IpcException(Protocol.ErrApplication, SevenZipNote)),
                _ => Task.FromResult(new DirectoryListing
                {
                    Path = docsPath,
                    Parent = archivePath,
                    Entries =
                    [
                        new FileEntry
                        {
                            Name = docsMode == "first" ? "before.txt" : "after.txt",
                            Path = docsPath + (docsMode == "first" ? @"\before.txt" : @"\after.txt"),
                            Size = 3,
                        },
                    ],
                }),
            };
        };

        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Primary, docsPath);
        Assert.Equal(@"C:\Users\test", workspace.Secondary.Path);
        Assert.Equal("before.txt", workspace.VisibleEntriesFor(PaneId.Primary).Single().Name);
        var homeListings = listings.GetValueOrDefault(@"C:\Users\test");

        // The engine went away: the pane re-lists and shows the note.
        docsMode = "missing";
        var removed = ArchivePaths.SetEngineFormats([]);
        await workspace.RefreshArchivePanesAsync([.. removed.Added, .. removed.Removed]);

        Assert.Equal(2, listings[docsPath]);
        Assert.Contains(SevenZipNote, workspace.ErrorMessage, StringComparison.Ordinal);

        // The engine is back: the pane lists again.
        docsMode = "second";
        var added = ArchivePaths.SetEngineFormats([ArchivePathsTests.SevenZipPluginRow()]);
        await workspace.RefreshArchivePanesAsync([.. added.Added, .. added.Removed]);

        Assert.Equal(3, listings[docsPath]);
        Assert.Equal("after.txt", workspace.VisibleEntriesFor(PaneId.Primary).Single().Name);
        Assert.Equal(homeListings, listings.GetValueOrDefault(@"C:\Users\test"));

        // An extension no pane is inside re-lists nothing.
        await workspace.RefreshArchivePanesAsync([".cab"]);
        await workspace.RefreshArchivePanesAsync([]);
        Assert.Equal(3, listings[docsPath]);
        Assert.Equal(homeListings, listings.GetValueOrDefault(@"C:\Users\test"));
    }

    [Fact]
    public async Task RefreshArchivePanes_DropsStaleEntriesWhileTheEngineIsGoneAndClearsTheNoteOnceItIsBack()
    {
        // Found by the UI smoke (sevenzip-backend-loss): a failed engine re-list kept the old rows under the note,
        // and a successful one kept showing the note over the restored listing.
        ArchivePaths.SetEngineFormats([ArchivePathsTests.SevenZipPluginRow()]);
        var backend = BackendWithSevenZipArchive(out var archivePath);
        var docsPath = archivePath + @"\docs";
        var available = true;
        backend.ListDirectoryHandler = (path, _) =>
        {
            if (!string.Equals(path, docsPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return available
                ? Task.FromResult(new DirectoryListing
                {
                    Path = docsPath,
                    Parent = archivePath,
                    Entries = [new FileEntry { Name = "a.txt", Path = docsPath + @"\a.txt", Size = 3 }],
                })
                : Task.FromException<DirectoryListing>(new IpcException(Protocol.ErrApplication, SevenZipNote));
        };

        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        await workspace.NavigatePaneAsync(PaneId.Primary, docsPath);
        Assert.Equal("a.txt", workspace.VisibleEntriesFor(PaneId.Primary).Single().Name);

        available = false;
        var removed = ArchivePaths.SetEngineFormats([]);
        await workspace.RefreshArchivePanesAsync([.. removed.Added, .. removed.Removed]);
        Assert.Equal(SevenZipNote, workspace.ErrorMessage);
        Assert.Empty(workspace.VisibleEntriesFor(PaneId.Primary));
        Assert.Equal(docsPath, workspace.Primary.Path);

        available = true;
        var added = ArchivePaths.SetEngineFormats([ArchivePathsTests.SevenZipPluginRow()]);
        await workspace.RefreshArchivePanesAsync([.. added.Added, .. added.Removed]);
        Assert.Null(workspace.ErrorMessage);
        Assert.Equal("a.txt", workspace.VisibleEntriesFor(PaneId.Primary).Single().Name);

        // An ordinary refresh keeps its old behaviour: an unrelated error stays until the next navigation.
        workspace.SetErrorMessageCore("unrelated");
        await workspace.RefreshAsync(PaneId.Primary);
        Assert.Equal("unrelated", workspace.ErrorMessage);
    }

    private static FakeExplorerBackend BackendWithSevenZipArchive(out string archivePath)
    {
        var backend = FakeExplorerBackend.Typical();
        archivePath = @"C:\Users\test\pack.7z";
        backend.Listings[@"C:\Users\test"].Entries.Add(new FileEntry
        {
            Name = "pack.7z",
            Path = archivePath,
            Extension = "7z",
            Size = 100,
        });
        return backend;
    }
}
