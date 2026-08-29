using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class DualPaneAndTabsTests
{
    [Fact]
    public async Task NavigateSpecial_OpensRecycleBinVirtualPath()
    {
        var workspace = await Started();
        await workspace.NavigateSpecialAsync("navigateRecycleBin");
        Assert.Equal(PathRules.RecycleBinPath, workspace.Primary.Path);
        Assert.False(workspace.Primary.CanGoUp);
    }

    [Fact]
    public async Task ToggleDualPane_CopiesPrimaryPathAndKeepsPrimaryActive()
    {
        var workspace = await Started();
        await workspace.NavigateToAsync(@"C:\Users\test\Desktop");
        await workspace.ToggleDualPaneAsync();

        Assert.True(workspace.DualPaneEnabled);
        Assert.Equal(PaneId.Primary, workspace.ActivePane);
        Assert.Equal(PaneId.Primary, workspace.SidebarTarget);
        Assert.Equal(@"C:\Users\test\Desktop", workspace.Primary.Path);
        Assert.Equal(@"C:\Users\test\Desktop", workspace.Secondary.Path);
        Assert.Equal("Left pane", workspace.ActivePaneLabel);
        Assert.Single(workspace.Secondary.Tabs);
    }

    [Fact]
    public async Task SidebarTarget_FollowsActivePaneOnlyWhenDual()
    {
        var workspace = await Started();
        Assert.Equal(PaneId.Primary, workspace.SidebarTarget);
        workspace.ActivatePane(PaneId.Secondary);
        Assert.Equal(PaneId.Primary, workspace.SidebarTarget);

        await workspace.ToggleDualPaneAsync();
        workspace.ActivatePane(PaneId.Secondary);
        Assert.Equal(PaneId.Secondary, workspace.SidebarTarget);
        Assert.Equal("Right pane", workspace.ActivePaneLabel);
    }

    [Fact]
    public async Task Normalize_RoutesSecondaryToPrimaryWhenSinglePane()
    {
        var workspace = await Started();
        Assert.Equal(PaneId.Primary, workspace.Normalize(PaneId.Secondary));
        await workspace.ToggleDualPaneAsync();
        Assert.Equal(PaneId.Secondary, workspace.Normalize(PaneId.Secondary));
    }

    [Fact]
    public async Task SecondaryNavigation_DoesNotChangePrimaryHistory()
    {
        var workspace = await Started();
        await workspace.NavigateToAsync(@"C:\Users\test\Desktop");
        var primaryHistory = workspace.Primary.History.ToArray();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\");

        Assert.Equal(@"C:\Users\test\Desktop", workspace.Primary.Path);
        Assert.Equal(primaryHistory, workspace.Primary.History);
        Assert.Equal(@"C:\", workspace.Secondary.Path);
        Assert.True(workspace.Primary.CanGoBack);
        Assert.True(workspace.Secondary.CanGoBack);
    }

    [Fact]
    public async Task SecondaryBack_IsIndependent()
    {
        var workspace = await Started();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\Users\test\Desktop");
        await workspace.GoBackAsync(PaneId.Secondary);
        Assert.Equal(@"C:\Users\test", workspace.Secondary.Path);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Path);
    }

    [Fact]
    public async Task NewTab_IsPaneLocal()
    {
        var workspace = await Started();
        await workspace.ToggleDualPaneAsync();
        await workspace.OpenNewTabAsync(PaneId.Primary, @"C:\Users\test\Desktop");
        await workspace.OpenNewTabAsync(PaneId.Secondary, @"C:\");

        Assert.Equal(2, workspace.Primary.Tabs.Count);
        Assert.Equal(2, workspace.Secondary.Tabs.Count);
        Assert.Equal(@"C:\Users\test\Desktop", workspace.Primary.Path);
        Assert.Equal(@"C:\", workspace.Secondary.Path);
        Assert.NotEqual(workspace.Primary.ActiveTabId, workspace.Secondary.ActiveTabId);
    }

    [Fact]
    public async Task SwitchTab_RestoresThatTabsHistory()
    {
        var workspace = await Started();
        var firstTab = workspace.Primary.ActiveTabId;
        await workspace.OpenNewTabAsync(PaneId.Primary, @"C:\Users\test\Desktop");
        Assert.Equal(@"C:\Users\test\Desktop", workspace.Primary.Path);
        Assert.True(workspace.Primary.CanGoBack is false);

        await workspace.SwitchToTabAsync(firstTab!, PaneId.Primary);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Path);
        Assert.Equal(firstTab, workspace.Primary.ActiveTabId);
        Assert.Contains(@"C:\Users\test", workspace.Primary.History);
    }

    [Fact]
    public async Task CloseActiveTab_SelectsNeighbor()
    {
        var workspace = await Started();
        var first = workspace.Primary.ActiveTabId;
        await workspace.OpenNewTabAsync(PaneId.Primary, @"C:\Users\test\Desktop");
        var second = workspace.Primary.ActiveTabId;
        await workspace.CloseTabAsync(second!, PaneId.Primary);

        Assert.Equal(first, workspace.Primary.ActiveTabId);
        Assert.Single(workspace.Primary.Tabs);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Path);
    }

    [Fact]
    public async Task CloseLastTabInSinglePane_OpensHome()
    {
        var workspace = await Started();
        await workspace.NavigateToAsync(@"C:\Users\test\Desktop");
        var only = workspace.Primary.ActiveTabId;
        await workspace.CloseTabAsync(only!, PaneId.Primary);

        Assert.Single(workspace.Primary.Tabs);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Path);
        Assert.NotEqual(only, workspace.Primary.ActiveTabId);
    }

    [Fact]
    public async Task CloseLastTabInRightPane_ClosesPaneAndReopensSecondPane()
    {
        var workspace = await Started();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\");
        var only = workspace.Secondary.ActiveTabId;

        await workspace.CloseTabAsync(only!, PaneId.Secondary);

        Assert.False(workspace.DualPaneEnabled);
        Assert.Equal(PaneId.Primary, workspace.ActivePane);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Path);
        Assert.Equal(@"C:\", workspace.Secondary.Path);
        Assert.Empty(workspace.Secondary.Tabs);

        var raises = 0;
        workspace.Changed += (_, _) => raises++;
        await workspace.ToggleDualPaneAsync();

        Assert.True(workspace.DualPaneEnabled);
        Assert.True(raises > 0);
        Assert.Equal(PaneId.Primary, workspace.ActivePane);
        Assert.Equal(@"C:\", workspace.Secondary.Path);
        Assert.Single(workspace.Secondary.Tabs);
        Assert.NotNull(workspace.Secondary.ActiveTabId);
    }

    [Fact]
    public async Task CloseLastTabInLeftPane_PromotesRightPaneAndReopensSecondPane()
    {
        var workspace = await Started();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\");
        var rightTab = workspace.Secondary.ActiveTabId;
        var onlyLeft = workspace.Primary.ActiveTabId;

        await workspace.CloseTabAsync(onlyLeft!, PaneId.Primary);

        Assert.False(workspace.DualPaneEnabled);
        Assert.Equal(PaneId.Primary, workspace.ActivePane);
        Assert.Equal(@"C:\", workspace.Primary.Path);
        Assert.Equal(rightTab, workspace.Primary.ActiveTabId);
        Assert.Single(workspace.Primary.Tabs);
        Assert.Equal(@"C:\Users\test", workspace.Secondary.Path);
        Assert.Empty(workspace.Secondary.Tabs);

        await workspace.ToggleDualPaneAsync();

        Assert.True(workspace.DualPaneEnabled);
        Assert.Equal(@"C:\Users\test", workspace.Secondary.Path);
        Assert.Single(workspace.Secondary.Tabs);
        Assert.NotNull(workspace.Secondary.ActiveTabId);
    }

    [Fact]
    public async Task Initialize_CreatesPrimaryTab()
    {
        var workspace = await Started();
        Assert.Single(workspace.Primary.Tabs);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Tabs[0].Path);
        Assert.Equal("test", workspace.Primary.Tabs[0].Title);
        Assert.Empty(workspace.Secondary.Tabs);
    }

    [Fact]
    public async Task ToggleOff_KeepsSecondaryPathForNextToggle()
    {
        var workspace = await Started();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\");
        await workspace.ToggleDualPaneAsync();
        Assert.False(workspace.DualPaneEnabled);
        Assert.Equal(PaneId.Primary, workspace.ActivePane);
        await workspace.ToggleDualPaneAsync();
        Assert.Equal(@"C:\", workspace.Secondary.Path);
    }

    [Fact]
    public async Task CloseRightPane_KeepsPrimaryAndSecondaryPath()
    {
        var workspace = await Started();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\");
        await workspace.CloseFilePaneAsync(PaneId.Secondary);

        Assert.False(workspace.DualPaneEnabled);
        Assert.Equal(PaneId.Primary, workspace.ActivePane);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Path);
        Assert.Equal(@"C:\", workspace.Secondary.Path);
    }

    [Fact]
    public async Task CloseLeftPane_PromotesRightPane()
    {
        var workspace = await Started();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\");
        workspace.SetFilterQuery(PaneId.Primary, "notes");
        workspace.SetFilterQuery(PaneId.Secondary, "desktop");
        workspace.SetFileListView(PaneId.Primary, "content");
        workspace.SetFileListIconSize(PaneId.Primary, 48);
        workspace.SetSort(PaneId.Primary, "size");
        workspace.SetFileListView(PaneId.Secondary, "tiles");
        workspace.SetFileListIconSize(PaneId.Secondary, 96);
        workspace.SetSort(PaneId.Secondary, "date");
        await workspace.CloseFilePaneAsync(PaneId.Primary);

        Assert.False(workspace.DualPaneEnabled);
        Assert.Equal(PaneId.Primary, workspace.ActivePane);
        Assert.Equal(@"C:\", workspace.Primary.Path);
        Assert.Equal(@"C:\Users\test", workspace.Secondary.Path);
        Assert.Equal("desktop", workspace.FilterQuery);
        Assert.Equal("tiles", workspace.ViewFor(PaneId.Primary));
        Assert.Equal(96, workspace.IconSizeFor(PaneId.Primary));
        Assert.Equal("date", workspace.SortByFor(PaneId.Primary));
        Assert.Contains(workspace.Primary.Tabs, tab => tab.Path == @"C:\");

        await workspace.ToggleDualPaneAsync();
        Assert.Equal("notes", workspace.FilterQueryFor(PaneId.Secondary));
        Assert.Equal("content", workspace.ViewFor(PaneId.Secondary));
        Assert.Equal(48, workspace.IconSizeFor(PaneId.Secondary));
        Assert.Equal("size", workspace.SortByFor(PaneId.Secondary));
    }

    [Fact]
    public async Task CloseLeftPane_NoopsWhenSinglePane()
    {
        var workspace = await Started();
        await workspace.NavigateToAsync(@"C:\Users\test\Desktop");
        await workspace.CloseFilePaneAsync(PaneId.Primary);

        Assert.False(workspace.DualPaneEnabled);
        Assert.Equal(@"C:\Users\test\Desktop", workspace.Primary.Path);
    }

    [Fact]
    public async Task FocusSecondary_EnablesDualPane()
    {
        var workspace = await Started();
        await workspace.FocusSecondaryAsync();
        Assert.True(workspace.DualPaneEnabled);
        Assert.Equal(PaneId.Secondary, workspace.ActivePane);
        Assert.Equal(PaneId.Secondary, workspace.SidebarTarget);
    }

    [Fact]
    public async Task ActivatePane_DoesNotRaiseWhenAlreadyActive()
    {
        var workspace = await Started();
        var raises = 0;
        workspace.Changed += (_, _) => raises++;

        workspace.ActivatePane(PaneId.Primary);
        workspace.ActivatePane(PaneId.Secondary);
        Assert.Equal(0, raises);

        await workspace.ToggleDualPaneAsync();
        raises = 0;
        workspace.ActivatePane(PaneId.Primary);
        Assert.Equal(0, raises);
        workspace.ActivatePane(PaneId.Secondary);
        Assert.Equal(1, raises);
        workspace.ActivatePane(PaneId.Secondary);
        Assert.Equal(1, raises);
    }

    [Fact]
    public async Task SelectPath_DoesNotRaiseForSelectionOnly()
    {
        var workspace = await Started();
        var file = workspace.VisibleEntries.First(entry => !entry.IsDir);
        var raises = 0;
        workspace.Changed += (_, _) => raises++;

        workspace.SelectPath(file.Path);
        workspace.SelectPath(null);
        workspace.SelectPath(file.Path, PaneId.Primary);
        Assert.Equal(0, raises);
        Assert.Equal(file.Path, workspace.SelectedPath);
    }

    [Fact]
    public async Task Refresh_KeepsSelectionAndDoesNotClearListing()
    {
        var workspace = await Started();
        var file = workspace.VisibleEntries.First(entry => !entry.IsDir);
        workspace.SelectPath(file.Path);
        var raises = 0;
        workspace.Changed += (_, _) =>
        {
            raises++;
            Assert.NotEmpty(workspace.VisibleEntries);
            Assert.Equal(file.Path, workspace.SelectedPath);
        };

        await workspace.RefreshAsync();

        Assert.True(raises >= 1);
        Assert.Equal(file.Path, workspace.SelectedPath);
        Assert.Contains(workspace.VisibleEntries, entry => entry.Path == file.Path);
    }

    [Fact]
    public async Task QuickFilter_IsPaneLocalAndClearsHiddenSelection()
    {
        var workspace = await Started();
        await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Secondary, @"C:\Users\test\Desktop");

        workspace.SelectPath(@"C:\Users\test\notes.txt", PaneId.Primary);
        workspace.SetFilterQuery(PaneId.Primary, "desktop");
        workspace.SetFilterQuery(PaneId.Secondary, "shot");

        Assert.Null(workspace.Primary.SelectedPath);
        Assert.Equal(["Desktop"], workspace.VisibleEntriesFor(PaneId.Primary).Select(entry => entry.Name));
        Assert.Equal(["shot.png"], workspace.VisibleEntriesFor(PaneId.Secondary).Select(entry => entry.Name));

        workspace.ActivatePane(PaneId.Secondary);
        Assert.Equal("shot", workspace.FilterQuery);
        workspace.ActivatePane(PaneId.Primary);
        Assert.Equal("desktop", workspace.FilterQuery);
    }

    [Fact]
    public async Task SwitchToTabAt_UsesOneBasedIndexAndNineIsLast()
    {
        var workspace = await Started();
        var first = workspace.Primary.ActiveTabId;
        await workspace.OpenNewTabAsync(PaneId.Primary, @"C:\Users\test\Desktop");
        await workspace.OpenNewTabAsync(PaneId.Primary, @"C:\");
        Assert.Equal(3, workspace.Primary.Tabs.Count);

        await workspace.SwitchToTabAtAsync(1);
        Assert.Equal(first, workspace.Primary.ActiveTabId);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Path);

        await workspace.SwitchToTabAtAsync(9);
        Assert.Equal(@"C:\", workspace.Primary.Path);
    }

    [Fact]
    public async Task OpenInOtherPane_EnablesDualPaneWithoutStealingFocus()
    {
        var workspace = await Started();
        await workspace.NavigateToAsync(@"C:\Users\test");
        Assert.False(workspace.DualPaneEnabled);

        await workspace.OpenInOtherPaneAsync(@"C:\Users\test\Desktop", isDirectory: true);

        Assert.True(workspace.DualPaneEnabled);
        Assert.Equal(PaneId.Primary, workspace.ActivePane);
        Assert.Equal(@"C:\Users\test", workspace.Primary.Path);
        Assert.Equal(@"C:\Users\test\Desktop", workspace.Secondary.Path);
    }

    [Fact]
    public async Task NudgeIconSize_StepsAndClamps()
    {
        var workspace = await Started();
        Assert.Equal(16, workspace.Primary.IconSize);
        Assert.Equal(32, workspace.NudgeFileListIconSize(PaneId.Primary, 2));
        Assert.Equal(256, workspace.NudgeFileListIconSize(PaneId.Primary, 100));
        Assert.Equal(16, workspace.NudgeFileListIconSize(PaneId.Primary, -100));
    }

    [Fact]
    public async Task ToggleShowHidden_FlipsSetting()
    {
        var workspace = await Started();
        Assert.False(workspace.ShowHiddenFiles);
        Assert.True(workspace.ToggleShowHidden());
        Assert.True(workspace.Settings.ShowHidden);
        Assert.False(workspace.ToggleShowHidden());
    }

    [Fact]
    public async Task SwitchTabBy_Wraps()
    {
        var workspace = await Started();
        var first = workspace.Primary.ActiveTabId;
        await workspace.OpenNewTabAsync(PaneId.Primary, @"C:\Users\test\Desktop");
        await workspace.SwitchTabByAsync(1);
        Assert.Equal(first, workspace.Primary.ActiveTabId);
        await workspace.SwitchTabByAsync(-1);
        Assert.Equal(@"C:\Users\test\Desktop", workspace.Primary.Path);
    }

    private static async Task<ExplorerWorkspace> Started()
    {
        var workspace = new ExplorerWorkspace(FakeExplorerBackend.Typical());
        await workspace.InitializeAsync();
        return workspace;
    }
}
