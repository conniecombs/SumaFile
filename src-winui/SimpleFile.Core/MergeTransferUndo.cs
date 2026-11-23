using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>Replays only changes made by this transfer, including its exact conflict retries.</summary>
internal static class MergeTransferUndo
{
    private sealed record Step(Func<CancellationToken, Task> Undo, Func<CancellationToken, Task> Redo);

    internal static UndoEntry? Create(IReadOnlyList<TransferResult> results, FileOperationService ops, bool move)
    {
        var directories = new List<Step>();
        var files = new List<Step>();
        var removed = new List<Step>();
        foreach (var item in results)
        {
            if (item.Changes is not { } changes)
            {
                files.Add(FileStep(item.Source, item.Destination, ops, move));
                continue;
            }

            // The service orders created directories shallowest first and removed ones deepest first.
            foreach (var relative in changes.CreatedDirectories)
                directories.Add(DirectoryStep(item.Destination, relative, ops, created: true));
            foreach (var relative in changes.Files)
                files.Add(FileStep(Child(item.Source, relative), Child(item.Destination, relative),
                    ops, move, item.Source, item.Destination));
            foreach (var relative in changes.RemovedSourceDirectories)
                removed.Add(DirectoryStep(item.Source, relative, ops, created: false));
        }

        List<Step> steps = [.. directories, .. files, .. removed];
        if (steps.Count == 0) return null;
        // Advance only after each successful operation, so a retry resumes at the failed step.
        var cursor = steps.Count;
        return new UndoEntry
        {
            Description = $"{(move ? "Move" : "Copy")} {results.Count} {(results.Count == 1 ? "item" : "items")}",
            Undo = async ct =>
            {
                while (cursor > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await steps[cursor - 1].Undo(ct).ConfigureAwait(false);
                    cursor--;
                }
            },
            Redo = async ct =>
            {
                while (cursor < steps.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    await steps[cursor].Redo(ct).ConfigureAwait(false);
                    cursor++;
                }
            },
        };
    }

    private static string Child(string root, string relative)
    {
        if (relative.Length == 0) return root;
        var path = Path.GetFullPath(relative, root);
        if (Path.IsPathRooted(relative) || !PathRules.PathContains(root, path) || PathRules.PathsEqual(root, path))
            throw new IOException($"Invalid relative transfer path '{relative}'.");
        return path;
    }

    private static void CheckParents(string? root, string path)
    {
        if (root is null) return;
        for (var dir = Path.GetDirectoryName(path); dir is not null && PathRules.PathContains(root, dir);
             dir = Path.GetDirectoryName(dir))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null)
                throw new IOException($"The transfer folder now points somewhere else: {dir}");
        }
    }

    private static Step DirectoryStep(string root, string relative, FileOperationService ops, bool created)
    {
        var path = Child(root, relative);
        var owned = created;
        async Task Create(CancellationToken ct)
        {
            CheckParents(root, path);
            var dir = new DirectoryInfo(path);
            if (dir.LinkTarget is not null) throw new IOException($"A link occupies {path}.");
            if (dir.Exists) return;
            await ops.CreateFolderAsync(dir.Parent!.FullName, dir.Name, ct).ConfigureAwait(false);
            owned = true;
        }
        Task RemoveEmpty(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CheckParents(root, path);
            if (owned)
            {
                if (new DirectoryInfo(path).LinkTarget is not null) throw new IOException($"A link occupies {path}.");
                try { Directory.Delete(path, recursive: false); }
                catch (DirectoryNotFoundException) { }
                catch (IOException e) when ((e.HResult & 0xffff) == 145) { } // ERROR_DIR_NOT_EMPTY
                owned = false;
            }
            return Task.CompletedTask;
        }
        return created ? new(RemoveEmpty, Create) : new(Create, RemoveEmpty);
    }

    private static Step FileStep(string source, string destination, FileOperationService ops, bool move,
        string? sourceRoot = null, string? destinationRoot = null)
    {
        string? recyclePath = null;
        async Task Transfer(string from, string to, CancellationToken ct)
        {
            var parent = Path.GetDirectoryName(to)!;
            TransferItemRequest[] items = [new() { Source = from, Destination = to, Action = "create-only" }];
            if (move) await ops.MoveItemsAsync(items, parent, ct: ct).ConfigureAwait(false);
            else await ops.CopyItemsAsync(items, parent, ct: ct).ConfigureAwait(false);
        }
        async Task Undo(CancellationToken ct)
        {
            CheckParents(destinationRoot, destination);
            if (destinationRoot is not null && NativePath.IsDirectoryNoFollow(destination))
                throw new IOException($"A folder now occupies the copied file's path: {destination}");
            if (move)
            {
                CheckParents(sourceRoot, source);
                await Transfer(destination, source, ct).ConfigureAwait(false);
            }
            else
            {
                var recycled = await ops.TrashAsync([destination], ct).ConfigureAwait(false);
                recyclePath = recycled.FirstOrDefault();
            }
        }
        async Task Redo(CancellationToken ct)
        {
            CheckParents(destinationRoot, destination);
            if (recyclePath is not null)
            {
                if (NativePath.ExistsNoFollow(destination)) throw new IOException($"The destination already exists: {destination}");
                var restored = await ops.RestoreRecycleBinAsync([recyclePath], ct).ConfigureAwait(false);
                destination = restored.Single();
                recyclePath = null;
                return;
            }
            CheckParents(sourceRoot, source);
            await Transfer(source, destination, ct).ConfigureAwait(false);
        }
        return new(Undo, Redo);
    }
}
