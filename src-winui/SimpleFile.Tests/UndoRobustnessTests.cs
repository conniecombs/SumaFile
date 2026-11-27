using System.Text.Json;
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
    private readonly List<string[]> _restoreCalls = [];
    private readonly List<TransferItemRequest[]> _moveCalls = [];
    private readonly List<string> _moveFolders = [];
    private readonly List<TransferItemRequest[]> _copyCalls = [];
    /// <summary>Paths the fake service cannot send to the Recycle Bin or move, as if another program held them.</summary>
    private readonly HashSet<string> _locked = new(StringComparer.Ordinal);
    /// <summary>Original paths whose Recycle Bin item the fake service cannot restore for now.</summary>
    private readonly HashSet<string> _unrestorable = new(StringComparer.Ordinal);
    /// <summary>False: the fake service trashes a file but does not find its Recycle Bin item afterwards.</summary>
    private bool _recycleItemsFound = true;

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

    /// <summary>
    /// B6: the panes are refreshed after the Undo succeeded, outside the handler that offers to remove a step that
    /// could not be undone. A refresh error used to be shown as a failed Undo, with a Remove button that did nothing.
    /// </summary>
    [Fact]
    public void TheApp_ReportsARefreshErrorAfterAnUndoOnItsOwn()
    {
        var transfer = File.ReadAllText(Path.Combine(UiAuditSprint3Tests.FindRepoRoot(), "SimpleFile.App", "MainWindow.Transfer.cs"));
        var start = transfer.IndexOf("private async Task UndoLastAsync()", StringComparison.Ordinal);
        var end = transfer.IndexOf("private async Task RedoLastAsync()", StringComparison.Ordinal);
        var method = transfer[start..end];

        var failure = method.IndexOf("ShowUndoFailure(", StringComparison.Ordinal);
        var refresh = method.IndexOf("RefreshAffectedPanesAsync(", StringComparison.Ordinal);
        Assert.True(failure >= 0 && refresh > failure, "a refresh error must not reach ShowUndoFailure");
        Assert.Equal(failure, method.LastIndexOf("ShowUndoFailure(", StringComparison.Ordinal));
        Assert.Contains("catch (Exception", method[refresh..]);
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
    public async Task CopyUndo_ThatMeetsACopyThatIsGoneRemovesTheOthersInTheSameCall()
    {
        var gone = Path.Combine(_root, "dest", "gone.txt");
        var kept = Write("dest", "kept.txt");
        var stack = new UndoStack();
        // Like the service, the fake goes on past the copy it cannot remove, then reports the failure.
        stack.PushCopy(
            [Result("src/gone.txt", gone), Result("src/kept.txt", kept)],
            new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        Assert.Single(_trashCalls);
        Assert.False(File.Exists(kept));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task CopyUndo_ThatFailsPartWayGoesOnWithTheCopyThatIsLeft()
    {
        var first = Write("dest", "a.txt");
        var second = Write("dest", "b.txt");
        var stack = new UndoStack();
        stack.PushCopy(
            [Result("src/a.txt", first), Result("src/b.txt", second)],
            new FileOperationService(RealFileIpc()));
        _locked.Add(second);

        await Assert.ThrowsAsync<IpcException>(() => stack.UndoAsync());
        Assert.False(File.Exists(first));
        Assert.True(stack.CanUndo);
        _locked.Clear();
        await stack.UndoAsync();

        Assert.Equal([second], _trashCalls[^1]);
        Assert.False(File.Exists(second));
        Assert.False(stack.CanUndo);
    }

    /// <summary>
    /// B2: Redo makes every copy again, so the next Undo removes every one, also the copies an earlier Undo
    /// that failed had already removed.
    /// </summary>
    [Fact]
    public async Task CopyUndoRedoUndo_AfterAnUndoThatFailedPartWayRemovesEveryCopy()
    {
        Write("src", "a.txt");
        Write("src", "b.txt");
        var first = Write("dest", "a.txt");
        var second = Write("dest", "b.txt");
        var stack = new UndoStack();
        stack.PushCopy(
            [Result("src/a.txt", first), Result("src/b.txt", second)],
            new FileOperationService(RealFileIpc()));
        _locked.Add(second);
        await Assert.ThrowsAsync<IpcException>(() => stack.UndoAsync());
        _locked.Clear();
        await stack.UndoAsync();

        await stack.RedoAsync();
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        await stack.UndoAsync();

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    /// <summary>B2: a copy that was already gone at the first Undo is made again by Redo, so the next Undo removes it.</summary>
    [Fact]
    public async Task CopyUndoRedoUndo_OfACopyThatWasGoneRemovesTheCopyRedoMade()
    {
        Write("src", "a.txt");
        Directory.CreateDirectory(Path.Combine(_root, "dest"));
        var copy = Path.Combine(_root, "dest", "a.txt");
        var stack = new UndoStack();
        stack.PushCopy([Result("src/a.txt", copy)], new FileOperationService(RealFileIpc()));
        await stack.UndoAsync();

        await stack.RedoAsync();
        Assert.True(File.Exists(copy));
        await stack.UndoAsync();

        Assert.False(File.Exists(copy));
    }

    /// <summary>B1: a copy that could not be checked after a failed Undo may still be there; the step is not done.</summary>
    [Fact]
    public async Task CopyUndo_ThatFailsOnACopyThatCouldNotBeCheckedIsReported()
    {
        var copy = Write("dest", "a.txt");
        var ipc = RealFileIpc();
        ipc.MoveToTrashHandler = (_, _) => throw new IpcException(Protocol.ErrApplication, "the share is offline");
        var stack = new UndoStack { Existence = _ => PathExistence.Unknown };
        stack.PushCopy([Result("src/a.txt", copy)], new FileOperationService(ipc));

        await Assert.ThrowsAsync<IpcException>(() => stack.UndoAsync());

        Assert.True(stack.CanUndo);
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

    /// <summary>B1: an item whose check failed (a path over 260 characters, a share that is offline) may still be there.</summary>
    [Fact]
    public async Task MoveUndo_ThatFailsOnAnItemThatCouldNotBeCheckedIsReported()
    {
        var moved = Write("dest", "moved.txt");
        var ipc = RealFileIpc();
        ipc.MoveEntryResolvedHandler = (_, _, _, _) => throw new IpcException(Protocol.ErrApplication, "the share is offline");
        var stack = new UndoStack { Existence = _ => PathExistence.Unknown };
        stack.PushMove([Result("src/moved.txt", moved)], new FileOperationService(ipc));

        await Assert.ThrowsAsync<IpcException>(() => stack.UndoAsync());

        Assert.True(stack.CanUndo);
    }

    [Fact]
    public async Task CreateUndo_ThatFailsOnAnItemThatCouldNotBeCheckedIsReported()
    {
        var path = Write("", "new.txt");
        var ipc = RealFileIpc();
        ipc.MoveToTrashHandler = (_, _) => throw new IpcException(Protocol.ErrApplication, "access is denied");
        var stack = new UndoStack { Existence = _ => PathExistence.Unknown };
        stack.PushCreate(_root, "new.txt", path, isDirectory: false, new FileOperationService(ipc));

        await Assert.ThrowsAsync<IpcException>(() => stack.UndoAsync());

        Assert.True(stack.CanUndo);
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
        var stack = new UndoStack();
        stack.PushCopy([Merged("src", destination, ["a.txt", "b.txt"], [])], new FileOperationService(RealFileIpc()));
        // The service goes past a path it cannot remove: one file is gone, one is not.
        _locked.Add(first);

        await Assert.ThrowsAnyAsync<Exception>(() => stack.UndoAsync());
        Assert.True(stack.CanUndo);
        Assert.Equal([first], new[] { first, second }.Where(File.Exists));

        _locked.Clear();
        await stack.UndoAsync();

        Assert.Equal([first], _trashCalls[^1]);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.False(stack.CanUndo);
    }

    /// <summary>B1: a file whose check failed is still sent to the service; only a file that is missing counts as undone.</summary>
    [Fact]
    public async Task MergedCopyUndo_SendsAFileThatCouldNotBeCheckedAndLeavesOutOnlyAMissingOne()
    {
        var destination = Path.Combine(_root, "dest");
        var first = Write("dest", "a.txt");
        var unchecked_ = Write("dest", "unchecked.txt");
        var stack = new UndoStack
        {
            Existence = path => path == unchecked_ ? PathExistence.Unknown : NativePath.Existence(path),
        };
        stack.PushCopy(
            [Merged("src", destination, ["a.txt", "unchecked.txt", "gone.txt"], [])],
            new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        Assert.Equal([unchecked_, first], Assert.Single(_trashCalls));
        Assert.False(File.Exists(unchecked_));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task MergedMoveUndo_SendsAFileThatCouldNotBeChecked()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var destination = Path.Combine(_root, "dest");
        var unchecked_ = Write("dest", "unchecked.txt");
        var stack = new UndoStack
        {
            Existence = path => path == unchecked_ ? PathExistence.Unknown : NativePath.Existence(path),
        };
        stack.PushMove([Merged(source, destination, ["unchecked.txt"], [])], new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        Assert.Single(Assert.Single(_moveCalls));
        Assert.True(File.Exists(Path.Combine(source, "unchecked.txt")));
    }

    /// <summary>
    /// B3: a trash call that fails on one file still says which files it sent to the Recycle Bin, so Redo brings
    /// those back from it instead of copying them again from sources that may have changed.
    /// </summary>
    [Fact]
    public async Task MergedCopyUndo_ThatFailsPartWayKeepsTheRecycleBinItemsOfTheFilesItRemoved()
    {
        var destination = Path.Combine(_root, "dest");
        var files = new[] { "a.txt", "b.txt", "c.txt" };
        foreach (var file in files)
        {
            Write("dest", file);
            File.WriteAllText(Write("src", file), "changed since");
        }

        var stack = new UndoStack();
        stack.PushCopy([Merged("src", destination, files, [])], new FileOperationService(RealFileIpc()));
        _locked.Add(Path.Combine(destination, "b.txt"));
        await Assert.ThrowsAnyAsync<Exception>(() => stack.UndoAsync());
        _locked.Clear();
        await stack.UndoAsync();

        await stack.RedoAsync();

        Assert.Empty(_copyCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_recycle));
        foreach (var file in files) Assert.Equal(file, File.ReadAllText(Path.Combine(destination, file)));
    }

    /// <summary>
    /// B3: a restore that fails on one file still says which files it restored. The next Redo restores only what
    /// is left; it used to meet the first restored file as "already exists" every time.
    /// </summary>
    [Fact]
    public async Task MergedCopyRedo_ThatRestoresPartOfTheFilesGoesOnWithTheRestNextTime()
    {
        var destination = Path.Combine(_root, "dest");
        var files = new[] { "a.txt", "b.txt", "c.txt" };
        foreach (var file in files) Write("dest", file);
        var stack = new UndoStack();
        stack.PushCopy([Merged("src", destination, files, [])], new FileOperationService(RealFileIpc()));
        await stack.UndoAsync();
        _unrestorable.Add(Path.Combine(destination, "b.txt"));

        await Assert.ThrowsAnyAsync<Exception>(() => stack.RedoAsync());
        Assert.True(stack.CanRedo);
        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
        _unrestorable.Clear();
        await stack.RedoAsync();

        Assert.Single(_restoreCalls[^1]);
        foreach (var file in files) Assert.Equal(file, File.ReadAllText(Path.Combine(destination, file)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_recycle));
        Assert.True(stack.CanUndo);
    }

    /// <summary>
    /// B4: a file the user put at a copy's place between Undo and Redo is in the way of Redo, every time; it does
    /// not count as the copy, which the next Undo would remove.
    /// </summary>
    [Fact]
    public async Task MergedCopyRedo_DoesNotTakeAFileTheUserPutThereForTheCopy()
    {
        var destination = Path.Combine(_root, "dest");
        Write("src", "a.txt");
        var copy = Write("dest", "a.txt");
        // The Recycle Bin item is not found, so Redo copies the file again.
        _recycleItemsFound = false;
        var stack = new UndoStack();
        stack.PushCopy([Merged("src", destination, ["a.txt"], [])], new FileOperationService(RealFileIpc()));
        await stack.UndoAsync();
        File.WriteAllText(copy, "the user's own");

        await Assert.ThrowsAnyAsync<Exception>(() => stack.RedoAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => stack.RedoAsync());

        Assert.Equal("the user's own", File.ReadAllText(copy));
        File.Delete(copy);
        await stack.RedoAsync();
        Assert.Equal("a.txt", File.ReadAllText(copy));
        await stack.UndoAsync();
        Assert.False(File.Exists(copy));
    }

    /// <summary>B4: a file that is no longer where Redo would take it from is left out, and the step says so.</summary>
    [Fact]
    public async Task MergedMoveRedo_LeavesOutAFileWhoseSourceIsGoneAndSaysSo()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var destination = Path.Combine(_root, "dest");
        Write("dest", "a.txt");
        Write("dest", "b.txt");
        var stack = new UndoStack();
        stack.PushMove([Merged(source, destination, ["a.txt", "b.txt"], [])], new FileOperationService(RealFileIpc()));
        await stack.UndoAsync();
        File.Delete(Path.Combine(source, "a.txt"));

        await stack.RedoAsync();

        Assert.True(File.Exists(Path.Combine(destination, "b.txt")));
        Assert.True(stack.CanUndo);
        Assert.Contains("1 file", stack.LastNotice);
    }

    /// <summary>
    /// B5: each call of a move's Undo holds the files of one transferred item, and gets that item's folder. The
    /// first file of the next item used to go out with the previous item's files.
    /// </summary>
    [Fact]
    public async Task MergedMoveUndo_OfSeveralItemsSendsTheFilesOfEachInItsOwnCall()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "src", "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "src", "second")).FullName;
        foreach (var file in new[] { "first/1.txt", "first/2.txt", "second/1.txt", "second/2.txt" }) Write("dest", file);
        var stack = new UndoStack();
        stack.PushMove(
            [
                Merged(first, Path.Combine(_root, "dest", "first"), ["1.txt", "2.txt"], []),
                Merged(second, Path.Combine(_root, "dest", "second"), ["1.txt", "2.txt"], []),
            ],
            new FileOperationService(RealFileIpc()));

        await stack.UndoAsync();

        Assert.Equal([second, first], _moveFolders);
        Assert.Equal([2, 2], _moveCalls.Select(call => call.Length));
        foreach (var (call, folder) in _moveCalls.Zip(_moveFolders))
            Assert.All(call, item => Assert.Equal(folder, Path.GetDirectoryName(item.Destination)));
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

    /// <summary>
    /// A fake service that trashes, restores, moves and copies real files, and records its calls. Like the service,
    /// it goes on past a path it cannot handle: move_to_trash then fails without saying what it did,
    /// restore_recycle_bin stops at the first item it cannot restore, the _each calls answer for every path, and a
    /// create-only transfer reports what it committed and what failed in the error's data.
    /// </summary>
    private ConfigurableIpc RealFileIpc()
    {
        var ipc = new ConfigurableIpc();
        ipc.MoveToTrashHandler = (paths, _) =>
        {
            _trashCalls.Add(paths);
            var outcomes = paths.Select(TrashOne).ToArray();
            var failures = outcomes.Where(outcome => !outcome.Done).Select(outcome => outcome.Error).ToArray();
            if (failures.Length > 0) throw new IpcException(Protocol.ErrApplication, string.Join("; ", failures));
            return Task.FromResult(outcomes.Where(outcome => outcome.Result is not null).Select(outcome => outcome.Result!).ToArray());
        };
        ipc.MoveToTrashEachHandler = (paths, _) =>
        {
            _trashCalls.Add(paths);
            return Task.FromResult(paths.Select(TrashOne).ToArray());
        };
        ipc.RestoreRecycleBinHandler = (paths, _) =>
        {
            _restoreCalls.Add(paths);
            var restored = new List<string>();
            foreach (var path in paths)
            {
                var outcome = RestoreOne(path);
                if (!outcome.Done) throw new IpcException(Protocol.ErrApplication, outcome.Error!);
                restored.Add(outcome.Result!);
            }

            return Task.FromResult(restored.ToArray());
        };
        ipc.RestoreRecycleBinEachHandler = (paths, _) =>
        {
            _restoreCalls.Add(paths);
            return Task.FromResult(paths.Select(RestoreOne).ToArray());
        };
        ipc.CreateDirectoryHandler = (parent, name, _) =>
            Task.FromResult(Directory.CreateDirectory(Path.Combine(parent, name)).FullName);
        ipc.CopyEntryResolvedHandler = (source, folder, _, _) =>
        {
            var target = Path.Combine(folder, Path.GetFileName(source));
            File.Copy(source, target);
            return Task.FromResult(target);
        };
        ipc.MoveItemsWithProgressHandler = (items, folder, operationId, _) =>
        {
            _moveCalls.Add(items);
            _moveFolders.Add(folder);
            return Task.FromResult(Transfer(items, operationId, File.Move));
        };
        ipc.CopyItemsWithProgressHandler = (items, _, operationId, _) =>
        {
            _copyCalls.Add(items);
            return Task.FromResult(Transfer(items, operationId, (from, to) => File.Copy(from, to)));
        };
        return ipc;
    }

    private PathOutcome TrashOne(string path)
    {
        if (_locked.Contains(path)) return new PathOutcome { Path = path, Error = $"{path}: in use" };
        if (!File.Exists(path)) return new PathOutcome { Path = path, Error = $"Path does not exist: {path}" };
        var target = Path.Combine(_recycle, Guid.NewGuid().ToString("N"));
        File.Move(path, target);
        _recycled[target] = path;
        return new PathOutcome { Path = path, Result = _recycleItemsFound ? target : null };
    }

    private PathOutcome RestoreOne(string item)
    {
        if (!_recycled.TryGetValue(item, out var original) || !File.Exists(item))
            return new PathOutcome { Path = item, Error = $"Recycle Bin item is no longer available: {item}" };
        if (_unrestorable.Contains(original))
            return new PathOutcome { Path = item, Error = "Could not read Recycle Bin info" };
        // Like the service, an item whose name is taken is restored under another one.
        var destination = original;
        for (var index = 1; File.Exists(destination); index++)
            destination = Path.Combine(Path.GetDirectoryName(original)!,
                $"{Path.GetFileNameWithoutExtension(original)} ({index}){Path.GetExtension(original)}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(item, destination);
        _recycled.Remove(item);
        return new PathOutcome { Path = item, Result = destination };
    }

    private TransferResult[] Transfer(TransferItemRequest[] items, string? operationId, Action<string, string> transfer)
    {
        var committed = new List<TransferResult>();
        var failed = new List<TransferFailure>();
        foreach (var item in items)
        {
            string? error = null;
            var kind = "error";
            if (File.Exists(item.Destination) || Directory.Exists(item.Destination))
            {
                kind = "conflict";
                error = $"destination exists: {item.Destination}";
            }
            else if (!File.Exists(item.Source)) error = $"Path does not exist: {item.Source}";
            else if (_locked.Contains(item.Source)) error = $"{item.Source}: in use";

            if (error is not null)
            {
                failed.Add(new TransferFailure { Source = item.Source, Destination = item.Destination, Kind = kind, Error = error });
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
            transfer(item.Source, item.Destination);
            committed.Add(new TransferResult { Source = item.Source, Destination = item.Destination });
        }

        if (failed.Count == 0) return committed.ToArray();
        var outcome = new TransferBatchOutcome
        {
            OperationId = operationId ?? "op", Status = "failed", Committed = [.. committed], Failed = [.. failed],
        };
        throw new IpcException(
            Protocol.ErrApplication,
            $"{failed.Count} of {items.Length} items failed: {failed[0].Error}",
            JsonSerializer.SerializeToElement(outcome));
    }
}
