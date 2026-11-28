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
    public double IndentWidth => Depth * 14.0;
    public string ChevronGlyph => !HasChildren ? "" : (Expanded ? "\uE70D" : "\uE76C");
    public double ChevronOpacity => HasChildren ? 1.0 : 0.0;
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
    /// <summary>
    /// Maps a WinUI <c>VirtualKey</c> name to a type-ahead character: letters, digits (top row
    /// and numeric keypad) and Backspace. Chords with Ctrl/Alt/Win never type.
    /// </summary>
    public static bool TryMapKey(string? virtualKeyName, bool hasCommandModifier, out char character)
    {
        character = '\0';
        if (hasCommandModifier || string.IsNullOrEmpty(virtualKeyName))
        {
            return false;
        }

        if (virtualKeyName.Length == 1 && char.IsLetter(virtualKeyName[0]))
        {
            character = char.ToLowerInvariant(virtualKeyName[0]);
            return true;
        }

        const string numberPad = "NumberPad";
        const string number = "Number";
        var digits = virtualKeyName.StartsWith(numberPad, StringComparison.Ordinal)
            ? virtualKeyName[numberPad.Length..]
            : virtualKeyName.StartsWith(number, StringComparison.Ordinal)
                ? virtualKeyName[number.Length..]
                : null;
        if (digits is { Length: 1 } && char.IsAsciiDigit(digits[0]))
        {
            character = digits[0];
            return true;
        }

        if (virtualKeyName == "Back")
        {
            character = '\b';
            return true;
        }

        return false;
    }

    public static int MatchIndex(IReadOnlyList<FileEntry> entries, string buffer) =>
        MatchIndex(entries, entry => entry.Name, buffer);

    /// <summary>Index of the first item whose name starts with what was typed, or -1.</summary>
    public static int MatchIndex<T>(IReadOnlyList<T> items, Func<T, string> name, string buffer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrEmpty(buffer))
        {
            return -1;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (name(items[index]).StartsWith(buffer, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
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
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "webp", "bmp", "dib", "tif", "tiff",
        "svg", "ico", "cur", "heic", "heif", "avif", "avifs", "jxl", "jp2", "j2k", "jpf",
        "tga", "dds", "exr", "hdr", "qoi", "pnm", "pbm", "pgm", "ppm", "pam",
        "dng", "arw", "cr2", "cr3", "nef", "orf", "rw2", "raf", "srw", "pef", "x3f",
    };

    public static bool IsImage(string? nameOrExtension)
    {
        return ExtensionCatalog.Contains(ImageExtensions, nameOrExtension);
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

    /// <summary>True for a video. The list is <see cref="MediaFormats.VideoExtensions"/>, which the file service shares.</summary>
    public static bool IsVideo(string? nameOrExtension)
    {
        return MediaFormats.IsVideoFormat(nameOrExtension);
    }
}

internal static class ExtensionCatalog
{
    public static bool Contains(IReadOnlySet<string> extensions, string? nameOrExtension)
    {
        var value = (nameOrExtension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (value.Length == 0)
        {
            return false;
        }

        if (extensions.Contains(value))
        {
            return true;
        }

        var dot = value.LastIndexOf('.');
        return dot >= 0 && dot < value.Length - 1 && extensions.Contains(value[(dot + 1)..]);
    }
}

/// <summary>
/// The rules of a rubber-band selection in a file list, for every view: the rectangle between where the drag began
/// and where the pointer is, the rows it touches, and the selection that makes.
/// </summary>
public static class MarqueeSelection
{
    public static bool Intersects(double x, double y, double width, double height, double itemTop, double itemBottom)
    {
        var top = Math.Min(y, y + height);
        var bottom = Math.Max(y, y + height);
        return itemBottom >= top && itemTop <= bottom && width != 0;
    }

    /// <summary>The rectangle spanned by the point the marquee started at and the pointer, whichever way it was dragged.</summary>
    public static (double X, double Y, double Width, double Height) Bounds(double startX, double startY, double x, double y) =>
        (Math.Min(startX, x), Math.Min(startY, y), Math.Abs(x - startX), Math.Abs(y - startY));

    /// <summary>Whether two rectangles meet. Rectangles that only share an edge do.</summary>
    public static bool Intersects(
        double x, double y, double width, double height,
        double otherX, double otherY, double otherWidth, double otherHeight) =>
        !(x + width < otherX || x > otherX + otherWidth || y + height < otherY || y > otherY + otherHeight);

    /// <summary>
    /// The selection a marquee makes: the items it touches. With Ctrl held (<paramref name="toggle"/>) it is the
    /// selection the marquee started from, with each touched item's state reversed, as in Explorer.
    /// </summary>
    public static HashSet<T> Resolve<T>(IEnumerable<T> initial, IEnumerable<T> touched, bool toggle)
        where T : notnull
    {
        if (!toggle)
        {
            return [.. touched];
        }

        var selection = new HashSet<T>(initial);
        selection.SymmetricExceptWith(new HashSet<T>(touched));
        return selection;
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
