using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Carries completed byte and file progress across the passes of one transfer operation
/// (initial pass plus conflict/error retries) and includes skipped conflicts as completed
/// work because those files already exist at the destination.
/// </summary>
public sealed class TransferProgressAccumulator
{
    private readonly HashSet<string> _countedSkippedSources = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeOperationId;
    private ulong _completedBytesOffset;
    private ulong _completedFilesOffset;
    private ulong _pendingBytes;
    private ulong _pendingFiles;
    private ulong _passMaxCurrent;
    private ulong _passMaxCurrentFiles;

    public ulong CompletedBytesOffset => _completedBytesOffset;

    public ulong CompletedFilesOffset => _completedFilesOffset;

    public ulong PendingBytes => _pendingBytes;

    public ulong PendingFiles => _pendingFiles;

    /// <summary>
    /// Marks the backend operation ID of the pass about to start so late progress notifications
    /// from an earlier pass cannot corrupt the current pass's high-water mark.
    /// </summary>
    public void BeginPass(string operationId)
    {
        _activeOperationId = operationId;
    }

    /// <summary>
    /// Records top-level conflicts whose actions were already chosen before Pass 1 when
    /// Pass 1 runs with <c>"error"</c> (so the service excludes those items from Pass 1's plan).
    /// Skipped items count as completed immediately; non-skipped items count toward total work
    /// until the retry pass transfers them.
    /// </summary>
    public void SeedPreDecidedConflicts(
        IReadOnlyList<string> sources,
        string destination,
        IReadOnlyDictionary<string, string> preDecided,
        Func<string, bool> destinationPathExists,
        Func<string, bool> isDirectoryNoFollow,
        Func<string, (ulong Bytes, ulong Files)> measureItem)
    {
        if (preDecided.Count == 0 || sources.Count == 0)
        {
            return;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var name = PathRules.Basename(source);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var target = PathRules.JoinPath(destination, name);
            var sourceIsDir = isDirectoryNoFollow(source);
            var destExists = destinationPathExists(target);
            var destIsDir = destExists && isDirectoryNoFollow(target);
            var sourceDuplicate = !seenNames.Add(name);
            var isTopLevelConflict = sourceDuplicate
                || (destExists && !(sourceIsDir && destIsDir && !PathRules.PathsEqual(source, target)));
            if (!isTopLevelConflict || !preDecided.TryGetValue(name, out var decision))
            {
                continue;
            }

            var (bytes, files) = measureItem(source);
            if (string.Equals(decision, "skip", StringComparison.OrdinalIgnoreCase))
            {
                if (_countedSkippedSources.Add(source))
                {
                    _completedBytesOffset += bytes;
                    _completedFilesOffset += files;
                }
            }
            else
            {
                _pendingBytes += bytes;
                _pendingFiles += files;
            }
        }
    }

    /// <summary>
    /// Translates one backend pass's <see cref="ProgressUpdate"/> into the cumulative progress
    /// of the overall transfer operation.
    /// </summary>
    public ProgressUpdate Apply(ProgressUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (_activeOperationId is not null
            && !string.IsNullOrEmpty(update.OperationId)
            && !string.Equals(_activeOperationId, update.OperationId, StringComparison.Ordinal))
        {
            return update;
        }

        if (_completedBytesOffset == 0
            && _completedFilesOffset == 0
            && _pendingBytes == 0
            && _pendingFiles == 0)
        {
            _passMaxCurrent = Math.Max(_passMaxCurrent, update.Current);
            _passMaxCurrentFiles = Math.Max(_passMaxCurrentFiles, update.CurrentFiles);
            return update;
        }

        // Preserve the preflight "Preparing transfer..." indeterminate event until the pass
        // has sized its own work; otherwise the bar could flash 100% before Pass 1's total arrives.
        if (update.Status == "running"
            && update.Current == 0
            && update.Total == 0
            && update.CurrentFiles == 0
            && update.TotalFiles == 0)
        {
            return update;
        }

        _passMaxCurrent = Math.Max(_passMaxCurrent, update.Current);
        _passMaxCurrentFiles = Math.Max(_passMaxCurrentFiles, update.CurrentFiles);

        var passTotal = Math.Max(update.Total, update.Current);
        var passTotalFiles = Math.Max(update.TotalFiles, update.CurrentFiles);
        return new ProgressUpdate
        {
            OperationId = update.OperationId,
            OperationType = update.OperationType,
            Current = _completedBytesOffset + update.Current,
            Total = _completedBytesOffset + _pendingBytes + passTotal,
            CurrentFiles = _completedFilesOffset + update.CurrentFiles,
            TotalFiles = _completedFilesOffset + _pendingFiles + passTotalFiles,
            CurrentItem = update.CurrentItem,
            Status = update.Status,
            Error = update.Error,
        };
    }

    /// <summary>
    /// Finalizes the just-finished pass before a retry pass begins, carrying forward bytes
    /// completed in that pass plus any conflicts the user chose to skip (without double-counting
    /// pre-decided skips already seeded before Pass 1).
    /// </summary>
    public void CompletePass(
        IEnumerable<TransferFailure> skippedConflicts,
        Func<string, (ulong Bytes, ulong Files)> measureItem)
    {
        _completedBytesOffset += _passMaxCurrent;
        _completedFilesOffset += _passMaxCurrentFiles;
        _passMaxCurrent = 0;
        _passMaxCurrentFiles = 0;
        _pendingBytes = 0;
        _pendingFiles = 0;

        foreach (var failure in skippedConflicts)
        {
            if (!failure.IsConflict || string.IsNullOrWhiteSpace(failure.Source))
            {
                continue;
            }

            if (_countedSkippedSources.Add(failure.Source))
            {
                var (bytes, files) = measureItem(failure.Source);
                _completedBytesOffset += bytes;
                _completedFilesOffset += files;
            }
        }
    }

    /// <summary>
    /// Measures one source item with the same no-follow regular-file semantics as the Rust
    /// planner: regular non-symlink files contribute their byte length and 1 file; symlinks
    /// contribute 0 bytes and 1 file; directories contribute 0 bytes and 0 files.
    /// </summary>
    public static (ulong Bytes, ulong Files) MeasureSourceItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (0, 0);
        }

        try
        {
            if (NativePath.IsDirectoryNoFollow(path))
            {
                return (0, 0);
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return (0, 0);
            }

            if (info.LinkTarget is not null)
            {
                return (0, 1);
            }

            return ((ulong)Math.Max(0, info.Length), 1);
        }
        catch
        {
            return (0, 0);
        }
    }
}
