using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class TransferProgressRateTrackerTests
{
    [Fact]
    public void Format_UsesByteDeltaToReportSpeedAndEta()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");

        var first = tracker.Format(context, new ProgressUpdate
        {
            OperationType = "copy",
            Current = 0,
            Total = 100UL * 1024 * 1024,
            Status = "running",
        });
        now = now.AddSeconds(1);
        var second = tracker.Format(context, new ProgressUpdate
        {
            OperationType = "copy",
            Current = 50UL * 1024 * 1024,
            Total = 100UL * 1024 * 1024,
            Status = "running",
        });

        Assert.Equal("Calculating speed", first.Speed);
        Assert.Equal("50 MB/s", second.Speed);
        Assert.Equal("1s remaining", second.Eta);
    }

    /// <summary>
    /// Files skipped, found already copied or moved by renaming count as done, so the percentage reaches 100,
    /// but nothing was transferred for them. Taken for speed, a merge that skipped most of a large tree read as
    /// gigabytes a second, and the time remaining followed.
    /// </summary>
    [Fact]
    public void Format_LeavesBytesThatWereNotTransferredOutOfTheSpeed()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");
        const ulong MB = 1024 * 1024;

        tracker.Format(context, Running(0, 1000 * MB));
        now = now.AddSeconds(1);
        var copying = tracker.Format(context, Running(10 * MB, 1000 * MB));
        now = now.AddSeconds(1);
        // 500 MB of skipped files in this second, and 10 MB copied.
        var skipping = tracker.Format(context, Running(520 * MB, 1000 * MB, untransferred: 500 * MB));

        Assert.Equal("10 MB/s", copying.Speed);
        Assert.Equal("10 MB/s", skipping.Speed);
        Assert.Equal("52%", skipping.Percent);
    }

    /// <summary>
    /// A transfer that stops moving (a share that does not answer, a drive spinning up) kept showing the last
    /// speed and a time remaining that never changed.
    /// </summary>
    [Fact]
    public void Format_StopsShowingASpeedWhenNothingHasMovedForAWhile()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");
        const ulong MB = 1024 * 1024;

        tracker.Format(context, Running(0, 100 * MB));
        now = now.AddSeconds(1);
        var moving = tracker.Format(context, Running(10 * MB, 100 * MB));
        now = now.AddSeconds(1);
        var paused = tracker.Format(context, Running(10 * MB, 100 * MB));
        now = now.AddSeconds(TransferProgressRateTracker.StallSeconds);
        var stalled = tracker.Format(context, Running(10 * MB, 100 * MB));
        now = now.AddSeconds(1);
        var resumed = tracker.Format(context, Running(20 * MB, 100 * MB));

        Assert.Equal("10 MB/s", moving.Speed);
        Assert.Equal("10 MB/s", paused.Speed);
        Assert.Equal("Calculating speed", stalled.Speed);
        Assert.Equal("Estimating time", stalled.Eta);
        Assert.Equal("10 MB/s", resumed.Speed);
    }

    private static ProgressUpdate Running(ulong current, ulong total, ulong untransferred = 0) => new()
    {
        OperationType = "copy",
        Current = current,
        Total = total,
        Untransferred = untransferred,
        Status = "running",
    };

    [Fact]
    public void Format_ResetsWhenProgressMovesBackward()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");

        tracker.Format(context, new ProgressUpdate
        {
            OperationType = "copy",
            Current = 90UL * 1024 * 1024,
            Total = 100UL * 1024 * 1024,
            Status = "running",
        });
        now = now.AddSeconds(1);
        var reset = tracker.Format(context, new ProgressUpdate
        {
            OperationType = "copy",
            Current = 10UL * 1024 * 1024,
            Total = 100UL * 1024 * 1024,
            Status = "running",
        });

        Assert.Equal("Calculating speed", reset.Speed);
        Assert.Equal("Estimating time", reset.Eta);
    }

    [Fact]
    public void Format_IgnoresUnsizedPreparingEventSoPreSkippedBytesDoNotSpikeSpeed()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 2, @"V:\Movies", @"R:\Movies");

        var preparing = tracker.Format(context, new ProgressUpdate
        {
            OperationType = "copy",
            Current = 0,
            Total = 0,
            CurrentItem = "Preparing transfer...",
            Status = "running",
        });
        Assert.Equal("Calculating speed", preparing.Speed);

        now = now.AddSeconds(1);
        var startedWithSkips = tracker.Format(context, new ProgressUpdate
        {
            OperationType = "copy",
            Current = 80UL * 1024 * 1024,
            Total = 100UL * 1024 * 1024,
            Status = "running",
        });
        Assert.Equal("80%", startedWithSkips.Percent);
        Assert.Equal("80 MB of 100 MB", startedWithSkips.Summary);
        Assert.Equal("Calculating speed", startedWithSkips.Speed);

        now = now.AddSeconds(1);
        var copying = tracker.Format(context, new ProgressUpdate
        {
            OperationType = "copy",
            Current = 90UL * 1024 * 1024,
            Total = 100UL * 1024 * 1024,
            Status = "running",
        });
        Assert.Equal("90%", copying.Percent);
        Assert.Equal("90 MB of 100 MB", copying.Summary);
        Assert.Equal("10 MB/s", copying.Speed);
        Assert.Equal("1s remaining", copying.Eta);
    }
}
