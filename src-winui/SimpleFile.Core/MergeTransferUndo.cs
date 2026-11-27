using System.Runtime.ExceptionServices;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Replays only changes made by this transfer, including its exact conflict retries.
///
/// The files are undone and redone in batches, a few calls to the service instead of one per file. A file
/// counts as done when the service says it did it. When the service could not say (the call failed as a
/// whole), the disk is asked, and only an answer that rules out a file the user put there counts. Before a
/// call, a file counts as already undone only when the disk says it is missing from where the transfer put it,
/// not when the check fails; and Redo leaves out, and says so, a file that is missing from where it would take
/// it from. So a step that failed part-way goes on with what is left when it is run again, and a file the user
/// has dealt with since does not block the rest.
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

    private sealed record Context(FileOperationService Ops, bool Move, Func<string, PathExistence> Existence)
    {
        public bool Missing(string path) => Existence(path) == PathExistence.Missing;
        public bool Exists(string path) => Existence(path) == PathExistence.Exists;
    }

    internal static UndoEntry? Create(
        IReadOnlyList<TransferResult> results, FileOperationService ops, bool move, Func<string, PathExistence> existence)
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
        var context = new Context(ops, move, existence);
        // Each list advances only after a successful operation, so a retry resumes at the failed step.
        var directoriesDone = directories.Count;
        var removedDone = removed.Count;
        UndoEntry? entry = null;
        entry = new UndoEntry
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

                await UndoFilesAsync(files, context, ct).ConfigureAwait(false);
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

                var leftOut = await RedoFilesAsync(files, context, ct).ConfigureAwait(false);
                while (removedDone < removed.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    await removed[removedDone].Redo(ct).ConfigureAwait(false);
                    removedDone++;
                }

                entry!.Notice = leftOut == 0
                    ? null
                    : leftOut == 1
                        ? "1 file was left out: it is no longer where Redo would take it from."
                        : $"{leftOut} files were left out: they are no longer where Redo would take them from.";
            },
        };
        return entry;
    }

    private static async Task UndoFilesAsync(List<FileItem> files, Context context, CancellationToken ct)
    {
        var shortfall = new Shortfall(context.Move ? "moved back" : "removed");
        var batch = new List<FileItem>();
        for (var index = files.Count - 1; index >= 0; index--)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[index];
            if (file.Undone) continue;
            CheckParents(file.DestinationRoot, file.Destination);
            if (context.Missing(file.Destination))
            {
                // Not where the transfer put it: already undone, or dealt with by the user since. A file whose
                // check failed (a long path, a share that is offline) may still be there, so it is sent.
                file.Undone = true;
                file.RecyclePath = null;
                continue;
            }

            if (file.DestinationRoot is not null && NativePath.IsDirectoryNoFollow(file.Destination))
                throw new IOException($"A folder now occupies the copied file's path: {file.Destination}");
            if (context.Move) CheckParents(file.SourceRoot, file.Source);
            // A call holds the files of one transferred item, whose folder it is given: checked before the file
            // is added, so the first file of the next item does not go out with the previous item's files.
            if (batch.Count == BatchSize || (context.Move && batch.Count > 0 && !SameRoot(batch[0], file)))
            {
                await UndoBatchAsync(batch, context, shortfall, ct).ConfigureAwait(false);
                batch.Clear();
            }

            batch.Add(file);
        }

        if (batch.Count > 0) await UndoBatchAsync(batch, context, shortfall, ct).ConfigureAwait(false);
        shortfall.ThrowIfAny();
    }

    private static async Task UndoBatchAsync(
        List<FileItem> batch, Context context, Shortfall shortfall, CancellationToken ct)
    {
        if (context.Move)
        {
            var items = batch
                .Select(file => new TransferItemRequest
                {
                    Source = file.Destination, Destination = file.Source, Action = "create-only",
                })
                .ToArray();
            var folder = PassFolder(batch[0].SourceRoot, batch[0].Source);
            var report = await TransferReport.RunAsync(
                () => context.Ops.MoveItemsAsync(items, folder, ct: ct)).ConfigureAwait(false);
            foreach (var file in batch)
            {
                // Moved back by the call; or, when it could not say, gone from the destination and at its source.
                file.Undone = report.Done(file.Destination)
                    ?? (context.Missing(file.Destination) && context.Exists(file.Source));
                if (!file.Undone) shortfall.Add(report.Reason(file.Destination));
            }

            report.ThrowIfCancelled();
            return;
        }

        var paths = batch.Select(file => file.Destination).ToArray();
        var outcomes = await OutcomeReport.RunAsync(() => context.Ops.TrashEachAsync(paths, ct), paths)
            .ConfigureAwait(false);
        for (var index = 0; index < batch.Count; index++)
        {
            var file = batch[index];
            if (outcomes[index] is { Done: true } outcome)
            {
                // Its Recycle Bin item, so Redo brings this copy back; without one, Redo copies the file again.
                file.Undone = true;
                file.RecyclePath = outcome.Result;
            }
            else if (context.Missing(file.Destination))
            {
                // Gone all the same, though not to a Recycle Bin item the step knows.
                file.Undone = true;
                file.RecyclePath = null;
            }
            else
            {
                shortfall.Add(outcomes.Reason(index));
            }
        }

        outcomes.ThrowIfCancelled();
    }

    /// <summary>Returns how many files were left out because they are missing from where Redo would take them.</summary>
    private static async Task<int> RedoFilesAsync(List<FileItem> files, Context context, CancellationToken ct)
    {
        var shortfall = new Shortfall(context.Move ? "moved again" : "copied again");
        var restore = new List<FileItem>();
        var transfer = new List<FileItem>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.Undone) continue;
            CheckParents(file.DestinationRoot, file.Destination);
            if (file.RecyclePath is null)
            {
                transfer.Add(file);
            }
            else if (context.Exists(file.Destination))
            {
                // Something has the copy's name since: restored, the copy would go beside it under another name.
                shortfall.Add($"The destination already exists: {file.Destination}");
            }
            else
            {
                restore.Add(file);
            }
        }

        foreach (var batch in restore.Chunk(BatchSize))
            await RestoreBatchAsync(batch, context, shortfall, transfer, ct).ConfigureAwait(false);

        var leftOut = 0;
        var pass = new List<FileItem>();
        foreach (var file in transfer)
        {
            ct.ThrowIfCancellationRequested();
            CheckParents(file.SourceRoot, file.Source);
            if (context.Missing(file.Source))
            {
                // No longer where Redo would take it from: it stays undone, and the step says so.
                leftOut++;
                continue;
            }

            if (pass.Count == BatchSize || (pass.Count > 0 && !SameRoot(pass[0], file)))
            {
                await TransferBatchAsync(pass, context, shortfall, ct).ConfigureAwait(false);
                pass.Clear();
            }

            pass.Add(file);
        }

        if (pass.Count > 0) await TransferBatchAsync(pass, context, shortfall, ct).ConfigureAwait(false);
        shortfall.ThrowIfAny();
        return leftOut;
    }

    /// <summary>
    /// Restores the copies of <paramref name="batch"/>. A copy whose Recycle Bin item is gone (the Recycle Bin was
    /// emptied) goes to <paramref name="transfer"/>, to be copied again from its source.
    /// </summary>
    private static async Task RestoreBatchAsync(
        FileItem[] batch, Context context, Shortfall shortfall, List<FileItem> transfer, CancellationToken ct)
    {
        var items = batch.Select(file => file.RecyclePath!).ToArray();
        var outcomes = await OutcomeReport.RunAsync(() => context.Ops.RestoreRecycleBinEachAsync(items, ct), items)
            .ConfigureAwait(false);
        for (var index = 0; index < batch.Length; index++)
        {
            var file = batch[index];
            if (outcomes[index] is { Done: true } outcome)
            {
                if (!string.IsNullOrEmpty(outcome.Result)) file.Destination = outcome.Result;
                Restored(file);
                continue;
            }

            // The service failed, or could not say. The destination was free before the call, so a copy back at
            // its place with its Recycle Bin item gone was restored by it.
            if (context.Missing(file.RecyclePath!))
            {
                if (context.Exists(file.Destination))
                {
                    Restored(file);
                    continue;
                }

                if (context.Missing(file.Destination))
                {
                    file.RecyclePath = null;
                    transfer.Add(file);
                    continue;
                }
            }

            shortfall.Add(outcomes.Reason(index));
        }

        outcomes.ThrowIfCancelled();

        static void Restored(FileItem file)
        {
            file.RecyclePath = null;
            file.Undone = false;
        }
    }

    private static async Task TransferBatchAsync(
        List<FileItem> batch, Context context, Shortfall shortfall, CancellationToken ct)
    {
        // A destination taken before the call is not the call's work: create-only refuses it.
        var freeBefore = batch.Select(file => context.Missing(file.Destination)).ToArray();
        var items = batch
            .Select(file => new TransferItemRequest
            {
                Source = file.Source, Destination = file.Destination, Action = "create-only",
            })
            .ToArray();
        var folder = PassFolder(batch[0].DestinationRoot, batch[0].Destination);
        var report = await TransferReport.RunAsync(() => context.Move
                ? context.Ops.MoveItemsAsync(items, folder, ct: ct)
                : context.Ops.CopyItemsAsync(items, folder, ct: ct))
            .ConfigureAwait(false);
        for (var index = 0; index < batch.Count; index++)
        {
            var file = batch[index];
            // Done when the service says it committed the file. A conflict with a file the user put there is not.
            var done = report.Done(file.Source)
                ?? (freeBefore[index] && context.Exists(file.Destination)
                    && (!context.Move || context.Missing(file.Source)));
            file.Undone = !done;
            if (!done) shortfall.Add(report.Reason(file.Source));
        }

        report.ThrowIfCancelled();
    }

    /// <summary>What a pass over the files could not do, for the message of the step when it fails.</summary>
    private sealed class Shortfall(string verb)
    {
        private string? _reason;

        public int Count { get; private set; }

        public void Add(string? reason)
        {
            Count++;
            _reason ??= reason;
        }

        public void ThrowIfAny()
        {
            if (Count == 0) return;
            var files = Count == 1 ? "1 file" : $"{Count} files";
            throw new IOException(string.IsNullOrWhiteSpace(_reason)
                ? $"{files} could not be {verb}."
                : $"{files} could not be {verb}. {_reason}");
        }
    }

    /// <summary>What one copy or move call of the service said about each file it was given, by source path.</summary>
    private sealed class TransferReport
    {
        private readonly HashSet<string> _committed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal);
        private Exception? _error;

        public static async Task<TransferReport> RunAsync(Func<Task<TransferResult[]>> call)
        {
            var report = new TransferReport();
            try
            {
                report.Commit(await call().ConfigureAwait(false));
            }
            catch (TransferOperationException exception)
            {
                report.Commit(exception.Outcome.Committed);
                foreach (var failure in exception.Outcome.Failed)
                    report._failed.TryAdd(PathRules.NormalizeComparablePath(failure.Source), failure.Error);
                report._error = exception;
            }
            catch (TransferCanceledException exception)
            {
                report.Commit(exception.Committed);
                report._error = exception;
            }
            catch (Exception exception)
            {
                // Nothing said about any file: the disk is asked instead.
                report._error = exception;
            }

            return report;
        }

        /// <summary>True when the call says it committed the file, false when it says it did not, null when it said nothing.</summary>
        public bool? Done(string source)
        {
            var key = PathRules.NormalizeComparablePath(source);
            return _committed.Contains(key) ? true : _failed.ContainsKey(key) ? false : null;
        }

        public string? Reason(string source) =>
            _failed.TryGetValue(PathRules.NormalizeComparablePath(source), out var error) ? error : _error?.Message;

        /// <summary>A cancelled call cancels the step, once what it did is recorded.</summary>
        public void ThrowIfCancelled()
        {
            if (_error is OperationCanceledException) ExceptionDispatchInfo.Throw(_error);
        }

        private void Commit(IEnumerable<TransferResult> results)
        {
            foreach (var result in results) _committed.Add(PathRules.NormalizeComparablePath(result.Source));
        }
    }

    /// <summary>What a call that answers one <see cref="PathOutcome"/> per path said about each path.</summary>
    private sealed class OutcomeReport
    {
        private readonly string[] _paths;
        private PathOutcome[]? _outcomes;
        private Exception? _error;

        private OutcomeReport(string[] paths) => _paths = paths;

        public static async Task<OutcomeReport> RunAsync(Func<Task<PathOutcome[]>> call, string[] paths)
        {
            var report = new OutcomeReport(paths);
            try
            {
                report._outcomes = await call().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Nothing said about any path: the disk is asked instead.
                report._error = exception;
            }

            return report;
        }

        /// <summary>The outcome of the path at <paramref name="index"/>; null when the call did not answer for it.</summary>
        public PathOutcome? this[int index] =>
            _outcomes is { } outcomes && outcomes.Length == _paths.Length
                && PathRules.PathsEqual(outcomes[index].Path, _paths[index])
                ? outcomes[index]
                : null;

        public string? Reason(int index) => this[index]?.Error ?? _error?.Message;

        /// <summary>A cancelled call cancels the step, once what it did is recorded.</summary>
        public void ThrowIfCancelled()
        {
            if (_error is OperationCanceledException) ExceptionDispatchInfo.Throw(_error);
        }
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
