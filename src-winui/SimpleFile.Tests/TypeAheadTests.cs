using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class TypeAheadTests
{
    [Fact]
    public void TypeAhead_MatchesPrefixAndResetsAfterIdleWindow()
    {
        var entries = new[]
        {
            new FileEntry { Name = "Alpha.txt" },
            new FileEntry { Name = "Bravo.txt" },
        };
        Assert.Equal(1, TypeAhead.MatchIndex(entries, "br"));
        var buffer = new TypeAheadBuffer();
        buffer.Append('A', TimeSpan.FromSeconds(1));
        Assert.Equal("A", buffer.Text);
    }

    [Fact]
    public async Task TypeAhead_MatchesInThePaneThatReceivedTheKey()
    {
        var workspace = new ExplorerWorkspace(FakeExplorerBackend.Typical());
        await workspace.InitializeAsync();
        await workspace.NavigateToAsync(@"C:\Users\test");
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\Users\test\Desktop");
        workspace.ActivatePane(PaneId.Primary);

        // The left pane is active, but the key was typed into the right pane's list.
        Assert.Equal(
            @"C:\Users\test\Desktop\shot.png",
            workspace.MatchTypeAheadPath('s', PaneId.Secondary));

        workspace.TypeAheadBuffer.Clear();
        Assert.Null(workspace.MatchTypeAheadPath('s', PaneId.Primary));
    }

    [Fact]
    public async Task TypeAhead_MatchesTheSearchResultsAPaneShows()
    {
        var workspace = new ExplorerWorkspace(FakeExplorerBackend.Typical());
        await workspace.InitializeAsync();
        await workspace.NavigateToAsync(@"C:\Users\test");
        SearchResult[] results =
        [
            new() { Name = "shot.png", Path = @"C:\Users\test\Desktop\shot.png" },
            new() { Name = "notes-old.txt", Path = @"C:\Users\test\Archive\notes-old.txt" },
        ];

        // "notes.txt" is in the folder, but the pane shows the results, not the folder.
        Assert.Equal(
            @"C:\Users\test\Desktop\shot.png",
            workspace.MatchTypeAheadPath('s', PaneId.Primary, results));

        workspace.TypeAheadBuffer.Clear();
        Assert.Equal(
            @"C:\Users\test\Archive\notes-old.txt",
            workspace.MatchTypeAheadPath('n', PaneId.Primary, results));

        workspace.TypeAheadBuffer.Clear();
        Assert.Null(workspace.MatchTypeAheadPath('d', PaneId.Primary, results));
    }
}
