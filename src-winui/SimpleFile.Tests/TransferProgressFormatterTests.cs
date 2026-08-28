using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class TransferProgressFormatterTests
{
    [Fact]
    public void TransferProgressFormatter_ShowsRichCopyState()
    {
        var context = new TransferProgressContext(false, 6, @"R:\Repos", @"V:\Stuff");
        var update = new ProgressUpdate
        {
            OperationType = "copy",
            Current = 1100UL * 1024 * 1024,
            Total = 1500UL * 1024 * 1024,
            CurrentFiles = 7,
            TotalFiles = 12,
            CurrentItem = @"R:\Repos\SumaFile\src-winui\SimpleFile.App\file.bin",
            Status = "running",
        };

        var display = TransferProgressFormatter.Format(context, update, 50 * 1024 * 1024, 2.5);

        Assert.Equal("Copying 6 items", display.Title);
        Assert.Equal("1.07 GB of 1.46 GB", display.Summary);
        Assert.Equal("73%", display.Percent);
        Assert.Equal("7 of 12 files", display.FileSummary);
        Assert.Equal("2.5 files/s avg", display.FileRate);
        Assert.InRange(display.FileProgressPercent, 58.3, 58.4);
        Assert.Equal("file.bin", display.CurrentItemName);
        Assert.Equal(@"From: R:\Repos", display.From);
        Assert.Equal(@"To: V:\Stuff", display.To);
        Assert.Equal("50 MB/s", display.Speed);
        Assert.Contains("remaining", display.Eta, StringComparison.Ordinal);
        Assert.False(display.IsIndeterminate);
    }
    [Fact]
    public void TransferProgressFormatter_ClampsOverCompleteProgress()
    {
        var context = new TransferProgressContext(true, 1, @"R:\Repos", @"V:\Stuff");
        var update = new ProgressUpdate
        {
            OperationType = "move",
            Current = 125,
            Total = 100,
            CurrentFiles = 4,
            TotalFiles = 3,
            CurrentItem = @"R:\Repos\file.txt",
            Status = "running",
        };

        var display = TransferProgressFormatter.Format(context, update, bytesPerSecond: null, averageFilesPerSecond: null);

        Assert.Equal("Moving 1 item", display.Title);
        Assert.Equal(100, display.ProgressPercent);
        Assert.Equal("100%", display.Percent);
        Assert.Equal("100 B of 100 B", display.Summary);
        Assert.Equal(100, display.FileProgressPercent);
        Assert.Equal("3 of 3 files", display.FileSummary);
    }
    [Fact]
    public void TransferProgressFormatter_CompletedZeroTotals_DoNotLookStuck()
    {
        var context = new TransferProgressContext(false, 1, @"R:\Empty", @"V:\Stuff");
        var update = new ProgressUpdate
        {
            OperationType = "copy",
            Status = "completed",
        };

        var display = TransferProgressFormatter.Format(context, update, bytesPerSecond: null, averageFilesPerSecond: null);

        Assert.Equal("Copy complete", display.Title);
        Assert.Equal("0 B transferred", display.Summary);
        Assert.Equal("0 files", display.FileSummary);
        Assert.Equal("Files complete", display.FileRate);
        Assert.False(display.IsIndeterminate);
        Assert.False(display.FileProgressIsIndeterminate);
    }
    [Fact]
    public void TransferProgressFormatter_ErrorWithoutItem_DoesNotShowPreparing()
    {
        var context = new TransferProgressContext(false, 1, @"R:\Source", @"V:\Stuff");
        var update = new ProgressUpdate
        {
            OperationType = "copy",
            Status = "error",
            Error = "Failed to preserve file timestamps: Access is denied. (os error 5)",
        };

        var display = TransferProgressFormatter.Format(context, update, bytesPerSecond: null, averageFilesPerSecond: null);

        Assert.Equal("Copy failed", display.Title);
        Assert.Equal(update.Error, display.Summary);
        Assert.Equal("Transfer failed", display.CurrentItemName);
        Assert.Equal("", display.CurrentItemPath);
    }
}
