using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// The columns whose values are read per entry on demand (folder size and items, dimensions, duration, date taken,
/// camera, pages): their fact kinds on the wire, which entries can have a value, and each column's mode.
/// </summary>
public static class LazyColumns
{
    public const string FolderSize = "folder_size";
    public const string ItemCount = "item_count";
    public const string Dimensions = "dimensions";
    public const string Duration = "duration";
    public const string DateTaken = "date_taken";
    public const string Camera = "camera";
    public const string Pages = "pages";

    public static readonly IReadOnlyList<string> Kinds = [FolderSize, ItemCount, Dimensions, Duration, DateTaken, Camera, Pages];

    /// <summary>Values are read for the rows on screen as they come into view.</summary>
    public const string Automatic = "automatic";

    /// <summary>Nothing is read until "Calculate for this folder" or a click on the cell.</summary>
    public const string OnRequest = "on-request";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "gif", "webp", "bmp" };
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "heic", "heif" };
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "flac", "wav", "m4a", "ogg", "oga", "opus", "aac", "wma", "aiff", "aif",
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "mpg", "mpeg", "3gp",
    };

    /// <summary>Whether <paramref name="entry"/> can have a value of <paramref name="kind"/>: the cell shows "…" until it is read.</summary>
    public static bool CanHaveValue(string kind, FileEntry entry)
    {
        var extension = (string.IsNullOrEmpty(entry.Extension) ? System.IO.Path.GetExtension(entry.Name) : entry.Extension).TrimStart('.');
        return kind switch
        {
            FolderSize or ItemCount => entry.IsDir,
            Dimensions => !entry.IsDir && ImageExtensions.Contains(extension),
            Duration => !entry.IsDir && MediaExtensions.Contains(extension),
            DateTaken or Camera => !entry.IsDir && PhotoExtensions.Contains(extension),
            Pages => !entry.IsDir && string.Equals(extension, "pdf", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>Folder size walks whole trees, so it waits to be asked; everything else reads one file.</summary>
    public static string DefaultMode(string kind) => kind == FolderSize ? OnRequest : Automatic;

    public static string ModeFor(IReadOnlyDictionary<string, string>? modes, string kind) =>
        modes is not null && modes.TryGetValue(kind, out var mode) && IsMode(mode) ? mode : DefaultMode(kind);

    /// <summary>Known kinds with a known mode only, modes lowercased.</summary>
    public static Dictionary<string, string> NormalizeModes(IReadOnlyDictionary<string, string>? modes)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (modes is null)
        {
            return normalized;
        }

        foreach (var (kind, mode) in modes)
        {
            var value = (mode ?? "").Trim().ToLowerInvariant();
            if (Kinds.Contains(kind, StringComparer.Ordinal) && IsMode(value))
            {
                normalized[kind] = value;
            }
        }

        return normalized;
    }

    private static bool IsMode(string? mode) => mode is Automatic or OnRequest;
}
