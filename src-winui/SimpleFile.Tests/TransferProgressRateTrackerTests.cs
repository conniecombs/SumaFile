using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class TransferProgressRateTrackerTests
{
    private const ulong MB = 1024 * 1024;

    [Fact]
    public void Format_UsesByteDeltaToReportSpeedAndEta()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");

        var first = tracker.Format(context, Running(0, 100 * MB));
        now = now.AddSeconds(1);
        var second = tracker.Format(context, Running(50 * MB, 100 * MB));

        Assert.Equal("Calculating speed", first.Speed);
        Assert.Equal("50 MB/s", second.Speed);
        Assert.Equal("1s remaining", second.Eta);
    }

    /// <summary>
    /// Files skipped, found already copied or moved by renaming count as done, so the percentage reaches 100,
    /// but nothing was written for them. Taken for speed, a merge that skipped most of a large tree read as
    /// gigabytes a second. The speed is measured on the bytes written.
    /// </summary>
    [Fact]
    public void Format_MeasuresTheSpeedOnTheBytesWritten()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");

        tracker.Format(context, Running(0, 1000 * MB, written: 0));
        now = now.AddSeconds(1);
        var copying = tracker.Format(context, Running(10 * MB, 1000 * MB, written: 10 * MB));
        now = now.AddSeconds(1);
        // 500 MB of skipped files in this second, and 10 MB copied.
        var skipping = tracker.Format(context, Running(520 * MB, 1000 * MB, written: 20 * MB));

        Assert.Equal("10 MB/s", copying.Speed);
        Assert.Equal("10 MB/s", skipping.Speed);
        Assert.Equal("52%", skipping.Percent);
    }

    /// <summary>
    /// The time remaining was the bytes left over the speed. In a merge most of those bytes are skipped, which takes
    /// no time: 990 GB of a 1 TB merge already there, the rest copied at 100 MB/s, read as hours for minutes of work.
    /// It is taken from how fast the bytes done grow.
    /// </summary>
    [Fact]
    public void Format_TakesTheTimeRemainingFromHowFastTheBytesDoneGrow()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");

        tracker.Format(context, Running(0, 10_000 * MB, written: 0));
        now = now.AddSeconds(1);
        // 2 GB done in a second, 10 MB of it written: 8 GB left at that pace is four seconds.
        var merging = tracker.Format(context, Running(2010 * MB, 10_000 * MB, written: 10 * MB));

        Assert.Equal("10 MB/s", merging.Speed);
        Assert.Equal("4s remaining", merging.Eta);
    }

    /// <summary>
    /// A transfer that stops moving (a share that does not answer, a drive spinning up) kept showing the last
    /// speed and a time remaining that never changed. The first bytes after it start a new measurement, which a
    /// measure over the whole stall would have read far too low.
    /// </summary>
    [Fact]
    public void Format_StopsShowingASpeedWhenNothingHasMovedForAWhile()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");

        tracker.Format(context, Running(0, 100 * MB));
        now = now.AddSeconds(1);
        var moving = tracker.Format(context, Running(10 * MB, 100 * MB));
        now = now.AddSeconds(1);
        var paused = tracker.Format(context, Running(10 * MB, 100 * MB));
        now = now.AddSeconds(TransferProgressRateTracker.StallSeconds);
        var stalled = tracker.Format(context, Running(10 * MB, 100 * MB));
        now = now.AddSeconds(1);
        var resumed = tracker.Format(context, Running(20 * MB, 100 * MB));
        now = now.AddSeconds(1);
        var measured = tracker.Format(context, Running(30 * MB, 100 * MB));

        Assert.Equal("10 MB/s", moving.Speed);
        Assert.Equal("10 MB/s", paused.Speed);
        Assert.Equal("Calculating speed", stalled.Speed);
        Assert.Equal("Estimating time", stalled.Eta);
        Assert.Equal("Calculating speed", resumed.Speed);
        Assert.Equal("10 MB/s", measured.Speed);
    }

    /// <summary>
    /// A slow file's bytes arrive when a buffer is written: 4 MB every four seconds at 1 MB/s, with the window told
    /// five times a second in between. Three seconds without a change read as a stall, and the next step was then
    /// divided by the time since the stall only: the row went "Calculating speed", then a speed several times too
    /// high, and round again.
    /// </summary>
    [Fact]
    public void Format_KeepsTheSpeedOfATransferWhoseBytesComeInSteps()
    {
        var start = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var now = start;
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"\\nas\share", @"C:\Movies");

        var speeds = new List<string>();
        for (var tick = 0; tick <= 200; tick++)
        {
            now = start.AddMilliseconds(tick * 200);
            var written = 4 * MB * (ulong)(tick / 20);
            var display = tracker.Format(context, Running(written, 1000 * MB));
            if (tick >= 40)
            {
                speeds.Add(display.Speed);
            }
        }

        Assert.All(speeds, speed => Assert.Equal("1 MB/s", speed));
    }

    private static ProgressUpdate Running(ulong current, ulong total, ulong? written = null) => new()
    {
        OperationType = "copy",
        Current = current,
        Total = total,
        Transferred = written ?? current,
        Status = "running",
    };

    [Fact]
    public void Format_ResetsWhenProgressMovesBackward()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var tracker = new TransferProgressRateTracker(() => now);
        var context = new TransferProgressContext(false, 1, @"V:\Movies", @"R:\Movies");

        tracker.Format(context, Running(90 * MB, 100 * MB));
        now = now.AddSeconds(1);
        var reset = tracker.Format(context, Running(10 * MB, 100 * MB));

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
        var startedWithSkips = tracker.Format(context, Running(80 * MB, 100 * MB, written: 0));
        Assert.Equal("80%", startedWithSkips.Percent);
        Assert.Equal("80 MB of 100 MB", startedWithSkips.Summary);
        Assert.Equal("Calculating speed", startedWithSkips.Speed);

        now = now.AddSeconds(1);
        var copying = tracker.Format(context, Running(90 * MB, 100 * MB, written: 10 * MB));
        Assert.Equal("90%", copying.Percent);
        Assert.Equal("90 MB of 100 MB", copying.Summary);
        Assert.Equal("10 MB/s", copying.Speed);
        Assert.Equal("1s remaining", copying.Eta);
    }
}
