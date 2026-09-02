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
                    "keepFoldersOnTop": false,
                    "enableGitIntegration": false,
                    "progressQueueVisible": true,
                    "previewVisible": false,
                    "previewWidth": 2000,
                    "sidebarVisible": false,
                    "sidebarWidth": 20,
                    "dualPanePrimaryPercent": 5,
                    "dualPanePrimaryWidth": 40,
                    "columnPreset": "developer",
                    "visibleColumnIds": [ "name", "git", "name", "missing", " " ],
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
        Assert.False(saved.Chrome!.KeepFoldersOnTop);
        Assert.False(saved.Chrome.EnableGitIntegration);
        Assert.True(saved.Chrome.ProgressQueueVisible);
        Assert.False(saved.Chrome.PreviewVisible);
        Assert.Equal(UiSettings.PreviewMaxWidth, saved.Chrome.PreviewWidth);
        Assert.False(saved.Chrome.SidebarVisible);
        Assert.Equal(UiSettings.SidebarMinWidth, saved.Chrome.SidebarWidth);
        Assert.Equal(UiSettings.DualPaneMinPercent, saved.Chrome.DualPanePrimaryPercent);
        Assert.Equal(UiSettings.FilePaneMinWidth, saved.Chrome.DualPanePrimaryWidth);
        Assert.Equal("developer", saved.Chrome.ColumnPreset);
        Assert.Equal(["name", "git"], saved.Chrome.VisibleColumnIds);
        Assert.Equal(321, saved.Chrome.ColumnWidths["name"]);
    }

    [Fact]
    public void WorkspaceProfilesDocument_SanitizesStoredProfilesAndTracksActiveBuiltIn()
    {
        var raw = """
            {
              "version": 99,
              "activeProfileId": "builtin-transfer",
              "profiles": [
                {
                  "id": "",
                  "name": "  Photo    triage  ",
                  "builtInId": "builtin-photos",
                  "sourceProfileId": " builtin-photos ",
                  "layout": {
                    "dualPaneEnabled": false,
                    "primary": { "path": "D:\\Photos", "view": "tiles", "iconSize": 192 }
                  },
                  "chrome": {
                    "columnPreset": "photo",
                    "visibleColumnIds": [ "name", "date", "missing" ]
                  }
                },
                {
                  "id": "duplicate",
                  "name": "photo triage",
                  "layout": {}
                }
              ]
            }
            """;

        var document = WorkspaceProfilesDocument.FromJson(raw);
        var saved = Assert.Single(document.Profiles);

        Assert.Equal(WorkspaceProfilesDocument.CurrentVersion, document.Version);
        Assert.Equal(WorkspaceProfileTemplates.TransferId, document.ActiveProfileId);
        Assert.NotEmpty(saved.Id);
        Assert.Equal("Photo triage", saved.Name);
        Assert.False(saved.IsBuiltIn);
        Assert.Equal(WorkspaceProfileTemplates.PhotosId, saved.SourceProfileId);
        Assert.Equal(@"D:\Photos", saved.Layout.Primary.Path);
        Assert.Equal("photo", saved.Chrome!.ColumnPreset);
        Assert.Equal(["name", "date"], saved.Chrome.VisibleColumnIds);
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
            ProgressQueueVisible = true,
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
            ShortcutOverrides = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["search.focus"] = ["Ctrl+K", "F3"],
                ["tabs.close"] = [],
            },
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
        settings.FolderViewSettings.Upsert(
            FolderViewScope.Descendants,
            @"D:\Work",
            [],
            new FolderViewOptions
            {
                View = "content",
                IconSize = 48,
                SortBy = "date",
                SortAscending = false,
                PreviewVisible = false,
                ShowHidden = true,
                WorkspaceProfileId = WorkspaceProfileTemplates.DeveloperId,
            });

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
        Assert.True(state.Settings.ProgressQueueVisible);
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
        Assert.Equal(["Ctrl+K", "F3"], state.Settings.ShortcutOverrides["search.focus"]);
        Assert.Equal([], state.Settings.ShortcutOverrides["tabs.close"]);
        var folderRule = Assert.Single(state.Settings.FolderViewSettings.Rules);
        Assert.Equal(FolderViewRuleScope.Descendants, folderRule.Scope);
        Assert.Equal("content", folderRule.Options.View);
        Assert.False(folderRule.Options.SortAscending);
        Assert.True(folderRule.Options.ShowHidden);
        Assert.Equal(WorkspaceProfileTemplates.DeveloperId, folderRule.Options.WorkspaceProfileId);
        Assert.Equal("true", ipc.Settings["progressQueue.visible"]);
        Assert.Contains("\"search.focus\"", ipc.Settings[KeyboardShortcutMap.SettingsKey], StringComparison.Ordinal);
        Assert.Contains("\"scope\": \"descendants\"", ipc.Settings[FolderViewSettingsDocument.SettingsKey], StringComparison.Ordinal);
        Assert.Equal(bookmarks.Single().Path, state.Bookmarks.Single().Path);
        Assert.Equal(recentPaths, state.RecentPaths);
    }

    [Fact]
    public async Task LoadAsync_SanitizesPlacesAndIgnoresMalformedColumnWidths()
    {
        var ipc = new ConfigurableIpc();
        var fileOps = new FileOperationService(ipc);
        ipc.Settings["columnWidths"] = "{not json";
        ipc.Settings[KeyboardShortcutMap.SettingsKey] = """
            {
              "search.focus": "Ctrl+K",
              "tabs.close": [],
              "directory.refresh": [ "F5" ],
              "tabs.jump": [ "Ctrl+9" ]
            }
            """;
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
        Assert.Equal(["Ctrl+K"], state.Settings.ShortcutOverrides["search.focus"]);
        Assert.Equal([], state.Settings.ShortcutOverrides["tabs.close"]);
        Assert.False(state.Settings.ShortcutOverrides.ContainsKey("directory.refresh"));
        Assert.False(state.Settings.ShortcutOverrides.ContainsKey("tabs.jump"));
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
