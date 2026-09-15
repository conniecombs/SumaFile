using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class WorkspaceNavigationTests
{
    [Fact]
    public void ResolveStartPath_UsesHomeLastCustomAndPrimaryFallback()
    {
        Assert.Equal(
            @"C:\Users\test",
            WorkspaceNavigation.ResolveStartPath(
                new UiSettings { StartLocation = "home" },
                @"C:\Users\test",
                @"C:\Primary"));
        Assert.Equal(
            @"D:\Work",
            WorkspaceNavigation.ResolveStartPath(
                new UiSettings { StartLocation = "custom", CustomPath = "  D:\\Work  " },
                @"C:\Users\test",
                @"C:\Primary"));
        Assert.Equal(
            @"D:\Last",
            WorkspaceNavigation.ResolveStartPath(
                new UiSettings { StartLocation = "last", LastPath = "  D:\\Last  " },
                @"C:\Users\test",
                @"C:\Primary"));
        Assert.Equal(
            @"C:\Primary",
            WorkspaceNavigation.ResolveStartPath(
                new UiSettings { StartLocation = "home" },
                "",
                @"C:\Primary"));
    }

    [Fact]
    public void BuildStreamedListingOptions_DisablesStreamingForNetworkPane()
    {
        var pane = new ExplorerPane(PaneId.Primary)
        {
            PathIsNetwork = true,
            SortBy = "date",
            SortAscending = false,
        };

        Assert.Null(WorkspaceNavigation.BuildStreamedListingOptions(pane));

        pane.PathIsNetwork = false;
        var options = WorkspaceNavigation.BuildStreamedListingOptions(pane);
        Assert.NotNull(options);
        Assert.Equal("light", options.Mode);
        Assert.False(options.FinalEntries);
        Assert.Equal("date", options.SortBy);
        Assert.False(options.SortAscending);
        Assert.True(options.IncludeHidden);
    }

    [Fact]
    public void ApplyListingChunk_UpdatesPaneWithProgressiveEntries()
    {
        var pane = new ExplorerPane(PaneId.Primary)
        {
            IsNavigating = true,
            Path = @"C:\Old",
        };
        var progressive = new List<FileEntry>();
        var chunk = new DirectoryListingChunk
        {
            Path = @"C:\Next",
            IsNetwork = true,
            Entries =
            [
                new FileEntry { Name = "a.txt", Path = @"C:\Next\a.txt" },
            ],
        };

        WorkspaceNavigation.ApplyListingChunk(pane, chunk, progressive);

        Assert.Equal(@"C:\Next", pane.Path);
        Assert.True(pane.PathIsNetwork);
        Assert.False(pane.IsNavigating);
        Assert.Equal(["a.txt"], pane.Entries.Select(entry => entry.Name));
        Assert.Single(progressive);
    }

    [Fact]
    public void CanUsePresortedEntries_OnlyForActiveNameAscendingLocalListing()
    {
        var pane = new ExplorerPane(PaneId.Primary)
        {
            ListingInProgress = true,
            PathIsNetwork = false,
            SortBy = "name",
            SortAscending = true,
        };

        Assert.True(WorkspaceNavigation.CanUsePresortedEntries(pane, keepFoldersOnTop: true));

        pane.SortBy = "date";
        Assert.False(WorkspaceNavigation.CanUsePresortedEntries(pane, keepFoldersOnTop: true));

        pane.SortBy = "name";
        pane.PathIsNetwork = true;
        Assert.False(WorkspaceNavigation.CanUsePresortedEntries(pane, keepFoldersOnTop: true));
        Assert.False(WorkspaceNavigation.CanUsePresortedEntries(pane, keepFoldersOnTop: false));
    }

    [Fact]
    public async Task PaneNavigator_NavigateAsyncKeepsStreamedChunksWhenFinalResultIsTooLarge()
    {
        var backend = FakeExplorerBackend.Typical();
        backend.ThrowTooLargeAfterChunks = true;
        var pane = new ExplorerPane(PaneId.Primary);
        var changes = 0;
        var navigator = new PaneNavigator(backend, new object(), () => changes++);
        var token = pane.NextNavigationToken();

        var result = await navigator.NavigateAsync(pane, @"C:\Users\test", token, CancellationToken.None);

        Assert.False(result.IsAbandoned);
        Assert.Null(result.Listing);
        Assert.Contains("RESULT_TOO_LARGE", result.PartialResultMessage, StringComparison.Ordinal);
        Assert.Equal(["Desktop", "notes.txt"], pane.Entries.Select(entry => entry.Name));
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task PaneNavigator_RefreshAsyncIgnoresStaleFinalListing()
    {
        var backend = FakeExplorerBackend.Typical();
        var release = new TaskCompletionSource<DirectoryListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Pending[@"C:\Users\test"] = release.Task;
        var pane = new ExplorerPane(PaneId.Primary)
        {
            Path = @"C:\Users\test",
        };
        var navigator = new PaneNavigator(backend, new object(), () => { });
        var token = pane.NextNavigationToken();

        var refresh = navigator.RefreshAsync(pane, @"C:\Users\test", token, CancellationToken.None);
        pane.Path = @"C:\Other";
        release.SetResult(new DirectoryListing
        {
            Path = @"C:\Users\test",
            Entries = [new FileEntry { Name = "stale.txt", Path = @"C:\Users\test\stale.txt" }],
        });

        var result = await refresh;

        Assert.True(result.IsAbandoned);
        Assert.Equal(@"C:\Other", pane.Path);
        Assert.Empty(pane.Entries);
    }
}
