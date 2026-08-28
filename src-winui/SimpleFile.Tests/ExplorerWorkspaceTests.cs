using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class ExplorerWorkspaceTests
{
    [Fact]
    public async Task Initialize_NavigatesHomeAndRecordsHistory()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();

        Assert.Equal(@"C:\Users\test", workspace.HomePath);
        Assert.Equal(@"C:\Users\test", workspace.CurrentPath);
        Assert.Equal(["Desktop", "notes.txt"], workspace.VisibleEntries.Select(entry => entry.Name));
        Assert.Equal([@"C:\Users\test"], workspace.History);
        Assert.False(workspace.CanGoBack);
        Assert.True(workspace.CanGoUp);
    }

    [Fact]
    public async Task Navigate_PushesHistoryAndBackRestores()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        await workspace.NavigateToAsync(@"C:\Users\test\Desktop");

        Assert.Equal(@"C:\Users\test\Desktop", workspace.CurrentPath);
        Assert.True(workspace.CanGoBack);
        await workspace.GoBackAsync();
        Assert.Equal(@"C:\Users\test", workspace.CurrentPath);
        Assert.True(workspace.CanGoForward);
        await workspace.GoForwardAsync();
        Assert.Equal(@"C:\Users\test\Desktop", workspace.CurrentPath);
    }

    [Fact]
    public async Task GoUp_UsesParentAndDoesNothingAtRoot()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        await workspace.NavigateToAsync(@"C:\Users\test\Desktop");
        await workspace.GoUpAsync();
        Assert.Equal(@"C:\Users\test", workspace.CurrentPath);

        await workspace.NavigateToAsync(@"C:\");
        await workspace.GoUpAsync();
        Assert.Equal(@"C:\", workspace.CurrentPath);
        Assert.False(workspace.CanGoUp);
    }

    [Fact]
    public async Task OpenFolder_Navigates_OpenFile_ReportsMissingFileService()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();

        var folder = workspace.VisibleEntries.First(entry => entry.IsDir);
        await workspace.OpenEntryAsync(folder);
        Assert.Equal(@"C:\Users\test\Desktop", workspace.CurrentPath);

        await workspace.NavigateToAsync(@"C:\Users\test");
        var file = workspace.VisibleEntries.First(entry => !entry.IsDir);
        await workspace.OpenEntryAsync(file);
        Assert.Equal(@"C:\Users\test", workspace.CurrentPath);
        Assert.True(workspace.FileOpenUnsupported);
        Assert.Contains("No file operation service", workspace.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenArchiveFile_NavigatesIntoArchive()
    {
        var backend = FakeExplorerBackend.Typical();
        var archivePath = @"C:\Users\test\pack.zip";
        backend.Listings[@"C:\Users\test"].Entries.Add(new FileEntry
        {
            Name = "pack.zip",
            Path = archivePath,
            Extension = "zip",
            Size = 100,
        });
        backend.Listings[archivePath] = new DirectoryListing
        {
            Path = archivePath,
            Parent = @"C:\Users\test",
            Entries =
            [
                new FileEntry { Name = "inside.txt", Path = archivePath + @"\inside.txt", Size = 5 },
            ],
        };
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();

        var archive = workspace.VisibleEntries.First(entry => entry.Name == "pack.zip");
        await workspace.OpenEntryAsync(archive);

        Assert.Equal(archivePath, workspace.CurrentPath);
        Assert.Equal("inside.txt", workspace.VisibleEntries.Single().Name);
        Assert.False(workspace.FileOpenUnsupported);
    }

    [Fact]
    public async Task OpenPath_UnknownDirectoryType_ProbesEntryInfoAndNavigates()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        settingsIpc.EntryInfo[@"C:\Users\test\Desktop"] = new FileEntry
        {
            Name = "Desktop",
            Path = @"C:\Users\test\Desktop",
            IsDir = true,
        };
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(settingsIpc));
        await workspace.InitializeAsync();

        await workspace.OpenPathAsync(@"C:\Users\test\Desktop", isDirectory: null);

        Assert.Empty(settingsIpc.OpenedFiles);
        Assert.Equal(@"C:\Users\test\Desktop", workspace.CurrentPath);
        Assert.Equal("shot.png", workspace.VisibleEntries.Single().Name);
    }

    [Fact]
    public async Task OpenPath_UnknownFileType_ProbesEntryInfoAndOpensFile()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        settingsIpc.EntryInfo[@"C:\Users\test\notes.txt"] = new FileEntry
        {
            Name = "notes.txt",
            Path = @"C:\Users\test\notes.txt",
            Extension = "txt",
        };
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(settingsIpc));
        await workspace.InitializeAsync();

        await workspace.OpenPathAsync(@"C:\Users\test\notes.txt", isDirectory: null);

        Assert.Equal([@"C:\Users\test\notes.txt"], settingsIpc.OpenedFiles);
        Assert.Equal(@"C:\Users\test", workspace.CurrentPath);
        Assert.Equal(@"C:\Users\test\notes.txt", workspace.SelectedPath);
        Assert.Equal("Opened notes.txt", workspace.StatusMessage);
        Assert.Null(workspace.ErrorMessage);
    }

    [Fact]
    public async Task OpenPath_FilePreservesAndPersistsPaneViewOptions()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(settingsIpc));
        await workspace.InitializeAsync();
        workspace.SetFileListView(PaneId.Primary, "tiles");
        workspace.SetFileListIconSize(PaneId.Primary, 224);

        await workspace.OpenPathAsync(@"C:\Users\test\notes.txt", isDirectory: false, PaneId.Primary);
        await workspace.SaveWorkspaceLayoutAsync();

        Assert.Equal([@"C:\Users\test\notes.txt"], settingsIpc.OpenedFiles);
        Assert.Equal("tiles", workspace.ViewFor(PaneId.Primary));
        Assert.Equal(224, workspace.IconSizeFor(PaneId.Primary));
        Assert.Contains("\"view\":\"tiles\"", settingsIpc.Settings[WorkspaceLayout.SettingsKey]);
        Assert.Contains("\"iconSize\":224", settingsIpc.Settings[WorkspaceLayout.SettingsKey]);
    }

    [Fact]
    public async Task ApplyGitStatuses_UpdatesPaneEntriesWhenEnabled()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        settingsIpc.GitFileStatuses[@"C:\Users\test"] =
        [
            new FileEntry
            {
                Name = "notes.txt",
                Path = @"C:\Users\test\notes.txt",
                GitStatus = "modified",
            },
        ];
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(settingsIpc));
        await workspace.InitializeAsync();

        await workspace.ApplyGitStatusesAsync(PaneId.Primary);

        Assert.Equal(1, settingsIpc.GitStatusCalls);
        Assert.Equal(
            "modified",
            workspace.VisibleEntries.Single(entry => entry.Name == "notes.txt").GitStatus);
    }

    [Fact]
    public async Task FillFolderMetrics_StaleNavigationDoesNotUpdateOldEntries()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        var sizeRequestStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sizeResponse = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        settingsIpc.CalculateFolderSizeHandler = (path, ct) =>
        {
            sizeRequestStarted.TrySetResult(path);
            return sizeResponse.Task.WaitAsync(ct);
        };
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(settingsIpc));
        await workspace.InitializeAsync();
        var oldFolder = workspace.VisibleEntries.Single(entry => entry.Name == "Desktop");
        var changedEvents = 0;
        workspace.Changed += (_, _) => changedEvents += 1;

        var metrics = workspace.FillFolderMetricsAsync(PaneId.Primary, includeSizes: true, includeItemCounts: false);
        Assert.Equal(@"C:\Users\test\Desktop", await sizeRequestStarted.Task);
        await workspace.NavigateToAsync(@"C:\Users\test\Desktop");
        var changedAfterNavigation = changedEvents;

        sizeResponse.SetResult(1234);
        await metrics;

        Assert.Equal(@"C:\Users\test\Desktop", workspace.CurrentPath);
        Assert.Equal(0UL, oldFolder.Size);
        Assert.Equal(changedAfterNavigation, changedEvents);
    }

    [Fact]
    public async Task CreateFolder_CancellationAfterMutationSkipsRefresh()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        using var cts = new CancellationTokenSource();
        var createTokenWasUsable = false;
        settingsIpc.CreateDirectoryHandler = (path, name, ct) =>
        {
            Assert.Equal(@"C:\Users\test", path);
            Assert.Equal("New Folder", name);
            createTokenWasUsable = !ct.IsCancellationRequested;
            cts.Cancel();
            return Task.FromResult(@"C:\Users\test\New Folder");
        };
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(settingsIpc));
        await workspace.InitializeAsync();
        var listCallsAfterInitialize = backend.ListDirectoryCalls;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => workspace.CreateFolderInCurrentPaneAsync("New Folder", cts.Token));

        Assert.True(createTokenWasUsable);
        Assert.Equal(listCallsAfterInitialize, backend.ListDirectoryCalls);
    }

    [Fact]
    public async Task PackIntoFolder_CancellationAfterCreateSkipsMoveAndRefresh()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        using var cts = new CancellationTokenSource();
        settingsIpc.CreateDirectoryHandler = (_, _, _) =>
        {
            cts.Cancel();
            return Task.FromResult(@"C:\Users\test\Packed");
        };
        settingsIpc.MoveWithProgressHandler = (_, _, _, _, _) =>
            throw new InvalidOperationException("move should not run after cancellation");
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(settingsIpc));
        await workspace.InitializeAsync();
        var listCallsAfterInitialize = backend.ListDirectoryCalls;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => workspace.PackIntoFolderAsync([@"C:\Users\test\notes.txt"], "Packed", cts.Token));

        Assert.Equal(0, settingsIpc.MoveWithProgressCalls);
        Assert.Equal(listCallsAfterInitialize, backend.ListDirectoryCalls);
    }

    [Fact]
    public async Task Initialize_CancellationFromSmartFolderLoadPropagates()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc
        {
            LoadSmartFoldersHandler = ct => Task.FromCanceled<SmartFolder[]>(ct),
        };
        var workspace = new ExplorerWorkspace(backend, new FileOperationService(settingsIpc));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.InitializeAsync(cts.Token));
    }

    [Fact]
    public async Task RefreshDrives_CancellationAfterListSkipsDriveMutation()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        var originalDrive = workspace.Drives.Single();

        using var cts = new CancellationTokenSource();
        backend.ListDrivesHandler = _ =>
        {
            cts.Cancel();
            return Task.FromResult<IReadOnlyList<DriveInfo>>(
            [
                new DriveInfo
                {
                    Name = "Stale replacement",
                    Path = @"Z:\",
                    DriveType = "Network",
                    DriveStatus = "available",
                },
            ]);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => workspace.RefreshDrivesAsync(cancellationToken: cts.Token));

        Assert.Same(originalDrive, workspace.Drives.Single());
    }

    [Fact]
    public async Task NavigateNetworkFailure_CanceledBeforeRecoverySkipsDriveRefreshAndError()
    {
        var backend = FakeExplorerBackend.Typical();
        backend.Drives.Add(new DriveInfo
        {
            Name = "Team Share",
            Path = @"Z:\",
            DriveType = "Network",
            DriveStatus = "offline",
            StatusDetail = "Network path unavailable",
        });
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();

        using var cts = new CancellationTokenSource();
        var driveCallsAfterInitialize = backend.ListDrivesCalls;
        backend.ListDirectoryHandler = (path, _) =>
        {
            if (string.Equals(path, @"Z:\Projects", StringComparison.OrdinalIgnoreCase))
            {
                cts.Cancel();
                throw new IpcException(Protocol.ErrApplication, "Path is not available");
            }

            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => workspace.NavigateToAsync(@"Z:\Projects", cancellationToken: cts.Token));

        Assert.Equal(driveCallsAfterInitialize, backend.ListDrivesCalls);
        Assert.Null(workspace.ErrorMessage);
    }

    [Fact]
    public async Task NavigateSpecial_HomeAndDesktop()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        await workspace.NavigateSpecialAsync("navigateDesktop");
        Assert.Equal(@"C:\Users\test\Desktop", workspace.CurrentPath);
        await workspace.NavigateSpecialAsync("navigateHome");
        Assert.Equal(@"C:\Users\test", workspace.CurrentPath);
    }

    [Fact]
    public async Task ListDirectoryChunks_PaintBeforeFinalResult()
    {
        var backend = FakeExplorerBackend.Typical();
        backend.EmitChunks = true;
        var workspace = new ExplorerWorkspace(backend);
        var paints = 0;
        workspace.Changed += (_, _) =>
        {
            if (workspace.VisibleEntries.Count > 0)
            {
                paints += 1;
            }
        };

        await workspace.NavigateToAsync(@"C:\Users\test");
        Assert.True(paints >= 1);
        Assert.Equal(2, workspace.VisibleEntries.Count);
        Assert.Equal("light", backend.LastListDirectoryOptions?.Mode);
        Assert.False(backend.LastListDirectoryOptions?.FinalEntries ?? true);
    }

    [Fact]
    public async Task ResultTooLarge_KeepsStreamedChunks()
    {
        var backend = FakeExplorerBackend.Typical();
        backend.ThrowTooLargeAfterChunks = true;
        var workspace = new ExplorerWorkspace(backend);
        await workspace.NavigateToAsync(@"C:\Users\test");
        Assert.NotEmpty(workspace.VisibleEntries);
        Assert.Contains(@"C:\Users\test", workspace.History);
        Assert.Contains("RESULT_TOO_LARGE", workspace.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleNavigation_IsIgnored()
    {
        var backend = FakeExplorerBackend.Typical();
        var first = new TaskCompletionSource<DirectoryListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Pending["slow"] = first.Task;
        var workspace = new ExplorerWorkspace(backend);

        var slow = workspace.NavigateToAsync("slow");
        await workspace.NavigateToAsync(@"C:\Users\test");
        first.SetResult(new DirectoryListing
        {
            Path = "slow",
            Entries = [new FileEntry { Name = "stale", Path = @"slow\stale" }],
        });
        await slow;

        Assert.Equal(@"C:\Users\test", workspace.CurrentPath);
        Assert.DoesNotContain(workspace.VisibleEntries, entry => entry.Name == "stale");
    }

    [Fact]
    public async Task OfflineNetworkDrive_SetsPendingReconnect()
    {
        var backend = FakeExplorerBackend.Typical();
        backend.Drives.Add(new DriveInfo
        {
            Name = "Share (Z:)",
            Path = @"Z:\",
            DriveType = "Network",
            DriveStatus = "offline",
            StatusDetail = "The network path was not found",
        });
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        await workspace.OpenPathAsync(@"Z:\", isDirectory: true);
        Assert.NotNull(workspace.PendingReconnect);
        Assert.Equal(@"Z:\", workspace.PendingReconnect!.Path);
        Assert.NotEqual(@"Z:\", workspace.CurrentPath);
    }

    [Fact]
    public async Task RefreshDrives_UsesFallbackWhenEmpty()
    {
        var backend = FakeExplorerBackend.Typical();
        backend.Drives.Clear();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        Assert.Single(workspace.Drives);
        Assert.Equal(@"C:\", workspace.Drives[0].Path);
    }

    [Fact]
    public async Task Initialize_RestoresSavedWorkspaceLayoutFromIpcSettings()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        settingsIpc.Settings["startLocation"] = "last";
        var fileOps = new FileOperationService(settingsIpc);
        var first = new ExplorerWorkspace(backend, fileOps);
        await first.InitializeAsync();
        await first.OpenNewTabAsync(PaneId.Primary, @"C:\Users\test\Desktop");
        await first.ToggleDualPaneAsync();
        await first.NavigatePaneAsync(PaneId.Secondary, @"C:\", HistoryMode.ReplaceCurrent);
        first.SetFileListView(PaneId.Primary, "content");
        first.SetFileListIconSize(PaneId.Primary, 48);
        first.SetSort(PaneId.Primary, "size");
        first.SetFileListView(PaneId.Secondary, "tiles");
        first.SetFileListIconSize(PaneId.Secondary, 96);
        first.SetSort(PaneId.Secondary, "date");
        first.SetSort(PaneId.Secondary, "date");
        await first.SaveWorkspaceLayoutAsync();

        var second = new ExplorerWorkspace(backend, fileOps);
        await second.InitializeAsync();

        Assert.True(second.DualPaneEnabled);
        Assert.Equal(@"C:\Users\test\Desktop", second.Primary.Path);
        Assert.Equal(@"C:\", second.Secondary.Path);
        Assert.Equal("content", second.ViewFor(PaneId.Primary));
        Assert.Equal(48, second.IconSizeFor(PaneId.Primary));
        Assert.Equal("size", second.SortByFor(PaneId.Primary));
        Assert.True(second.SortAscendingFor(PaneId.Primary));
        Assert.Equal("tiles", second.ViewFor(PaneId.Secondary));
        Assert.Equal(96, second.IconSizeFor(PaneId.Secondary));
        Assert.Equal("date", second.SortByFor(PaneId.Secondary));
        Assert.False(second.SortAscendingFor(PaneId.Secondary));
        Assert.Equal(PaneId.Secondary, second.ActivePane);
        Assert.Equal("date", second.SortBy);
        Assert.True(second.Primary.Tabs.Count >= 1);
        var activePrimaryTab = second.Primary.Tabs.First(tab => tab.Id == second.Primary.ActiveTabId);
        Assert.Equal(second.Primary.Path, activePrimaryTab.Path);
    }

    [Fact]
    public async Task Initialize_HomeStartLocationIgnoresSavedWorkspaceLayout()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        settingsIpc.Settings["startLocation"] = "last";
        var fileOps = new FileOperationService(settingsIpc);
        var first = new ExplorerWorkspace(backend, fileOps);
        await first.InitializeAsync();
        await first.OpenNewTabAsync(PaneId.Primary, @"C:\Users\test\Desktop");
        await first.ToggleDualPaneAsync();
        await first.NavigatePaneAsync(PaneId.Secondary, @"C:\", HistoryMode.ReplaceCurrent);
        await first.SaveWorkspaceLayoutAsync();

        settingsIpc.Settings["startLocation"] = "home";
        var second = new ExplorerWorkspace(backend, fileOps);
        await second.InitializeAsync();

        Assert.False(second.DualPaneEnabled);
        Assert.Equal(@"C:\Users\test", second.Primary.Path);
        Assert.Equal([@"C:\Users\test"], second.Primary.History);
    }

    [Fact]
    public async Task Initialize_CustomStartLocationIgnoresSavedWorkspaceLayout()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        settingsIpc.Settings["startLocation"] = "last";
        var fileOps = new FileOperationService(settingsIpc);
        var first = new ExplorerWorkspace(backend, fileOps);
        await first.InitializeAsync();
        await first.OpenNewTabAsync(PaneId.Primary, @"C:\Users\test\Desktop");
        await first.SaveWorkspaceLayoutAsync();

        settingsIpc.Settings["startLocation"] = "custom";
        settingsIpc.Settings["customPath"] = @"C:\";
        var second = new ExplorerWorkspace(backend, fileOps);
        await second.InitializeAsync();

        Assert.False(second.DualPaneEnabled);
        Assert.Equal(@"C:\", second.Primary.Path);
        Assert.Equal([@"C:\"], second.Primary.History);
    }

    [Fact]
    public async Task UiSettings_RestoresColumnPresetAndWidths()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        var fileOps = new FileOperationService(settingsIpc);
        var first = new ExplorerWorkspace(backend, fileOps);
        await first.InitializeAsync();
        var settings = UiSettings.CreateDefault();
        settings.ColumnPreset = "developer";
        settings.DefaultView = "tiles";
        settings.DefaultIconSize = 96;
        settings.ShowQuickAccess = false;
        settings.ShowFolderTree = true;
        settings.ShowBookmarks = false;
        settings.ShowRecentLocations = false;
        settings.ShowSmartFolders = false;
        settings.SidebarVisible = false;
        settings.SidebarWidth = 344;
        settings.PreviewWidth = 420;
        settings.DualPanePrimaryPercent = 35;
        settings.DualPanePrimaryWidth = 410;
        settings.QuickAccessCollapsed = true;
        settings.MyPcCollapsed = true;
        first.ApplyUiSettings(settings);
        first.Columns.Resize("path", 360);
        await first.SaveUiSettingsAsync();

        var second = new ExplorerWorkspace(backend, fileOps);
        await second.InitializeAsync();

        Assert.Equal("developer", second.Settings.ColumnPreset);
        Assert.Equal("tiles", second.Settings.DefaultView);
        Assert.Equal(96, second.Settings.DefaultIconSize);
        Assert.Equal("tiles", second.ViewFor(PaneId.Primary));
        Assert.Equal(96, second.IconSizeFor(PaneId.Primary));
        Assert.Equal(["name", "size", "date", "extension", "git", "symlink", "path"], second.Columns.VisibleColumns.Select(column => column.Id));
        Assert.Equal(360, second.Columns.WidthOf("path"));
        Assert.False(second.Settings.ShowQuickAccess);
        Assert.True(second.Settings.ShowFolderTree);
        Assert.False(second.Settings.ShowBookmarks);
        Assert.False(second.Settings.ShowRecentLocations);
        Assert.False(second.Settings.ShowSmartFolders);
        Assert.False(second.Settings.SidebarVisible);
        Assert.Equal(344, second.Settings.SidebarWidth);
        Assert.Equal(420, second.Settings.PreviewWidth);
        Assert.Equal(35, second.Settings.DualPanePrimaryPercent);
        Assert.Equal(410, second.Settings.DualPanePrimaryWidth);
        Assert.True(second.Settings.QuickAccessCollapsed);
        Assert.True(second.Settings.MyPcCollapsed);
        Assert.Equal("tiles", settingsIpc.Settings["defaultView"]);
        Assert.Equal("96", settingsIpc.Settings["defaultIconSize"]);
        Assert.Equal("false", settingsIpc.Settings["sidebar.showQuickAccess"]);
        Assert.Equal("true", settingsIpc.Settings["sidebar.showFolders"]);
        Assert.Equal("false", settingsIpc.Settings["sidebar.showBookmarks"]);
        Assert.Equal("false", settingsIpc.Settings["sidebar.showRecent"]);
        Assert.Equal("false", settingsIpc.Settings["sidebar.showSmartFolders"]);
        Assert.Equal("false", settingsIpc.Settings["sidebar.visible"]);
        Assert.Equal("344", settingsIpc.Settings["sidebar.width"]);
        Assert.Equal("420", settingsIpc.Settings["preview.width"]);
        Assert.Equal("35", settingsIpc.Settings["dualPane.primaryPercent"]);
    }

    [Fact]
    public async Task UiSettings_RestoresBookmarksAndRecentPaths()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        var fileOps = new FileOperationService(settingsIpc);
        var first = new ExplorerWorkspace(backend, fileOps);
        await first.InitializeAsync();
        first.AddBookmark(@"C:\Users\test\Desktop");
        await first.NavigateToAsync(@"C:\Users\test\Desktop");
        await first.NavigateToAsync(@"C:\");
        await first.SaveUiSettingsAsync();

        Assert.True(settingsIpc.Settings.ContainsKey("places.bookmarks"));
        Assert.True(settingsIpc.Settings.ContainsKey("places.recents"));

        var second = new ExplorerWorkspace(backend, fileOps);
        await second.InitializeAsync();

        Assert.Equal([@"C:\Users\test\Desktop"], second.Bookmarks.Select(bookmark => bookmark.Path));
        Assert.Equal(
            [@"C:\Users\test", @"C:\", @"C:\Users\test\Desktop"],
            second.RecentPaths.Take(3));

        second.ClearRecentHistory();
        await second.SaveUiSettingsAsync();

        Assert.Empty(second.RecentPaths);
        Assert.Equal("[]", settingsIpc.Settings["places.recents"]);
    }

    [Fact]
    public async Task PaneViewOptions_ArePaneLocalAndCanApplyToBothPanes()
    {
        var backend = FakeExplorerBackend.Typical();
        var settingsIpc = new WorkspaceSettingsIpc();
        var fileOps = new FileOperationService(settingsIpc);
        var workspace = new ExplorerWorkspace(backend, fileOps);
        await workspace.InitializeAsync();

        var settings = UiSettings.CreateDefault();
        settings.DefaultView = "tiles";
        settings.DefaultIconSize = 96;
        workspace.ApplyUiSettings(settings);
        await workspace.ToggleDualPaneAsync();

        workspace.SetFileListView(PaneId.Primary, "content");
        workspace.SetFileListIconSize(PaneId.Primary, 48);
        workspace.SetSort(PaneId.Primary, "size");
        workspace.SetFileListView(PaneId.Secondary, "details");
        workspace.SetFileListIconSize(PaneId.Secondary, 16);
        workspace.SetSort(PaneId.Secondary, "date");
        workspace.SetSort(PaneId.Secondary, "date");

        Assert.Equal("content", workspace.ViewFor(PaneId.Primary));
        Assert.Equal(48, workspace.IconSizeFor(PaneId.Primary));
        Assert.Equal("size", workspace.SortByFor(PaneId.Primary));
        Assert.True(workspace.SortAscendingFor(PaneId.Primary));
        Assert.Equal("details", workspace.ViewFor(PaneId.Secondary));
        Assert.Equal(16, workspace.IconSizeFor(PaneId.Secondary));
        Assert.Equal("date", workspace.SortByFor(PaneId.Secondary));
        Assert.False(workspace.SortAscendingFor(PaneId.Secondary));

        await workspace.SaveUiSettingsAsync();
        Assert.Equal("tiles", settingsIpc.Settings["defaultView"]);
        Assert.Equal("96", settingsIpc.Settings["defaultIconSize"]);

        workspace.ApplyViewOptionsToBothPanes(PaneId.Secondary);

        Assert.Equal("details", workspace.ViewFor(PaneId.Primary));
        Assert.Equal(16, workspace.IconSizeFor(PaneId.Primary));
        Assert.Equal("date", workspace.SortByFor(PaneId.Primary));
        Assert.False(workspace.SortAscendingFor(PaneId.Primary));
        Assert.Equal("details", workspace.ViewFor(PaneId.Secondary));
        Assert.Equal(16, workspace.IconSizeFor(PaneId.Secondary));
        Assert.Equal("date", workspace.SortByFor(PaneId.Secondary));
        Assert.False(workspace.SortAscendingFor(PaneId.Secondary));
    }

    [Fact]
    public async Task ApplyingDefaultViewSettingsCanPreserveCurrentPaneViewOptions()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();

        workspace.SetFileListView(PaneId.Primary, "content");
        workspace.SetFileListIconSize(PaneId.Primary, 48);
        var settings = workspace.Settings;
        settings.DefaultView = "tiles";
        settings.DefaultIconSize = 96;

        workspace.ApplyUiSettings(settings, applyViewDefaultsToPanes: false);

        Assert.Equal("tiles", workspace.Settings.DefaultView);
        Assert.Equal(96, workspace.Settings.DefaultIconSize);
        Assert.Equal("content", workspace.ViewFor(PaneId.Primary));
        Assert.Equal(48, workspace.IconSizeFor(PaneId.Primary));
    }

    [Fact]
    public async Task KeepFoldersOnTop_CanBeTurnedOffToMixFiles()
    {
        var backend = FakeExplorerBackend.Typical();
        backend.Listings[@"C:\Users\test"].Entries =
        [
            new FileEntry { Name = "zoo", Path = @"C:\Users\test\zoo", IsDir = true },
            new FileEntry { Name = "alpha.txt", Path = @"C:\Users\test\alpha.txt" },
            new FileEntry { Name = "beta", Path = @"C:\Users\test\beta", IsDir = true },
        ];
        var workspace = new ExplorerWorkspace(backend);
        await workspace.InitializeAsync();
        Assert.True(workspace.Settings.KeepFoldersOnTop);
        Assert.Equal(["beta", "zoo", "alpha.txt"], workspace.VisibleEntries.Select(entry => entry.Name));

        workspace.Settings.KeepFoldersOnTop = false;
        workspace.ApplyUiSettings(workspace.Settings, applyViewDefaultsToPanes: false);
        Assert.Equal(["alpha.txt", "beta", "zoo"], workspace.VisibleEntries.Select(entry => entry.Name));
    }

}
internal sealed class FakeExplorerBackend : IExplorerBackend
{
    public string Home { get; set; } = @"C:\Users\test";
    public List<DriveInfo> Drives { get; } = [];
    public Dictionary<string, DirectoryListing> Listings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Task<DirectoryListing>> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EmitChunks { get; set; }
    public bool ThrowTooLargeAfterChunks { get; set; }
    public int ListDirectoryCalls { get; private set; }
    public int ListDrivesCalls { get; private set; }
    public ListDirectoryOptions? LastListDirectoryOptions { get; private set; }
    public Func<CancellationToken, Task<IReadOnlyList<DriveInfo>>>? ListDrivesHandler { get; set; }
    public Func<string, CancellationToken, Task<DirectoryListing>?>? ListDirectoryHandler { get; set; }

    public static FakeExplorerBackend Typical()
    {
        var backend = new FakeExplorerBackend();
        backend.Drives.Add(new DriveInfo
        {
            Name = "Windows (C:)",
            Path = @"C:\",
            DriveType = "Fixed",
            DriveStatus = "available",
            TotalSpace = 100,
            FreeSpace = 40,
        });
        backend.Listings[@"C:\Users\test"] = new DirectoryListing
        {
            Path = @"C:\Users\test",
            Parent = @"C:\Users",
            Entries =
            [
                new FileEntry { Name = "Desktop", Path = @"C:\Users\test\Desktop", IsDir = true },
                new FileEntry { Name = "notes.txt", Path = @"C:\Users\test\notes.txt", Extension = "txt", Size = 12 },
            ],
        };
        backend.Listings[@"C:\Users\test\Desktop"] = new DirectoryListing
        {
            Path = @"C:\Users\test\Desktop",
            Parent = @"C:\Users\test",
            Entries =
            [
                new FileEntry { Name = "shot.png", Path = @"C:\Users\test\Desktop\shot.png", Extension = "png" },
            ],
        };
        backend.Listings[@"C:\"] = new DirectoryListing
        {
            Path = @"C:\",
            Entries =
            [
                new FileEntry { Name = "Users", Path = @"C:\Users", IsDir = true },
            ],
        };
        return backend;
    }

    public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Home);
    }

    public Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default)
    {
        ListDrivesCalls += 1;
        return ListDrivesHandler?.Invoke(cancellationToken)
            ?? Task.FromResult<IReadOnlyList<DriveInfo>>(Drives);
    }

    public async Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListDirectoryCalls += 1;
        LastListDirectoryOptions = options;
        var handled = ListDirectoryHandler?.Invoke(path, cancellationToken);
        if (handled is not null)
        {
            return await handled.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Pending.TryGetValue(path, out var pending))
        {
            return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Listings.TryGetValue(path, out var listing))
        {
            throw new IpcException(Protocol.ErrApplication, $"Path is not a directory: {path}");
        }

        if (EmitChunks || ThrowTooLargeAfterChunks)
        {
            onChunk?.Invoke(new DirectoryListingChunk
            {
                Path = listing.Path,
                Entries = listing.Entries,
                ChunkIndex = 0,
                Done = true,
            });
        }

        if (ThrowTooLargeAfterChunks)
        {
            throw new IpcException(
                Protocol.ErrApplication,
                "RESULT_TOO_LARGE: list_directory result exceeds 80 MiB; use streamed chunks");
        }

        return listing;
    }
}

internal sealed class WorkspaceSettingsIpc : ISimpleFileIpc
{
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FileEntry> EntryInfo { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileEntry[]> GitFileStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ulong> FolderSizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ulong> FolderItemCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> OpenedFiles { get; } = [];
    public Func<string, CancellationToken, Task<ulong>>? CalculateFolderSizeHandler { get; set; }
    public Func<string, CancellationToken, Task<ulong>>? CountFolderItemsHandler { get; set; }
    public Func<string, string, CancellationToken, Task<string>>? CreateDirectoryHandler { get; set; }
    public Func<string[], string, string?, string, CancellationToken, Task<TransferResult[]>>? MoveWithProgressHandler { get; set; }
    public Func<CancellationToken, Task<SmartFolder[]>>? LoadSmartFoldersHandler { get; set; }
    public int GitStatusCalls { get; private set; }
    public int MoveWithProgressCalls { get; private set; }
    public bool IsConnected => true;

#pragma warning disable CS0067
    public event EventHandler<Exception?>? Disconnected;
#pragma warning restore CS0067

    public Task<string?> GetDbSettingAsync(string key, CancellationToken ct = default)
    {
        Settings.TryGetValue(key, out var value);
        return Task.FromResult<string?>(value);
    }

    public Task SetDbSettingAsync(string key, string value, CancellationToken ct = default)
    {
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public Task<GitStatus> GetGitStatusAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FileEntry[]> GetGitFileStatusesAsync(string path, CancellationToken ct = default)
    {
        GitStatusCalls += 1;
        return Task.FromResult(GitFileStatuses.TryGetValue(path, out var statuses) ? statuses : []);
    }

    public Task GitPullAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task GitPushAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CancelFolderSizeAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task CancelFolderItemCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task CancelCountItemsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> CheckRarInstalledAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RarInstallPlan> PrepareRarInstallAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task DiscardRarInstallAsync(string confirmationToken, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> InstallRarAsync(string confirmationToken, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CleanupResult> DiskCleanupAsync(string path, ulong? minSize, string? opId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CancelDiskCleanupAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<DuplicateCheckResult> DuplicateCheckAsync(string path, ulong? minSize, ulong? hashBytes, string? opId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CancelDuplicateCheckAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Tag[]> GetAllTagsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Tag> CreateTagAsync(string name, string color, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Tag> UpdateTagAsync(long id, string name, string color, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteTagAsync(long id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Tag[]> GetTagsForPathAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SetTagsForPathAsync(string path, long[] tags, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Dictionary<string, Tag>> GetAllFileTagsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string[]> GetFilesWithTagAsync(long id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default)
    {
        return LoadSmartFoldersHandler?.Invoke(ct)
            ?? throw new NotImplementedException();
    }
    public Task<SmartFolder[]> SaveSmartFolderAsync(SmartFolder folder, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SmartFolder[]> DeleteSmartFolderAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AppAboutInfo> GetAppAboutInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task InstallUpdateAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task OpenTerminalAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task OpenPowershellAdminAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public IDisposable On<T>(string eventName, Action<T> handler) => new NoopSubscription();
    public Task<HandshakeResult> HandshakeAsync(string authToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<TResult> InvokeAsync<TResult>(string method, object? args, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task InvokeAsync(string method, object? args, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<DirectoryListing> ListDirectoryAsync(string path, Action<DirectoryListingChunk>? onChunk = null, CancellationToken cancellationToken = default, ListDirectoryOptions? options = null) => throw new NotImplementedException();
    public Task<HealthResult> HealthAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<string> GetAppVersionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task SelectDirectoryAsync(string? defaultPath = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ShowMainWindowAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<string> CreateDirectoryAsync(string path, string name, CancellationToken ct = default)
    {
        return CreateDirectoryHandler?.Invoke(path, name, ct)
            ?? throw new NotImplementedException();
    }
    public Task<string> CreateFileAsync(string path, string name, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteEntryAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task MoveToTrashAsync(string[] paths, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> RenameEntryAsync(string path, string newName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> CopyEntryAsync(string source, string destination, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> MoveEntryAsync(string source, string destination, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> CopyEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> MoveEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FileEntry> GetEntryInfoAsync(string path, CancellationToken ct = default)
    {
        if (EntryInfo.TryGetValue(path, out var entry))
        {
            return Task.FromResult(entry);
        }

        throw new IpcException(Protocol.ErrApplication, $"Path does not exist: {path}");
    }

    public Task OpenFileAsync(string path, CancellationToken ct = default)
    {
        OpenedFiles.Add(path);
        return Task.CompletedTask;
    }

    public Task RevealInFolderAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task OpenExternalUrlAsync(string url, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ArchiveInfo> ListArchiveAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateArchiveAsync(string[] paths, string archivePath, string format, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FilePreview> ReadFilePreviewAsync(string path, ulong? maxSize = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> GenerateThumbnailAsync(string path, uint size, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ThumbnailResult[]> GenerateThumbnailsAsync(string[] paths, uint size, CancellationToken ct = default) => throw new NotImplementedException();
    public Task OpenFileWithAsync(string path, string application, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FileComparison> CompareFilesAsync(string pathA, string pathB, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Checksums> ComputeChecksumAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ImageMetadata> GetImageMetadataAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FileMetadata> GetFileMetadataAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TreeNode[]> ListSubdirectoriesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ulong> CalculateFolderSizeAsync(string path, CancellationToken ct = default)
    {
        return CalculateFolderSizeHandler?.Invoke(path, ct)
            ?? Task.FromResult(FolderSizes.TryGetValue(path, out var size) ? size : 0UL);
    }

    public Task<ulong> CountFolderItemsAsync(string path, CancellationToken ct = default)
    {
        return CountFolderItemsHandler?.Invoke(path, ct)
            ?? Task.FromResult(FolderItemCounts.TryGetValue(path, out var count) ? count : 0UL);
    }

    public Task<TransferResult[]> CopyWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<TransferResult[]> MoveWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default)
    {
        MoveWithProgressCalls += 1;
        return MoveWithProgressHandler?.Invoke(sources, destination, operationId, conflictAction, ct)
            ?? throw new NotImplementedException();
    }
    public Task CancelOperationAsync(string operationId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SearchResult[]> SearchFilesAsync(SearchOptions options, Action<SearchResult[]>? onBatch = null, Action<int>? onComplete = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CancelSearchAsync(string searchId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task WatchDirectoryAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UnwatchDirectoryAsync(CancellationToken ct = default) => throw new NotImplementedException();

    private sealed class NoopSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
