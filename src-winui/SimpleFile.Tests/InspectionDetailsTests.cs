using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class InspectionDetailsTests
{
    [Fact]
    public void MetadataSections_GroupsAudioForPreviewPane()
    {
        var metadata = new FileMetadata
        {
            Kind = "audio",
            Summary = "Connie - Glass Notes (3:05)",
            Fields =
            [
                ["Title", "Glass Notes"],
                ["Artist", "Connie"],
                ["Album", "Night Desk"],
                ["Duration", "3:05"],
                ["Bitrate", "320 kbps"],
                ["Sample rate", "44100 Hz"],
                ["Channels", "2"],
                ["Encoder", "FLAC"],
            ],
        };

        var sections = InspectionDetails.MetadataSections(metadata, includeKind: true);

        Assert.Equal(["Overview", "Track", "Audio", "Media Details"], sections.Select(section => section.Title));
        Assert.Contains(sections[0].Rows, row => row.Label == "Summary" && row.Value == "Connie - Glass Notes (3:05)");
        Assert.Contains(sections[0].Rows, row => row.Label == "Metadata kind" && row.Value == "audio");
        Assert.Equal(["Title", "Artist", "Album"], sections[1].Rows.Select(row => row.Label));
        Assert.Equal(["Duration", "Bitrate", "Sample rate", "Channels"], sections[2].Rows.Select(row => row.Label));
        Assert.Equal("Encoder", Assert.Single(sections[3].Rows).Label);
    }

    [Fact]
    public void MetadataSections_GroupsLabelsCaseInsensitivelyAndKeepsOriginalLabels()
    {
        var metadata = new FileMetadata
        {
            Kind = "AuDio",
            Fields =
            [
                ["title", "Glass Notes"],
                ["DURATION", "0:42"],
                ["Encoder", "AAC"],
            ],
        };

        var sections = InspectionDetails.MetadataSections(metadata, includeSummary: false);

        Assert.Equal(["Track", "Audio", "Media Details"], sections.Select(section => section.Title));
        Assert.Equal("title", Assert.Single(sections[0].Rows).Label);
        Assert.Equal("DURATION", Assert.Single(sections[1].Rows).Label);
        Assert.Equal("Encoder", Assert.Single(sections[2].Rows).Label);
    }

    [Fact]
    public void MetadataSections_UnsupportedKindHidesKindButKeepsSummaryAndFields()
    {
        var metadata = new FileMetadata
        {
            Kind = "unsupported",
            Summary = "Metadata unavailable",
            Fields =
            [
                ["Reason", "Format not supported"],
            ],
        };

        var sections = InspectionDetails.MetadataSections(metadata, includeKind: true);

        Assert.Equal(["Overview", "Metadata"], sections.Select(section => section.Title));
        Assert.Contains(sections[0].Rows, row => row.Label == "Summary" && row.Value == "Metadata unavailable");
        Assert.DoesNotContain(sections[0].Rows, row => row.Label == "Metadata kind");
        Assert.Equal("Reason", Assert.Single(sections[1].Rows).Label);
    }

    [Fact]
    public void MetadataSections_GroupsDocumentCountsAndTruncation()
    {
        var metadata = new FileMetadata
        {
            Kind = "office",
            Summary = "Budget",
            Fields =
            [
                ["Format", "DOCX"],
                ["Title", "Budget"],
                ["Creator", "Connie"],
                ["Pages", "3"],
                ["Words", "1200"],
                ["Company", "SimpleFile"],
                ["Template", "Normal"],
            ],
        };

        var sections = InspectionDetails.MetadataSections(metadata, includeSummary: false, maxFields: 6);

        Assert.Equal(["Document", "Counts", "Publisher", "More"], sections.Select(section => section.Title));
        Assert.Equal(["Format", "Title", "Creator"], sections[0].Rows.Select(row => row.Label));
        Assert.Equal(["Pages", "Words"], sections[1].Rows.Select(row => row.Label));
        Assert.Equal("Company", Assert.Single(sections[2].Rows).Label);
        Assert.Equal("+ 1 more fields...", Assert.Single(sections[3].Rows).Value);
    }

    [Fact]
    public void MetadataSections_GroupsDataAndArchiveDetails()
    {
        var data = new FileMetadata
        {
            Kind = "data",
            Summary = "Object with 2 top-level keys",
            Fields =
            [
                ["Format", "JSON"],
                ["Structure", "Object"],
                ["Top-level keys", "2"],
                ["Keys", "name, items"],
                ["Parse", "Valid JSON"],
            ],
        };
        var archive = new FileMetadata
        {
            Kind = "archive",
            Summary = "3 entries",
            Fields =
            [
                ["Format", "ZIP"],
                ["Entries", "3"],
                ["Files", "2"],
                ["Folders", "1"],
                ["Sample entries", "docs/, docs/readme.md"],
                ["Compressed size", "128 B"],
            ],
        };

        var dataSections = InspectionDetails.MetadataSections(data, includeSummary: false);
        var archiveSections = InspectionDetails.MetadataSections(archive, includeSummary: false);

        Assert.Equal(["Data", "Shape", "Validation"], dataSections.Select(section => section.Title));
        Assert.Equal("Format", Assert.Single(dataSections[0].Rows).Label);
        Assert.Equal(["Structure", "Top-level keys", "Keys"], dataSections[1].Rows.Select(row => row.Label));
        Assert.Equal("Parse", Assert.Single(dataSections[2].Rows).Label);
        Assert.Equal(["Archive", "Contents", "Storage"], archiveSections.Select(section => section.Title));
        Assert.Equal(["Format", "Entries"], archiveSections[0].Rows.Select(row => row.Label));
        Assert.Equal(["Files", "Folders", "Sample entries"], archiveSections[1].Rows.Select(row => row.Label));
        Assert.Equal("Compressed size", Assert.Single(archiveSections[2].Rows).Label);
    }

    [Fact]
    public void RawRows_IgnoresMalformedOrBlankRows()
    {
        var rows = InspectionDetails.RawRows(
            [
                ["Title", "Budget"],
                ["", "Missing label"],
                ["Whitespace value", "   "],
                ["Only label"],
            ]);

        var row = Assert.Single(rows);
        Assert.Equal("Title", row.Label);
        Assert.Equal("Budget", row.Value);
    }

    [Fact]
    public void PropertiesRows_LabelsRecycleBinSymlinkAsOriginalLocation()
    {
        var row = new InspectionRow
        {
            Name = "$R123.txt",
            Path = @"C:\$Recycle.Bin\S-1-5-21-1\$R123.txt",
            TypeText = "TXT File",
            SizeText = "4 B",
            ModifiedText = "9/7/2026 5:00 PM",
            SymlinkText = @"C:\Users\Connie\Desktop\notes.txt",
        };

        var recycleRows = InspectionDetails.PropertiesRows(row, PathRules.RecycleBinPath);
        var normalRows = InspectionDetails.PropertiesRows(row, @"C:\Users\Connie\Desktop");

        Assert.Contains(recycleRows, detail => detail.Label == "Original location");
        Assert.DoesNotContain(recycleRows, detail => detail.Label == "Link target");
        Assert.Contains(normalRows, detail => detail.Label == "Link target");
    }

    private sealed class InspectionRow : IInspectionEntryPresentation
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public bool IsDir { get; init; }
        public string SizeText { get; init; } = "";
        public string ModifiedText { get; init; } = "";
        public string TypeText { get; init; } = "";
        public string SymlinkText { get; init; } = "";
    }
}
