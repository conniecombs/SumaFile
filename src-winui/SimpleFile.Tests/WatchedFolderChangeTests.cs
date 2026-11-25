using System.Text.Json;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// What the app does with the two things the folder watcher tells it beyond "this entry changed": an entry was
/// renamed (the selection follows it), and a watched folder itself is gone (the pane leaves it).
/// </summary>
public class WatchedFolderChangeTests
{
    private const string Work = @"C:\work";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static FileEntry File(string name, ulong size = 0) => FakeListingViewBackend.Entry(Work, name, size: size);

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public void Apply_ARenamedRowKeepsItsSelectionUnderItsNewPath()
    {
        FileEntry[] held = [File("a.txt"), File("b.txt"), File("c.txt")];
        var renamed = new ListingViewDelta
        {
            Changes =
            [
                new ListingViewChange { Kind = ListingViewChange.Remove, Index = 0 },
                new ListingViewChange
                {
                    Kind = ListingViewChange.Insert,
                    Index = 2,
                    Entry = File("z.txt"),
                    RenamedFrom = Work + @"\a.txt",
                },
            ],
        };
        List<PathRename> renames = [];

        var rows = PaneListing.Apply(held, renamed, Work + @"\A.TXT", out var lost, out var renamedTo, renames);

        Assert.Equal(["b.txt", "c.txt", "z.txt"], rows.Select(entry => entry.Name));
        Assert.False(lost);
        Assert.Equal(Work + @"\z.txt", renamedTo);
        Assert.Equal([new PathRename(Work + @"\a.txt", Work + @"\z.txt")], renames);

        // Another row is selected: it is neither lost nor renamed, and the rename is still reported.
        renames.Clear();
        _ = PaneListing.Apply(held, renamed, Work + @"\b.txt", out lost, out renamedTo, renames);
        Assert.False(lost);
        Assert.Null(renamedTo);
        Assert.Single(renames);

        // The form without renames behaves as it did: the old name's row left, so the selection is lost.
        _ = PaneListing.Apply(held, new ListingViewDelta { Changes = [renamed.Changes[0]] }, Work + @"\a.txt", out lost);
        Assert.True(lost);
    }

    [Fact]
    public void Apply_ARenameInPlaceAndOnePastTheRowsHeldAreFollowedToo()
    {
        FileEntry[] held = [File("a.txt"), File("b.txt")];
        // Only the letter case changed: the service updates the row where it is.
        var cased = new ListingViewDelta
        {
            Changes =
            [
                new ListingViewChange
                {
                    Kind = ListingViewChange.Update,
                    Index = 0,
                    Entry = File("A.txt"),
                    RenamedFrom = Work + @"\a.txt",
                },
            ],
        };
        var rows = PaneListing.Apply(held, cased, Work + @"\a.txt", out var lost, out var renamedTo, renames: null);
        Assert.Equal(["A.txt", "b.txt"], rows.Select(entry => entry.Name));
        Assert.False(lost);
        Assert.Equal(Work + @"\A.txt", renamedTo);

        // The new name sorts past the rows the pane has read: the row leaves them, and the selection names the
        // new path, which the pane reads when it gets there.
        var far = new ListingViewDelta
        {
            Changes =
            [
                new ListingViewChange { Kind = ListingViewChange.Remove, Index = 0 },
                new ListingViewChange
                {
                    Kind = ListingViewChange.Insert,
                    Index = 40,
                    Entry = File("zz.txt"),
                    RenamedFrom = Work + @"\a.txt",
                },
            ],
        };
        rows = PaneListing.Apply(held, far, Work + @"\a.txt", out _, out renamedTo, renames: null);
        Assert.Equal(["b.txt"], rows.Select(entry => entry.Name));
        Assert.Equal(Work + @"\zz.txt", renamedTo);
    }

    [Fact]
    public void Normalize_TheNameARenamedEntryHadBecomesThePathItHad()
    {
        var delta = JsonSerializer.Deserialize<ListingViewDelta>(
            """
            {"view_id":"pane-1","base_revision":1,"revision":2,"total":2,"reset":false,"changes":[
              {"kind":"remove","index":0},
              {"kind":"insert","index":1,"renamed_from":"old name.txt",
               "entry":{"name":"new.txt","path":"","is_dir":false,"is_symlink":false,"size":1,"modified":"","extension":""}}]}
            """)!;
        Assert.Null(delta.Changes[0].RenamedFrom);
        Assert.Equal("old name.txt", delta.Changes[1].RenamedFrom);

        NamedPipeJsonClient.NormalizeListingViewDelta(@"C:\Users\Public", delta);
        Assert.Equal(@"C:\Users\Public\old name.txt", delta.Changes[1].RenamedFrom);
        Assert.Equal(@"C:\Users\Public\new.txt", delta.Changes[1].Entry!.Path);
        // A delta kept while a navigation runs passes here again: it is left as it is.
        NamedPipeJsonClient.NormalizeListingViewDelta(@"C:\Users\Public", delta);
        Assert.Equal(@"C:\Users\Public\old name.txt", delta.Changes[1].RenamedFrom);

        // A change without it does not write the field.
        Assert.DoesNotContain("renamed_from", JsonSerializer.Serialize(delta.Changes[0]));
        Assert.Equal("gone", FileChangeEvent.Gone);
    }

    [Fact]
    public async Task Delta_ARenameMovesThePanesSelectionAndIsHandedToTheWindowOnce()
    {
        var backend = new FakeListingViewBackend();
        backend.AddFolder(Work, File("a.txt"), File("b.txt"), File("c.txt"));
        var workspace = new ExplorerWorkspace(backend);
        await workspace.NavigatePaneAsync(PaneId.Primary, Work);
        await UntilAsync(() => !workspace.Primary.ListingInProgress && !workspace.Primary.IsNavigating, "the listing");
        workspace.SelectPath(Work + @"\a.txt", PaneId.Primary);
        var revision = backend.RevisionOf(workspace.Primary.ListingViewId);

        // As the service sends it: names only, the old name on the arriving row.
        var arriving = File("d.txt");
        arriving.Path = "";
        backend.Raise(new ListingViewDelta
        {
            ViewId = workspace.Primary.ListingViewId,
            BaseRevision = revision,
            Revision = revision + 1,
            Total = 3,
            Changes =
            [
                new ListingViewChange { Kind = ListingViewChange.Remove, Index = 0 },
                new ListingViewChange { Kind = ListingViewChange.Insert, Index = 2, Entry = arriving, RenamedFrom = "a.txt" },
            ],
        });

        Assert.Equal(["b.txt", "c.txt", "d.txt"], workspace.VisibleEntriesFor(PaneId.Primary).Select(entry => entry.Name));
        Assert.Equal(Work + @"\d.txt", workspace.Primary.SelectedPath);
        Assert.Equal([new PathRename(Work + @"\a.txt", Work + @"\d.txt")], workspace.TakeRenamedPaths(PaneId.Primary));
        Assert.Empty(workspace.TakeRenamedPaths(PaneId.Primary));
        Assert.Empty(workspace.TakeRenamedPaths(PaneId.Secondary));
    }

    private static DirectoryListing Listing(string path, params string[] folders) => new()
    {
        Path = path,
        Entries = [.. folders.Select(name => new FileEntry { Name = name, Path = path.TrimEnd('\\') + @"\" + name, IsDir = true })],
    };

    private static FakeExplorerBackend Tree()
    {
        var backend = new FakeExplorerBackend { Home = @"C:\Users\test" };
        backend.Listings[@"C:\"] = Listing(@"C:\", "Users", "work");
        backend.Listings[@"C:\Users\test"] = Listing(@"C:\Users\test");
        backend.Listings[@"C:\work"] = Listing(@"C:\work", "project");
        backend.Listings[@"C:\work\project"] = Listing(@"C:\work\project", "out");
        backend.Listings[@"C:\work\project\out"] = Listing(@"C:\work\project\out");
        return backend;
    }

    [Fact]
    public async Task LeaveGoneFolder_ThePaneGoesToTheNearestFolderThatStillExists()
    {
        var backend = Tree();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.NavigatePaneAsync(PaneId.Primary, @"C:\work");
        await workspace.NavigatePaneAsync(PaneId.Primary, @"C:\work\project\out");
        // The folder and the one above it are deleted from outside.
        backend.Listings.Remove(@"C:\work\project\out");
        backend.Listings.Remove(@"C:\work\project");

        var left = await workspace.LeaveGoneFolderAsync(@"C:\work\project\OUT");

        Assert.Equal([PaneId.Primary], left);
        Assert.Equal(@"C:\work", workspace.Primary.Path);
        Assert.Null(workspace.ErrorMessage);
        Assert.Equal("\"OUT\" is no longer there.", workspace.StatusMessage);
        // The folder that is gone is not left in the history to go back to.
        Assert.DoesNotContain(workspace.Primary.History, path => path.Contains("out", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\work", workspace.Primary.History[workspace.Primary.HistoryIndex]);
    }

    [Fact]
    public async Task LeaveGoneFolder_AFolderThatWasMadeAgainIsListedAgainAndKept()
    {
        var backend = Tree();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.NavigatePaneAsync(PaneId.Primary, @"C:\work\project\out");
        // A build removed its output folder and made it again, with something in it.
        backend.Listings[@"C:\work\project\out"] = Listing(@"C:\work\project\out", "bin");
        var history = workspace.Primary.History.ToArray();

        var left = await workspace.LeaveGoneFolderAsync(@"C:\work\project\out");

        Assert.Equal([PaneId.Primary], left);
        Assert.Equal(@"C:\work\project\out", workspace.Primary.Path);
        Assert.Equal(["bin"], workspace.VisibleEntriesFor(PaneId.Primary).Select(entry => entry.Name));
        Assert.Null(workspace.StatusMessage);
        Assert.Equal(history, workspace.Primary.History);
    }

    [Fact]
    public async Task LeaveGoneFolder_OnlyThePanesThatShowTheFolderMoveAndTheHomeFolderIsTheLastResort()
    {
        var backend = Tree();
        backend.Listings[@"E:\stick\photos"] = Listing(@"E:\stick\photos");
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        await workspace.NavigatePaneAsync(PaneId.Primary, @"C:\work");
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"E:\stick\photos");

        // A folder no pane shows: nothing moves.
        Assert.Empty(await workspace.LeaveGoneFolderAsync(@"C:\work\project"));
        Assert.Equal((@"C:\work", @"E:\stick\photos"), (workspace.Primary.Path, workspace.Secondary.Path));

        // The drive was taken out: no folder above is left either.
        backend.Listings.Remove(@"E:\stick\photos");
        var left = await workspace.LeaveGoneFolderAsync(@"E:\stick\photos");

        Assert.Equal([PaneId.Secondary], left);
        Assert.Equal(@"C:\Users\test", workspace.Secondary.Path);
        Assert.Equal(@"C:\work", workspace.Primary.Path);
        Assert.Null(workspace.ErrorMessage);
    }

    [Fact]
    public void Window_LeavesAGoneFolderAndKeepsRenamedRowsSelected()
    {
        var repo = UiAuditSprint3Tests.FindRepoRoot();
        var window = System.IO.File.ReadAllText(Path.Combine(repo, "SimpleFile.App", "MainWindow.xaml.cs"));
        var onChange = window[window.IndexOf("private void OnFileChange(FileChangeEvent change)", StringComparison.Ordinal)..];
        onChange = onChange[..onChange.IndexOf("private async Task LeaveGoneFolderAsync(", StringComparison.Ordinal)];
        // Before the checks that skip an ordinary change: a search or a previewed file does not keep a pane in a
        // folder that is gone.
        var gone = onChange.IndexOf("string.Equals(change.Kind, FileChangeEvent.Gone, StringComparison.Ordinal)", StringComparison.Ordinal);
        Assert.True(gone > 0);
        Assert.True(gone < onChange.IndexOf("_previewPresenter.CurrentPath", StringComparison.Ordinal));
        Assert.True(gone < onChange.IndexOf("_search?.IsActive == true", StringComparison.Ordinal));

        var leave = window[window.IndexOf("private async Task LeaveGoneFolderAsync(", StringComparison.Ordinal)..];
        leave = leave[..leave.IndexOf("private static IReadOnlyList<string> FileChangeAffectedPaths", StringComparison.Ordinal)];
        Assert.Contains("await workspace.LeaveGoneFolderAsync(path);", leave);
        // The service let the folder go, so the watch is asked for again whatever happened.
        var afterLeave = leave[leave.IndexOf("finally", StringComparison.Ordinal)..];
        Assert.Contains("_watchTargetPath = null;", afterLeave);
        Assert.Contains("QueueWatchActiveDirectory();", afterLeave);

        // The renames are handed over inside the update that brings the renamed rows.
        foreach (var (list, pane) in new[] { ("PrimaryDetailsFileList", "Primary"), ("SecondaryDetailsFileList", "Secondary") })
        {
            var lower = char.ToLowerInvariant(pane[0]) + pane[1..];
            Assert.Contains($"var {lower}Renames = _workspace.TakeRenamedPaths(PaneId.{pane});", window);
            var begin = window.IndexOf($"{list}.FollowRenames({lower}Renames);", StringComparison.Ordinal);
            Assert.True(begin > 0, list);
            var before = window[..begin];
            Assert.True(before.LastIndexOf($"{list}.BeginUpdate();", StringComparison.Ordinal) > before.LastIndexOf($"{list}.EndUpdate();", StringComparison.Ordinal));
            // The other views' list holds its selection by row: asked before the rows change, selected after.
            var asked = window.IndexOf($"var {lower}Renamed = RenamedSelection({pane}FileList, {lower}Renames);", StringComparison.Ordinal);
            var selected = window.IndexOf($"SelectRenamedRows({pane}FileList, {pane}Files, {lower}Renamed);", StringComparison.Ordinal);
            Assert.True(asked > 0 && asked < begin && begin < selected, pane);
        }

        var details = System.IO.File.ReadAllText(Path.Combine(repo, "SimpleFile.App", "DetailsFileListView.cs"));
        var follow = details[details.IndexOf("public void FollowRenames(", StringComparison.Ordinal)..];
        follow = follow[..follow.IndexOf("public void EndUpdate()", StringComparison.Ordinal)];
        Assert.Contains("if (selected.Remove(rename.From))", follow);
        Assert.Contains("selected.Add(rename.To);", follow);
    }
}
