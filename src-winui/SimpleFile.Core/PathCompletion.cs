namespace SimpleFile.Core;

public static class PathCompletion
{
    public static bool TrySplit(string? typed, out string directory, out string prefix)
    {
        directory = "";
        prefix = "";
        var value = (typed ?? "").Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value.EndsWith('\\') || value.EndsWith('/'))
        {
            directory = PathRules.TrimTrailingSeparators(value);
            prefix = "";
            return directory.Length > 0;
        }

        var parent = PathRules.GetParentPath(value);
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        directory = parent;
        prefix = PathRules.Basename(value);
        return true;
    }

    public static IReadOnlyList<string> Suggest(
        IEnumerable<string> candidatePaths,
        string prefix,
        int max = 12)
    {
        var needle = prefix ?? "";
        return candidatePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path =>
            {
                var name = PathRules.Basename(path);
                return needle.Length == 0
                    || name.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, max))
            .ToList();
    }
}
