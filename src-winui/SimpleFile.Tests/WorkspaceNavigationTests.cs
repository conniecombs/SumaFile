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
}
