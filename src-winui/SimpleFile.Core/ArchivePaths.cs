using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>The engine extensions one <see cref="ArchivePaths.SetEngineFormats"/> call added and removed.</summary>
public sealed record ArchiveFormatChange(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

public static class ArchivePaths
{
    private const string PluginEnginePrefix = "plugin:";

    private static readonly string[] KnownArchiveExtensions =
    [
        ".tar.gz",
        ".tgz",
        ".zip",
        ".tar",
        ".gz",
        ".rar",
    ];

    // A plain .gz is one compressed file, not a folder: it can be extracted but not browsed.
    private static readonly string[] BrowsableBuiltInExtensions =
    [
        ".tar.gz",
        ".tgz",
        ".zip",
        ".tar",
        ".rar",
    ];

    // Replaced as a whole on every SetEngineFormats, so a reader always sees one consistent set.
    private static EngineFormatSet s_engineFormats = EngineFormatSet.Empty;

    /// <summary>
    /// Replaces the archive formats served by plugin engines with the listable plugin rows of
    /// <paramref name="capabilities"/>. Built-in and unavailable rows are ignored. Returns the extensions this
    /// added and removed.
    /// </summary>
    public static ArchiveFormatChange SetEngineFormats(IEnumerable<ArchiveFormatCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var formats = new List<EngineFormat>();
        foreach (var capability in capabilities)
        {
            if (capability is null
                || !capability.CanList
                || !(capability.Engine ?? "").StartsWith(PluginEnginePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var extension = NormalizeExtension(capability.Extension);
            // As in the service, a built-in extension never reaches an engine.
            if (extension.Length < 2
                || EndsWithAny("archive" + extension, KnownArchiveExtensions)
                || formats.Any(format => format.Extension == extension))
            {
                continue;
            }

            formats.Add(new EngineFormat(NormalizeFormat(capability.Format), extension));
        }

        var next = new EngineFormatSet(
            [.. formats.OrderByDescending(format => format.Extension.Length).ThenBy(format => format.Extension, StringComparer.Ordinal)]);
        var previous = Interlocked.Exchange(ref s_engineFormats, next);
        var before = previous.Formats.Select(format => format.Extension).ToHashSet(StringComparer.Ordinal);
        var after = next.Formats.Select(format => format.Extension).ToHashSet(StringComparer.Ordinal);
        return new ArchiveFormatChange(
            [.. after.Where(extension => !before.Contains(extension)).Order(StringComparer.Ordinal)],
            [.. before.Where(extension => !after.Contains(extension)).Order(StringComparer.Ordinal)]);
    }

    /// <summary>A built-in archive (.tar.gz .tgz .zip .tar .gz .rar), or a file a plugin engine can list.</summary>
    public static bool IsArchiveFile(string? path)
    {
        var name = LowerName(path);
        return EndsWithAny(name, KnownArchiveExtensions) || EngineFormatFor(name) is not null;
    }

    /// <summary>An archive a pane can browse into: a built-in other than a plain .gz, or a listable engine format.</summary>
    public static bool IsBrowsableArchive(string? path)
    {
        var name = LowerName(path);
        return EndsWithAny(name, BrowsableBuiltInExtensions) || EngineFormatFor(name) is not null;
    }

    /// <summary>An archive served by a plugin engine rather than a built-in one.</summary>
    public static bool IsEngineArchive(string? path)
    {
        var name = LowerName(path);
        return !EndsWithAny(name, KnownArchiveExtensions) && EngineFormatFor(name) is not null;
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

    /// <summary>
    /// The extension for a format label: the built-in labels, then a registered engine format with that label,
    /// otherwise ".zip".
    /// </summary>
    public static string ExtensionForFormat(string? format)
    {
        var normalized = NormalizeFormat(format);
        switch (normalized)
        {
            case "tar.gz":
                return ".tar.gz";
            case "tgz":
                return ".tgz";
            case "tar":
                return ".tar";
            case "gz":
                return ".gz";
            case "rar":
                return ".rar";
            case "zip":
                return ".zip";
        }

        var engine = s_engineFormats.Formats.FirstOrDefault(candidate =>
            string.Equals(candidate.Format, normalized, StringComparison.Ordinal));
        return engine?.Extension ?? ".zip";
    }

    // The longest registered engine extension first, then the built-ins.
    private static string RemoveKnownArchiveExtension(string name)
    {
        if (EngineFormatFor(name.ToLowerInvariant()) is { } engine)
        {
            return name[..^engine.Extension.Length];
        }

        foreach (var extension in KnownArchiveExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    private static EngineFormat? EngineFormatFor(string lowerName)
    {
        foreach (var format in s_engineFormats.Formats)
        {
            if (lowerName.Length > format.Extension.Length
                && lowerName.EndsWith(format.Extension, StringComparison.Ordinal))
            {
                return format;
            }
        }

        return null;
    }

    private static bool EndsWithAny(string lowerName, IEnumerable<string> extensions) =>
        extensions.Any(extension => lowerName.EndsWith(extension, StringComparison.Ordinal));

    private static string LowerName(string? path) => PathRules.Basename(path ?? "").ToLowerInvariant();

    private static string NormalizeFormat(string? format) => (format ?? "").Trim().TrimStart('.').ToLowerInvariant();

    private static string NormalizeExtension(string? extension)
    {
        var trimmed = (extension ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return "";
        }

        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    private sealed record EngineFormat(string Format, string Extension);

    private sealed class EngineFormatSet(IReadOnlyList<EngineFormat> formats)
    {
        public static readonly EngineFormatSet Empty = new([]);

        public IReadOnlyList<EngineFormat> Formats { get; } = formats;
    }
}
