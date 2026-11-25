using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Lazy columns on a workspace: what the pane asks the backend for as rows come into view, on a click and for a
/// sort, and what it does with the answers.
/// </summary>
public class LazyColumnWorkspaceTests
{
    private const string Work = @"C:\work";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static FileEntry Photo(int index) => FakeEntryFactsBackend.Entry(Work, $"img{index:D3}.jpg");
    private static FileEntry Folder(string name) => FakeEntryFactsBackend.Entry(Work, name, isDir: true);

    private static async Task<(ExplorerWorkspace Workspace, FakeEntryFactsBackend Backend)> OpenAsync(
        FileEntry[] entries,
        string folder = Work,
        bool isNetwork = false)
    {
        var backend = new FakeEntryFactsBackend();
        backend.AddFolder(folder, isNetwork, entries);
        var workspace = new ExplorerWorkspace(backend);
        await workspace.NavigatePaneAsync(PaneId.Primary, folder);
        await UntilAsync(() => !workspace.Primary.ListingInProgress && !workspace.Primary.IsNavigating, "the listing");
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

    private static FileEntry Row(ExplorerWorkspace workspace, string name) =>
        workspace.Primary.Entries.Single(entry => entry.Name == name);

    [Fact]
    public async Task OnlyTheVisibleRangeAndTheLookAheadAreRequested()
    {
        var (workspace, backend) = await OpenAsync([Folder("Album"), .. Enumerable.Range(0, 200).Select(Photo)]);
        workspace.SetColumnVisible(PaneId.Primary, "dimensions", true);

        workspace.ReportViewport(PaneId.Primary, 0, 19);

        var request = Assert.Single(backend.Requests);
        Assert.Equal(["dimensions"], request.Kinds);
        // Row 0 is the folder, which has no dimensions: 19 photos on screen and 24 more behind them.
        Assert.Equal(19 + LazyColumnController.LookAhead, request.Paths.Length);
        Assert.Equal(Work + @"\img000.jpg", request.Paths[0]);
        Assert.Equal(Work + @"\img042.jpg", request.Paths[^1]);
        // Folder size waits to be asked; item count is not shown.
        Assert.DoesNotContain(backend.Requests, candidate => candidate.Kinds.Contains(LazyColumns.FolderSize));
    }

    [Fact]
    public async Task ANewRangeReplacesTheOutstandingRequest()
    {
        var (workspace, backend) = await OpenAsync([.. Enumerable.Range(0, 200).Select(Photo)]);
        workspace.SetColumnVisible(PaneId.Primary, "dimensions", true);

        workspace.ReportViewport(PaneId.Primary, 0, 19);
        workspace.ReportViewport(PaneId.Primary, 100, 119);

        await UntilAsync(() => backend.Cancelled.Count == 1, "the first request to be cancelled");
        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal(backend.Requests[0].Id, backend.Cancelled[0]);
        Assert.Equal(Work + @"\img100.jpg", backend.Requests[1].Paths[0]);
    }

    [Fact]
    public async Task ResultsForAnEarlierNavigationAreDropped()
    {
        var (workspace, backend) = await OpenAsync([.. Enumerable.Range(0, 3).Select(Photo)]);
        backend.AddFolder(@"C:\other", entries: [FakeEntryFactsBackend.Entry(@"C:\other", "x.txt")]);
        workspace.SetColumnVisible(PaneId.Primary, "dimensions", true);
        workspace.ReportViewport(PaneId.Primary, 0, 2);
        var request = Assert.Single(backend.Requests);
        var photo = Row(workspace, "img000.jpg");

        await workspace.NavigatePaneAsync(PaneId.Primary, @"C:\other");
        backend.Deliver(request.Id, FakeEntryFactsBackend.Ready(photo.Path, "dimensions", "6000 × 4000", 24_000_000));
        backend.Complete(request.Id, 1);

        // Neither the late value nor the request's end settles the old folder's rows.
        await Task.Delay(100);
        Assert.Null(photo.FactFor("dimensions"));
    }

    [Fact]
    public async Task AFailedFactIsNotRequestedAgain()
    {
        var (workspace, backend) = await OpenAsync([.. Enumerable.Range(0, 5).Select(Photo)]);
        backend.Answer = (path, kind) => FakeEntryFactsBackend.Settled(path, kind, EntryFact.Failed);
        workspace.SetColumnVisible(PaneId.Primary, "dimensions", true);

        workspace.ReportViewport(PaneId.Primary, 0, 4);
        await UntilAsync(() => Row(workspace, "img004.jpg").FactFor("dimensions") is not null, "the answers");
        workspace.ReportViewport(PaneId.Primary, 0, 4);

        Assert.Single(backend.Requests);
        Assert.Equal(EntryFact.Failed, Row(workspace, "img000.jpg").FactFor("dimensions")!.State);
        Assert.Equal("", EntryPresentation.CellText(Row(workspace, "img000.jpg"), workspace.PrimaryColumns.Find("dimensions")!));
    }

    [Fact]
    public async Task ARequestThatEndsWithoutAnAnswerSettlesTheRowsAsFailed()
    {
        var (workspace, backend) = await OpenAsync([.. Enumerable.Range(0, 2).Select(Photo)]);
        workspace.SetColumnVisible(PaneId.Primary, "dimensions", true);
        workspace.ReportViewport(PaneId.Primary, 0, 1);
        var request = Assert.Single(backend.Requests);

        backend.Complete(request.Id, 0);

        await UntilAsync(() => Row(workspace, "img001.jpg").FactFor("dimensions") is { State: EntryFact.Failed }, "the rows to settle");
        workspace.ReportViewport(PaneId.Primary, 0, 1);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task OnRequestColumnsWaitForAnExplicitRequest()
    {
        var (workspace, backend) = await OpenAsync([Folder("Album"), Folder("Backups"), Photo(0)]);
        backend.Answer = (path, kind) => kind == LazyColumns.FolderSize ? FakeEntryFactsBackend.Ready(path, kind, "4096", 4096) : null;
        var album = Row(workspace, "Album");
        var sizeColumn = workspace.PrimaryColumns.Find("size")!;
        Assert.Equal(EntryPresentation.PendingMark, EntryPresentation.CellText(album, sizeColumn));

        // The size column is visible by default, but folder size is read on request only.
        workspace.ReportViewport(PaneId.Primary, 0, 2);
        Assert.Empty(backend.Requests);

        workspace.RequestFact(PaneId.Primary, album.Path, LazyColumns.FolderSize);
        await UntilAsync(() => album.Size == 4096, "the folder size");
        var request = Assert.Single(backend.Requests);
        Assert.Equal([album.Path], request.Paths);
        Assert.Equal([LazyColumns.FolderSize], request.Kinds);
        Assert.Equal("4.0 KB", EntryPresentation.CellText(album, sizeColumn));
        Assert.Equal(EntryPresentation.PendingMark, EntryPresentation.CellText(Row(workspace, "Backups"), sizeColumn));

        // The old "show folder sizes" setting makes the column automatic again.
        workspace.Settings.ShowFolderSizes = true;
        workspace.ReportViewport(PaneId.Primary, 0, 2);
        await UntilAsync(() => backend.Requests.Count == 2, "the automatic request");
        Assert.Equal([Row(workspace, "Backups").Path], backend.Requests[1].Paths);
    }

    [Fact]
    public async Task NothingIsRequestedOnANetworkPath()
    {
        const string share = @"\\server\share";
        var (workspace, backend) = await OpenAsync(
            [FakeEntryFactsBackend.Entry(share, "clip.mp4"), FakeEntryFactsBackend.Entry(share, "Sub", isDir: true)],
            share,
            isNetwork: true);
        workspace.SetColumnVisible(PaneId.Primary, "duration", true);
        workspace.SetColumnVisible(PaneId.Primary, "items", true);

        workspace.ReportViewport(PaneId.Primary, 0, 1);

        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task FolderSizeAndItemCountFactsUpdateTheEntry()
    {
        var (workspace, backend) = await OpenAsync([Folder("Album"), Photo(0)]);
        backend.Answer = (path, kind) => kind switch
        {
            LazyColumns.ItemCount => FakeEntryFactsBackend.Ready(path, kind, "3", 3),
            LazyColumns.FolderSize => FakeEntryFactsBackend.Ready(path, kind, "0", 0),
            _ => null,
        };
        workspace.SetColumnVisible(PaneId.Primary, "items", true);
        var changes = 0;
        workspace.Changed += (_, _) => Interlocked.Increment(ref changes);
        var album = Row(workspace, "Album");

        workspace.ReportViewport(PaneId.Primary, 0, 1);
        await UntilAsync(() => album.ItemCount == 3, "the item count");

        Assert.Equal([LazyColumns.ItemCount], Assert.Single(backend.Requests).Kinds);
        Assert.Equal("3 items", EntryPresentation.ColumnText(album, "items"));
        // The change event follows the value, outside the workspace lock: wait for it rather than race it.
        await UntilAsync(() => Volatile.Read(ref changes) >= 1, "the change event");

        workspace.RequestFact(PaneId.Primary, album.Path, LazyColumns.FolderSize);
        await UntilAsync(() => album.FactFor(LazyColumns.FolderSize) is not null, "the folder size");
        // An empty measured folder shows 0 B rather than nothing.
        Assert.Equal("0 B", EntryPresentation.ColumnText(album, "size"));
    }

    [Fact]
    public async Task AnUnsupportedDurationIsHandedToTheFallbackAndCached()
    {
        var (workspace, backend) = await OpenAsync([FakeEntryFactsBackend.Entry(Work, "old.avi")]);
        backend.Answer = (path, kind) => FakeEntryFactsBackend.Settled(path, kind, EntryFact.Unsupported);
        var asked = new List<(string Path, string Kind)>();
        workspace.FactFallback = (path, kind, _) =>
        {
            asked.Add((path, kind));
            return Task.FromResult<EntryFact?>(FakeEntryFactsBackend.Ready(path, kind, "0:42", 42));
        };
        workspace.SetColumnVisible(PaneId.Primary, "duration", true);
        var clip = Row(workspace, "old.avi");

        workspace.ReportViewport(PaneId.Primary, 0, 0);

        await UntilAsync(() => backend.Put.Count == 1, "the fallback's answer to be cached");
        Assert.Equal([(clip.Path, LazyColumns.Duration)], asked);
        Assert.Equal("0:42", EntryPresentation.ColumnText(clip, "duration"));
        Assert.Equal((clip.Path, "duration", "0:42"), (backend.Put[0].Path, backend.Put[0].Kind, backend.Put[0].Text));
    }

    [Fact]
    public async Task FallbackResultsReSortAPaneSortedByThatColumn()
    {
        var (workspace, backend) = await OpenAsync([FakeEntryFactsBackend.Entry(Work, "long.avi"), FakeEntryFactsBackend.Entry(Work, "short.avi")]);
        backend.Answer = (path, kind) => FakeEntryFactsBackend.Settled(path, kind, EntryFact.Unsupported);
        workspace.FactFallback = (path, kind, _) => Task.FromResult<EntryFact?>(path.EndsWith("short.avi", StringComparison.Ordinal)
            ? FakeEntryFactsBackend.Ready(path, kind, "0:05", 5)
            : FakeEntryFactsBackend.Ready(path, kind, "1:40", 100));
        workspace.SetColumnVisible(PaneId.Primary, "duration", true);

        // Sorting by duration fills the folder; the service has nothing, so every value comes from the fallback.
        workspace.SetSort("duration");
        await UntilAsync(() => backend.Put.Count == 2, "both fallback answers");

        Assert.Equal(["short.avi", "long.avi"], workspace.VisibleEntriesFor(PaneId.Primary).Select(entry => entry.Name));
    }

    [Fact]
    public async Task FillFolderFactsReadsEveryCandidateInChunksAndTheSortFollows()
    {
        var pdfs = Enumerable.Range(0, 100).Select(index => FakeEntryFactsBackend.Entry(Work, $"doc{index:D3}.pdf")).ToArray();
        var (workspace, backend) = await OpenAsync([Folder("Notes"), .. pdfs]);
        // Page counts fall as the names rise, so a sort by pages reverses the names.
        backend.Answer = (path, kind) => FakeEntryFactsBackend.Ready(path, kind, (200 - int.Parse(path[^7..^4])).ToString(), 200 - int.Parse(path[^7..^4]));
        workspace.SetColumnVisible(PaneId.Primary, "pages", true);
        var progress = new List<LazyColumnFill>();

        var fill = await workspace.FillFolderFactsAsync(PaneId.Primary, LazyColumns.Pages, new SynchronousProgress<LazyColumnFill>(progress.Add));

        Assert.Equal(new LazyColumnFill(100, 100, 0), fill);
        Assert.Equal([64, 36], backend.Requests.Select(request => request.Paths.Length));
        Assert.Equal(new LazyColumnFill(100, 64, 0), progress[1]);
        Assert.All(pdfs.Select(pdf => Row(workspace, pdf.Name)), row => Assert.Equal(EntryFact.Ready, row.FactFor(LazyColumns.Pages)!.State));

        workspace.SetSort("pages");
        var names = workspace.VisibleEntriesFor(PaneId.Primary).Select(entry => entry.Name).ToList();
        Assert.Equal("Notes", names[0]);
        Assert.Equal("doc099.pdf", names[1]);
        Assert.Equal("doc000.pdf", names[^1]);
    }

    [Fact]
    public async Task SortingByAnAutomaticLazyColumnFillsTheFolderThenSorts()
    {
        var pdfs = Enumerable.Range(0, 30).Select(index => FakeEntryFactsBackend.Entry(Work, $"doc{index:D3}.pdf")).ToArray();
        var (workspace, backend) = await OpenAsync(pdfs);
        backend.Answer = (path, kind) => FakeEntryFactsBackend.Ready(path, kind, (100 - int.Parse(path[^7..^4])).ToString(), 100 - int.Parse(path[^7..^4]));
        workspace.SetColumnVisible(PaneId.Primary, "pages", true);

        workspace.SetSort("pages");

        await UntilAsync(() => workspace.LazyColumnFill is { Done: true }, "the fill to finish");
        Assert.Equal(new LazyColumnFillStatus(PaneId.Primary, LazyColumns.Pages, new LazyColumnFill(30, 30, 0), true, false), workspace.LazyColumnFill);
        Assert.Equal("doc029.pdf", workspace.VisibleEntriesFor(PaneId.Primary)[0].Name);
        // Sorting the other way needs no second read.
        workspace.SetSort("pages");
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task SortingByAnOnRequestColumnSortsByWhatIsKnownWithoutReading()
    {
        var (workspace, backend) = await OpenAsync([Folder("B"), Folder("A"), Photo(0)]);

        workspace.SetSort("size");

        await Task.Delay(50);
        Assert.Empty(backend.Requests);
        Assert.Null(workspace.LazyColumnFill);
    }

    [Fact]
    public async Task AFillBeyondTheCapReadsTheCapAndSaysHowManyItLeft()
    {
        var pdfs = Enumerable.Range(0, LazyColumnController.MaxFillEntries + 10)
            .Select(index => FakeEntryFactsBackend.Entry(Work, $"doc{index:D5}.pdf"))
            .ToArray();
        var (workspace, backend) = await OpenAsync(pdfs);
        backend.Answer = (path, kind) => FakeEntryFactsBackend.Ready(path, kind, "1", 1);

        workspace.ReadColumnForFolder(PaneId.Primary, LazyColumns.Pages);

        await UntilAsync(() => workspace.LazyColumnFill is { Done: true }, "the fill to finish");
        Assert.Equal(new LazyColumnFill(LazyColumnController.MaxFillEntries, LazyColumnController.MaxFillEntries, 10), workspace.LazyColumnFill!.Fill);
        Assert.Null(Row(workspace, pdfs[^1].Name).FactFor(LazyColumns.Pages));
    }

    [Fact]
    public async Task CancellingTheFillKeepsWhatWasRead()
    {
        var pdfs = Enumerable.Range(0, 100).Select(index => FakeEntryFactsBackend.Entry(Work, $"doc{index:D3}.pdf")).ToArray();
        var (workspace, backend) = await OpenAsync(pdfs);
        workspace.ReadColumnForFolder(PaneId.Primary, LazyColumns.Pages);
        await UntilAsync(() => backend.Requests.Count == 1, "the first chunk");
        var first = backend.Requests[0];
        backend.Deliver(first.Id, [.. first.Paths.Select(path => FakeEntryFactsBackend.Ready(path, LazyColumns.Pages, "2", 2))]);
        backend.Complete(first.Id, first.Paths.Length);
        await UntilAsync(() => backend.Requests.Count == 2, "the second chunk");

        workspace.CancelColumnFill(PaneId.Primary);

        await UntilAsync(() => workspace.LazyColumnFill is { Done: true, Cancelled: true }, "the fill to stop");
        Assert.Equal(new LazyColumnFill(100, 64, 0), workspace.LazyColumnFill!.Fill);
        // The service is told from a pool thread, which may run after the fill has stopped: wait for it rather than race it.
        var second = backend.Requests[1].Id;
        await UntilAsync(() => backend.Cancelled.Contains(second), "the second chunk's request to be cancelled");
        Assert.Equal("2",EntryPresentation.ColumnText(Row(workspace, "doc000.pdf"), "pages"));
        Assert.Null(Row(workspace, "doc099.pdf").FactFor(LazyColumns.Pages));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
