using System.Collections.Concurrent;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class TransferManagerViewModelTests
{
    [Fact]
    public void DefaultMaxConcurrentTransfers_IsTwo()
    {
        var manager = new TransferManagerViewModel();

        Assert.Equal(2, manager.MaxConcurrentTransfers);
    }

    [Fact]
    public async Task Enqueue_StartsTwoIndependentTransfersAndLeavesThirdQueued()
    {
        var manager = new TransferManagerViewModel { MaxConcurrentTransfers = 2 };
        var started = new ConcurrentQueue<string>();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = manager.Enqueue([@"C:\src\one.txt"], @"D:\one", move: false, Runner("one", started, firstRelease));
        var second = manager.Enqueue([@"C:\src\two.txt"], @"E:\two", move: false, Runner("two", started, secondRelease));
        var third = manager.Enqueue([@"C:\src\three.txt"], @"F:\three", move: false, Runner("three", started, thirdRelease));

        await WaitUntilAsync(() => Started(started, "one") && Started(started, "two"));
        Assert.False(Started(started, "three"));
        Assert.Equal(TransferOperationStatus.Queued, third.Status);
        Assert.Equal(2, manager.ActiveCount);
        Assert.Equal(1, manager.QueuedCount);

        firstRelease.SetResult();

        await WaitUntilAsync(() => Started(started, "three"));
        Assert.Equal(TransferOperationStatus.Running, third.Status);

        secondRelease.SetResult();
        thirdRelease.SetResult();
        await first.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await second.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await third.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Enqueue_SerializesSameDestinationTreeButStartsUnrelatedTransfer()
    {
        var manager = new TransferManagerViewModel { MaxConcurrentTransfers = 2 };
        var started = new ConcurrentQueue<string>();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelatedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = manager.Enqueue([@"C:\src\one.txt"], @"R:\Dest", move: false, Runner("one", started, firstRelease));
        var blocked = manager.Enqueue([@"C:\src\two.txt"], @"r:\dest\Sub", move: false, Runner("blocked", started, blockedRelease));
        var unrelated = manager.Enqueue([@"C:\src\three.txt"], @"V:\Other", move: false, Runner("unrelated", started, unrelatedRelease));

        await WaitUntilAsync(() => Started(started, "one") && Started(started, "unrelated"));
        Assert.False(Started(started, "blocked"));
        Assert.Equal(TransferOperationStatus.Queued, blocked.Status);
        Assert.True(blocked.IsWaitingForDestination);
        Assert.Equal(TransferOperationStatus.Running, unrelated.Status);

        firstRelease.SetResult();

        await WaitUntilAsync(() => Started(started, "blocked"));
        Assert.Equal(TransferOperationStatus.Running, blocked.Status);

        blockedRelease.SetResult();
        unrelatedRelease.SetResult();
        await first.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await blocked.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await unrelated.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancel_QueuedTransferCompletesWithoutStartingRunner()
    {
        var manager = new TransferManagerViewModel { MaxConcurrentTransfers = 1 };
        var started = new ConcurrentQueue<string>();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = manager.Enqueue([@"C:\src\one.txt"], @"D:\one", move: false, Runner("one", started, firstRelease));
        var queued = manager.Enqueue(
            [@"C:\src\two.txt"],
            @"E:\two",
            move: false,
            Runner("queued", started, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));

        manager.Cancel(queued);

        var status = await queued.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TransferOperationStatus.Cancelled, status);
        Assert.Equal(TransferOperationStatus.Cancelled, queued.Status);
        Assert.False(Started(started, "queued"));
        Assert.False(queued.CanCancel);

        firstRelease.SetResult();
        await first.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ApplyProgress_IgnoresUpdatesForOtherOperationIds()
    {
        var manager = new TransferManagerViewModel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = manager.Enqueue(
            [@"C:\src\one.txt"],
            @"D:\one",
            move: false,
            Runner("one", new ConcurrentQueue<string>(), release));

        operation.SetOperationId("op-current");
        operation.ApplyProgress(new ProgressUpdate
        {
            OperationId = "op-other",
            OperationType = "copy",
            Current = 10,
            Total = 10,
            Status = "running",
        });
        operation.ApplyProgress(new ProgressUpdate
        {
            OperationId = "op-current",
            OperationType = "copy",
            Current = 5,
            Total = 10,
            Status = "running",
        });

        Assert.Equal(50, operation.ProgressPercent);
        Assert.Equal("50%", operation.PercentText);

        release.SetResult();
        await operation.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ApplyProgress_DropsLateUpdatesAfterTransferCompletes()
    {
        var manager = new TransferManagerViewModel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = manager.Enqueue(
            [@"C:\src\one.txt"],
            @"D:\one",
            move: false,
            Runner("one", new ConcurrentQueue<string>(), release));

        release.SetResult();
        await operation.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        operation.ApplyProgress(new ProgressUpdate
        {
            OperationId = operation.OperationId ?? "op-late",
            OperationType = "copy",
            Current = 1,
            Total = 10,
            Status = "running",
        });

        Assert.Equal(TransferOperationStatus.Completed, operation.Status);
        Assert.Equal("100%", operation.PercentText);
        Assert.Equal("Transfer complete", operation.SummaryText);
    }

    [Fact]
    public async Task SummaryText_NotifiesDerivedStatusDetailText()
    {
        var manager = new TransferManagerViewModel();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = manager.Enqueue(
            [@"C:\src\one.txt"],
            @"D:\one",
            move: false,
            Runner("one", new ConcurrentQueue<string>(), release));
        var changed = new ConcurrentQueue<string>();
        operation.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Enqueue(args.PropertyName);
            }
        };

        operation.ApplyProgress(new ProgressUpdate
        {
            OperationId = operation.OperationId ?? "op-current",
            OperationType = "copy",
            Current = 5,
            Total = 10,
            Status = "running",
        });

        Assert.Contains(nameof(TransferOperationViewModel.SummaryText), changed);
        Assert.Contains(nameof(TransferOperationViewModel.StatusDetailText), changed);

        release.SetResult();
        await operation.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TransferOperationRunner Runner(
        string name,
        ConcurrentQueue<string> started,
        TaskCompletionSource release) =>
        async (_, cancellationToken) =>
        {
            started.Enqueue(name);
            await release.Task.WaitAsync(cancellationToken);
            return TransferOperationStatus.Completed;
        };

    private static bool Started(ConcurrentQueue<string> started, string name) =>
        started.Contains(name);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
