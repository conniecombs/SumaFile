namespace SumaFile.Git.Model;

/// <summary>
/// Path comparisons with the semantics of Core's <c>PathRules</c> (the plugin cannot reference Core): <c>\</c> and
/// <c>/</c> are both separators, case is ignored and trailing separators do not count, on every OS.
/// </summary>
public static class GitPaths
{
    /// <summary>The Recycle Bin's virtual path, as SumaFile shows it.</summary>
    public const string RecycleBinPath = "recycle-bin:";

    public static bool IsRecycleBinPath(string? path) =>
        string.Equals((path ?? "").Trim(), RecycleBinPath, StringComparison.OrdinalIgnoreCase);

    public static bool PathsEqual(string? left, string? right) =>
        left is not null && right is not null && string.Equals(Comparable(left), Comparable(right), StringComparison.Ordinal);

    /// <summary>Whether <paramref name="child"/> is <paramref name="parent"/> or lies below it.</summary>
    public static bool PathContains(string? parent, string? child)
    {
        var parentPath = Comparable(parent ?? "");
        var childPath = Comparable(child ?? "");
        if (parentPath.Length == 0 || childPath.Length == 0)
        {
            return false;
        }

        if (parentPath == childPath)
        {
            return true;
        }

        var prefix = parentPath.EndsWith('/') ? parentPath : parentPath + "/";
        return childPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>The last segment of <paramref name="path"/>, whichever separator it uses.</summary>
    public static string Basename(string path)
    {
        var trimmed = (path ?? "").TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    private static string Comparable(string path) => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
