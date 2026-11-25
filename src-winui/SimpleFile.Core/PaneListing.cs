using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// The service-held view behind a pane's <see cref="ExplorerPane.Entries"/>: the order and filter the service
/// applied, how many rows the view has, and which revision of it the pane holds. The pane's entries are always the
/// first rows of the view, in view order. Every member is read and written under the workspace gate.
/// </summary>
internal sealed class PaneListing
{
    public required string ViewId { get; init; }

    /// <summary>The folder the view shows, as the service spelled it.</summary>
    public required string Path { get; init; }

    /// <summary>The order and filter the service applied to the rows the pane holds.</summary>
    public required ListingViewRequest Request { get; set; }

    /// <summary>An order or filter that was asked for and has not answered yet.</summary>
    public ListingViewRequest? Pending { get; set; }

    public ulong Revision { get; set; }

    /// <summary>The rows the view shows; the pane may hold fewer while the rest is still being read.</summary>
    public int Total { get; set; }

    /// <summary>A delta did not follow from the pane's revision: the rows must be read again.</summary>
    public bool NeedsReload { get; set; }

    /// <summary>The columns the service sorts by. Any other column is sorted by the pane itself.</summary>
    public static bool IsServiceSortColumn(string? sortBy, bool folderSizesShown) =>
        (sortBy ?? "name").Trim().ToLowerInvariant() switch
        {
            "" or "name" or "modified" or "date" or "created" or "accessed" or "attributes" or "extension" or "type" => true,
            // Folder sizes are measured by the pane and never reach the service.
            "size" => !folderSizesShown,
            _ => false,
        };

    /// <summary>The service's name for a column it sorts by (<c>date</c> is its <c>modified</c>).</summary>
    public static string ServiceSortColumn(string? sortBy)
    {
        var column = (sortBy ?? "name").Trim().ToLowerInvariant();
        return column switch
        {
            "" => "name",
            "date" => "modified",
            _ => column,
        };
    }

    /// <summary>
    /// True when the rows the pane holds are already what <paramref name="request"/> asks for, or the service was
    /// asked for exactly that and has not answered yet (the rows shown stay until it does).
    /// </summary>
    public bool ShowsOrAwaits(ListingViewRequest request) =>
        Request.SameViewAs(request) || (Pending is not null && Pending.SameViewAs(request));

    /// <summary>
    /// Applies <paramref name="delta"/> to <paramref name="entries"/>, the first rows of the view, and returns the
    /// new rows; the argument is not changed. A change past the rows held only moves the view's total.
    /// <paramref name="selectionRemoved"/> is true when the row at <paramref name="selectedPath"/> left the rows
    /// (a row that only moved leaves and comes back in the same delta, and stays selected).
    /// </summary>
    public static List<FileEntry> Apply(
        IReadOnlyList<FileEntry> entries,
        ListingViewDelta delta,
        string? selectedPath,
        out bool selectionRemoved) =>
        Apply(entries, delta, selectedPath, out selectionRemoved, out _, renames: null);

    /// <summary>
    /// As above, and follows the entries that were renamed: each is added to <paramref name="renames"/> as the path
    /// it had and the path it has, and <paramref name="selectionRenamedTo"/> is the new path of the selected row
    /// when it was one of them (null otherwise). A renamed row leaves and arrives in one delta; without this the
    /// selection would be dropped with the old name.
    /// </summary>
    public static List<FileEntry> Apply(
        IReadOnlyList<FileEntry> entries,
        ListingViewDelta delta,
        string? selectedPath,
        out bool selectionRemoved,
        out string? selectionRenamedTo,
        ICollection<PathRename>? renames)
    {
        selectionRemoved = false;
        selectionRenamedTo = null;
        string? renamedSelection = null;
        var rows = new List<FileEntry>(entries.Count + delta.Changes.Count);
        rows.AddRange(entries);
        foreach (var change in delta.Changes)
        {
            var index = (int)Math.Min(change.Index, int.MaxValue);
            switch (change.Kind)
            {
                case ListingViewChange.Remove:
                    if (index < rows.Count)
                    {
                        if (selectedPath is not null && PathRules.PathsEqual(rows[index].Path, selectedPath))
                        {
                            selectionRemoved = true;
                        }

                        rows.RemoveAt(index);
                    }

                    break;
                case ListingViewChange.Insert when change.Entry is not null:
                    Follow(change);
                    // A row at the end of the rows held is the next row of the view: it still belongs to them.
                    if (index <= rows.Count)
                    {
                        if (selectedPath is not null && PathRules.PathsEqual(change.Entry.Path, selectedPath))
                        {
                            selectionRemoved = false;
                        }

                        rows.Insert(index, change.Entry);
                    }

                    break;
                case ListingViewChange.Update when change.Entry is not null:
                    Follow(change);
                    if (index < rows.Count)
                    {
                        rows[index] = change.Entry;
                    }

                    break;
            }
        }

        selectionRenamedTo = selectedPath is not null && renamedSelection is not null ? selectedPath : null;
        return rows;

        // The arriving row of a rename: what is held for the path it had now belongs to the path it has. Also for
        // a row past the rows held, which the selection may still name.
        void Follow(ListingViewChange change)
        {
            if (change.RenamedFrom is not { Length: > 0 } hadPath || change.Entry is not { Path.Length: > 0 } entry)
            {
                return;
            }

            renames?.Add(new PathRename(hadPath, entry.Path));
            if (selectedPath is not null && PathRules.PathsEqual(hadPath, selectedPath))
            {
                selectedPath = entry.Path;
                renamedSelection = entry.Path;
            }
        }
    }

    public static List<FileEntry> Apply(IReadOnlyList<FileEntry> entries, ListingViewDelta delta) =>
        Apply(entries, delta, selectedPath: null, out _);
}
