using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class StartupTimerTests
{
    private sealed record Written(string Stage, TimeSpan Elapsed, TimeSpan Delta, string? Detail);

    private static StartupTimer Timer(List<Written> lines, Action<string>? onWrite = null) =>
        new("Test.Scope", (scope, stage, elapsed, delta, detail) =>
        {
            onWrite?.Invoke(stage);
            lines.Add(new Written($"{scope}.{stage}", elapsed, delta, detail));
        });

    [Fact]
    public void Lap_IsWrittenWithTheNextMark_AndTheLapsAddUpToTheSpan()
    {
        var lines = new List<Written>();
        var timer = Timer(lines);

        timer.Mark("begin");
        timer.Lap("first", "a");
        timer.Lap("second");
        Assert.Single(lines);

        timer.Mark("end", "done");

        Assert.Equal(
            ["Test.Scope.begin", "Test.Scope.first", "Test.Scope.second", "Test.Scope.end"],
            lines.Select(line => line.Stage));
        // Each delta runs from the previous mark or lap, so the laps and the closing mark add up to the whole span.
        Assert.Equal(lines[3].Elapsed - lines[0].Elapsed, lines[1].Delta + lines[2].Delta + lines[3].Delta);
        Assert.True(lines[0].Elapsed <= lines[1].Elapsed && lines[1].Elapsed <= lines[2].Elapsed && lines[2].Elapsed <= lines[3].Elapsed);
        Assert.Equal("a", lines[1].Detail);
        Assert.Null(lines[2].Detail);
        Assert.Equal("done", lines[3].Detail);
    }

    [Fact]
    public void Mark_TakesItsTimeBeforeWritingTheLaps()
    {
        // A slow write of a lap must never land in the span the lap splits: it belongs to the next span.
        var lines = new List<Written>();
        var timer = Timer(lines, stage =>
        {
            if (stage == "slow-lap") Thread.Sleep(200);
        });

        timer.Mark("begin");
        timer.Lap("slow-lap");
        timer.Mark("end");
        timer.Mark("after");

        var end = lines.Single(line => line.Stage == "Test.Scope.end");
        var after = lines.Single(line => line.Stage == "Test.Scope.after");
        Assert.True(end.Delta < TimeSpan.FromMilliseconds(100), $"end took {end.Delta.TotalMilliseconds} ms");
        Assert.True(after.Delta >= TimeSpan.FromMilliseconds(150), $"after took {after.Delta.TotalMilliseconds} ms");
    }

    [Fact]
    public void Mark_WithoutLaps_WritesOnlyItself()
    {
        var lines = new List<Written>();
        var timer = Timer(lines);

        timer.Mark("begin");
        timer.Mark("end");

        Assert.Equal(["Test.Scope.begin", "Test.Scope.end"], lines.Select(line => line.Stage));
        Assert.Equal(lines[1].Elapsed - lines[0].Elapsed, lines[1].Delta);
    }
}
