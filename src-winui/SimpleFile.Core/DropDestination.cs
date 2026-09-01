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

    public static bool IsValidDrop(IReadOnlyList<string> sources, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || sources.Count == 0)
        {
            return false;
        }

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
            if (parent is not null && PathRules.PathsEqual(parent, destination) && sources.Count == 1)
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> ConflictingNames(
        IReadOnlyList<string> sources,
        IReadOnlyList<string> destinationEntryNames)
    {
        var dest = new HashSet<string>(destinationEntryNames, StringComparer.OrdinalIgnoreCase);
        return sources
            .Select(PathRules.Basename)
            .Where(name => dest.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        CancellationToken cancellationToken = default)
    {
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
            var destinationConflict = destinationPathExists(PathRules.JoinPath(destination, name));
            if ((sourceDuplicate || destinationConflict) && emitted.Add(name))
            {
                conflicts.Add(name);
            }
        }

        return conflicts;
    }
}
