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

    private static string Items(int count) => count == 1 ? "1 item" : $"{count} items";

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>The entry Undo would run next.</summary>
    public UndoEntry? NextUndo => _undo.Count > 0 ? _undo.Peek() : null;
    public string? NextUndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;
    public string? NextRedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;
    public IReadOnlyList<string> History => _undo.Reverse().Select(entry => entry.Description).ToList();
    public IReadOnlyList<UndoEntry> Entries => _undo.Reverse().ToList();

    public void Push(UndoEntry entry)
    {
        _undo.Push(entry);
        _redo.Clear();
    }

    /// <summary>
    /// Removes the entry that Undo would run next, without running it, and returns its description. For an
    /// entry that cannot be undone any more: it would otherwise stand before every older entry for good.
    /// What can be redone stays.
    /// </summary>
    public string? DropNextUndo() => _undo.Count > 0 ? _undo.Pop().Description : null;

    public void PushCopy(IReadOnlyList<TransferResult> transferred, FileOperationService ops)
    {
        if (transferred.Any(item => item.Changes is not null))
        {
            var entry = MergeTransferUndo.Create(transferred, ops, move: false);
            if (entry is not null) Push(entry);
            return;
        }

        var items = transferred.ToArray();
        var currentDestinations = items.Select(item => item.Destination).ToArray();
        // Set by an Undo that failed: the copies that were found gone afterwards.
        var removed = new bool[items.Length];
        string[] pendingDestinations() => currentDestinations.Where((_, index) => !removed[index]).ToArray();
        Push(new UndoEntry
        {
            Description = $"Copy {Items(items.Length)}",
            Undo = async ct =>
            {
                try
                {
                    await ops.TrashAsync(pendingDestinations(), ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The service goes on past a copy it cannot remove. A copy that is gone by now needs
                    // nothing more; asked for again, it would fail this step every time.
                    removed = currentDestinations.Select(path => !NativePath.ExistsNoFollow(path)).ToArray();
                    if (removed.All(gone => gone))
                    {
                        return;
                    }

                    throw;
                }
            },
            Redo = async ct =>
            {
                for (var index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    var parent = PathRules.GetParentPath(item.Destination) ?? item.Destination;
                    var recreated = await ops.CopyEntryResolvedAsync(item.Source, parent, "rename", ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(recreated))
                    {
                        currentDestinations[index] = recreated;
                    }
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
                try
                {
                    var recyclePaths = await ops.TrashAsync([currentPath], ct).ConfigureAwait(false);
                    recyclePath = recyclePaths.FirstOrDefault();
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                    && !NativePath.ExistsNoFollow(currentPath))
                {
                    // Gone by now: there is nothing to remove, and Redo creates it again.
                    recyclePath = null;
                }
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

    public void PushCreateShortcut(
        string parentPath,
        string name,
        string targetPath,
        string? arguments,
        string? workingDirectory,
        string? iconPath,
        string createdPath,
        FileOperationService ops)
    {
        var currentPath = createdPath;
        string? recyclePath = null;
        Push(new UndoEntry
        {
            Description = "Create shortcut",
            Undo = async ct =>
            {
                try
                {
                    var recyclePaths = await ops.TrashAsync([currentPath], ct).ConfigureAwait(false);
                    recyclePath = recyclePaths.FirstOrDefault();
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                    && !NativePath.ExistsNoFollow(currentPath))
                {
                    // Gone by now: there is nothing to remove, and Redo creates it again.
                    recyclePath = null;
                }
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

                currentPath = await ops.CreateShortcutAsync(
                    parentPath,
                    name,
                    targetPath,
                    arguments,
                    workingDirectory,
                    iconPath,
                    ct).ConfigureAwait(false);
            },
        });
    }

    public void PushMove(IReadOnlyList<TransferResult> transferred, FileOperationService ops)
    {
        if (transferred.Any(item => item.Changes is not null))
        {
            var entry = MergeTransferUndo.Create(transferred, ops, move: true);
            if (entry is not null) Push(entry);
            return;
        }

        var items = transferred.ToArray();
        var currentSources = items.Select(item => item.Source).ToArray();
        var currentDestinations = items.Select(item => item.Destination).ToArray();
        // Per-item bookkeeping lets a failed undo be retried for the items still in place
        // instead of failing forever on the ones already restored.
        var restored = new bool[items.Length];
        // Items that were no longer at their destination when Undo ran: Undo and Redo leave them alone.
        var gone = new bool[items.Length];
        Push(new UndoEntry
        {
            Description = $"Move {Items(items.Length)}",
            Undo = async ct =>
            {
                for (var index = items.Length - 1; index >= 0; index--)
                {
                    if (restored[index] || gone[index])
                    {
                        continue;
                    }

                    var originalSource = items[index].Source;
                    var parent = PathRules.GetParentPath(originalSource);
                    if (parent is null)
                    {
                        restored[index] = true;
                        continue;
                    }

                    string movedBack;
                    try
                    {
                        movedBack = await ops.MoveEntryResolvedAsync(currentDestinations[index], parent, "rename", ct).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException
                        && !NativePath.ExistsNoFollow(currentDestinations[index]))
                    {
                        // No longer where the move put it: there is nothing to move back.
                        gone[index] = true;
                        continue;
                    }

                    var desiredName = PathRules.Basename(originalSource);
                    if (!string.IsNullOrEmpty(movedBack)
                        && !string.Equals(PathRules.Basename(movedBack), desiredName, StringComparison.Ordinal))
                    {
                        try
                        {
                            var renamed = await ops.RenameAsync(movedBack, desiredName, ct).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(renamed))
                            {
                                movedBack = renamed;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Keep the non-colliding name if originalSource is now occupied,
                            // and record it so Redo moves the restored item.
                        }
                    }

                    if (!string.IsNullOrEmpty(movedBack))
                    {
                        currentSources[index] = movedBack;
                    }

                    restored[index] = true;
                }
            },
            Redo = async ct =>
            {
                for (var index = 0; index < items.Length; index++)
                {
                    if (!restored[index])
                    {
                        continue;
                    }

                    var item = items[index];
                    var parent = PathRules.GetParentPath(item.Destination) ?? item.Destination;
                    var movedAgain = await ops.MoveEntryResolvedAsync(currentSources[index], parent, "rename", ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(movedAgain))
                    {
                        currentDestinations[index] = movedAgain;
                    }

                    restored[index] = false;
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
            Description = $"Rename {Items(originals.Length)}",
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
            Description = $"Move to Recycle Bin {Items(currentPaths.Length)}",
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
