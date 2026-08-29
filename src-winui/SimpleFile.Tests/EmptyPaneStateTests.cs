using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class EmptyPaneStateTests
{
    [Fact]
    public void HiddenWhenVisibleItemsExist()
    {
        var state = EmptyPaneState.Resolve(3, 3, false, false, null, "", true, @"C:\");
        Assert.False(state.Visible);
        Assert.False(EmptyPaneState.Hidden.Visible);
    }

    [Fact]
    public void DistinguishesLoadingSearchErrorFilterHiddenAndEmpty()
    {
        var loading = EmptyPaneState.Resolve(0, 0, listingInProgress: true, searching: false, null, "", true, @"C:\");
        Assert.Equal("Loading…", loading.Title);

        var search = EmptyPaneState.Resolve(0, 0, false, searching: true, null, "", true, @"C:\");
        Assert.Equal("No search results", search.Title);

        var error = EmptyPaneState.Resolve(0, 0, false, false, "Access is denied.", "", true, @"C:\Windows\System32");
        Assert.Equal("Can't open this folder", error.Title);
        Assert.Equal("Access is denied.", error.Hint);

        var filter = EmptyPaneState.Resolve(0, 8, false, false, null, "xyz", true, @"C:\");
        Assert.Equal("No items match this filter", filter.Title);

        var hiddenOnly = EmptyPaneState.Resolve(0, 4, false, false, null, "", showHidden: false, @"C:\");
        Assert.Equal("Hidden items are hidden", hiddenOnly.Title);
        Assert.Contains("Ctrl+H", hiddenOnly.Hint, StringComparison.Ordinal);

        var empty = EmptyPaneState.Resolve(0, 0, false, false, null, "", true, @"C:\Empty");
        Assert.Equal("This folder is empty", empty.Title);
        Assert.Contains("Ctrl+Shift+N", empty.Hint, StringComparison.Ordinal);

        var noPath = EmptyPaneState.Resolve(0, 0, false, false, null, "", true, "");
        Assert.Equal("Select a folder", noPath.Title);
    }
}
