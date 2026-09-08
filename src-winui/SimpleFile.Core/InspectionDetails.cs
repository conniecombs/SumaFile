using SimpleFile.Ipc;

namespace SimpleFile.Core;

public readonly record struct InspectionDetailRow(string Label, string Value);

public readonly record struct InspectionDetailSection(string Title, IReadOnlyList<InspectionDetailRow> Rows);

public interface IInspectionEntryPresentation
{
    string Name { get; }
    string Path { get; }
    bool IsDir { get; }
    string SizeText { get; }
    string ModifiedText { get; }
    string TypeText { get; }
    string SymlinkText { get; }
}

public static class InspectionDetails
{
    private static readonly MetadataGroupDefinition[] ImageGroups =
    [
        new("Image", ["Dimensions"]),
    ];

    private static readonly MetadataGroupDefinition[] AudioGroups =
    [
        new("Track", ["Title", "Artist", "Album", "Album artist", "Genre", "Date", "Track", "Disc"]),
        new("Audio", ["Duration", "Bitrate", "Sample rate", "Channels"]),
    ];

    private static readonly MetadataGroupDefinition[] VideoGroups =
    [
        new("Video", ["Dimensions", "Duration", "Format", "Brand"]),
    ];

    private static readonly MetadataGroupDefinition[] PdfGroups =
    [
        new("PDF", ["Pages", "Title", "Author", "Subject", "Creator", "Producer", "Created", "Modified"]),
    ];

    private static readonly MetadataGroupDefinition[] OfficeGroups =
    [
        new("Document", ["Format", "Title", "Creator", "Last modified by", "Subject", "Keywords", "Created", "Modified", "Revision"]),
        new("Counts", ["Pages", "Words", "Characters", "Slides", "Notes", "Sheets"]),
        new("Publisher", ["Company"]),
    ];

    private static readonly MetadataGroupDefinition[] DataGroups =
    [
        new("Data", ["Format"]),
        new("Shape", ["Structure", "Rows", "Columns", "Records", "Items", "Top-level keys", "Keys", "Root", "Headers"]),
        new("Document", ["Title", "Headings", "Words", "Links", "Lines"]),
        new("Validation", ["Parse", "Valid sample records"]),
    ];

    private static readonly MetadataGroupDefinition[] ArchiveGroups =
    [
        new("Archive", ["Format", "Entries"]),
        new("Contents", ["Files", "Folders", "Sample entries"]),
        new("Storage", ["Stored size", "Uncompressed size", "Compressed size"]),
    ];

    private static readonly MetadataGroupDefinition[] FontGroups =
    [
        new("Font", ["Format", "Family", "Subfamily", "Full name", "PostScript name", "Version"]),
    ];

    private static readonly MetadataGroupDefinition[] EbookGroups =
    [
        new("Ebook", ["Format", "Title", "Creator", "Language", "Identifier", "Documents"]),
    ];

    private static readonly MetadataGroupDefinition[] MessageGroups =
    [
        new("Message", ["Format", "Subject", "From", "To", "Date", "Content type"]),
    ];

    private static readonly MetadataGroupDefinition[] CalendarGroups =
    [
        new("Calendar", ["Format", "Summary", "Starts", "Ends", "Location", "Events"]),
    ];

    private static readonly MetadataGroupDefinition[] ContactGroups =
    [
        new("Contact", ["Format", "Name", "Organization", "Title", "Email", "Phone"]),
    ];

    private static readonly MetadataGroupDefinition[] CertificateGroups =
    [
        new("Certificate", ["Format", "PEM block"]),
    ];

    public static IReadOnlyList<InspectionDetailRow> PreviewSelectionRows(IInspectionEntryPresentation row)
    {
        var rows = new List<InspectionDetailRow>();
        Add(rows, "Type", row.TypeText);
        Add(rows, "Size", row.SizeText);
        Add(rows, "Modified", row.ModifiedText);
        return rows;
    }

    public static IReadOnlyList<InspectionDetailRow> PropertiesRows(IInspectionEntryPresentation row, string currentPath)
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
            "data" => "Data Details",
            "archive" => "Archive Details",
            "font" => "Font Details",
            "ebook" => "Ebook Details",
            "email" => "Message Details",
            "calendar" => "Calendar Details",
            "contact" => "Contact Details",
            "certificate" => "Certificate Details",
            _ => "Details",
        };
    }

    public static IReadOnlyList<InspectionDetailSection> MetadataSections(
        FileMetadata metadata,
        bool includeSummary = true,
        bool includeKind = false,
        int maxFields = int.MaxValue)
    {
        var sections = new List<InspectionDetailSection>();
        var overview = new List<InspectionDetailRow>();
        if (includeSummary)
        {
            Add(overview, "Summary", metadata.Summary);
        }

        if (includeKind && !string.Equals(metadata.Kind, "unsupported", StringComparison.OrdinalIgnoreCase))
        {
            Add(overview, "Metadata kind", metadata.Kind);
        }

        AddSection(sections, "Overview", overview);

        var fieldLimit = Math.Max(0, maxFields);
        var fields = RawRows(metadata.Fields.Take(fieldLimit)).ToList();
        var consumedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in GroupsFor(metadata.Kind))
        {
            var grouped = fields
                .Where(row => group.Contains(row.Label))
                .ToList();
            if (grouped.Count == 0)
            {
                continue;
            }

            AddSection(sections, group.Title, grouped);
            foreach (var row in grouped)
            {
                consumedLabels.Add(row.Label);
            }
        }

        var remaining = fields
            .Where(row => !consumedLabels.Contains(row.Label))
            .ToList();
        AddSection(sections, OtherMetadataTitle(metadata.Kind), remaining);

        var truncated = metadata.Fields.Count - fields.Count;
        if (truncated > 0)
        {
            AddSection(sections, "More", [new InspectionDetailRow("Fields", MoreFieldsText(truncated))]);
        }

        return sections;
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

    private static IReadOnlyList<MetadataGroupDefinition> GroupsFor(string? kind)
    {
        return kind?.ToLowerInvariant() switch
        {
            "image" => ImageGroups,
            "audio" => AudioGroups,
            "video" => VideoGroups,
            "pdf" => PdfGroups,
            "office" => OfficeGroups,
            "data" => DataGroups,
            "archive" => ArchiveGroups,
            "font" => FontGroups,
            "ebook" => EbookGroups,
            "email" => MessageGroups,
            "calendar" => CalendarGroups,
            "contact" => ContactGroups,
            "certificate" => CertificateGroups,
            _ => [],
        };
    }

    private static string OtherMetadataTitle(string? kind)
    {
        return kind?.ToLowerInvariant() switch
        {
            "image" => "EXIF",
            "audio" or "video" => "Media Details",
            "pdf" => "Document Details",
            "office" => "Package Details",
            "data" => "Data Details",
            "archive" => "Package Details",
            "font" => "Font Details",
            "ebook" => "Publication Details",
            "email" => "Headers",
            "calendar" => "Event Details",
            "contact" => "Contact Details",
            "certificate" => "Key Details",
            _ => "Metadata",
        };
    }

    private static void AddSection(
        ICollection<InspectionDetailSection> sections,
        string title,
        IReadOnlyList<InspectionDetailRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        sections.Add(new InspectionDetailSection(title, rows));
    }

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

    private readonly record struct MetadataGroupDefinition(string Title, string[] Labels)
    {
        public bool Contains(string label) =>
            Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
    }
}
