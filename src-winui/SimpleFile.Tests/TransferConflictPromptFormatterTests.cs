using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class TransferConflictPromptFormatterTests
{
    [Fact]
    public void Format_NamesOperationDestinationPaneAndConflicts()
    {
        var text = TransferConflictPromptFormatter.Format(new TransferConflictPromptContext(
            Move: false,
            SourceCount: 3,
            Destination: @"D:\Sorted",
            TargetPane: PaneId.Secondary,
            Conflicts: ["report.pdf", "photo.jpg"]));

        Assert.Equal("Copy conflict", text.Title);
        Assert.Equal("Copy 3 selected items", text.OperationSummary);
        Assert.Equal(@"Destination: right pane - D:\Sorted", text.DestinationSummary);
        Assert.StartsWith("2 names already exist:", text.ConflictSummary, StringComparison.Ordinal);
        Assert.Contains("report.pdf", text.ConflictSummary, StringComparison.Ordinal);
        Assert.Contains("photo.jpg", text.ConflictSummary, StringComparison.Ordinal);
        Assert.Equal("Choose how SumaFile should handle this transfer.", text.ChoiceHint);
    }

    [Fact]
    public void Format_HandlesSingleMoveWithoutKnownPane()
    {
        var text = TransferConflictPromptFormatter.Format(new TransferConflictPromptContext(
            Move: true,
            SourceCount: 1,
            Destination: @"R:\Inbox",
            TargetPane: null,
            Conflicts: ["notes.txt"]));

        Assert.Equal("Move conflict", text.Title);
        Assert.Equal("Move 1 selected item", text.OperationSummary);
        Assert.Equal(@"Destination: R:\Inbox", text.DestinationSummary);
        Assert.Equal("1 name already exists: notes.txt", text.ConflictSummary);
    }

    [Fact]
    public void KeepBothLabel_NamesThePreviewWhenKnown()
    {
        Assert.Equal("Keep both (saves as report (1).pdf)", TransferConflictPromptFormatter.KeepBothLabel("report (1).pdf"));
        Assert.Equal("Keep both", TransferConflictPromptFormatter.KeepBothLabel(""));
        Assert.Equal("Keep both", TransferConflictPromptFormatter.KeepBothLabel(null));
    }

    [Fact]
    public void Badges_MarkTheNewerAndLargerSide()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = older.AddHours(1);
        var incoming = new ConflictFileSnapshot(@"C:\a\x.txt", "File", 200, newer);
        var existing = new ConflictFileSnapshot(@"D:\x.txt", "File", 100, older);

        Assert.Equal(["Newer", "Larger"], TransferConflictPromptFormatter.Badges(incoming, existing));
        Assert.Empty(TransferConflictPromptFormatter.Badges(existing, incoming));

        var unknown = new ConflictFileSnapshot(@"D:\y.txt", "Metadata unavailable", null, null);
        Assert.Empty(TransferConflictPromptFormatter.Badges(incoming, unknown));
        Assert.Empty(TransferConflictPromptFormatter.Badges(incoming, incoming));
    }
}
