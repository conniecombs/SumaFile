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
}
