using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class BookmarkItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class FolderTreeItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool HasChildren { get; set; }
    public bool Expanded { get; set; }
    public int Depth { get; set; }
}

public sealed class ClipboardHistoryEntry
{
    public ClipboardOperation Operation { get; set; }
    public string[] Paths { get; set; } = [];
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OperationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Sources { get; set; } = [];
    public string Destination { get; set; } = "";
    public bool Move { get; set; }
    public string Status { get; set; } = "completed";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public static class PlacesStore
{
    public const int RecentLimit = 12;

    public static List<BookmarkItem> AddBookmark(IEnumerable<BookmarkItem> current, string path)
    {
        var next = current.Where(item => !PathRules.PathsEqual(item.Path, path)).ToList();
        next.Insert(0, new BookmarkItem { Name = PathRules.Basename(path), Path = path });
        return next;
    }

    public static List<BookmarkItem> RemoveBookmark(IEnumerable<BookmarkItem> current, string path)
    {
        return current.Where(item => !PathRules.PathsEqual(item.Path, path)).ToList();
    }

    public static List<string> RecordRecent(IEnumerable<string> current, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return current.ToList();
        }

        var next = current.Where(item => !PathRules.PathsEqual(item, path)).ToList();
        next.Insert(0, path);
        if (next.Count > RecentLimit)
        {
            next.RemoveRange(RecentLimit, next.Count - RecentLimit);
        }

        return next;
    }
}

public sealed class TypeAheadBuffer
{
    public string Text { get; private set; } = "";
    public DateTimeOffset LastAt { get; private set; }

    public string Append(char character, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - LastAt > window)
        {
            Text = "";
        }

        LastAt = now;
        if (character == '\b' && Text.Length > 0)
        {
            Text = Text[..^1];
            return Text;
        }

        Text += character;
        return Text;
    }

    public void Clear() => Text = "";
}

public static class TypeAhead
{
    public static int MatchIndex(IReadOnlyList<FileEntry> entries, string buffer)
    {
        if (string.IsNullOrEmpty(buffer) || entries.Count == 0)
        {
            return -1;
        }

        return entries.ToList().FindIndex(entry =>
            entry.Name.StartsWith(buffer, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ClipboardHistory
{
    public const int Limit = 12;
    private readonly List<ClipboardHistoryEntry> _items = [];

    public IReadOnlyList<ClipboardHistoryEntry> Items => _items;

    public void Push(ClipboardOperation operation, string[] paths)
    {
        if (paths.Length == 0)
        {
            return;
        }

        _items.Insert(0, new ClipboardHistoryEntry { Operation = operation, Paths = [.. paths] });
        if (_items.Count > Limit)
        {
            _items.RemoveRange(Limit, _items.Count - Limit);
        }
    }
}

public static class PhotoFolder
{
    public static bool IsPhotoFolder(IEnumerable<FileEntry> entries, int thresholdPercent = 70)
    {
        var files = entries.Where(entry => !entry.IsDir).ToList();
        if (files.Count == 0)
        {
            return false;
        }

        var images = files.Count(entry => IsImage(entry.Extension) || IsImage(entry.Name));
        return images * 100 / files.Count >= thresholdPercent;
    }

    public static bool IsImage(string? nameOrExtension)
    {
        var value = (nameOrExtension ?? "").Trim().ToLowerInvariant();
        return value is "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "tif" or "tiff"
            || value.EndsWith(".png", StringComparison.Ordinal)
            || value.EndsWith(".jpg", StringComparison.Ordinal)
            || value.EndsWith(".jpeg", StringComparison.Ordinal)
            || value.EndsWith(".gif", StringComparison.Ordinal)
            || value.EndsWith(".webp", StringComparison.Ordinal)
            || value.EndsWith(".bmp", StringComparison.Ordinal);
    }
}

public static class MediaFolder
{
    public static bool IsMediaFolder(IEnumerable<FileEntry> entries, int thresholdPercent = 70)
    {
        var files = entries.Where(entry => !entry.IsDir).ToList();
        if (files.Count == 0)
        {
            return false;
        }

        var visualMedia = files.Count(entry =>
            IsVisualMedia(entry.Extension) || IsVisualMedia(entry.Name));
        return visualMedia * 100 / files.Count >= thresholdPercent;
    }

    public static bool IsVisualMedia(string? nameOrExtension) =>
        PhotoFolder.IsImage(nameOrExtension) || IsVideo(nameOrExtension);

    public static bool IsVideo(string? nameOrExtension)
    {
        var value = (nameOrExtension ?? "").Trim().ToLowerInvariant();
        return value is "mp4" or "m4v" or "mov" or "webm" or "mkv" or "avi" or "wmv" or "mpg" or "mpeg" or "flv" or "3gp"
            || value.EndsWith(".mp4", StringComparison.Ordinal)
            || value.EndsWith(".m4v", StringComparison.Ordinal)
            || value.EndsWith(".mov", StringComparison.Ordinal)
            || value.EndsWith(".webm", StringComparison.Ordinal)
            || value.EndsWith(".mkv", StringComparison.Ordinal)
            || value.EndsWith(".avi", StringComparison.Ordinal)
            || value.EndsWith(".wmv", StringComparison.Ordinal)
            || value.EndsWith(".mpg", StringComparison.Ordinal)
            || value.EndsWith(".mpeg", StringComparison.Ordinal)
            || value.EndsWith(".flv", StringComparison.Ordinal)
            || value.EndsWith(".3gp", StringComparison.Ordinal);
    }
}

public static class MarqueeSelection
{
    public static bool Intersects(double x, double y, double width, double height, double itemTop, double itemBottom)
    {
        var top = Math.Min(y, y + height);
        var bottom = Math.Max(y, y + height);
        return itemBottom >= top && itemTop <= bottom && width != 0;
    }
}

public static class FolderTree
{
    public static List<FolderTreeItem> Flatten(IEnumerable<TreeNode> roots, ISet<string> expanded)
    {
        var rows = new List<FolderTreeItem>();
        Append(roots, 0, expanded, rows);
        return rows;
    }

    private static void Append(IEnumerable<TreeNode> nodes, int depth, ISet<string> expanded, List<FolderTreeItem> rows)
    {
        foreach (var node in nodes)
        {
            var isExpanded = expanded.Contains(node.Path);
            rows.Add(new FolderTreeItem
            {
                Name = node.Name,
                Path = node.Path,
                HasChildren = node.HasChildren || node.Children.Count > 0,
                Expanded = isExpanded,
                Depth = depth,
            });
            if (isExpanded && node.Children.Count > 0)
            {
                Append(node.Children, depth + 1, expanded, rows);
            }
        }
    }
}
