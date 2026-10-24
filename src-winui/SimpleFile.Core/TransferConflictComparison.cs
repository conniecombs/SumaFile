namespace SimpleFile.Core;

public sealed record ConflictFileSnapshot(string Path, string Kind, long? Size, DateTime? ModifiedUtc)
{
    public string Description => Kind + (Size is { } size ? $" · {size:N0} bytes" : "")
        + (ModifiedUtc is { } modified ? $" · Modified {modified.ToLocalTime():g}" : "");
}

public sealed record TransferConflictComparison(
    ConflictFileSnapshot Incoming, ConflictFileSnapshot Existing, string Comparison, string KeepBothName);

public static class TransferConflictComparisonLoader
{
    public static IReadOnlyList<TransferConflictComparison> Load(
        IReadOnlyList<string> sources, string destination, IReadOnlyList<string> conflicts, CancellationToken token,
        string? existingConflictPath = null)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TransferConflictComparison>();
        foreach (var name in conflicts.Take(6))
        {
            token.ThrowIfCancellationRequested();
            var target = existingConflictPath is not null && Path.IsPathFullyQualified(existingConflictPath)
                && PathRules.PathContains(destination, existingConflictPath)
                ? existingConflictPath : Path.Combine(destination, name);
            var relative = Path.GetRelativePath(destination, target);
            var source = sources.Select(path =>
            {
                var top = PathRules.Basename(path);
                if (string.Equals(relative, top, StringComparison.OrdinalIgnoreCase)) return path;
                if (relative.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(path, relative[(top.Length + 1)..]);
                return null;
            }).FirstOrDefault(path => path is not null);
            var incoming = Read(source);
            var existing = Read(target);
            var targetFolder = Path.GetDirectoryName(target) ?? destination;
            var keepBoth = NextName(Path.GetFileName(target), candidate => reserved.Contains(candidate)
                || NativePath.ExistsNoFollow(Path.Combine(targetFolder, candidate)), token);
            if (keepBoth.Length > 0) reserved.Add(keepBoth);
            result.Add(new(incoming, existing, Compare(incoming, existing), keepBoth));
        }
        return result;
    }

    public static string NextName(string name, Func<string, bool> exists, CancellationToken token = default)
    {
        // Match the Rust keep-both policy, including dotfiles and extensions.
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 && dot < name.Length - 1 ? name[dot..] : "";
        for (var number = 1; number < 10_000; number++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = $"{stem} ({number}){extension}";
            if (!exists(candidate)) return candidate;
        }
        return "";
    }

    public static string Compare(ConflictFileSnapshot incoming, ConflictFileSnapshot existing)
    {
        if (incoming.ModifiedUtc is null || existing.ModifiedUtc is null) return "Comparison unavailable";
        if (incoming.Kind != existing.Kind) return "Different item types";
        if (incoming.ModifiedUtc is not { } left || existing.ModifiedUtc is not { } right) return "Comparison unavailable";
        if (left > right) return "Incoming item is newer";
        if (left < right) return "Existing item is newer";
        return incoming.Size is { } size && size == existing.Size
            ? "Same size and date; contents have not been compared" : "Same modification date";
    }

    private static ConflictFileSnapshot Read(string? path)
    {
        if (string.IsNullOrEmpty(path)) return new("Incoming path unavailable", "Unavailable", null, null);
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return new(path, "Link", null, null);
            if ((attributes & FileAttributes.Directory) != 0) return new(path, "Folder", null, Directory.GetLastWriteTimeUtc(path));
            var info = new FileInfo(path);
            return new(path, "File", info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(path, "Metadata unavailable", null, null);
        }
    }
}
