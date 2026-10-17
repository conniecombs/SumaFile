namespace SimpleFile.Core;

internal sealed class OperationLogController
{
    public const int Limit = 50;

    public List<OperationRecord> Entries { get; } = [];
    public ClipboardHistory ClipboardHistory { get; } = new();

    public void RememberClipboard(ClipboardState clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.HasItems)
        {
            ClipboardHistory.Push(clipboard.Operation, clipboard.SourcePaths);
        }
    }

    public void RememberOperation(
        string kind,
        string description,
        string[] sources,
        string destination,
        bool move,
        string status = "completed")
    {
        Entries.Insert(0, new OperationRecord
        {
            Kind = kind,
            Description = description,
            Sources = sources,
            Destination = destination,
            Move = move,
            Status = status,
        });
        if (Entries.Count > Limit)
        {
            Entries.RemoveRange(Limit, Entries.Count - Limit);
        }
    }

    public async Task<IReadOnlyList<string>> RetryOperationAsync(
        FileOperationService? fileOps,
        OperationRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (fileOps is null || record.Sources.Length == 0 || string.IsNullOrWhiteSpace(record.Destination))
        {
            return [];
        }

        if (record.Move)
        {
            await fileOps.MoveAsync(record.Sources, record.Destination, "keep-both", ct: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await fileOps.CopyAsync(record.Sources, record.Destination, "keep-both", ct: cancellationToken).ConfigureAwait(false);
        }

        return TransferAffectedPaths(record.Sources, record.Destination, record.Move);
    }

    public static IReadOnlyList<string> TransferAffectedPaths(string[] sources, string destination, bool move)
    {
        List<string> paths = [];
        if (!string.IsNullOrWhiteSpace(destination))
        {
            paths.Add(destination);
        }

        if (move)
        {
            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                paths.Add(source);
                var parent = PathRules.GetParentPath(source);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    paths.Add(parent);
                }
            }
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
