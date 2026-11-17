namespace SumaFile.Remote.Model;

/// <summary>A run of consecutive rows to select: <paramref name="First"/> and the <paramref name="Length"/> rows from it.</summary>
public readonly record struct RemoteSelectionRange(int First, int Length);

/// <summary>
/// Carries a list's selection over a re-list. Every listing brings new entry objects, so replacing a list's items
/// drops its selection; the rows whose paths were selected are selected again. A listing of another folder shares no
/// path with the old one, so nothing is carried into it.
/// </summary>
public static class RemoteSelection
{
    /// <summary>
    /// The rows of <paramref name="entries"/> whose path is in <paramref name="selectedPaths"/>, as ascending runs.
    /// <paramref name="comparer"/> is the list's file system's: ordinal for remote paths, ignoring case for local ones.
    /// </summary>
    public static IReadOnlyList<RemoteSelectionRange> Carry(
        IEnumerable<string> selectedPaths, IReadOnlyList<RemoteFileEntry> entries, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(comparer);

        var selected = new HashSet<string>(selectedPaths, comparer);
        var ranges = new List<RemoteSelectionRange>();
        if (selected.Count == 0)
        {
            return ranges;
        }

        var first = -1;
        for (var index = 0; index <= entries.Count; index++)
        {
            var isSelected = index < entries.Count && selected.Contains(entries[index].Path);
            if (isSelected && first < 0)
            {
                first = index;
            }
            else if (!isSelected && first >= 0)
            {
                ranges.Add(new RemoteSelectionRange(first, index - first));
                first = -1;
            }
        }

        return ranges;
    }
}
