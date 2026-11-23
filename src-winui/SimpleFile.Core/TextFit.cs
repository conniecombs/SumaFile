using System.Globalization;

namespace SimpleFile.Core;

/// <summary>
/// Shortens text that does not fit where it is shown. The caller supplies <c>fits</c>, which says whether a
/// candidate fits (the app measures its rendered width), and gets back the longest form that does. A shorter
/// candidate is assumed never to be wider than a longer one.
/// </summary>
public static class TextFit
{
    public const string Ellipsis = "…";

    // The end of a file name kept by FileName: at most this many characters, and at most a third of the name.
    private const int MaxFileNameTail = 12;

    /// <summary>The text, or its start followed by the mark.</summary>
    public static string End(string text, Func<string, bool> fits)
    {
        if (fits(text))
        {
            return text;
        }

        var starts = StringInfo.ParseCombiningCharacters(text);
        var kept = Largest(1, starts.Length - 1, count => fits(Head(text, starts, count) + Ellipsis));
        return kept == 0 ? Ellipsis : Head(text, starts, kept) + Ellipsis;
    }

    /// <summary>
    /// A file name, or its start, the mark, and its end. The end carries the extension and the last characters
    /// before it, which is where numbered files differ.
    /// </summary>
    public static string FileName(string name, Func<string, bool> fits)
    {
        if (fits(name))
        {
            return name;
        }

        var starts = StringInfo.ParseCombiningCharacters(name);
        var tailCount = Math.Min(MaxFileNameTail, starts.Length / 3);
        if (tailCount > 0)
        {
            var tail = name[starts[^tailCount]..].TrimStart();
            var kept = Largest(1, starts.Length - tailCount - 1,
                count => fits(Head(name, starts, count) + Ellipsis + tail));
            if (kept > 0)
            {
                return Head(name, starts, kept) + Ellipsis + tail;
            }
        }

        return End(name, fits);
    }

    /// <summary>
    /// A folder path, or its root, the mark, and as many of its last folders as fit. When not even the last
    /// folder fits beside the root, the root goes; after that the last folder is cut at its end.
    /// </summary>
    public static string FolderPath(string path, Func<string, bool> fits)
    {
        if (fits(path))
        {
            return path;
        }

        var separator = PathRules.PathSeparator(path);
        var (root, folders) = SplitRoot(path, separator);
        if (folders.Length < 2)
        {
            // Nothing to drop from the middle.
            return End(path, fits);
        }

        var trailing = path[^1] is '\\' or '/' ? separator.ToString() : "";
        string Shortened(int count) =>
            root + Ellipsis + separator + string.Join(separator, folders[^count..]) + trailing;

        var kept = Largest(1, folders.Length - 1, count => fits(Shortened(count)));
        if (kept > 0)
        {
            return Shortened(kept);
        }

        var last = Ellipsis + separator + folders[^1];
        return fits(last + trailing) ? last + trailing : End(last, fits);
    }

    /// <summary>The root (<c>D:\</c>, <c>\\server\share\</c>, <c>/</c> or nothing) and the folders after it.</summary>
    private static (string Root, string[] Folders) SplitRoot(string path, char separator)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var startsWithSeparator = path.Length > 0 && path[0] is '\\' or '/';
        if (startsWithSeparator && path.Length > 1 && path[1] is '\\' or '/')
        {
            // A share: the server and the share name together are the root.
            return parts.Length >= 2
                ? ($"{separator}{separator}{parts[0]}{separator}{parts[1]}{separator}", parts[2..])
                : (path, []);
        }

        if (parts.Length > 0 && parts[0].Length == 2 && parts[0][1] == ':' && !startsWithSeparator)
        {
            return ($"{parts[0]}{separator}", parts[1..]);
        }

        return (startsWithSeparator ? separator.ToString() : "", parts);
    }

    /// <summary>The first <paramref name="count"/> characters, without white space before the mark.</summary>
    private static string Head(string text, int[] starts, int count) =>
        text[..(count < starts.Length ? starts[count] : text.Length)].TrimEnd();

    /// <summary>The largest value in the range for which the test holds, or zero when it never does.</summary>
    private static int Largest(int low, int high, Func<int, bool> test)
    {
        var best = 0;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (test(middle))
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }
}
