using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// One request at a time, and of those that wait only the newest. The preview pane's video frame picture uses
/// it: the file service finishes every request it is sent, so the pane must never have more than one with it,
/// or a quick walk through a folder of videos would take every slot video thumbnails have.
/// </summary>
public class LatestRequestTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ARequestRunsWhenNothingElseDoes()
    {
        var latest = new LatestRequest();

        var result = await latest.RunAsync(() => Task.FromResult("picture"));

        Assert.Equal("picture", result);
    }

    [Fact]
    public async Task OneRequestRunsAtATime()
    {
        var latest = new LatestRequest();
        var first = new TaskCompletionSource<string>();
        var secondStarted = false;

        var firstCall = latest.RunAsync(() => first.Task);
        var secondCall = latest.RunAsync(() =>
        {
            secondStarted = true;
            return Task.FromResult("second");
        });
        await Task.Delay(50);

        Assert.False(secondStarted, "the second waits for the first");
        first.SetResult("first");
        Assert.Equal("first", await firstCall.WaitAsync(Soon));
        Assert.Equal("second", await secondCall.WaitAsync(Soon));
        Assert.True(secondStarted);
    }

    [Fact]
    public async Task OfTheRequestsThatWaitOnlyTheNewestIsMade()
    {
        var latest = new LatestRequest();
        var first = new TaskCompletionSource<string>();
        var made = new List<string>();
        Task<string?> Call(string name) => latest.RunAsync(() =>
        {
            made.Add(name);
            return Task.FromResult(name);
        });

        var firstCall = latest.RunAsync(() => first.Task);
        var passedOver = Call("passed over");
        var alsoPassedOver = Call("also passed over");
        var newest = Call("newest");
        first.SetResult("first");

        Assert.Null(await passedOver.WaitAsync(Soon));
        Assert.Null(await alsoPassedOver.WaitAsync(Soon));
        Assert.Equal("newest", await newest.WaitAsync(Soon));
        Assert.Equal("first", await firstCall.WaitAsync(Soon));
        Assert.Equal(["newest"], made);
    }

    [Fact]
    public async Task ARequestCancelledWhileItWaitsIsNeverMade()
    {
        var latest = new LatestRequest();
        var first = new TaskCompletionSource<string>();
        using var selection = new CancellationTokenSource();
        var made = false;

        var firstCall = latest.RunAsync(() => first.Task);
        var waiting = latest.RunAsync(
            () =>
            {
                made = true;
                return Task.FromResult("late");
            },
            selection.Token);
        selection.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Soon));
        first.SetResult("first");
        await firstCall.WaitAsync(Soon);
        Assert.False(made);
    }

    [Fact]
    public async Task AFailedRequestLetsTheNextOneRun()
    {
        var latest = new LatestRequest();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => latest.RunAsync<string>(() => throw new InvalidOperationException("no frame")));
        var next = await latest.RunAsync(() => Task.FromResult("next")).WaitAsync(Soon);

        Assert.Equal("next", next);
    }
}
