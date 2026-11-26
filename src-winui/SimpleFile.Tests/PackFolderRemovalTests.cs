using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Pack into Folder, Flatten Folder and their Undo and Redo remove a folder only when nothing is left in it.
/// The tests work on real folders; the fake service moves real files.
/// </summary>
public sealed class PackFolderRemovalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sumafile-pack-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void EmptyFolder_Remove_RemovesAFolderThatHoldsNothing()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;

        Assert.True(EmptyFolder.Remove(folder));

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void EmptyFolder_Remove_KeepsAFolderThatHoldsAFile()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "full")).FullName;
        var file = Path.Combine(folder, "keep.txt");
        File.WriteAllText(file, "keep");

        Assert.False(EmptyFolder.Remove(folder));

        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public void EmptyFolder_Remove_KeepsAFolderThatHoldsOnlyAnEmptyFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "outer")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(folder, "inner")).FullName;

        Assert.False(EmptyFolder.Remove(folder));

        Assert.True(Directory.Exists(inner));
    }

    [Fact]
    public void EmptyFolder_Remove_RemovesAnEmptyFolderMarkedReadOnly()
    {
        // Windows marks a folder with a custom icon read-only and then refuses a plain removal.
        var folder = Directory.CreateDirectory(Path.Combine(_root, "read-only"));
        folder.Attributes |= FileAttributes.ReadOnly;

        Assert.True(EmptyFolder.Remove(folder.FullName));

        Assert.False(Directory.Exists(folder.FullName));
    }

    [Fact]
    public void EmptyFolder_Remove_KeepsAReadOnlyFolderThatHoldsAFile()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "read-only-full"));
        var file = Path.Combine(folder.FullName, "keep.txt");
        File.WriteAllText(file, "keep");
        folder.Attributes |= FileAttributes.ReadOnly;
        try
        {
            Assert.False(EmptyFolder.Remove(folder.FullName));

            Assert.Equal("keep", File.ReadAllText(file));
            folder.Refresh();
            Assert.True((folder.Attributes & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            folder.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    [Fact]
    public void EmptyFolder_Remove_OfAFolderThatIsNotThereIsDone()
    {
        Assert.True(EmptyFolder.Remove(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void EmptyFolder_Remove_NeverRemovesAFile()
    {
        var file = Path.Combine(_root, "file.txt");
        File.WriteAllText(file, "keep");

        Assert.False(EmptyFolder.Remove(file));

        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public async Task UndoOfPack_KeepsTheFolderWhenSomethingElseWasPutInIt()
    {
        var note = Path.Combine(_root, "notes.txt");
        File.WriteAllText(note, "notes");
        var ipc = RealFileIpc();
        var workspace = await WorkspaceAtRootAsync(ipc);

        await workspace.PackIntoFolderAsync([note], "Packed");
        var packed = Path.Combine(_root, "Packed");
        Assert.True(File.Exists(Path.Combine(packed, "notes.txt")));
        var added = Path.Combine(packed, "added-later.txt");
        File.WriteAllText(added, "added");

        await workspace.Undo.UndoAsync();

        Assert.Equal("notes", File.ReadAllText(note));
        Assert.Equal("added", File.ReadAllText(added));
        Assert.Equal(0, ipc.DeleteEntryCalls);
    }

    [Fact]
    public async Task UndoOfPack_RemovesTheFolderWhenNothingElseIsInIt()
    {
        var note = Path.Combine(_root, "notes.txt");
        File.WriteAllText(note, "notes");
        var ipc = RealFileIpc();
        var workspace = await WorkspaceAtRootAsync(ipc);

        await workspace.PackIntoFolderAsync([note], "Packed");
        await workspace.Undo.UndoAsync();

        Assert.Equal("notes", File.ReadAllText(note));
        Assert.False(Directory.Exists(Path.Combine(_root, "Packed")));
    }

    [Fact]
    public async Task RedoOfPack_UsesTheFolderThatUndoKept()
    {
        var note = Path.Combine(_root, "notes.txt");
        File.WriteAllText(note, "notes");
        var ipc = RealFileIpc();
        var workspace = await WorkspaceAtRootAsync(ipc);
        await workspace.PackIntoFolderAsync([note], "Packed");
        var packed = Path.Combine(_root, "Packed");
        File.WriteAllText(Path.Combine(packed, "added-later.txt"), "added");
        await workspace.Undo.UndoAsync();

        await workspace.Undo.RedoAsync();

        Assert.Equal("notes", File.ReadAllText(Path.Combine(packed, "notes.txt")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(packed, "added-later.txt")));
        Assert.False(File.Exists(note));
    }

    [Fact]
    public async Task Flatten_KeepsTheFolderAndWhatCouldNotBeMovedOutOfIt()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Album")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var stuck = Path.Combine(folder, "stuck.txt");
        File.WriteAllText(stuck, "stuck");
        var ipc = RealFileIpc(leaveBehind: "stuck.txt");
        var workspace = await WorkspaceAtRootAsync(ipc);

        await workspace.UnpackFolderAsync(folder);

        Assert.Equal("a", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("stuck", File.ReadAllText(stuck));
        Assert.Equal(0, ipc.DeleteEntryCalls);
    }

    [Fact]
    public async Task Flatten_RemovesTheFolderWhenEverythingMovedOut()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Album")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var workspace = await WorkspaceAtRootAsync(RealFileIpc());

        await workspace.UnpackFolderAsync(folder);

        Assert.Equal("a", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task UndoOfFlatten_PutsTheFilesBackIntoTheFolderThatWasKept()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Album")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var stuck = Path.Combine(folder, "stuck.txt");
        File.WriteAllText(stuck, "stuck");
        var workspace = await WorkspaceAtRootAsync(RealFileIpc(leaveBehind: "stuck.txt"));
        await workspace.UnpackFolderAsync(folder);

        await workspace.Undo.UndoAsync();

        Assert.Equal("a", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal("stuck", File.ReadAllText(stuck));
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task RedoOfFlatten_KeepsTheFolderAndWhatCouldNotBeMovedOutOfIt()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Album")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var ipc = RealFileIpc();
        var workspace = await WorkspaceAtRootAsync(ipc);
        await workspace.UnpackFolderAsync(folder);
        await workspace.Undo.UndoAsync();
        var stuck = Path.Combine(folder, "stuck.txt");
        File.WriteAllText(stuck, "stuck");
        ipc.LeaveBehind = "stuck.txt";

        await workspace.Undo.RedoAsync();

        Assert.Equal("a", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("stuck", File.ReadAllText(stuck));
        Assert.Equal(0, ipc.DeleteEntryCalls);
    }

    [Fact]
    public async Task UndoOfPack_InsideAnArchive_KeepsAFolderThatListsSomething()
    {
        const string archive = @"C:\data\photos.zip";
        const string packed = archive + @"\Packed";
        var backend = FakeExplorerBackend.Typical();
        backend.Home = archive;
        backend.Listings[archive] = new DirectoryListing { Path = archive, Entries = [] };
        backend.Listings[packed] = new DirectoryListing
        {
            Path = packed,
            Parent = archive,
            Entries = [new FileEntry { Name = "added-later.txt", Path = packed + @"\added-later.txt" }],
        };
        var ipc = ArchiveService(packed);
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc.Ipc));
        await workspace.InitializeAsync();
        await workspace.PackIntoFolderAsync([archive + @"\notes.txt"], "Packed");

        await workspace.Undo.UndoAsync();

        Assert.Equal(0, ipc.DeleteEntryCalls);
    }

    [Fact]
    public async Task UndoOfPack_InsideAnArchive_RemovesAFolderThatListsNothing()
    {
        const string archive = @"C:\data\photos.zip";
        const string packed = archive + @"\Packed";
        var backend = FakeExplorerBackend.Typical();
        backend.Home = archive;
        backend.Listings[archive] = new DirectoryListing { Path = archive, Entries = [] };
        backend.Listings[packed] = new DirectoryListing { Path = packed, Parent = archive, Entries = [] };
        var ipc = ArchiveService(packed);
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc.Ipc));
        await workspace.InitializeAsync();
        await workspace.PackIntoFolderAsync([archive + @"\notes.txt"], "Packed");

        await workspace.Undo.UndoAsync();

        Assert.Equal([packed], ipc.DeletedEntries);
    }

    /// <summary>A fake service for paths inside an archive: nothing is on disk, the moves only answer.</summary>
    private static FakeService ArchiveService(string createdFolder)
    {
        var service = new FakeService();
        service.Ipc.CreateDirectoryHandler = (_, _, _) => Task.FromResult(createdFolder);
        service.Ipc.MoveWithProgressHandler = (sources, destination, _, _, _) => Task.FromResult(sources
            .Select(source => new TransferResult
            {
                Source = source,
                Destination = destination + "\\" + PathRules.Basename(source),
            })
            .ToArray());
        service.Ipc.MoveEntryResolvedHandler = (source, destination, _, _) =>
            Task.FromResult(destination + "\\" + PathRules.Basename(source));
        return service;
    }

    private async Task<ExplorerWorkspace> WorkspaceAtRootAsync(FakeService ipc)
    {
        var backend = new FakeExplorerBackend
        {
            Home = _root,
            ListDirectoryHandler = (path, _) => Task.FromResult(ListRealFolder(path)),
        };
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc.Ipc));
        await workspace.InitializeAsync();
        return workspace;
    }

    private static DirectoryListing ListRealFolder(string path) => new()
    {
        Path = path,
        Parent = Path.GetDirectoryName(path),
        Entries =
        [
            .. Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal).Select(entry => new FileEntry
            {
                Name = Path.GetFileName(entry),
                Path = entry,
                IsDir = Directory.Exists(entry),
            }),
        ],
    };

    /// <summary>A fake service that creates, moves and renames real files, and counts the deletes it is asked for.</summary>
    private static FakeService RealFileIpc(string? leaveBehind = null)
    {
        var ipc = new FakeService { LeaveBehind = leaveBehind };
        ipc.Ipc.CreateDirectoryHandler = (parent, name, _) =>
        {
            var path = Path.Combine(parent, name);
            if (Directory.Exists(path))
            {
                throw new IpcException(Protocol.ErrApplication, $"Directory already exists: {name}");
            }

            Directory.CreateDirectory(path);
            return Task.FromResult(path);
        };
        ipc.Ipc.MoveWithProgressHandler = (sources, destination, _, _, _) =>
        {
            var results = new List<TransferResult>();
            foreach (var source in sources)
            {
                if (Path.GetFileName(source) == ipc.LeaveBehind)
                {
                    continue;
                }

                var target = Path.Combine(destination, Path.GetFileName(source));
                Directory.Move(source, target);
                results.Add(new TransferResult { Source = source, Destination = target });
            }

            return Task.FromResult(results.ToArray());
        };
        ipc.Ipc.MoveEntryResolvedHandler = (source, destination, _, _) =>
        {
            var target = Path.Combine(destination, Path.GetFileName(source));
            Directory.Move(source, target);
            return Task.FromResult(target);
        };
        return ipc;
    }

    /// <summary>The fake service of a test, with the deletes it was asked for.</summary>
    private sealed class FakeService
    {
        public FakeService()
        {
            Ipc.DeleteEntryHandler = (path, _) =>
            {
                DeletedEntries.Add(path);
                if (Directory.Exists(path))
                {
                    // What the service does with a folder: all of it, for good.
                    Directory.Delete(path, recursive: true);
                }

                return Task.CompletedTask;
            };
        }

        public ConfigurableIpc Ipc { get; } = new();
        public string? LeaveBehind { get; set; }
        public List<string> DeletedEntries { get; } = [];
        public int DeleteEntryCalls => DeletedEntries.Count;
    }
}
