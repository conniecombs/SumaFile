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

public class AppCommandCatalogTests
{
    [Fact]
    public void CommandPalette_ContainsWinUiIdsAndFilters()
    {
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "go-home");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "git-pull");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "keyboard-help");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "view-tiles");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "icon-size-extra-large");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "icon-size-maximum");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "clear-recent-history");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "toggle-side-menu");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "toggle-hidden");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "focus-path");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "switch-pane");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "copy-to-pane");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "move-to-pane");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "reopen-closed-tab");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "command-palette");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "copy-path");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "bookmark-folder");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "go-back");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "go-recycle-bin");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "restore-selected");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "empty-recycle-bin");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "profile-manage");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "profile-transfer");
        Assert.Equal("Go home", AppCommandCatalog.Find("go-home")?.Label);
        Assert.Equal("Alt+Enter", AppCommandCatalog.Find("properties")?.Shortcut);
        Assert.Equal("Disk cleanup", AppCommandCatalog.Find("disk-cleanup")?.Label);
        Assert.Equal("Open or close second pane", AppCommandCatalog.Find("dual-pane")?.Label);
        Assert.Equal("Manage workspace profiles", AppCommandCatalog.Find("profile-manage")?.Label);
        Assert.Equal("Ctrl+Alt+C", AppCommandCatalog.Find("copy-to-pane")?.Shortcut);
        Assert.Equal("Move to Recycle Bin", AppCommandCatalog.Find("delete")?.Label);
        Assert.Equal("Delete Permanently", AppCommandCatalog.Find("delete-permanent")?.Label);
        Assert.Equal(AppCommandCatalog.All.Count, AppCommandCatalog.Filter("").Count);
        var git = AppCommandCatalog.Filter("git");
        Assert.Equal(2, git.Count);
        Assert.All(git, command => Assert.StartsWith("git-", command.Id, StringComparison.Ordinal));
        Assert.Equal(7, AppCommandCatalog.Filter("icon size").Count);
        Assert.Equal(7, AppCommandCatalog.Filter("profile").Count);
        Assert.Equal("toggle-side-menu", Assert.Single(AppCommandCatalog.Filter("side menu")).Id);
        Assert.Equal("settings", AppCommandCatalog.Find("settings")?.Id);
        Assert.Null(AppCommandCatalog.Find("missing"));
    }

    [Theory]
    [InlineData("ctx-rename", "rename")]
    [InlineData("ctx-copy-path", "copy-path")]
    [InlineData("ctx-bookmark", "bookmark-selected-folder")]
    [InlineData("ctx-open-tab", "open-selected-tab")]
    [InlineData("ctx-open-other-pane", "open-other-pane")]
    [InlineData("overflow-filter", "filter")]
    [InlineData("overflow-profiles", "profile-manage")]
    [InlineData("ctx-restore", "restore-selected")]
    [InlineData("ctx-empty-recycle-bin", "empty-recycle-bin")]
    [InlineData("view:details", "view:details")]
    public void CommandAliases_NormalizeSharedRouterIds(string id, string expected)
    {
        Assert.Equal(expected, CommandAliasCatalog.Normalize(id));
    }
}
