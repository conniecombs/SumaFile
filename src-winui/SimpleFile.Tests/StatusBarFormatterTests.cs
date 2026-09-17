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

    [Fact]
    public void StatusCenter_PrioritizesActiveTransfersLoadingThenIdle()
    {
        var transfers = StatusCenterFormatter.Format(
            activeTransfers: 2,
            queuedTransfers: 1,
            hasCompletedTransfers: false,
            listingInProgress: true,
            workspaceStatus: "Opened folder",
            operationHistoryCount: 4);
        Assert.Equal("2 active", transfers.BadgeText);
        Assert.Equal("Transfers in progress", transfers.HeaderText);
        Assert.True(transfers.HasWork);

        var loading = StatusCenterFormatter.Format(
            activeTransfers: 0,
            queuedTransfers: 0,
            hasCompletedTransfers: false,
            listingInProgress: true,
            workspaceStatus: null,
            operationHistoryCount: 0);
        Assert.Equal("Loading", loading.BadgeText);
        Assert.Equal("Folder is loading", loading.HeaderText);

        var idle = StatusCenterFormatter.Format(
            activeTransfers: 0,
            queuedTransfers: 0,
            hasCompletedTransfers: false,
            listingInProgress: false,
            workspaceStatus: null,
            operationHistoryCount: 0);
        Assert.Equal("Idle", idle.BadgeText);
        Assert.False(idle.HasWork);
    }

    [Fact]
    public void StatusCenter_MarksCompletedTransfersAsAttention()
    {
        var snapshot = StatusCenterFormatter.Format(
            activeTransfers: 0,
            queuedTransfers: 0,
            hasCompletedTransfers: true,
            listingInProgress: false,
            workspaceStatus: null,
            operationHistoryCount: 2);

        Assert.Equal("Done", snapshot.BadgeText);
        Assert.True(snapshot.HasAttention);
    }
}
