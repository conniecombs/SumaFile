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
        var ipc = new WorkspaceSettingsIpc();
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
        Assert.Equal("Search complete: 2 result(s)", viewModel.StatusText);
        Assert.Contains(1, resultCounts);
        Assert.Contains(2, resultCounts);
    }

    [Fact]
    public async Task SearchViewModel_CancelActiveAsyncCancelsBackendSearch()
    {
        var backend = FakeExplorerBackend.Typical();
        var ipc = new WorkspaceSettingsIpc();
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
        var ipc = new WorkspaceSettingsIpc();
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
