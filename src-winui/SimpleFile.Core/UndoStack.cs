using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class UndoEntry
{
    public required string Description { get; init; }
    public required Func<CancellationToken, Task> Undo { get; init; }
    public required Func<CancellationToken, Task> Redo { get; init; }
}

/// <summary>
/// Copy/move/delete undo stack matching frontend/src/lib/transferUndo.ts.
/// </summary>
public sealed class UndoStack
{
    private readonly Stack<UndoEntry> _undo = new();
    private readonly Stack<UndoEntry> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;
    public string? NextRedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;
    public IReadOnlyList<string> History => _undo.Reverse().Select(entry => entry.Description).ToList();

    public void Push(UndoEntry entry)
    {
        _undo.Push(entry);
        _redo.Clear();
    }

    public void PushCopy(IReadOnlyList<TransferResult> transferred, FileOperationService ops)
    {
        var items = transferred.ToArray();
        Push(new UndoEntry
        {
            Description = $"Copy {items.Length} item(s)",
            Undo = ct => ops.TrashAsync(items.Select(item => item.Destination).ToArray(), ct),
            Redo = async ct =>
            {
                foreach (var item in items)
                {
                    var parent = PathRules.GetParentPath(item.Destination) ?? item.Destination;
                    await ops.CopyEntryResolvedAsync(item.Source, parent, "rename", ct).ConfigureAwait(false);
                }
            },
        });
    }

    public void PushMove(IReadOnlyList<TransferResult> transferred, FileOperationService ops)
    {
        var items = transferred.ToArray();
        Push(new UndoEntry
        {
            Description = $"Move {items.Length} item(s)",
            Undo = async ct =>
            {
                foreach (var item in Enumerable.Reverse(items))
                {
                    var parent = PathRules.GetParentPath(item.Source);
                    if (parent is null)
                    {
                        continue;
                    }

                    await ops.MoveEntryResolvedAsync(item.Destination, parent, "rename", ct).ConfigureAwait(false);
                }
            },
            Redo = async ct =>
            {
                foreach (var item in items)
                {
                    var parent = PathRules.GetParentPath(item.Destination) ?? item.Destination;
                    await ops.MoveEntryResolvedAsync(item.Source, parent, "rename", ct).ConfigureAwait(false);
                }
            },
        });
    }

    public async Task UndoAsync(CancellationToken cancellationToken = default)
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var entry = _undo.Pop();
        try
        {
            await entry.Undo(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _undo.Push(entry);
            throw;
        }

        _redo.Push(entry);
    }

    public async Task RedoAsync(CancellationToken cancellationToken = default)
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var entry = _redo.Pop();
        try
        {
            await entry.Redo(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _redo.Push(entry);
            throw;
        }

        _undo.Push(entry);
    }
}
