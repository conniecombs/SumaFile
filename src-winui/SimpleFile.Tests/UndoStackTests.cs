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

public class UndoStackTests
{
    [Fact]
    public async Task UndoStack_UndoThenRedo_InvokesInOrder()
    {
        var log = new List<string>();
        var stack = new UndoStack();
        stack.Push(new UndoEntry
        {
            Description = "Copy 1 item(s)",
            Undo = _ => { log.Add("undo"); return Task.CompletedTask; },
            Redo = _ => { log.Add("redo"); return Task.CompletedTask; },
        });

        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
        await stack.UndoAsync();
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
        await stack.RedoAsync();
        Assert.Equal(["undo", "redo"], log);
        Assert.Equal("Copy 1 item(s)", stack.History.Single());
    }
    [Fact]
    public async Task UndoStack_UndoCancellationKeepsEntry()
    {
        var stack = new UndoStack();
        stack.Push(new UndoEntry
        {
            Description = "Copy 1 item(s)",
            Undo = _ => throw new OperationCanceledException(),
            Redo = _ => Task.CompletedTask,
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => stack.UndoAsync());

        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Equal("Copy 1 item(s)", stack.NextUndoDescription);
    }
    [Fact]
    public async Task UndoStack_RedoFailureKeepsEntry()
    {
        var stack = new UndoStack();
        stack.Push(new UndoEntry
        {
            Description = "Move 1 item(s)",
            Undo = _ => Task.CompletedTask,
            Redo = _ => throw new InvalidOperationException("redo failed"),
        });

        await stack.UndoAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stack.RedoAsync());

        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
        Assert.Equal("Move 1 item(s)", stack.NextRedoDescription);
    }

    [Fact]
    public async Task PushCreate_UndoTrashesCreatedItemAndRedoRestoresIt()
    {
        var log = new List<string>();
        var ipc = new ConfigurableIpc
        {
            MoveToTrashHandler = (paths, ct) =>
            {
                log.Add($"trash:{string.Join("|", paths)}");
                return Task.FromResult(new[] { @"C:\$Recycle.Bin\S-1-5-21-1\$R123" });
            },
            RestoreRecycleBinHandler = (paths, ct) =>
            {
                log.Add($"restore:{string.Join("|", paths)}");
                return Task.FromResult(new[] { @"C:\Users\test\New Folder" });
            },
        };
        var stack = new UndoStack();

        stack.PushCreate(
            @"C:\Users\test",
            "New Folder",
            @"C:\Users\test\New Folder",
            isDirectory: true,
            new FileOperationService(ipc));

        await stack.UndoAsync();
        await stack.RedoAsync();

        Assert.Equal(
            [
                @"trash:C:\Users\test\New Folder",
                @"restore:C:\$Recycle.Bin\S-1-5-21-1\$R123",
            ],
            log);
        Assert.Equal("Create folder", stack.History.Single());
    }

    [Fact]
    public async Task PushCreate_RedoFallsBackToCreateWhenRecyclePathIsUnavailable()
    {
        var log = new List<string>();
        var ipc = new ConfigurableIpc
        {
            MoveToTrashHandler = (paths, ct) =>
            {
                log.Add($"trash:{paths[0]}");
                return Task.FromResult(Array.Empty<string>());
            },
            CreateFileHandler = (path, name, ct) =>
            {
                log.Add($"create-file:{path}|{name}");
                return Task.FromResult($@"{path}\{name}");
            },
        };
        var stack = new UndoStack();

        stack.PushCreate(
            @"C:\Users\test",
            "notes.txt",
            @"C:\Users\test\notes.txt",
            isDirectory: false,
            new FileOperationService(ipc));

        await stack.UndoAsync();
        await stack.RedoAsync();

        Assert.Equal(
            [
                @"trash:C:\Users\test\notes.txt",
                @"create-file:C:\Users\test|notes.txt",
            ],
            log);
        Assert.Equal("Create file", stack.History.Single());
    }

    [Fact]
    public async Task PushRename_UndoAndRedoRenameCurrentPath()
    {
        var log = new List<string>();
        var ipc = new ConfigurableIpc
        {
            RenameEntryHandler = (path, newName, ct) =>
            {
                log.Add($"rename:{path}|{newName}");
                return Task.FromResult($@"C:\Users\test\{newName}");
            },
        };
        var stack = new UndoStack();

        stack.PushRename(
            @"C:\Users\test\old.txt",
            @"C:\Users\test\new.txt",
            new FileOperationService(ipc));

        await stack.UndoAsync();
        await stack.RedoAsync();

        Assert.Equal(
            [
                @"rename:C:\Users\test\new.txt|old.txt",
                @"rename:C:\Users\test\old.txt|new.txt",
            ],
            log);
        Assert.Equal("Rename 1 item(s)", stack.History.Single());
    }

    [Fact]
    public async Task PushRename_BatchUndoAndRedoUseBatchRename()
    {
        var batches = new List<string>();
        var ipc = new ConfigurableIpc
        {
            BatchRenameHandler = (entries, ct) =>
            {
                batches.Add(string.Join(",", entries.Select(entry => $"{entry.Path}->{entry.NewName}")));
                return Task.FromResult(entries.Select(entry =>
                {
                    var parent = PathRules.GetParentPath(entry.Path) ?? "";
                    return PathRules.JoinPath(parent, entry.NewName);
                }).ToArray());
            },
        };
        var stack = new UndoStack();

        stack.PushRename(
            [@"C:\Users\test\a.txt", @"C:\Users\test\b.txt"],
            [@"C:\Users\test\b.txt", @"C:\Users\test\a.txt"],
            new FileOperationService(ipc));

        await stack.UndoAsync();
        await stack.RedoAsync();

        Assert.Equal(
            [
                @"C:\Users\test\b.txt->a.txt,C:\Users\test\a.txt->b.txt",
                @"C:\Users\test\a.txt->b.txt,C:\Users\test\b.txt->a.txt",
            ],
            batches);
    }

    [Fact]
    public async Task PushTrash_UndoRestoresRecycleItemsAndRedoTrashesRestoredPaths()
    {
        var log = new List<string>();
        var ipc = new ConfigurableIpc
        {
            RestoreRecycleBinHandler = (paths, ct) =>
            {
                log.Add($"restore:{string.Join("|", paths)}");
                return Task.FromResult(new[] { @"C:\Users\test\notes.txt" });
            },
            MoveToTrashHandler = (paths, ct) =>
            {
                log.Add($"trash:{string.Join("|", paths)}");
                return Task.FromResult(new[] { @"C:\$Recycle.Bin\S-1-5-21-1\$R456" });
            },
        };
        var stack = new UndoStack();

        stack.PushTrash(
            [@"C:\Users\test\notes.txt"],
            [@"C:\$Recycle.Bin\S-1-5-21-1\$R123"],
            new FileOperationService(ipc));

        await stack.UndoAsync();
        await stack.RedoAsync();

        Assert.Equal(
            [
                @"restore:C:\$Recycle.Bin\S-1-5-21-1\$R123",
                @"trash:C:\Users\test\notes.txt",
            ],
            log);
        Assert.Equal("Move to Recycle Bin 1 item(s)", stack.History.Single());
    }
}
