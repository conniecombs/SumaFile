using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Replays only changes made by this transfer, including its exact conflict retries.
///
/// The files are undone and redone in batches, a few calls to the service instead of one per file, and each
/// file's state is read from the disk afterwards: a file that is no longer at the place the step would take
/// it from counts as done. So a step that failed part-way goes on with what is left when it is run again,
/// and a file the user has dealt with since does not block the rest.
/// </summary>
internal static class MergeTransferUndo
{
    /// <summary>Files sent to the service in one call.</summary>
    internal const int BatchSize = 500;

    private sealed record Step(Func<CancellationToken, Task> Undo, Func<CancellationToken, Task> Redo);

    /// <summary>One transferred file. <see cref="Undone"/> says on which side of the step it is.</summary>
    private sealed class FileItem(string source, string destination, string? sourceRoot, string? destinationRoot)
    {
        public string Source { get; } = source;
        public string Destination { get; set; } = destination;
        public string? SourceRoot { get; } = sourceRoot;
        public string? DestinationRoot { get; } = destinationRoot;
        /// <summary>Where the Recycle Bin holds the copy that Undo removed.</summary>
        public string? RecyclePath { get; set; }
        public bool Undone { get; set; }
    }

    internal static UndoEntry? Create(IReadOnlyList<TransferResult> results, FileOperationService ops, bool move)
    {
        var directories = new List<Step>();
        var files = new List<FileItem>();
        var removed = new List<Step>();
        foreach (var item in results)
        {
            if (item.Changes is not { } changes)
            {
                files.Add(new FileItem(item.Source, item.Destination, null, null));
                continue;
            }

            // The service orders created directories shallowest first and removed ones deepest first.
            foreach (var relative in changes.CreatedDirectories)
                directories.Add(DirectoryStep(item.Destination, relative, ops, created: true));
            foreach (var relative in changes.Files)
                files.Add(new FileItem(Child(item.Source, relative), Child(item.Destination, relative),
                    item.Source, item.Destination));
            foreach (var relative in changes.RemovedSourceDirectories)
                removed.Add(DirectoryStep(item.Source, relative, ops, created: false));
        }

        if (directories.Count + files.Count + removed.Count == 0) return null;
        // Each list advances only after a successful operation, so a retry resumes at the failed step.
        var directoriesDone = directories.Count;
        var removedDone = removed.Count;
        return new UndoEntry
        {
            Description = $"{(move ? "Move" : "Copy")} {results.Count} {(results.Count == 1 ? "item" : "items")}",
            Undo = async ct =>
            {
                // The reverse of the transfer: the source folders it removed, the files, the folders it made.
                while (removedDone > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await removed[removedDone - 1].Undo(ct).ConfigureAwait(false);
                    removedDone--;
                }

                await UndoFilesAsync(files, ops, move, ct).ConfigureAwait(false);
                while (directoriesDone > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await directories[directoriesDone - 1].Undo(ct).ConfigureAwait(false);
                    directoriesDone--;
                }
            },
            Redo = async ct =>
            {
                while (directoriesDone < directories.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    await directories[directoriesDone].Redo(ct).ConfigureAwait(false);
                    directoriesDone++;
                }

                await RedoFilesAsync(files, ops, move, ct).ConfigureAwait(false);
                while (removedDone < removed.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    await removed[removedDone].Redo(ct).ConfigureAwait(false);
                    removedDone++;
                }
            },
        };
    }

    private static async Task UndoFilesAsync(
        List<FileItem> files, FileOperationService ops, bool move, CancellationToken ct)
    {
        var batch = new List<FileItem>();
        for (var index = files.Count - 1; index >= 0; index--)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[index];
            if (file.Undone) continue;
            CheckParents(file.DestinationRoot, file.Destination);
            if (!NativePath.ExistsNoFollow(file.Destination))
            {
                // Not where the transfer put it: already undone, or dealt with by the user since.
                file.Undone = true;
                file.RecyclePath = null;
                continue;
            }

            if (file.DestinationRoot is not null && NativePath.IsDirectoryNoFollow(file.Destination))
                throw new IOException($"A folder now occupies the copied file's path: {file.Destination}");
            if (move) CheckParents(file.SourceRoot, file.Source);
            batch.Add(file);
            if (batch.Count == BatchSize || (move && batch.Count > 0 && !SameRoot(batch[0], file)))
            {
                await UndoBatchAsync(batch, ops, move, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0) await UndoBatchAsync(batch, ops, move, ct).ConfigureAwait(false);
    }

    private static async Task UndoBatchAsync(
        List<FileItem> batch, FileOperationService ops, bool move, CancellationToken ct)
    {
        try
        {
            if (move)
            {
                var items = batch
                    .Select(file => new TransferItemRequest
                    {
                        Source = file.Destination, Destination = file.Source, Action = "create-only",
                    })
                    .ToArray();
                await ops.MoveItemsAsync(items, PassFolder(batch[0].SourceRoot, batch[0].Source), ct: ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var recycled = await ops.TrashAsync(batch.Select(file => file.Destination).ToArray(), ct)
                    .ConfigureAwait(false);
                // One Recycle Bin path for each file, in order; otherwise Redo copies the file again.
                for (var index = 0; index < batch.Count; index++)
                    batch[index].RecyclePath = recycled.Length == batch.Count ? recycled[index] : null;
            }
        }
        finally
        {
            // Also after a failure: the service goes on past a file it cannot handle, so some are done.
            foreach (var file in batch)
            {
                file.Undone = !NativePath.ExistsNoFollow(file.Destination);
                if (!file.Undone) file.RecyclePath = null;
            }
        }

        var left = batch.Count(file => !file.Undone);
        if (left > 0)
            throw new IOException($"{left} of {batch.Count} files could not be {(move ? "moved back" : "removed")}.");
    }

    private static async Task RedoFilesAsync(
        List<FileItem> files, FileOperationService ops, bool move, CancellationToken ct)
    {
        var restore = new List<FileItem>();
        var transfer = new List<FileItem>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.Undone) continue;
            CheckParents(file.DestinationRoot, file.Destination);
            if (file.RecyclePath is not null)
            {
                if (NativePath.ExistsNoFollow(file.Destination))
                    throw new IOException($"The destination already exists: {file.Destination}");
                restore.Add(file);
                if (restore.Count == BatchSize)
                {
                    await RestoreBatchAsync(restore, ops, ct).ConfigureAwait(false);
                    restore.Clear();
                }

                continue;
            }

            CheckParents(file.SourceRoot, file.Source);
            if (!NativePath.ExistsNoFollow(file.Source) && NativePath.ExistsNoFollow(file.Destination))
            {
                // Already where the transfer put it.
                file.Undone = false;
                continue;
            }

            if (transfer.Count == BatchSize || (transfer.Count > 0 && !SameRoot(transfer[0], file)))
            {
                await TransferBatchAsync(transfer, ops, move, ct).ConfigureAwait(false);
                transfer.Clear();
            }

            transfer.Add(file);
        }

        if (restore.Count > 0) await RestoreBatchAsync(restore, ops, ct).ConfigureAwait(false);
        if (transfer.Count > 0) await TransferBatchAsync(transfer, ops, move, ct).ConfigureAwait(false);
    }

    private static async Task RestoreBatchAsync(List<FileItem> batch, FileOperationService ops, CancellationToken ct)
    {
        var restored = await ops.RestoreRecycleBinAsync(batch.Select(file => file.RecyclePath!).ToArray(), ct)
            .ConfigureAwait(false);
        if (restored.Length != batch.Count)
            throw new IOException($"{batch.Count - restored.Length} of {batch.Count} files could not be restored.");
        for (var index = 0; index < batch.Count; index++)
        {
            batch[index].Destination = restored[index];
            batch[index].RecyclePath = null;
            batch[index].Undone = false;
        }
    }

    private static async Task TransferBatchAsync(
        List<FileItem> batch, FileOperationService ops, bool move, CancellationToken ct)
    {
        var items = batch
            .Select(file => new TransferItemRequest
            {
                Source = file.Source, Destination = file.Destination, Action = "create-only",
            })
            .ToArray();
        var folder = PassFolder(batch[0].DestinationRoot, batch[0].Destination);
        try
        {
            if (move) await ops.MoveItemsAsync(items, folder, ct: ct).ConfigureAwait(false);
            else await ops.CopyItemsAsync(items, folder, ct: ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var file in batch) file.Undone = !NativePath.ExistsNoFollow(file.Destination);
        }

        var left = batch.Count(file => file.Undone);
        if (left > 0)
            throw new IOException($"{left} of {batch.Count} files could not be {(move ? "moved" : "copied")} again.");
    }

    /// <summary>The files of one call belong to one transferred item, whose folder the call is given.</summary>
    private static bool SameRoot(FileItem first, FileItem other) =>
        first.SourceRoot is null
            ? other.SourceRoot is null
                && PathRules.PathsEqual(Path.GetDirectoryName(first.Source) ?? "", Path.GetDirectoryName(other.Source) ?? "")
            : other.SourceRoot is not null && PathRules.PathsEqual(first.SourceRoot, other.SourceRoot);

    /// <summary>
    /// The folder a pass of the service is given for its files: the transferred folder when it is there, or
    /// the folder of the first file. Each file carries its own exact destination.
    /// </summary>
    private static string PassFolder(string? root, string path) =>
        root is not null && Directory.Exists(root) ? root : Path.GetDirectoryName(path)!;

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
                // A folder that holds something by now stays, with what it holds.
                EmptyFolder.Remove(path);
                owned = false;
            }
            return Task.CompletedTask;
        }
        return created ? new(RemoveEmpty, Create) : new(Create, RemoveEmpty);
    }

}
