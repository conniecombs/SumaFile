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

public class PlacesStoreTests
{
    [Fact]
    public void PlacesStore_AddsRemovesAndCapsRecents()
    {
        var bookmarks = PlacesStore.AddBookmark([], @"C:\Work");
        bookmarks = PlacesStore.AddBookmark(bookmarks, @"C:\Temp");
        Assert.Equal(@"C:\Temp", bookmarks[0].Path);
        bookmarks = PlacesStore.RemoveBookmark(bookmarks, @"C:\Temp");
        Assert.Equal(@"C:\Work", bookmarks.Single().Path);

        var recents = new List<string>();
        for (var index = 0; index < 20; index++)
        {
            recents = PlacesStore.RecordRecent(recents, $@"C:\item{index}");
        }

        Assert.Equal(PlacesStore.RecentLimit, recents.Count);
        Assert.Equal(@"C:\item19", recents[0]);
    }
}
