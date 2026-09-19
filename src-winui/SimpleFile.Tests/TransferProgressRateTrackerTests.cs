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
}
