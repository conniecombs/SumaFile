using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// A workspace on a backend with listing views: the service sorts and filters, the pane reads pages, and row
/// changes arrive as deltas. The pane lists nothing whole and sorts nothing itself.
/// </summary>
public class ListingViewWorkspaceTests
{
    private const string Work = @"C:\work";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static FileEntry File(string name, ulong size = 0, bool hidden = false) =>
        FakeListingViewBackend.Entry(Work, name, size: size, hidden: hidden);

    private static FileEntry Folder(string name) => FakeListingViewBackend.Entry(Work, name, isDir: true);

    private static async Task<(ExplorerWorkspace Workspace, FakeListingViewBackend Backend)> OpenAsync(params FileEntry[] entries)
    {
        var backend = new FakeListingViewBackend();
        backend.AddFolder(Work, entries);
        var workspace = new ExplorerWorkspace(backend);
        workspace.SetShowHidden(true);
        await workspace.NavigatePaneAsync(PaneId.Primary, Work);
        await SettledAsync(workspace);
        return (workspace, backend);
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    private static Task SettledAsync(ExplorerWorkspace workspace) =>
        UntilAsync(() => !workspace.Primary.ListingInProgress && !workspace.Primary.IsNavigating, "the listing to finish");

    private static string[] Names(ExplorerWorkspace workspace) =>
        [.. workspace.VisibleEntriesFor(PaneId.Primary).Select(entry => entry.Name)];

    private static FileEntry[] ManyFiles(int count) =>
        [.. Enumerable.Range(0, count).Select(index => File($"file{index:D5}.txt", (ulong)index))];

    [Fact]
    public async Task Navigate_ShowsTheServicesOrderWithoutListingOrSortingTheFolderItself()
    {
        var (workspace, backend) = await OpenAsync(File("b.txt"), Folder("Zed"), File("A.txt"));

        Assert.Equal(["Zed", "A.txt", "b.txt"], Names(workspace));
        Assert.Equal(0, backend.ListDirectoryCalls);
        var request = Assert.Single(backend.OpenRequests);
        Assert.Equal(("name", true, true), (request.SortBy, request.SortAscending, request.FoldersFirst));
        // The pane hands the service's rows straight on: no copy, no second sort.
        Assert.Same(workspace.Primary.Entries, workspace.Primary.VisibleEntries(showHidden: true, "", keepFoldersOnTop: true));
        Assert.Equal(3, workspace.Primary.TotalEntryCount);
    }

    [Fact]
    public async Task Navigate_ShowsTheFirstPageAtOnceAndReadsTheRestBehindIt()
    {
        var backend = new FakeListingViewBackend { RangeGate = new TaskCompletionSource() };
        backend.AddFolder(Work, ManyFiles(9000));
        var workspace = new ExplorerWorkspace(backend);

        await workspace.NavigatePaneAsync(PaneId.Primary, Work);

        // Published with the first page only; the pane keeps listing until the rest is read.
        Assert.Equal((int)ListingViewController.FirstPageRows, workspace.Primary.Entries.Count);
        Assert.True(workspace.Primary.ListingInProgress);
        Assert.Equal(9000, workspace.Primary.TotalEntryCount);
        Assert.Equal("file00000.txt", workspace.Primary.Entries[0].Name);

        backend.RangeGate.SetResult();
        await SettledAsync(workspace);

        Assert.Equal(9000, workspace.Primary.Entries.Count);
        Assert.Equal(
            Enumerable.Range(0, 9000).Select(index => $"file{index:D5}.txt"),
            workspace.Primary.Entries.Select(entry => entry.Name));
        // 9000 rows after a 512-row first page, 4096 rows a request.
        Assert.Equal([512u, 4608u, 8704u], backend.RangeRequests.Select(range => range.Offset));
        Assert.Equal(0, backend.ListDirectoryCalls);
        Assert.Equal(1, backend.EnumerationCount);
    }

    [Fact]
    public async Task ChangingTheSort_AsksTheServiceToReorderAndNeverEnumeratesAgain()
    {
        var (workspace, backend) = await OpenAsync(File("small.txt", 1), File("big.txt", 900), File("mid.txt", 50));

        workspace.SetSort("size");
        await UntilAsync(() => Names(workspace).SequenceEqual(["small.txt", "mid.txt", "big.txt"]), "the size order");
        workspace.SetSort("size");
        await UntilAsync(() => Names(workspace).SequenceEqual(["big.txt", "mid.txt", "small.txt"]), "the descending size order");

        Assert.Equal(["name", "size", "size"], backend.OpenRequests.Select(request => request.SortBy));
        Assert.Equal([true, true, false], backend.OpenRequests.Select(request => request.SortAscending));
        // The navigation may enumerate; a re-sort only ever reuses the view the pane has.
        Assert.Equal([false, true, true], backend.OpenRequests.Select(request => request.Reuse));
        Assert.Equal(1, backend.EnumerationCount);
        Assert.Equal(0, backend.ListDirectoryCalls);
        Assert.Null(workspace.Primary.Listing!.Pending);
    }

    [Fact]
    public async Task ChangingTheSortTwiceBeforeTheServiceAnswers_EndsOnTheLastOrder()
    {
        var (workspace, backend) = await OpenAsync(File("small.txt", 1), File("big.txt", 900), File("mid.txt", 50));
        backend.OpenGate = new TaskCompletionSource();

        // Both requests are in flight together; whichever answer lands last, the pane must end on the second.
        workspace.SetSort("size");
        workspace.SetSort("size");
        await UntilAsync(() => backend.OpenRequests.Count >= 3, "both re-sort requests");
        backend.OpenGate.SetResult();

        await UntilAsync(
            () => workspace.Primary.Listing is { Pending: null } listing
                && listing.Request is { SortBy: "size", SortAscending: false }
                && Names(workspace).SequenceEqual(["big.txt", "mid.txt", "small.txt"]),
            "the descending size order");
        Assert.Equal(1, backend.EnumerationCount);
    }

    [Fact]
    public async Task ChangingTheFilterOrHiddenFiles_IsAppliedByTheService()
    {
        var (workspace, backend) = await OpenAsync(File("notes.txt"), File("photo.png"), File(".secret.txt", hidden: true));
        Assert.Equal([".secret.txt", "notes.txt", "photo.png"], Names(workspace));

        workspace.SetShowHidden(false);
        await UntilAsync(() => Names(workspace).SequenceEqual(["notes.txt", "photo.png"]), "hidden files to go");
        workspace.SetFilterQuery("txt");
        await UntilAsync(() => Names(workspace).SequenceEqual(["notes.txt"]), "the filter");
        workspace.SetFilterQuery("");
        await UntilAsync(() => Names(workspace).SequenceEqual(["notes.txt", "photo.png"]), "the filter to clear");

        Assert.Equal(1, backend.EnumerationCount);
        Assert.Equal(["", "", "txt", ""], backend.OpenRequests.Select(request => request.Filter));
        Assert.Equal([true, false, false, false], backend.OpenRequests.Select(request => request.IncludeHidden));
    }

    [Fact]
    public async Task AColumnTheServiceCannotSortBy_IsSortedByThePaneOverTheServicesNameOrder()
    {
        var (workspace, backend) = await OpenAsync(File("b.txt"), File("a.txt"));
        var before = backend.OpenRequests.Count;

        workspace.SetSort("path");
        workspace.SetSort("path");

        // Path descending, sorted here; the service was not asked for an order it does not have.
        Assert.Equal(["b.txt", "a.txt"], Names(workspace));
        Assert.Equal(before, backend.OpenRequests.Count);
        Assert.Equal("name", workspace.Primary.Listing!.Request.SortBy);
    }

    [Fact]
    public async Task AReportedChange_IsAppliedInPlaceAndKeepsTheSelection()
    {
        var (workspace, backend) = await OpenAsync(File("a.txt"), File("c.txt"));
        workspace.SelectPath(Work + @"\c.txt", PaneId.Primary);
        var kept = workspace.Primary.Entries[0];

        backend.ChangeFolder(Work, folder => folder.Add(File("b.txt")));

        Assert.Equal(["a.txt", "b.txt", "c.txt"], Names(workspace));
        // The rows that did not change are the same objects: nothing was listed or rebuilt.
        Assert.Same(kept, workspace.Primary.Entries[0]);
        Assert.Equal(Work + @"\c.txt", workspace.Primary.SelectedPath);
        Assert.Equal(1, backend.EnumerationCount);
        Assert.Equal(0, backend.ListDirectoryCalls);
        Assert.Equal(3, workspace.Primary.TotalEntryCount);
    }

    [Fact]
    public async Task ARemovedRow_TakesItsSelectionWithIt()
    {
        var (workspace, backend) = await OpenAsync(File("a.txt"), File("b.txt"));
        workspace.SelectPath(Work + @"\b.txt", PaneId.Primary);

        backend.ChangeFolder(Work, folder => folder.RemoveAll(entry => entry.Name == "b.txt"));

        Assert.Equal(["a.txt"], Names(workspace));
        Assert.Null(workspace.Primary.SelectedPath);
    }

    [Fact]
    public async Task AChangeAlreadyReflected_IsIgnored()
    {
        var (workspace, backend) = await OpenAsync(File("a.txt"));
        var delta = Assert.Single(backend.ChangeFolder(Work, folder => folder.Add(File("b.txt"))));
        Assert.Equal(["a.txt", "b.txt"], Names(workspace));

        // The same change again (a refresh's reply and the watcher's event can both carry it).
        backend.Raise(delta);

        Assert.Equal(["a.txt", "b.txt"], Names(workspace));
    }

    [Fact]
    public async Task AChangeThatDoesNotFollowFromThePanesRows_ReadsTheRowsAgain()
    {
        var (workspace, backend) = await OpenAsync(File("a.txt"));
        // One change the pane never hears of, then one it does.
        backend.ChangeFolder(Work, folder => folder.Add(File("b.txt")), raise: false);
        var opens = backend.OpenRequests.Count;

        backend.ChangeFolder(Work, folder => folder.Add(File("c.txt")));

        await UntilAsync(() => Names(workspace).SequenceEqual(["a.txt", "b.txt", "c.txt"]), "the rows to be read again");
        Assert.Equal(opens + 1, backend.OpenRequests.Count);
        // Read again from the view the service holds: the folder itself was not enumerated again.
        Assert.Equal(1, backend.EnumerationCount);
    }

    [Fact]
    public async Task AChangeDuringTheFill_IsAppliedAndTheRestIsStillReadInOrder()
    {
        var backend = new FakeListingViewBackend { RangeGate = new TaskCompletionSource() };
        backend.AddFolder(Work, ManyFiles(2000));
        var workspace = new ExplorerWorkspace(backend);
        await workspace.NavigatePaneAsync(PaneId.Primary, Work);
        Assert.True(workspace.Primary.ListingInProgress);

        // A file that sorts first arrives while only the first page is in hand.
        backend.ChangeFolder(Work, folder => folder.Add(File("aaa-first.txt")));
        backend.RangeGate.SetResult();
        await SettledAsync(workspace);

        Assert.Equal(2001, workspace.Primary.Entries.Count);
        Assert.Equal("aaa-first.txt", workspace.Primary.Entries[0].Name);
        Assert.Equal("file01999.txt", workspace.Primary.Entries[^1].Name);
        Assert.Equal(workspace.Primary.Entries.Count, workspace.Primary.Entries.Select(entry => entry.Path).Distinct().Count());
    }

    [Fact]
    public async Task Refresh_AsksTheServiceWhatChangedInsteadOfListingTheFolder()
    {
        var (workspace, backend) = await OpenAsync(File("a.txt"), File("b.txt"));
        workspace.SelectPath(Work + @"\a.txt", PaneId.Primary);
        backend.Folders[Work].Add(File("c.txt"));
        backend.Folders[Work].RemoveAll(entry => entry.Name == "b.txt");

        await workspace.RefreshAsync(PaneId.Primary);

        Assert.Equal(["a.txt", "c.txt"], Names(workspace));
        Assert.Equal(Work + @"\a.txt", workspace.Primary.SelectedPath);
        Assert.Equal(1, backend.RefreshCalls);
        Assert.Equal(0, backend.ListDirectoryCalls);
        Assert.Single(backend.OpenRequests);
    }

    [Fact]
    public async Task Refresh_WhenTheViewIsGone_OpensItAgain()
    {
        var (workspace, backend) = await OpenAsync(File("a.txt"));
        backend.Folders[Work].Add(File("b.txt"));
        backend.RefreshFailure = "Listing view is not open";

        await workspace.RefreshAsync(PaneId.Primary);
        await SettledAsync(workspace);

        Assert.Equal(["a.txt", "b.txt"], Names(workspace));
        Assert.NotNull(workspace.Primary.Listing);
        // Opened again with a fresh enumeration.
        Assert.True(backend.OpenRequests[^1].Reload);
        Assert.Equal(0, backend.ListDirectoryCalls);
    }

    [Fact]
    public async Task AChangeReportedWhileANavigationIsPublishing_IsAppliedByThePublish()
    {
        var backend = new FakeListingViewBackend { OpenGate = new TaskCompletionSource() };
        backend.AddFolder(Work, File("a.txt"));
        var workspace = new ExplorerWorkspace(backend);
        var navigation = workspace.NavigatePaneAsync(PaneId.Primary, Work);
        await UntilAsync(() => backend.OpenRequests.Count == 1, "the open request");

        // The service will answer the open at revision 1; this change takes the view to revision 2 before the
        // pane has published the page.
        var early = new ListingViewDelta
        {
            ViewId = workspace.Primary.ListingViewId,
            BaseRevision = 1,
            Revision = 2,
            Total = 2,
            Changes = [new ListingViewChange { Kind = ListingViewChange.Insert, Index = 1, Entry = File("b.txt") }],
        };
        backend.Raise(early);
        backend.OpenGate.SetResult();
        await navigation;
        await SettledAsync(workspace);

        Assert.Equal(["a.txt", "b.txt"], Names(workspace));
        Assert.Equal(2ul, workspace.Primary.Listing!.Revision);
    }

    [Fact]
    public async Task AReSortTheServiceRefuses_FallsBackToListingTheFolderWhole()
    {
        var (workspace, backend) = await OpenAsync(File("b.txt", 1), File("a.txt", 2));
        // The service lost the view (it restarted): a re-sort of "the view shown" has nothing to work on.
        await backend.CloseListingViewAsync(workspace.Primary.ListingViewId);

        workspace.SetSort("size");

        await UntilAsync(() => workspace.Primary.Listing is null && backend.ListDirectoryCalls > 0, "the fallback listing");
        await SettledAsync(workspace);
        Assert.Equal(["b.txt", "a.txt"], Names(workspace));
        Assert.True(workspace.Primary.ListingViewUnavailable);

        // The next navigation tries the view again.
        await workspace.NavigatePaneAsync(PaneId.Primary, Work);
        await SettledAsync(workspace);
        Assert.NotNull(workspace.Primary.Listing);
        Assert.Equal(["b.txt", "a.txt"], Names(workspace));
    }

    [Fact]
    public async Task NavigatingAway_StopsFollowingTheOldFoldersView()
    {
        var (workspace, backend) = await OpenAsync(File("a.txt"));
        backend.AddFolder(@"C:\other", FakeListingViewBackend.Entry(@"C:\other", "x.txt"));
        var stale = new ListingViewDelta
        {
            ViewId = workspace.Primary.ListingViewId,
            BaseRevision = workspace.Primary.Listing!.Revision,
            Revision = workspace.Primary.Listing.Revision + 100,
            Total = 2,
            Changes = [new ListingViewChange { Kind = ListingViewChange.Insert, Index = 0, Entry = File("ghost.txt") }],
        };

        await workspace.NavigatePaneAsync(PaneId.Primary, @"C:\other");
        await SettledAsync(workspace);
        backend.Raise(stale);

        // The delta named a revision the new view never had: the rows are read again, and ghost.txt is not in them.
        await UntilAsync(() => workspace.Primary.Listing is { NeedsReload: false }, "the reload");
        Assert.Equal(["x.txt"], Names(workspace));
    }

    [Fact]
    public async Task ABackendWithoutViews_StillListsAndSortsTheFolderItself()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);

        await workspace.NavigatePaneAsync(PaneId.Primary, @"C:\Users\test");

        Assert.Null(workspace.Primary.Listing);
        Assert.Equal(["Desktop", "notes.txt"], Names(workspace));
        Assert.True(backend.ListDirectoryCalls > 0);
    }

    [Fact]
    public void Apply_ChangesPastTheRowsHeldOnlyMoveTheTotal()
    {
        FileEntry[] held = [File("a.txt"), File("b.txt")];
        var delta = new ListingViewDelta
        {
            Changes =
            [
                new ListingViewChange { Kind = ListingViewChange.Remove, Index = 7 },
                new ListingViewChange { Kind = ListingViewChange.Insert, Index = 9, Entry = File("z.txt") },
                new ListingViewChange { Kind = ListingViewChange.Update, Index = 5, Entry = File("y.txt") },
                // The row right after the rows held is the next row of the view.
                new ListingViewChange { Kind = ListingViewChange.Insert, Index = 2, Entry = File("c.txt") },
                new ListingViewChange { Kind = ListingViewChange.Update, Index = 0, Entry = File("a.txt", size: 9) },
            ],
        };

        var rows = PaneListing.Apply(held, delta);

        Assert.Equal(["a.txt", "b.txt", "c.txt"], rows.Select(entry => entry.Name));
        Assert.Equal(9ul, rows[0].Size);
        Assert.Equal(2, held.Length);
    }

    [Fact]
    public void Apply_ARowThatOnlyMovedKeepsItsSelection()
    {
        FileEntry[] held = [File("a.txt"), File("b.txt"), File("c.txt")];
        var moved = new ListingViewDelta
        {
            Changes =
            [
                new ListingViewChange { Kind = ListingViewChange.Remove, Index = 0 },
                new ListingViewChange { Kind = ListingViewChange.Insert, Index = 2, Entry = File("a.txt", size: 500) },
            ],
        };
        var removed = new ListingViewDelta { Changes = [new ListingViewChange { Kind = ListingViewChange.Remove, Index = 0 }] };

        var rows = PaneListing.Apply(held, moved, Work + @"\a.txt", out var lostAfterMove);
        _ = PaneListing.Apply(held, removed, Work + @"\a.txt", out var lostAfterRemove);

        Assert.Equal(["b.txt", "c.txt", "a.txt"], rows.Select(entry => entry.Name));
        Assert.False(lostAfterMove);
        Assert.True(lostAfterRemove);
    }

    [Theory]
    [InlineData("name", false, true)]
    [InlineData("Modified", false, true)]
    [InlineData("date", true, true)]
    [InlineData("extension", true, true)]
    [InlineData("type", false, true)]
    [InlineData("size", false, true)]
    // Folder sizes are measured by the pane, so the size order is the pane's too.
    [InlineData("size", true, false)]
    [InlineData("items", false, false)]
    [InlineData("path", false, false)]
    [InlineData("decoration", false, false)]
    public void IsServiceSortColumn_NamesTheColumnsTheServiceOrders(string column, bool folderSizesShown, bool expected)
    {
        Assert.Equal(expected, PaneListing.IsServiceSortColumn(column, folderSizesShown));
    }

    [Fact]
    public void SameViewAs_ComparesTheOrderAndFilterNotThePageOrTheReload()
    {
        var request = new ListingViewRequest { ViewId = "v", Path = Work, SortBy = "name", Filter = " txt " };

        Assert.True(request.SameViewAs(new ListingViewRequest { ViewId = "other", Path = @"C:\x", SortBy = "NAME", Filter = "txt", PageSize = 9, Reload = true }));
        Assert.False(request.SameViewAs(new ListingViewRequest { ViewId = "v", Path = Work, SortBy = "name", Filter = "TXT" }));
        Assert.False(request.SameViewAs(new ListingViewRequest { ViewId = "v", Path = Work, SortBy = "name", Filter = "txt", IncludeHidden = false }));
        Assert.False(request.SameViewAs(new ListingViewRequest { ViewId = "v", Path = Work, SortBy = "name", Filter = "txt", FoldersFirst = false }));
        Assert.False(request.SameViewAs(null));
    }
}
