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

public class StatusBarFormatterTests
{
    [Fact]
    public void StatusBar_IncludesSelectionSizeAndEmptyLoading()
    {
        var loading = StatusBarFormatter.Format(0, [], @"C:\", "Left pane", listingInProgress: true);
        Assert.Equal("Loading…", loading.ItemText);
        Assert.Contains("Left pane", loading.Combined, StringComparison.Ordinal);

        var empty = StatusBarFormatter.Format(0, [], @"C:\", null, isEmpty: true);
        Assert.Equal("Empty folder", empty.ItemText);

        var selected = StatusBarFormatter.Format(
            3,
            [
                new FileEntry { Name = "a.txt", Path = @"C:\a.txt", Size = 1024 },
                new FileEntry { Name = "b", Path = @"C:\b", IsDir = true },
            ],
            @"C:\",
            null);
        Assert.Equal("3 items", selected.ItemText);
        Assert.Equal("2 selected (1.0 KB)", selected.SelectionText);
    }
}
