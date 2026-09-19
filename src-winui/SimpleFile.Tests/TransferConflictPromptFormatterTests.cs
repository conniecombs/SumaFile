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
}
