using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class UndoEntry
{
    public required string Description { get; init; }
    public required Func<CancellationToken, Task> Undo { get; init; }
    public required Func<CancellationToken, Task> Redo { get; init; }
}

/// <summary>
/// File operation undo stack matching the README Ctrl+Z/Ctrl+Y contract.
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
    public IReadOnlyList<UndoEntry> Entries => _undo.Reverse().ToList();

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

    public void PushCreate(
        string parentPath,
        string name,
        string createdPath,
        bool isDirectory,
        FileOperationService ops)
    {
        var currentPath = createdPath;
        string? recyclePath = null;
        Push(new UndoEntry
        {
            Description = isDirectory ? "Create folder" : "Create file",
            Undo = async ct =>
            {
                var recyclePaths = await ops.TrashAsync([currentPath], ct).ConfigureAwait(false);
                recyclePath = recyclePaths.FirstOrDefault();
            },
            Redo = async ct =>
            {
                if (!string.IsNullOrEmpty(recyclePath))
                {
                    var restored = await ops.RestoreRecycleBinAsync([recyclePath], ct).ConfigureAwait(false);
                    currentPath = restored.FirstOrDefault() ?? currentPath;
                    recyclePath = null;
                    return;
                }

                currentPath = isDirectory
                    ? await ops.CreateFolderAsync(parentPath, name, ct).ConfigureAwait(false)
                    : await ops.CreateFileAsync(parentPath, name, ct).ConfigureAwait(false);
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

    public void PushRename(string originalPath, string renamedPath, FileOperationService ops)
    {
        PushRename([originalPath], [renamedPath], ops);
    }

    public void PushRename(
        IReadOnlyList<string> originalPaths,
        IReadOnlyList<string> renamedPaths,
        FileOperationService ops)
    {
        var originals = originalPaths.ToArray();
        var renamed = renamedPaths.ToArray();
        if (originals.Length == 0 || originals.Length != renamed.Length)
        {
            return;
        }

        var currentPaths = renamed.ToArray();
        Push(new UndoEntry
        {
            Description = $"Rename {originals.Length} item(s)",
            Undo = async ct =>
            {
                currentPaths = await RenameAllAsync(
                    currentPaths,
                    originals.Select(PathRules.Basename).ToArray(),
                    ops,
                    ct).ConfigureAwait(false);
            },
            Redo = async ct =>
            {
                currentPaths = await RenameAllAsync(
                    currentPaths,
                    renamed.Select(PathRules.Basename).ToArray(),
                    ops,
                    ct).ConfigureAwait(false);
            },
        });
    }

    public void PushTrash(
        IReadOnlyList<string> originalPaths,
        IReadOnlyList<string> recycleBinPaths,
        FileOperationService ops)
    {
        var currentPaths = originalPaths.ToArray();
        var recyclePaths = recycleBinPaths.ToArray();
        if (currentPaths.Length == 0 || recyclePaths.Length != currentPaths.Length)
        {
            return;
        }

        Push(new UndoEntry
        {
            Description = $"Move to Recycle Bin {currentPaths.Length} item(s)",
            Undo = async ct =>
            {
                currentPaths = await ops.RestoreRecycleBinAsync(recyclePaths, ct).ConfigureAwait(false);
            },
            Redo = async ct =>
            {
                recyclePaths = await ops.TrashAsync(currentPaths, ct).ConfigureAwait(false);
                if (recyclePaths.Length != currentPaths.Length)
                {
                    throw new InvalidOperationException("Redo could not locate the Recycle Bin items.");
                }
            },
        });
    }

    private static async Task<string[]> RenameAllAsync(
        IReadOnlyList<string> paths,
        IReadOnlyList<string> newNames,
        FileOperationService ops,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 1)
        {
            return
            [
                await ops.RenameAsync(paths[0], newNames[0], cancellationToken).ConfigureAwait(false),
            ];
        }

        var requests = paths.Select((path, index) => new RenameRequest
        {
            Path = path,
            NewName = newNames[index],
        }).ToArray();
        return await ops.BatchRenameAsync(requests, cancellationToken).ConfigureAwait(false);
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
