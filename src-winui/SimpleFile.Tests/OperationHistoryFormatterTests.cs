using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class OperationHistoryFormatterTests
{
    [Fact]
    public void Format_UsesOutcomeSpecificTransferSummary()
    {
        var completed = OperationHistoryFormatter.Format(new OperationRecord
        {
            Move = false,
            Sources = [@"C:\src\a.txt", @"C:\src\b.txt", @"C:\src\c.txt"],
            Destination = @"D:\Sorted",
            Status = "completed",
        });
        var skipped = OperationHistoryFormatter.Format(new OperationRecord
        {
            Move = false,
            Sources = [@"C:\src\a.txt", @"C:\src\b.txt", @"C:\src\c.txt"],
            Destination = @"D:\Sorted",
            Status = "skipped",
        });
        var failed = OperationHistoryFormatter.Format(new OperationRecord
        {
            Move = true,
            Sources = [@"R:\Inbox\notes.txt"],
            Destination = @"V:\Archive",
            Status = "failed",
        });

        Assert.Equal(@"Copied 3 items to D:\Sorted", completed.RowText);
        Assert.Equal(@"Copy skipped for 3 items to D:\Sorted", skipped.RowText);
        Assert.Equal(@"Move failed for 1 item to V:\Archive", failed.RowText);
    }

    [Fact]
    public void Format_IncludesInspectableSourceAndDestinationDetail()
    {
        var display = OperationHistoryFormatter.Format(new OperationRecord
        {
            Move = true,
            Sources = [@"R:\Inbox\notes.txt", @"R:\Inbox\photo.jpg"],
            Destination = @"V:\Archive",
            Status = "completed",
        });

        Assert.Equal("Completed", display.StatusLabel);
        Assert.Equal(@"From: notes.txt, photo.jpg", display.SourceSummary);
        Assert.Equal(@"Destination: V:\Archive", display.DestinationSummary);
        Assert.Equal("Retry selected transfer", display.RetryLabel);
    }
}
