using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Undo and Redo work on real folders here; the fake service moves real files. An undo step whose work is
/// already done counts as done, a merged transfer is undone in a few calls instead of one per file, and an
/// entry that cannot be undone can be dropped so that the older ones are reachable.
/// </summary>
public sealed class UndoRobustnessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sumafile-undo-").FullName;
    private readonly string _recycle;
    private readonly Dictionary<string, string> _recycled = [];
    private readonly List<string[]> _trashCalls = [];
    private readonly List<TransferItemRequest[]> _moveCalls = [];
    private readonly List<TransferItemRequest[]> _copyCalls = [];

    public UndoRobustnessTests()
    {
        _recycle = Directory.CreateDirectory(Path.Combine(_root, "recycle")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task DropNextUndo_RemovesTheEntryThatCannotBeUndoneAndReachesTheOlderOne()
    {
        var log = new List<string>();
        var stack = new UndoStack();
        stack.Push(Entry("older", log));
        stack.Push(new UndoEntry
        {
            Description = "stuck",
            Undo = _ => throw new IOException("cannot be undone"),
            Redo = _ => Task.CompletedTask,
        });
        await Assert.ThrowsAsync<IOException>(() => stack.UndoAsync());
        Assert.Equal("stuck", stack.NextUndoDescription);

        Assert.Equal("stuck", stack.DropNextUndo());

        Assert.Equal("older", stack.NextUndoDescription);
        await stack.UndoAsync();
        Assert.Equal(["undo older"], log);
        Assert.Null(stack.DropNextUndo());
    }

    [Fact]
    public void NextUndo_IsTheEntryThatUndoWouldRun()
    {
        var stack = new UndoStack();
        Assert.Null(stack.NextUndo);
        var first = Entry("first", []);
        var second = Entry("second", []);
        stack.Push(first);
        stack.Push(second);

        Assert.Same(second, stack.NextUndo);
        stack.DropNextUndo();
        Assert.Same(first, stack.NextUndo);
    }

    /// <summary>
    /// An Undo that fails is offered with a way to take that step out of the history. Without one, a step
    /// that can never succeed stood before every older step for as long as the window was open.
    /// </summary>
    [Fact]
    public void TheApp_OffersToRemoveAStepThatCouldNotBeUndone()
    {
        var app = Path.Combine(UiAuditSprint3Tests.FindRepoRoot(), "SimpleFile.App");
        var transfer = File.ReadAllText(Path.Combine(app, "MainWindow.Transfer.cs"));
        var start = transfer.IndexOf("private async Task UndoLastAsync()", StringComparison.Ordinal);
        var end = transfer.IndexOf("private async Task RedoLastAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("ShowUndoFailure(workspace, failed, exception.Message);", transfer[start..end]);

        var statusCenter = File.ReadAllText(Path.Combine(app, "MainWindow.StatusCenter.cs"));
        var method = statusCenter[statusCenter.IndexOf("private void ShowUndoFailure(", StringComparison.Ordinal)..];
        Assert.Contains("ReferenceEquals(workspace.Undo.NextUndo, failed)", method);
        Assert.Contains("workspace.Undo.DropNextUndo();", method);
        Assert.Contains("MessageBar.ActionButton = remove;", method);
    }

    [Fact]
    public async Task DropNextUndo_KeepsWhatCanBeRedone()
    {
        var log = new List<string>();
        var stack = new UndoStack();
        stack.Push(Entry("first", log));
        stack.Push(Entry("second", log));
        await stack.UndoAsync();

        stack.DropNextUndo();

        Assert.False(stack.CanUndo);
        Assert.Equal("second", stack.NextRedoDescription);
    }

    [Fact]
    public async Task CopyUndo_LeavesOutAnItemThatIsAlreadyGone()
    {
        var kept = Write("dest", "kept.txt");
        var gone = Path.Combine(_root, "dest", "gone.txt");
        var stack = new UndoStack();
        stack.PushCopy(
            [Result("src/kept.txt", kept), Result("src/gone.txt", gone)],
            new FileOperationService(RealFileIpc()));

        // The service refuses the copy that is gone; the step is done all the same.
        await stack.UndoAsync();

        Assert.False(File.Exists(kept));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task CopyUndo_ThatFailsOnACopyThatIsGoneGoesOnWithTheOthers()
    {
        var gone = Path.Combine(_root, "dest", "gone.txt");
        var kept = Write("dest", "kept.txt");
        var stack = new UndoStack();
        // The fake service stops at the first path it cannot remove, so the second copy is still there.
        stack.PushCopy(
            [Result("src/gone.txt", gone), Result("src/kept.txt", kept)],
            new FileOperationService(RealFileIpc()));

        await Assert.ThrowsAsync<IpcException>(() => stack.UndoAsync());
        Assert.True(stack.CanUndo);
        await stack.UndoAsync();

        Assert.Equal([kept], _trashCalls[^1]);
        Assert.False(File.Exists(kept));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task CopyUndo_OfCopiesThatAreAllGoneIsDone()
    {
        var stack = new UndoStack();
        stack.PushCopy(
            [Result("src/a.txt", Path.Combine(_root, "dest", "a.txt"))],
            new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task MoveUndo_CountsAnItemThatIsGoneFromItsDestinationAsDone()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var moved = Write("dest", "moved.txt");
        var gone = Path.Combine(_root, "dest", "gone.txt");
        var ipc = RealFileIpc();
        ipc.MoveEntryResolvedHandler = (from, toFolder, _, _) =>
        {
            var target = Path.Combine(toFolder, Path.GetFileName(from));
            File.Move(from, target);
            return Task.FromResult(target);
        };
        var stack = new UndoStack();
        stack.PushMove(
            [Result(Path.Combine(source, "moved.txt"), moved), Result(Path.Combine(source, "gone.txt"), gone)],
            new FileOperationService(ipc));

        await stack.UndoAsync();

        Assert.True(File.Exists(Path.Combine(source, "moved.txt")));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task CreateUndo_OfAnItemThatIsAlreadyGoneIsDone()
    {
        var stack = new UndoStack();
        stack.PushCreate(_root, "new.txt", Path.Combine(_root, "new.txt"), isDirectory: false,
            new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
    }

    [Fact]
    public async Task CreateUndo_ThatFailsOnAnItemThatIsStillThereIsReported()
    {
        var path = Write("", "new.txt");
        var ipc = RealFileIpc();
        ipc.MoveToTrashHandler = (_, _) => throw new IpcException(Protocol.ErrApplication, "in use");
        var stack = new UndoStack();
        stack.PushCreate(_root, "new.txt", path, isDirectory: false, new FileOperationService(ipc));

        await Assert.ThrowsAsync<IpcException>(() => stack.UndoAsync());

        Assert.True(stack.CanUndo);
    }

    [Fact]
    public async Task MergedCopyUndo_SendsTheFilesToTheRecycleBinInOneCall()
    {
        var destination = Path.Combine(_root, "dest");
        var files = Enumerable.Range(0, 40).Select(index => Path.Combine("sub", $"{index}.txt")).ToArray();
        foreach (var file in files) Write("dest", file);
        var stack = new UndoStack();
        stack.PushCopy([Merged("src", destination, files, ["sub"])], new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        var call = Assert.Single(_trashCalls);
        Assert.Equal(40, call.Length);
        Assert.False(Directory.Exists(Path.Combine(destination, "sub")));
    }

    [Fact]
    public async Task MergedCopyUndoAndRedo_BringEveryFileBack()
    {
        var destination = Path.Combine(_root, "dest");
        var files = Enumerable.Range(0, 5).Select(index => Path.Combine("sub", $"{index}.txt")).ToArray();
        foreach (var file in files) Write("dest", file);
        var stack = new UndoStack();
        stack.PushCopy([Merged("src", destination, files, ["sub"])], new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();
        await stack.RedoAsync();

        foreach (var file in files) Assert.True(File.Exists(Path.Combine(destination, file)), file);
        await stack.UndoAsync();
        foreach (var file in files) Assert.False(File.Exists(Path.Combine(destination, file)), file);
    }

    [Fact]
    public async Task MergedCopyUndo_LeavesOutAFileThatIsAlreadyGone()
    {
        var destination = Path.Combine(_root, "dest");
        var kept = Write("dest", "kept.txt");
        var stack = new UndoStack();
        stack.PushCopy(
            [Merged("src", destination, ["kept.txt", "gone.txt"], [])],
            new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        Assert.Equal([[kept]], _trashCalls);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task MergedCopyUndo_ThatFailsPartWayGoesOnWithWhatIsLeft()
    {
        var destination = Path.Combine(_root, "dest");
        var first = Write("dest", "a.txt");
        var second = Write("dest", "b.txt");
        var ipc = RealFileIpc();
        var fail = true;
        var trash = ipc.MoveToTrashHandler!;
        ipc.MoveToTrashHandler = async (paths, ct) =>
        {
            if (!fail) return await trash(paths, ct);
            // The service goes past a failed path and then reports the failure: one file is gone, one is not.
            await trash([paths[0]], ct);
            throw new IpcException(Protocol.ErrApplication, $"{paths[1]}: in use");
        };
        var stack = new UndoStack();
        stack.PushCopy([Merged("src", destination, ["a.txt", "b.txt"], [])], new FileOperationService(ipc));

        await Assert.ThrowsAsync<IpcException>(() => stack.UndoAsync());
        Assert.True(stack.CanUndo);
        var left = new[] { first, second }.Where(File.Exists).ToArray();
        Assert.Single(left);

        fail = false;
        await stack.UndoAsync();

        Assert.Equal(left, _trashCalls[^1]);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task MergedMoveUndo_MovesTheFilesBackInOneCall()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var destination = Path.Combine(_root, "dest");
        var files = Enumerable.Range(0, 40).Select(index => Path.Combine("sub", $"{index}.txt")).ToArray();
        foreach (var file in files) Write("dest", file);
        var stack = new UndoStack();
        stack.PushMove(
            [Merged(source, destination, files, ["sub"], removedSourceDirectories: ["sub"])],
            new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        var call = Assert.Single(_moveCalls);
        Assert.Equal(40, call.Length);
        Assert.All(call, item => Assert.Equal("create-only", item.Action));
        foreach (var file in files) Assert.True(File.Exists(Path.Combine(source, file)), file);
        Assert.False(Directory.Exists(Path.Combine(destination, "sub")));
    }

    [Fact]
    public async Task MergedMoveUndo_CountsAFileThatIsGoneFromItsDestinationAsDone()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var destination = Path.Combine(_root, "dest");
        Write("dest", "moved.txt");
        var stack = new UndoStack();
        stack.PushMove(
            [Merged(source, destination, ["moved.txt", "gone.txt"], [])],
            new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        Assert.Single(Assert.Single(_moveCalls));
        Assert.True(File.Exists(Path.Combine(source, "moved.txt")));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task MergedMoveRedo_MovesTheFilesAgainInOneCall()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var destination = Path.Combine(_root, "dest");
        var files = Enumerable.Range(0, 10).Select(index => $"{index}.txt").ToArray();
        foreach (var file in files) Write("dest", file);
        var stack = new UndoStack();
        stack.PushMove([Merged(source, destination, files, [])], new FileOperationService(RealFileIpc()));
        await stack.UndoAsync();
        _moveCalls.Clear();

        await stack.RedoAsync();

        Assert.Equal(10, Assert.Single(_moveCalls).Length);
        foreach (var file in files) Assert.True(File.Exists(Path.Combine(destination, file)), file);
        foreach (var file in files) Assert.False(File.Exists(Path.Combine(source, file)), file);
    }

    private static UndoEntry Entry(string name, List<string> log) => new()
    {
        Description = name,
        Undo = _ => { log.Add($"undo {name}"); return Task.CompletedTask; },
        Redo = _ => { log.Add($"redo {name}"); return Task.CompletedTask; },
    };

    private string Write(string folder, string relative)
    {
        var path = Path.Combine(_root, folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
        return path;
    }

    private TransferResult Result(string source, string destination) => new()
    {
        Source = Path.IsPathRooted(source) ? source : Path.Combine(_root, source),
        Destination = destination,
    };

    private TransferResult Merged(
        string source,
        string destination,
        string[] files,
        string[] createdDirectories,
        string[]? removedSourceDirectories = null) => new()
    {
        Source = Path.IsPathRooted(source) ? source : Path.Combine(_root, source),
        Destination = destination,
        Changes = new TransferChanges
        {
            Files = files,
            CreatedDirectories = createdDirectories,
            RemovedSourceDirectories = removedSourceDirectories ?? [],
        },
    };

    /// <summary>A fake service that trashes, restores, moves and copies real files, and records its calls.</summary>
    private ConfigurableIpc RealFileIpc()
    {
        var ipc = new ConfigurableIpc();
        ipc.MoveToTrashHandler = (paths, _) =>
        {
            _trashCalls.Add(paths);
            var recycled = new List<string>();
            foreach (var path in paths)
            {
                if (!File.Exists(path))
                {
                    throw new IpcException(Protocol.ErrApplication, $"{path}: not found");
                }

                var target = Path.Combine(_recycle, Guid.NewGuid().ToString("N"));
                File.Move(path, target);
                _recycled[target] = path;
                recycled.Add(target);
            }

            return Task.FromResult(recycled.ToArray());
        };
        ipc.RestoreRecycleBinHandler = (paths, _) =>
        {
            var restored = new List<string>();
            foreach (var path in paths)
            {
                var original = _recycled[path];
                File.Move(path, original);
                restored.Add(original);
            }

            return Task.FromResult(restored.ToArray());
        };
        ipc.CreateDirectoryHandler = (parent, name, _) =>
            Task.FromResult(Directory.CreateDirectory(Path.Combine(parent, name)).FullName);
        ipc.MoveItemsWithProgressHandler = (items, _, _, _) =>
        {
            _moveCalls.Add(items);
            return Task.FromResult(Transfer(items, File.Move));
        };
        ipc.CopyItemsWithProgressHandler = (items, _, _, _) =>
        {
            _copyCalls.Add(items);
            return Task.FromResult(Transfer(items, (from, to) => File.Copy(from, to)));
        };
        return ipc;
    }

    private static TransferResult[] Transfer(TransferItemRequest[] items, Action<string, string> transfer)
    {
        var results = new List<TransferResult>();
        foreach (var item in items)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
            transfer(item.Source, item.Destination);
            results.Add(new TransferResult { Source = item.Source, Destination = item.Destination });
        }

        return results.ToArray();
    }
}
