using System.Text.Json;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class WorkspaceSettingsStoreTests
{
    [Fact]
    public void SavedWorkspaceLayoutsDocument_SanitizesStoredLayouts()
    {
        var raw = """
            {
              "version": 99,
              "layouts": [
                {
                  "id": "",
                  "name": "  Code    review  ",
                  "createdAt": "0001-01-01T00:00:00+00:00",
                  "updatedAt": "0001-01-01T00:00:00+00:00",
                  "layout": {
                    "dualPaneEnabled": true,
                    "primary": { "path": "C:\\Work" }
                  },
                  "chrome": {
                    "previewVisible": false,
                    "previewWidth": 2000,
                    "sidebarVisible": false,
                    "sidebarWidth": 20,
                    "dualPanePrimaryPercent": 5,
                    "dualPanePrimaryWidth": 40,
                    "columnPreset": "developer",
                    "columnWidths": { "name": 321 }
                  }
                },
                {
                  "id": "duplicate",
                  "name": "code review",
                  "layout": {}
                },
                {
                  "id": "blank",
                  "name": "   ",
                  "layout": {}
                }
              ]
            }
            """;

        var document = SavedWorkspaceLayoutsDocument.FromJson(raw);
        var saved = Assert.Single(document.Layouts);

        Assert.Equal(SavedWorkspaceLayoutsDocument.CurrentVersion, document.Version);
        Assert.NotEmpty(saved.Id);
        Assert.Equal("Code review", saved.Name);
        Assert.True(saved.Layout.DualPaneEnabled);
        Assert.Equal(@"C:\Work", saved.Layout.Primary.Path);
        Assert.NotNull(saved.Chrome);
        Assert.False(saved.Chrome!.PreviewVisible);
        Assert.Equal(UiSettings.PreviewMaxWidth, saved.Chrome.PreviewWidth);
        Assert.False(saved.Chrome.SidebarVisible);
        Assert.Equal(UiSettings.SidebarMinWidth, saved.Chrome.SidebarWidth);
        Assert.Equal(UiSettings.DualPaneMinPercent, saved.Chrome.DualPanePrimaryPercent);
        Assert.Equal(UiSettings.FilePaneMinWidth, saved.Chrome.DualPanePrimaryWidth);
        Assert.Equal("developer", saved.Chrome.ColumnPreset);
        Assert.Equal(321, saved.Chrome.ColumnWidths["name"]);
    }

    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsSettingsPlacesAndColumnWidths()
    {
        var ipc = new ConfigurableIpc();
        var fileOps = new FileOperationService(ipc);
        var columns = new ColumnLayout();
        columns.ApplyPreset("developer");
        columns.Resize("name", 321);

        var settings = new UiSettings
        {
            Theme = "light",
            DefaultView = "tiles",
            DefaultIconSize = 95,
            ConfirmDelete = false,
            KeepFoldersOnTop = false,
            StartLocation = "custom",
            CustomPath = @"D:\Work",
            OpenInNewTab = true,
            EnableGitIntegration = false,
            ShowFolderSizes = true,
            PreviewVisible = false,
            PreviewWidth = 2000,
            DualPanePrimaryPercent = 5,
            DualPanePrimaryWidth = 40,
            ColumnPreset = "developer",
            ShowQuickAccess = false,
            ShowFolderTree = true,
            ShowBookmarks = false,
            ShowRecentLocations = false,
            ShowSmartFolders = false,
            SidebarVisible = false,
            SidebarWidth = 900,
            QuickAccessCollapsed = true,
            MyPcCollapsed = true,
            LastPath = @"C:\Last",
        };
        var bookmarks = new List<BookmarkItem>
        {
            new() { Name = "Work", Path = @"D:\Work" },
        };
        var recentPaths = new List<string>
        {
            @"D:\Work",
            @"C:\Last",
        };

        await WorkspaceSettingsStore.SaveAsync(
            fileOps,
            settings,
            columns,
            showHidden: true,
            bookmarks,
            recentPaths,
            CancellationToken.None);
        var state = await WorkspaceSettingsStore.LoadAsync(fileOps, CancellationToken.None);

        Assert.True(state.Settings.ShowHidden);
        Assert.Equal("light", state.Settings.Theme);
        Assert.Equal("tiles", state.Settings.DefaultView);
        Assert.Equal(96, state.Settings.DefaultIconSize);
        Assert.False(state.Settings.ConfirmDelete);
        Assert.False(state.Settings.KeepFoldersOnTop);
        Assert.Equal("custom", state.Settings.StartLocation);
        Assert.Equal(@"D:\Work", state.Settings.CustomPath);
        Assert.True(state.Settings.OpenInNewTab);
        Assert.False(state.Settings.EnableGitIntegration);
        Assert.True(state.Settings.ShowFolderSizes);
        Assert.False(state.Settings.PreviewVisible);
        Assert.Equal(UiSettings.PreviewMaxWidth, state.Settings.PreviewWidth);
        Assert.Equal(UiSettings.DualPaneMinPercent, state.Settings.DualPanePrimaryPercent);
        Assert.Equal(UiSettings.FilePaneMinWidth, state.Settings.DualPanePrimaryWidth);
        Assert.Equal("developer", state.Settings.ColumnPreset);
        Assert.Equal(321, state.Settings.ColumnWidths["name"]);
        Assert.False(state.Settings.ShowQuickAccess);
        Assert.True(state.Settings.ShowFolderTree);
        Assert.False(state.Settings.ShowBookmarks);
        Assert.False(state.Settings.ShowRecentLocations);
        Assert.False(state.Settings.ShowSmartFolders);
        Assert.False(state.Settings.SidebarVisible);
        Assert.Equal(UiSettings.SidebarMaxWidth, state.Settings.SidebarWidth);
        Assert.True(state.Settings.QuickAccessCollapsed);
        Assert.True(state.Settings.MyPcCollapsed);
        Assert.Equal(@"C:\Last", state.Settings.LastPath);
        Assert.Equal(bookmarks.Single().Path, state.Bookmarks.Single().Path);
        Assert.Equal(recentPaths, state.RecentPaths);
    }

    [Fact]
    public async Task LoadAsync_SanitizesPlacesAndIgnoresMalformedColumnWidths()
    {
        var ipc = new ConfigurableIpc();
        var fileOps = new FileOperationService(ipc);
        ipc.Settings["columnWidths"] = "{not json";
        ipc.Settings["places.bookmarks"] = JsonSerializer.Serialize(new[]
        {
            new BookmarkItem { Name = "", Path = "  C:\\Work  " },
            new BookmarkItem { Name = "Duplicate", Path = "c:\\work" },
            new BookmarkItem { Name = "Blank", Path = "   " },
            new BookmarkItem { Name = "Temp", Path = "D:\\Temp" },
        });
        ipc.Settings["places.recents"] = JsonSerializer.Serialize(new[]
        {
            "  C:\\Work  ",
            "c:\\work",
            "",
            "D:\\Temp",
            "E:\\More",
        });

        var state = await WorkspaceSettingsStore.LoadAsync(fileOps, CancellationToken.None);

        Assert.Empty(state.Settings.ColumnWidths);
        Assert.Equal(
            [
                @"C:\Work",
                @"D:\Temp",
            ],
            state.Bookmarks.Select(bookmark => bookmark.Path));
        Assert.Equal("Work", state.Bookmarks[0].Name);
        Assert.Equal(
            [
                @"C:\Work",
                @"D:\Temp",
                @"E:\More",
            ],
            state.RecentPaths);
    }
}
