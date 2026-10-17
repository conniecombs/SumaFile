using System.Globalization;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Listing presentation ported from frontend/src/lib/coreFileManager.ts.
/// </summary>
public static class EntryPresentation
{
    public static string FormatFileSize(ulong bytes, bool isDirectory = false)
    {
        if (isDirectory)
        {
            return "";
        }

        return FormatByteSize(bytes, "0.0");
    }

    public static string FormatCompactFileSize(long bytes) =>
        FormatCompactFileSize((ulong)Math.Max(0, bytes));

    public static string FormatCompactFileSize(ulong bytes) =>
        FormatByteSize(bytes, "0.##");

    private static string FormatByteSize(ulong bytes, string fractionalFormat)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex += 1;
        }

        return unitIndex == 0
            ? $"{size:0} {units[unitIndex]}"
            : $"{size.ToString(fractionalFormat, CultureInfo.CurrentCulture)} {units[unitIndex]}";
    }

    public static string FormatModified(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            && !DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out date))
        {
            return value;
        }

        return date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    public static string FileType(FileEntry entry)
    {
        if (entry.IsDir)
        {
            return "Folder";
        }

        return string.IsNullOrEmpty(entry.Extension)
            ? "File"
            : $"{entry.Extension.ToUpperInvariant()} File";
    }

    public static string ColumnText(FileEntry entry, string columnId)
    {
        return columnId switch
        {
            "name" => entry.Name,
            "size" => FormatEntrySize(entry),
            "items" => FormatItemCount(entry),
            "date" or "modified" => FormatModified(entry.Modified),
            "type" => FileType(entry),
            "extension" => FormatExtension(entry),
            "git" => entry.GitStatus ?? "",
            "symlink" => entry.SymlinkTarget ?? "",
            "path" => entry.Path,
            "parent" => PathRules.GetParentPath(entry.Path) ?? "",
            _ => "",
        };
    }

    public static string FormatEntrySize(FileEntry entry)
    {
        return entry.IsDir && entry.Size == 0
            ? ""
            : FormatFileSize(entry.Size);
    }

    public static string FormatItemCount(FileEntry entry)
    {
        if (!entry.IsDir || entry.ItemCount is not { } count)
        {
            return "";
        }

        return count == 1 ? "1 item" : $"{count:N0} items";
    }

    public static string EntryIcon(FileEntry entry)
    {
        return entry.IsDir ? "\uE8B7" : "\uE8A5";
    }

    public static bool IsHiddenFromUser(FileEntry entry)
    {
        return entry.IsHidden
            || entry.IsSystem
            || (!string.IsNullOrEmpty(entry.Name) && entry.Name[0] == '.');
    }

    public const int DefaultProgressiveViewportCount = 256;

    public static IReadOnlyList<FileEntry> FilterEntries(
        IEnumerable<FileEntry> entries,
        string query = "",
        bool showHidden = false)
    {
        var normalizedQuery = query.Trim();
        var hasQuery = normalizedQuery.Length > 0;
        if (entries is ICollection<FileEntry> collection)
        {
            var list = new List<FileEntry>(collection.Count);
            foreach (var entry in entries)
            {
                if (!showHidden && IsHiddenFromUser(entry))
                {
                    continue;
                }

                if (!hasQuery || entry.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(entry);
                }
            }

            return list;
        }

        return entries.Where(entry =>
        {
            if (!showHidden && IsHiddenFromUser(entry))
            {
                return false;
            }

            return !hasQuery
                || entry.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

    public static IReadOnlyList<FileEntry> SortEntries(
        IEnumerable<FileEntry> entries,
        string sortBy = "name",
        bool sortAsc = true,
        bool keepFoldersOnTop = true)
    {
        var list = entries is List<FileEntry> ownedList ? [.. ownedList] : entries.ToList();
        SortEntriesInPlace(list, sortBy, sortAsc, keepFoldersOnTop);
        return list;
    }

    internal static void SortEntriesInPlace(
        List<FileEntry> list,
        string sortBy = "name",
        bool sortAsc = true,
        bool keepFoldersOnTop = true)
    {
        if (list.Count <= 1)
        {
            return;
        }

        var direction = sortAsc ? 1 : -1;
        var normalizedSortBy = (sortBy ?? "name").Trim().ToLowerInvariant();
        list.Sort((left, right) => CompareEntries(left, right, normalizedSortBy, direction, keepFoldersOnTop));
    }

    public static IReadOnlyList<FileEntry> VisibleEntries(
        IEnumerable<FileEntry> entries,
        string filterQuery = "",
        bool showHidden = false,
        string sortBy = "name",
        bool sortAsc = true,
        bool keepFoldersOnTop = true)
    {
        var filtered = FilterEntries(entries, filterQuery, showHidden);
        if (filtered is List<FileEntry> list)
        {
            SortEntriesInPlace(list, sortBy, sortAsc, keepFoldersOnTop);
            return list;
        }

        return SortEntries(filtered, sortBy, sortAsc, keepFoldersOnTop);
    }

    /// <summary>
    /// Streams the initial viewport rows during progressive directory enumeration of large folders
    /// so the UI does not wait for a full scan/sort pass before rendering the first screenful.
    /// </summary>
    public static IReadOnlyList<FileEntry> VisibleViewportEntries(
        IEnumerable<FileEntry> entries,
        string filterQuery = "",
        bool showHidden = false,
        string sortBy = "name",
        bool sortAsc = true,
        bool keepFoldersOnTop = true,
        bool listingInProgress = false,
        int progressiveViewportLimit = DefaultProgressiveViewportCount)
    {
        if (!listingInProgress || progressiveViewportLimit <= 0)
        {
            return VisibleEntries(entries, filterQuery, showHidden, sortBy, sortAsc, keepFoldersOnTop);
        }

        var normalizedQuery = filterQuery.Trim();
        var hasQuery = normalizedQuery.Length > 0;
        var viewportEntries = new List<FileEntry>(Math.Min(progressiveViewportLimit, DefaultProgressiveViewportCount));
        foreach (var entry in entries)
        {
            if (!showHidden && IsHiddenFromUser(entry))
            {
                continue;
            }

            if (hasQuery && !entry.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            viewportEntries.Add(entry);
            if (viewportEntries.Count >= progressiveViewportLimit)
            {
                break;
            }
        }

        SortEntriesInPlace(viewportEntries, sortBy, sortAsc, keepFoldersOnTop);
        return viewportEntries;
    }

    /// <summary>
    /// Returns visible entries, skipping the sort step when the caller knows
    /// the entries are already in the desired order (e.g. during progressive
    /// chunk accumulation from the backend which sorts dirs-first by name).
    /// </summary>
    public static IReadOnlyList<FileEntry> VisibleEntriesPreSorted(
        IEnumerable<FileEntry> entries,
        string filterQuery = "",
        bool showHidden = false)
    {
        return FilterEntries(entries, filterQuery, showHidden);
    }

    private static int CompareEntries(
        FileEntry left,
        FileEntry right,
        string normalizedSortBy,
        int direction,
        bool keepFoldersOnTop)
    {
        if (keepFoldersOnTop && left.IsDir != right.IsDir)
        {
            return left.IsDir ? -1 : 1;
        }

        var primary = CompareColumn(left, right, normalizedSortBy) * direction;
        if (primary != 0)
        {
            return primary;
        }

        return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
    }

    private static int CompareColumn(FileEntry left, FileEntry right, string normalizedSortBy)
    {
        switch (normalizedSortBy)
        {
            case "size":
                return left.Size.CompareTo(right.Size);
            case "modified":
            case "date":
                return ParseModifiedMillis(left.Modified).CompareTo(ParseModifiedMillis(right.Modified));
            case "extension":
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Extension ?? "", right.Extension ?? "");
            case "type":
            {
                var leftType = left.IsDir ? "folder" : (left.Extension ?? "");
                var rightType = right.IsDir ? "folder" : (right.Extension ?? "");
                return StringComparer.CurrentCultureIgnoreCase.Compare(leftType, rightType);
            }
            case "items":
                return (left.ItemCount ?? 0UL).CompareTo(right.ItemCount ?? 0UL);
            case "git":
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.GitStatus ?? "", right.GitStatus ?? "");
            case "path":
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Path ?? "", right.Path ?? "");
            case "parent":
                return StringComparer.CurrentCultureIgnoreCase.Compare(
                    PathRules.GetParentPath(left.Path) ?? "",
                    PathRules.GetParentPath(right.Path) ?? "");
            case "symlink":
                return StringComparer.CurrentCultureIgnoreCase.Compare(
                    left.SymlinkTarget ?? "",
                    right.SymlinkTarget ?? "");
            default:
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        }
    }

    private static long ParseModifiedMillis(string? modified)
    {
        return !string.IsNullOrEmpty(modified) && DateTimeOffset.TryParse(modified, out var date)
            ? date.ToUnixTimeMilliseconds()
            : 0L;
    }

    private static string FormatExtension(FileEntry entry)
    {
        if (entry.IsDir)
        {
            return "";
        }

        var extension = (entry.Extension ?? "").Trim().TrimStart('.');
        return extension.Length == 0 ? "" : "." + extension;
    }
}
