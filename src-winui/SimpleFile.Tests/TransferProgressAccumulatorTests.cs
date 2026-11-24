using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class TransferProgressAccumulatorTests
{
    [Fact]
    public void SeedPreDecidedConflicts_IncludesSkippedFilesInCompletedAndTotalProgress()
    {
        var accumulator = new TransferProgressAccumulator();
        var sources = new[] { @"C:\src\existing.bin", @"C:\src\fresh.bin" };
        var preDecided = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["existing.bin"] = "skip",
        };
        var sizes = new Dictionary<string, (ulong Bytes, ulong Files)>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\src\existing.bin"] = (400, 1),
            [@"C:\src\fresh.bin"] = (100, 1),
        };

        accumulator.SeedPreDecidedConflicts(
            sources,
            @"D:\dst",
            preDecided,
            path => string.Equals(path, @"D:\dst\existing.bin", StringComparison.OrdinalIgnoreCase),
            _ => false,
            path => sizes[path]);

        accumulator.BeginPass("op-1");

        // Preflight 0/0 stays indeterminate so the bar does not flash 100% before Pass 1 sizes its work.
        var preparing = accumulator.Apply(new ProgressUpdate
        {
            OperationId = "op-1",
            OperationType = "copy",
            Current = 0,
            Total = 0,
            Status = "running",
            CurrentItem = "Preparing transfer...",
        });
        Assert.Equal(0UL, preparing.Current);
        Assert.Equal(0UL, preparing.Total);

        var midPass = accumulator.Apply(new ProgressUpdate
        {
            OperationId = "op-1",
            OperationType = "copy",
            Current = 50,
            Total = 100,
            CurrentFiles = 0,
            TotalFiles = 1,
            Status = "running",
            CurrentItem = @"C:\src\fresh.bin",
        });
        Assert.Equal(450UL, midPass.Current);
        Assert.Equal(500UL, midPass.Total);
        Assert.Equal(1UL, midPass.CurrentFiles);
        Assert.Equal(2UL, midPass.TotalFiles);
        Assert.Equal(90, TransferProgressFormatter.Percent(midPass.Current, midPass.Total));
    }

    [Fact]
    public void MultiPassMixedConflicts_CarriesSkippedAndCopiedBytesWithoutDoubleCounting()
    {
        var accumulator = new TransferProgressAccumulator();
        var sources = new[] { @"C:\src\skip.bin", @"C:\src\replace.bin", @"C:\src\fresh.bin" };
        var preDecided = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["skip.bin"] = "skip",
            ["replace.bin"] = "replace",
        };
        var sizes = new Dictionary<string, (ulong Bytes, ulong Files)>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\src\skip.bin"] = (400, 1),
            [@"C:\src\replace.bin"] = (400, 1),
            [@"C:\src\fresh.bin"] = (200, 1),
        };

        accumulator.SeedPreDecidedConflicts(
            sources,
            @"D:\dst",
            preDecided,
            path => path.EndsWith("skip.bin", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("replace.bin", StringComparison.OrdinalIgnoreCase),
            _ => false,
            path => sizes[path]);

        accumulator.BeginPass("op-1");
        var pass1Done = accumulator.Apply(new ProgressUpdate
        {
            OperationId = "op-1",
            OperationType = "copy",
            Current = 200,
            Total = 200,
            CurrentFiles = 1,
            TotalFiles = 1,
            Status = "running",
        });
        Assert.Equal(600UL, pass1Done.Current);
        Assert.Equal(1000UL, pass1Done.Total);
        Assert.Equal(2UL, pass1Done.CurrentFiles);
        Assert.Equal(3UL, pass1Done.TotalFiles);

        // Pass 1 fails with skip.bin and replace.bin; skip.bin was already counted before Pass 1.
        accumulator.CompletePass(
            [
                new TransferFailure
                {
                    Source = @"C:\src\skip.bin",
                    Destination = @"D:\dst\skip.bin",
                    Kind = "conflict",
                    Error = "CONFLICT: destination already exists",
                },
            ],
            path => sizes[path]);

        accumulator.BeginPass("op-2");
        var pass2Mid = accumulator.Apply(new ProgressUpdate
        {
            OperationId = "op-2",
            OperationType = "copy",
            Current = 200,
            Total = 400,
            CurrentFiles = 0,
            TotalFiles = 1,
            Status = "running",
        });
        Assert.Equal(800UL, pass2Mid.Current);
        Assert.Equal(1000UL, pass2Mid.Total);
        Assert.Equal(2UL, pass2Mid.CurrentFiles);
        Assert.Equal(3UL, pass2Mid.TotalFiles);
    }

    [Fact]
    public void CompletePass_CountsNewlySkippedFolderMergeConflictsButNotErrorSkips()
    {
        var accumulator = new TransferProgressAccumulator();
        var sizes = new Dictionary<string, (ulong Bytes, ulong Files)>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\src\Folder\existing.bin"] = (600, 1),
            [@"C:\src\Folder\locked.bin"] = (100, 1),
        };

        accumulator.BeginPass("op-1");
        accumulator.Apply(new ProgressUpdate
        {
            OperationId = "op-1",
            OperationType = "copy",
            Current = 200,
            Total = 1000,
            CurrentFiles = 1,
            TotalFiles = 4,
            Status = "running",
        });

        accumulator.CompletePass(
            [
                new TransferFailure
                {
                    Source = @"C:\src\Folder\existing.bin",
                    Destination = @"D:\dst\Folder\existing.bin",
                    Kind = "conflict",
                    Error = "CONFLICT: destination already exists",
                },
                new TransferFailure
                {
                    Source = @"C:\src\Folder\locked.bin",
                    Destination = @"D:\dst\Folder\locked.bin",
                    Kind = "error",
                    Error = "Access is denied",
                },
            ],
            path => sizes[path]);

        accumulator.BeginPass("op-2");
        var pass2Start = accumulator.Apply(new ProgressUpdate
        {
            OperationId = "op-2",
            OperationType = "copy",
            Current = 0,
            Total = 200,
            CurrentFiles = 0,
            TotalFiles = 1,
            Status = "running",
        });
        // 200 copied in Pass 1 + 600 conflict-skipped = 800; locked.bin (error) is not counted as on destination.
        Assert.Equal(800UL, pass2Start.Current);
        Assert.Equal(1000UL, pass2Start.Total);
        Assert.Equal(2UL, pass2Start.CurrentFiles);
        Assert.Equal(3UL, pass2Start.TotalFiles);

        // Stale updates from Pass 1 are ignored after BeginPass("op-2").
        var stale = accumulator.Apply(new ProgressUpdate
        {
            OperationId = "op-1",
            OperationType = "copy",
            Current = 999,
            Total = 1000,
            Status = "running",
        });
        Assert.Equal(999UL, stale.Current);

        var pass2Done = accumulator.Apply(new ProgressUpdate
        {
            OperationId = "op-2",
            OperationType = "copy",
            Current = 200,
            Total = 200,
            CurrentFiles = 1,
            TotalFiles = 1,
            Status = "completed",
        });
        Assert.Equal(1000UL, pass2Done.Current);
        Assert.Equal(1000UL, pass2Done.Total);
    }
}
