namespace SimpleFile.Core;

public static class ArchivePaths
{
    private static readonly string[] KnownArchiveExtensions =
    [
        ".tar.gz",
        ".tgz",
        ".7z",
        ".zip",
        ".tar",
        ".gz",
        ".rar",
    ];

    public static bool IsArchiveFile(string? path)
    {
        var name = PathRules.Basename(path ?? "").ToLowerInvariant();
        return KnownArchiveExtensions.Any(extension => name.EndsWith(extension, StringComparison.Ordinal));
    }

    public static string ExtractFolderName(string? archiveName)
    {
        var name = PathRules.Basename((archiveName ?? "").Trim());
        var withoutArchiveExtension = RemoveKnownArchiveExtension(name);
        if (!string.Equals(name, withoutArchiveExtension, StringComparison.Ordinal))
        {
            return withoutArchiveExtension;
        }

        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    public static string WithArchiveExtension(string? archiveNameOrStem, string? format)
    {
        var name = PathRules.Basename((archiveNameOrStem ?? "").Trim());
        var stem = RemoveKnownArchiveExtension(name);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "Archive";
        }

        return $"{stem}{ExtensionForFormat(format)}";
    }

    public static string ExtensionForFormat(string? format)
    {
        var normalized = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "tar.gz" => ".tar.gz",
            "tgz" => ".tgz",
            "7z" => ".7z",
            "tar" => ".tar",
            "gz" => ".gz",
            "rar" => ".rar",
            _ => ".zip",
        };
    }

    private static string RemoveKnownArchiveExtension(string name)
    {
        foreach (var extension in KnownArchiveExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }
}
