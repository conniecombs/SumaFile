namespace SimpleFile.Core;

public sealed class DropTarget
{
    public DropTarget(string destination, bool ontoFolder)
    {
        Destination = destination;
        OntoFolder = ontoFolder;
    }

    public string Destination { get; }
    public bool OntoFolder { get; }
}

/// <summary>
/// Resolves a drop destination the same way the legacy frontend drop target did:
/// hover a folder row → that folder; otherwise the pane path. Rejects drops into a
/// source or a descendant of a source.
/// </summary>
public static class DropDestination
{
    public static DropTarget Resolve(string panePath, string? hoveredPath, bool hoveredIsDirectory)
    {
        if (!string.IsNullOrWhiteSpace(hoveredPath) && hoveredIsDirectory)
        {
            return new DropTarget(hoveredPath, ontoFolder: true);
        }

        return new DropTarget(panePath, ontoFolder: false);
    }

    /// <summary>
    /// Whether <paramref name="sources"/> may be copied or moved to <paramref name="destination"/>.
    /// Nothing is dropped onto itself or into itself. Into the folder the items are already in, a
    /// copy is allowed and makes duplicates (the conflict prompt offers to keep both), while a
    /// <paramref name="move"/> of items that are all already there would change nothing and is
    /// refused. Neither depends on how many items are dropped: one item used to be refused where
    /// two were duplicated.
    /// </summary>
    public static bool IsValidDrop(IReadOnlyList<string> sources, string destination, bool move)
    {
        if (string.IsNullOrWhiteSpace(destination) || sources.Count == 0)
        {
            return false;
        }

        var allAlreadyThere = true;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            if (PathRules.PathsEqual(source, destination) || PathRules.PathContains(source, destination))
            {
                return false;
            }

            var parent = PathRules.GetParentPath(source);
            allAlreadyThere &= parent is not null && PathRules.PathsEqual(parent, destination);
        }

        return !(move && allAlreadyThere);
    }

    public static IReadOnlyList<string> ConflictingTransferNames(
        IReadOnlyList<string> sources,
        IReadOnlyList<string> destinationEntryNames)
    {
        var destination = new HashSet<string>(destinationEntryNames, StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var name = PathRules.Basename(source);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if ((destination.Contains(name) || !seenSources.Add(name)) && emitted.Add(name))
            {
                conflicts.Add(name);
            }
        }

        return conflicts;
    }

    public static IReadOnlyList<string> ProbeConflictingTransferNames(
        IReadOnlyList<string> sources,
        string destination,
        Func<string, bool> destinationPathExists,
        CancellationToken cancellationToken = default,
        Func<string, bool>? isDirectoryNoFollow = null)
    {
        isDirectoryNoFollow ??= NativePath.IsDirectoryNoFollow;
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = PathRules.Basename(source);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var sourceDuplicate = !seenSources.Add(name);
            var target = PathRules.JoinPath(destination, name);
            var destinationConflict = destinationPathExists(target)
                && !(isDirectoryNoFollow(target) && isDirectoryNoFollow(source)
                    && !PathRules.PathsEqual(source, target));
            if ((sourceDuplicate || destinationConflict) && emitted.Add(name))
            {
                conflicts.Add(name);
            }
        }

        return conflicts;
    }
}
