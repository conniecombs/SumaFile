using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class WorkspaceControllerTests
{
    [Fact]
    public void PlacesController_TracksBookmarksAndRecentHistory()
    {
        var controller = new PlacesController();
        controller.AddBookmark(@"C:\Users\test\Projects");
        controller.RecordRecent(@"C:\Users\test\Documents");
        controller.RecordRecent(@"C:\Users\test\Downloads");

        Assert.Single(controller.Bookmarks);
        Assert.Equal(@"C:\Users\test\Projects", controller.Bookmarks[0].Path);
        Assert.Equal([@"C:\Users\test\Downloads", @"C:\Users\test\Documents"], controller.RecentPaths);

        Assert.True(controller.ClearRecentHistory());
        Assert.Empty(controller.RecentPaths);
        Assert.False(controller.ClearRecentHistory());

        controller.RemoveBookmark(@"C:\Users\test\Projects");
        Assert.Empty(controller.Bookmarks);
    }

    [Fact]
    public async Task WorkspaceTagController_SeedsDefaultTagsAndFiltersEntries()
    {
        var ipc = new ConfigurableIpc();
        var fileOps = new FileOperationService(ipc);
        var controller = new WorkspaceTagController();

        await controller.LoadTagsAsync(fileOps);
        Assert.Equal(WorkspaceTagController.DefaultTags.Count, controller.AllTags.Count);

        var firstTag = controller.AllTags[0];
        await controller.SetColorLabelAsync(fileOps, [@"C:\Users\test\notes.txt"], firstTag.Id);
        Assert.Equal([@"C:\Users\test\notes.txt"], controller.FilesWithTag(firstTag.Id));

        FileEntry[] entries =
        [
            new() { Name = "notes.txt", Path = @"C:\Users\test\notes.txt" },
            new() { Name = "other.txt", Path = @"C:\Users\test\other.txt" },
        ];

        controller.SetTagFilter(firstTag.Id);
        var filtered = controller.FilterEntries(entries);
        Assert.Equal(["notes.txt"], filtered.Select(entry => entry.Name));

        controller.SetTagFilter(null);
        Assert.Equal(2, controller.FilterEntries(entries).Count);
    }

    [Fact]
    public void ClosedTabController_EnforcesMaxCapacityAndRestoresTab()
    {
        var controller = new ClosedTabController();
        for (var index = 0; index < ClosedTabController.Limit + 5; index++)
        {
            var tab = ExplorerPane.CreateTab($@"C:\Folder{index}");
            controller.Remember(PaneId.Primary, tab, index);
        }

        Assert.True(controller.CanReopen);
        Assert.True(controller.TryPop(out var mostRecent));
        Assert.NotNull(mostRecent);
        Assert.Equal($@"C:\Folder{ClosedTabController.Limit + 4}", mostRecent!.Tab.Path);

        var pane = new ExplorerPane(PaneId.Primary);
        var restored = ClosedTabController.RestoreIntoPane(pane, mostRecent);
        Assert.Equal(mostRecent.Tab.Path, restored.Path);
        Assert.Equal(restored.Id, pane.ActiveTabId);
    }

    [Fact]
    public void DriveController_SynthesizesFallbackAndTracksPendingReconnect()
    {
        var controller = new DriveController();
        var message = controller.ApplyDrives([], @"C:\Users\test", quiet: false);

        Assert.Equal("Drives refreshed", message);
        Assert.Single(controller.Drives);
        Assert.Equal(@"C:\", controller.Drives[0].Path);

        var networkDrive = new DriveInfo
        {
            Name = "NAS (Z:)",
            Path = @"Z:\",
            DriveType = "Network",
            DriveStatus = "disconnected",
        };
        controller.BeginPendingReconnect(networkDrive, PaneId.Secondary);
        Assert.Same(networkDrive, controller.PendingReconnect);
        Assert.Equal(PaneId.Secondary, controller.PendingReconnectPane);

        var (taken, pane) = controller.TakePendingReconnect();
        Assert.Same(networkDrive, taken);
        Assert.Equal(PaneId.Secondary, pane);
        Assert.Null(controller.PendingReconnect);
    }

    [Fact]
    public void OperationLogController_CapsHistoryAndComputesTransferAffectedPaths()
    {
        var controller = new OperationLogController();
        for (var index = 0; index < OperationLogController.Limit + 3; index++)
        {
            controller.RememberOperation(
                "copy",
                $"Copy {index}",
                [$@"C:\Src\file{index}.txt"],
                @"D:\Dst",
                move: false);
        }

        Assert.Equal(OperationLogController.Limit, controller.Entries.Count);
        Assert.Equal($"Copy {OperationLogController.Limit + 2}", controller.Entries[0].Description);

        var moveAffected = OperationLogController.TransferAffectedPaths([@"C:\Src\item.txt"], @"D:\Dst", move: true);
        Assert.Contains(@"D:\Dst", moveAffected);
        Assert.Contains(@"C:\Src\item.txt", moveAffected);
    }

    [Fact]
    public void FolderViewController_UpsertsAndResolvesFolderViewRule()
    {
        var controller = new FolderViewController();
        var settings = UiSettings.CreateDefault();
        var pane = new ExplorerPane(PaneId.Primary)
        {
            Path = @"C:\Users\test\Pictures",
            View = "grid",
            IconSize = 96,
            SortBy = "modified",
            SortAscending = false,
        };
        var columns = new ColumnLayout();
        IReadOnlyList<DriveInfo> drives =
        [
            new() { Name = "Windows (C:)", Path = @"C:\", DriveType = "Fixed" },
        ];

        var saved = controller.UpsertRule(
            settings,
            FolderViewScope.Folder,
            pane,
            columns,
            drives,
            showHiddenFiles: true,
            activeProfileId: "photos");

        var resolved = controller.ResolveRule(settings, @"C:\Users\test\Pictures", drives);
        Assert.NotNull(resolved);
        Assert.Equal(saved.Options.View, resolved!.Options.View);
        Assert.Equal(96, resolved.Options.IconSize);
        Assert.Equal("modified", resolved.Options.SortBy);
        Assert.False(resolved.Options.SortAscending);
    }
}
