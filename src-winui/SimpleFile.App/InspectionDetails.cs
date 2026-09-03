using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

internal readonly record struct InspectionDetailRow(string Label, string Value);

internal static class InspectionDetails
{
    public static IReadOnlyList<InspectionDetailRow> PreviewSelectionRows(FileRow row)
    {
        var rows = new List<InspectionDetailRow>();
        Add(rows, "Type", row.TypeText);
        Add(rows, "Size", row.SizeText);
        Add(rows, "Modified", row.ModifiedText);
        return rows;
    }

    public static IReadOnlyList<InspectionDetailRow> PropertiesRows(FileRow row, string currentPath)
    {
        var rows = new List<InspectionDetailRow>();
        Add(rows, "Name", row.Name);
        Add(rows, "Type", row.TypeText);
        Add(rows, "Location", PathRules.GetParentPath(row.Path) ?? row.Path);
        Add(rows, "Path", row.Path);
        if (!string.IsNullOrWhiteSpace(row.SymlinkText))
        {
            Add(
                rows,
                PathRules.IsRecycleBinPath(currentPath) ? "Original location" : "Link target",
                row.SymlinkText);
        }

        Add(rows, "Size", row.SizeText);
        Add(rows, "Modified", row.ModifiedText);
        return rows;
    }

    public static IReadOnlyList<InspectionDetailRow> FolderMetricRows(FolderMetrics metrics)
    {
        var folderCount = metrics.Subdirectories.Count;
        return
        [
            new("Subfolders", CountWithNoun(folderCount, "folder", "folders")),
            new("Total Items", CountWithNoun(metrics.ItemCount, "item", "items")),
            new("Total Size", EntryPresentation.FormatFileSize(metrics.Size)),
        ];
    }

    public static IReadOnlyList<InspectionDetailRow> PreviewRows(FilePreview preview)
    {
        var rows = new List<InspectionDetailRow>();
        Add(rows, "Preview type", preview.FileType);
        Add(rows, "MIME", preview.MimeType);
        Add(rows, "Preview size", EntryPresentation.FormatFileSize(preview.Size, isDirectory: false));
        return rows;
    }

    public static string MetadataHeading(FileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Summary))
        {
            return metadata.Summary!;
        }

        return metadata.Kind switch
        {
            "image" => "Image Details",
            "audio" => "Audio Details",
            "video" => "Video Details",
            "pdf" => "PDF Details",
            "office" => "Document Details",
            _ => "Details",
        };
    }

    public static IReadOnlyList<InspectionDetailRow> MetadataRows(
        FileMetadata metadata,
        bool includeSummary = true,
        bool includeKind = false,
        int maxFields = int.MaxValue)
    {
        var rows = new List<InspectionDetailRow>();
        if (includeSummary)
        {
            Add(rows, "Summary", metadata.Summary);
        }

        if (includeKind && !string.Equals(metadata.Kind, "unsupported", StringComparison.OrdinalIgnoreCase))
        {
            Add(rows, "Metadata kind", metadata.Kind);
        }

        rows.AddRange(RawRows(metadata.Fields.Take(maxFields)));
        return rows;
    }

    public static IReadOnlyList<InspectionDetailRow> RawRows(IEnumerable<string[]> rows)
    {
        var normalized = new List<InspectionDetailRow>();
        foreach (var row in rows)
        {
            if (row.Length >= 2)
            {
                Add(normalized, row[0], row[1]);
            }
        }

        return normalized;
    }

    public static string MoreFieldsText(int remaining) =>
        remaining <= 0 ? "" : $"+ {remaining} more fields...";

    public static string ChecksumsText(Checksums checksums) =>
        $"MD5    {checksums.Md5}{Environment.NewLine}" +
        $"SHA-1  {checksums.Sha1}{Environment.NewLine}" +
        $"SHA-256 {checksums.Sha256}";

    private static string CountWithNoun(ulong count, string singular, string plural) =>
        $"{count:N0} {(count == 1 ? singular : plural)}";

    private static string CountWithNoun(int count, string singular, string plural) =>
        CountWithNoun((ulong)Math.Max(0, count), singular, plural);

    private static void Add(ICollection<InspectionDetailRow> rows, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        rows.Add(new InspectionDetailRow(label, value));
    }
}
