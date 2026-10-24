using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class UiImprovementTests
{
    [Fact]
    public void Preview_PinningAndExplicitExpansionOverrideAutomaticCollapse()
    {
        var settings = new UiSettings();
        Assert.True(PreviewLayoutPolicy.ShouldCollapse(settings, false, false, 1600, false));
        Assert.False(PreviewLayoutPolicy.ShouldCollapse(settings, true, false, 1600, false));
        Assert.True(PreviewLayoutPolicy.ShouldCollapse(settings, true, true, 900, false));
        Assert.False(PreviewLayoutPolicy.ShouldCollapse(settings, true, true, 1600, false));
        Assert.False(PreviewLayoutPolicy.ShouldCollapse(settings, false, true, 900, true));
        settings.PreviewPinned = true;
        Assert.False(PreviewLayoutPolicy.ShouldCollapse(settings, false, true, 900, false));
        settings.PreviewPinned = false;
        settings.PreviewAutoCollapse = false;
        Assert.False(PreviewLayoutPolicy.ShouldCollapse(settings, false, true, 900, false));
    }

    [Theory]
    [InlineData("report.txt", "report (2).txt")]
    [InlineData(".gitignore", ".gitignore (2)")]
    [InlineData("Archive", "Archive (2)")]
    [InlineData("archive.tar.gz", "archive.tar (2).gz")]
    public void KeepBothPreview_SkipsOccupiedNamesAndPreservesExtension(string name, string expected)
    {
        var calls = 0;
        Assert.Equal(expected, TransferConflictComparisonLoader.NextName(name, _ => ++calls == 1));
        Assert.Equal("", TransferConflictComparisonLoader.NextName(name, _ => true));
    }

    [Fact]
    public void ConflictComparison_DoesNotClaimMatchingMetadataProvesMatchingContents()
    {
        var existing = new ConflictFileSnapshot("existing", "File", 123, DateTime.UnixEpoch);
        var incoming = existing with { Path = "incoming" };
        Assert.Contains("contents have not been compared", TransferConflictComparisonLoader.Compare(incoming, existing));
        Assert.Equal("Incoming item is newer", TransferConflictComparisonLoader.Compare(incoming with { ModifiedUtc = DateTime.UnixEpoch.AddSeconds(1) }, existing));
        Assert.Equal("Existing item is newer", TransferConflictComparisonLoader.Compare(incoming with { ModifiedUtc = DateTime.UnixEpoch.AddSeconds(-1) }, existing));
        Assert.Equal("Comparison unavailable", TransferConflictComparisonLoader.Compare(incoming with { ModifiedUtc = null }, existing));
    }

    [Fact]
    public void ConflictComparison_MapsNestedBackendConflictToTheActualSourceAndDestination()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sumafile-conflict-" + Guid.NewGuid().ToString("N")));
        try
        {
            var incomingFolder = Path.Combine(root, "incoming", "project");
            var destination = Path.Combine(root, "destination");
            var existingFolder = Path.Combine(destination, "project");
            Directory.CreateDirectory(incomingFolder);
            Directory.CreateDirectory(existingFolder);
            var incoming = Path.Combine(incomingFolder, "report.txt");
            var existing = Path.Combine(existingFolder, "report.txt");
            File.WriteAllText(incoming, "new content");
            File.WriteAllText(existing, "old");
            File.WriteAllText(Path.Combine(existingFolder, "report (1).txt"), "reserved");
            var comparison = Assert.Single(TransferConflictComparisonLoader.Load(
                [incomingFolder], destination, ["report.txt"], CancellationToken.None, existing));
            Assert.Equal(incoming, comparison.Incoming.Path);
            Assert.Equal(existing, comparison.Existing.Path);
            Assert.Equal(11, comparison.Incoming.Size);
            Assert.Equal(3, comparison.Existing.Size);
            Assert.Equal("report (2).txt", comparison.KeepBothName);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), root, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PreviewChrome_RoundTripsPinCollapseAndChosenWidth()
    {
        var settings = new UiSettings { PreviewPinned = true, PreviewAutoCollapse = false, PreviewWidth = 420 };
        var columns = new ColumnLayout();
        var saved = WorkspaceChromeLayout.Capture(settings, columns, columns);
        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspaceChromeLayout>(System.Text.Json.JsonSerializer.Serialize(saved))!;
        var applied = new UiSettings();
        restored.Apply(applied, new ColumnLayout(), new ColumnLayout());
        Assert.True(applied.PreviewPinned);
        Assert.False(applied.PreviewAutoCollapse);
        Assert.Equal(420, applied.PreviewWidth);
    }

    [Theory]
    [InlineData("invoice.txt", "invoice_001.txt", 7, 0, 4)]
    [InlineData("old.txt", "new.txt", 0, 3, 3)]
    [InlineData("note.txt", "note.md", 5, 3, 2)]
    public void RenameHighlight_IdentifiesChangedTextWithoutMarkingSharedExtension(
        string before, string after, int start, int oldLength, int newLength)
    {
        Assert.Equal(new RenameNameChange(start, oldLength, start, newLength), Assert.Single(RenameNameChange.Between(before, after)));
    }

    [Fact]
    public void RenameHighlight_SkipsUnchangedMiddleTextAndPreservesUnicodeTextElements()
    {
        Assert.Empty(RenameNameChange.Between("same.txt", "same.txt"));
        Assert.Equal([new(0, 3, 0, 3), new(11, 3, 11, 3)], RenameNameChange.Between("old-middle-old.txt", "new-middle-new.txt"));
        Assert.Equal(new(1, 2, 1, 2), Assert.Single(RenameNameChange.Between("x😀.txt", "x😁.txt")));
        Assert.Equal(new(3, 2, 3, 1), Assert.Single(RenameNameChange.Between("cafe\u0301.txt", "café.txt")));
    }
}
