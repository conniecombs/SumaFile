using System;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class PluginOperationWaitTests
{
    private static readonly TimeSpan LongGrace = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WaitAsync_CompletedOperation_ReportsCompleted()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var wait = PluginOperationWait.WaitAsync(finished.Task, cancellation.Token, LongGrace);
        Assert.False(wait.IsCompleted);
        finished.SetResult();

        Assert.Equal(TransferOperationStatus.Completed, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitAsync_FailedOperation_Throws()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var wait = PluginOperationWait.WaitAsync(finished.Task, cancellation.Token, LongGrace);
        finished.SetException(new InvalidOperationException("Plugin backend stopped."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Plugin backend stopped.", error.Message);
    }

    [Fact]
    public async Task WaitAsync_CancelThenFail_ReportsCancelledOnlyAfterThePluginFinished()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var wait = PluginOperationWait.WaitAsync(finished.Task, cancellation.Token, LongGrace);
        cancellation.Cancel();
        await Task.Delay(200);
        // The plugin has not answered the cancel yet: the row stays Cancelling.
        Assert.False(wait.IsCompleted);

        finished.SetException(new InvalidOperationException("Transfer cancelled"));
        Assert.Equal(TransferOperationStatus.Cancelled, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitAsync_CancelWithoutAnAnswer_ReportsCancelledAfterTheGrace()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var wait = PluginOperationWait.WaitAsync(finished.Task, cancellation.Token, TimeSpan.FromMilliseconds(100));
        cancellation.Cancel();

        Assert.Equal(TransferOperationStatus.Cancelled, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(finished.Task.IsCompleted);
    }

    [Fact]
    public async Task WaitAsync_CancelThenComplete_ReportsCancelled()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var wait = PluginOperationWait.WaitAsync(finished.Task, cancellation.Token, LongGrace);
        cancellation.Cancel();
        finished.SetResult();

        Assert.Equal(TransferOperationStatus.Cancelled, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitAsync_FinishedBeforeTheCancel_KeepsItsOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(
            TransferOperationStatus.Completed,
            await PluginOperationWait.WaitAsync(Task.CompletedTask, cancellation.Token, LongGrace).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
