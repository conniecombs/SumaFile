using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class ViewModelCutoverTests
{
    [Fact]
    public async Task SearchViewModel_StartAsyncOwnsResultsAndStatus()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        await workspace.InitializeAsync();

        ipc.SearchFilesHandler = (options, onBatch, onComplete, ct) =>
        {
            onBatch?.Invoke(
            [
                new SearchResult
                {
                    Name = "batch.txt",
                    Path = @"C:\Users\test\batch.txt",
                    MatchType = "name",
                },
            ]);
            onComplete?.Invoke(2);
            return Task.FromResult(
                new[]
                {
                    new SearchResult
                    {
                        Name = "final-a.txt",
                        Path = @"C:\Users\test\final-a.txt",
                        MatchType = "name",
                    },
                    new SearchResult
                    {
                        Name = "final-b.txt",
                        Path = @"C:\Users\test\final-b.txt",
                        MatchType = "name",
                    },
                });
        };

        var viewModel = new SearchViewModel(workspace)
        {
            Query = "  final  ",
        };
        var resultCounts = new List<int>();
        viewModel.ResultsChanged += (_, args) => resultCounts.Add(args.Results.Count);

        await viewModel.StartAsync(PaneId.Primary, action => action());

        Assert.True(viewModel.IsActive);
        Assert.False(viewModel.CanCancel);
        Assert.Equal(PaneId.Primary, viewModel.Pane);
        Assert.Equal(@"C:\Users\test", viewModel.Root);
        Assert.Equal(2, viewModel.ResultCount);
        Assert.Equal(["final-a.txt", "final-b.txt"], viewModel.Results.Select(result => result.Name));
        Assert.Equal("final", ipc.LastSearchOptions?.Query);
        Assert.Equal(@"C:\Users\test", ipc.LastSearchOptions?.SearchPath);
        Assert.False(ipc.LastSearchOptions?.IncludeHidden);
        Assert.False(ipc.LastSearchOptions?.ContentSearch);
        Assert.Equal("Search complete: 2 result(s)", viewModel.StatusText);
        Assert.Contains(1, resultCounts);
        Assert.Contains(2, resultCounts);

        workspace.SetShowHidden(true);
        viewModel.ContentSearch = true;
        await viewModel.StartAsync(PaneId.Primary, action => action());
        Assert.True(ipc.LastSearchOptions?.IncludeHidden);
        Assert.True(ipc.LastSearchOptions?.ContentSearch);
    }

    [Fact]
    public async Task SearchViewModel_CancelActiveAsyncCancelsBackendSearch()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        await workspace.InitializeAsync();

        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ipc.SearchFilesHandler = async (options, onBatch, onComplete, ct) =>
        {
            started.SetResult(options.SearchId ?? "");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        };

        var viewModel = new SearchViewModel(workspace)
        {
            Query = "notes",
        };

        var searchTask = viewModel.StartAsync(PaneId.Primary, action => action());
        var searchId = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.CancelActiveAsync();
        await searchTask;

        Assert.Equal(searchId, ipc.LastCancelledSearchId);
        Assert.False(viewModel.CanCancel);
    }

    [Fact]
    public async Task TransferViewModel_OwnsProgressFilteringAndCancellation()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        var viewModel = new TransferViewModel(workspace);
        var seen = new List<ProgressUpdate>();
        viewModel.ProgressReceived += (_, update) => seen.Add(update);

        var cts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-current");

        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-other", Current = 10, Total = 10, Status = "running" });
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-current", Current = 5, Total = 10, Status = "running" });
        await viewModel.CancelAsync();

        Assert.Single(seen);
        Assert.Equal(50, viewModel.ProgressPercent);
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal("op-current", ipc.LastCancelledOperationId);
        Assert.True(viewModel.IsCancelling);

        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-current", Current = 10, Total = 10, Status = "cancelled" });
        Assert.True(viewModel.FinishTransfer(cts));

        Assert.False(viewModel.HasActiveTransfer);
        Assert.False(viewModel.IsTransferring);
        Assert.False(viewModel.IsCancelling);
    }

    [Fact]
    public void TransferViewModel_DropsLateProgressAfterCompletion()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        var viewModel = new TransferViewModel(workspace);
        var seen = new List<ProgressUpdate>();
        viewModel.ProgressReceived += (_, update) => seen.Add(update);

        var cts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-current");

        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-current", Current = 5, Total = 10, Status = "running" });
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-current", Current = 10, Total = 10, Status = "completed" });
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-current", Current = 6, Total = 10, Status = "running" });
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-other", Current = 10, Total = 10, Status = "running" });

        Assert.Equal(["running", "completed"], seen.Select(update => update.Status));
        Assert.Equal(100, viewModel.ProgressPercent);
        Assert.Null(viewModel.CurrentOperationId);
        Assert.False(viewModel.IsTransferring);
        Assert.False(viewModel.IsCancelling);
        Assert.True(viewModel.FinishTransfer(cts));
        Assert.False(viewModel.HasActiveTransfer);
    }


    [Fact]
    public void TransferViewModel_BeginTransferClearsPriorOperationId()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        var viewModel = new TransferViewModel(workspace);

        var firstCts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-old");
        Assert.Equal("op-old", viewModel.CurrentOperationId);

        var secondCts = viewModel.BeginTransfer();
        Assert.Null(viewModel.CurrentOperationId);
        Assert.True(firstCts.IsCancellationRequested);
        Assert.True(viewModel.IsTransferring);
        Assert.True(viewModel.HasActiveTransfer);
        Assert.True(viewModel.FinishTransfer(secondCts));
        firstCts.Dispose();
    }

    [Fact]
    public void TransferViewModel_StaleTerminalProgressDoesNotCompleteNewTransfer()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        var viewModel = new TransferViewModel(workspace);
        var completedStatuses = new List<string>();
        viewModel.Completed += (_, args) => completedStatuses.Add(args.Status);

        var firstCts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-old");

        var secondCts = viewModel.BeginTransfer();
        Assert.Null(viewModel.CurrentOperationId);

        // Stale terminal events for the cancelled transfer must not finish the new one.
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-old", Current = 10, Total = 10, Status = "completed" });
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-old", Current = 10, Total = 10, Status = "cancelled" });
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-old", Current = 10, Total = 10, Status = "error" });

        Assert.Empty(completedStatuses);
        Assert.True(viewModel.IsTransferring);
        Assert.True(viewModel.HasActiveTransfer);

        viewModel.SetOperationId("op-new");
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-old", Current = 10, Total = 10, Status = "completed" });
        Assert.Empty(completedStatuses);
        Assert.Equal("op-new", viewModel.CurrentOperationId);
        Assert.True(viewModel.IsTransferring);

        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-new", Current = 10, Total = 10, Status = "completed" });
        Assert.Equal(["completed"], completedStatuses);
        Assert.Null(viewModel.CurrentOperationId);
        Assert.False(viewModel.IsTransferring);

        Assert.True(viewModel.FinishTransfer(secondCts));
        firstCts.Dispose();
    }

    [Fact]
    public async Task TransferViewModel_CancelThenStartNewIgnoresOldTerminalEvents()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        var viewModel = new TransferViewModel(workspace);
        var completedStatuses = new List<string>();
        viewModel.Completed += (_, args) => completedStatuses.Add(args.Status);

        var firstCts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-old");
        await viewModel.CancelAsync();

        var secondCts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-new");

        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-old", Current = 10, Total = 10, Status = "cancelled" });
        Assert.Empty(completedStatuses);
        Assert.Equal("op-new", viewModel.CurrentOperationId);
        Assert.True(viewModel.IsTransferring);

        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-new", Current = 4, Total = 10, Status = "running" });
        Assert.Equal(40, viewModel.ProgressPercent);

        Assert.True(viewModel.FinishTransfer(secondCts));
        firstCts.Dispose();
    }

    [Fact]
    public async Task TransferViewModel_CancelThenStartWaitsForBackendCancel()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        var viewModel = new TransferViewModel(workspace);

        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        ipc.CancelOperationHandler = async (operationId, ct) =>
        {
            order.Add($"cancel:{operationId}");
            cancelEntered.TrySetResult();
            await releaseCancel.Task.WaitAsync(ct);
            order.Add("cancel-done");
        };

        var firstCts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-old");

        var beginTask = viewModel.BeginTransferAsync();
        await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(beginTask.IsCompleted);
        Assert.Equal(["cancel:op-old"], order);

        releaseCancel.TrySetResult();
        var secondCts = await beginTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["cancel:op-old", "cancel-done"], order);
        Assert.Equal("op-old", ipc.LastCancelledOperationId);
        Assert.True(firstCts.IsCancellationRequested);
        Assert.Null(viewModel.CurrentOperationId);
        Assert.True(viewModel.IsTransferring);

        viewModel.SetOperationId("op-new");
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-old", Current = 10, Total = 10, Status = "cancelled" });
        Assert.Equal("op-new", viewModel.CurrentOperationId);

        Assert.True(viewModel.FinishTransfer(secondCts));
        firstCts.Dispose();
    }

    [Fact]
    public async Task TransferViewModel_CancelAsyncAwaitsBackendCancelCompletion()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        var viewModel = new TransferViewModel(workspace);

        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ipc.CancelOperationHandler = async (_, ct) =>
        {
            cancelEntered.TrySetResult();
            await releaseCancel.Task.WaitAsync(ct);
        };

        var cts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-slow");

        var cancelTask = viewModel.CancelAsync();
        await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(cancelTask.IsCompleted);

        releaseCancel.TrySetResult();
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("op-slow", ipc.LastCancelledOperationId);
        Assert.True(cts.IsCancellationRequested);
        Assert.True(viewModel.IsCancelling);

        Assert.True(viewModel.FinishTransfer(cts));
    }

    [Fact]
    public void TransferViewModel_CompleteCurrentOperationFinishesWhenTerminalProgressIsMissing()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new ConfigurableIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(ipc));
        var viewModel = new TransferViewModel(workspace);
        var seen = new List<ProgressUpdate>();
        var completedStatuses = new List<string>();
        viewModel.ProgressReceived += (_, update) => seen.Add(update);
        viewModel.Completed += (_, args) => completedStatuses.Add(args.Status);

        var cts = viewModel.BeginTransfer();
        viewModel.SetOperationId("op-current");

        viewModel.CompleteCurrentOperation("completed");
        viewModel.OnProgress(new ProgressUpdate { OperationId = "op-current", Current = 6, Total = 10, Status = "running" });

        Assert.Empty(seen);
        Assert.Equal(["completed"], completedStatuses);
        Assert.Equal(100, viewModel.ProgressPercent);
        Assert.Null(viewModel.CurrentOperationId);
        Assert.False(viewModel.IsTransferring);
        Assert.False(viewModel.IsCancelling);
        Assert.True(viewModel.FinishTransfer(cts));
        Assert.False(viewModel.HasActiveTransfer);
    }

    [Fact]
    public async Task ToolbarViewModel_OwnsNavigationAndStatusSnapshots()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();

        var viewModel = new ToolbarViewModel(workspace);
        viewModel.SyncFromWorkspace();
        viewModel.UpdateStatusBar(
            visibleCount: 2,
            selectedEntries:
            [
                new FileEntry
                {
                    Name = "notes.txt",
                    Path = @"C:\Users\test\notes.txt",
                    Size = 12,
                },
            ],
            searchResultCount: null,
            searchQuery: null);

        Assert.True(viewModel.CanGoUp);
        Assert.False(viewModel.CanGoBack);
        Assert.Equal(@"C:\Users\test", viewModel.PrimaryPath);
        Assert.Equal("2 items · 1 selected (12 B)", viewModel.CountText);
        Assert.Equal(@"C:\Users\test", viewModel.StatusText);

        viewModel.UpdateStatusBar(
            visibleCount: 2,
            selectedEntries: [],
            searchResultCount: 1,
            searchQuery: "notes");

        Assert.Equal("1 search result", viewModel.CountText);
        Assert.Equal("Search results for \"notes\"", viewModel.StatusText);
    }
}
