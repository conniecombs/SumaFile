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

[Collection("Plugin catalogs")]
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
        Assert.DoesNotContain(AppCommandCatalog.All, command => command.Id == "icon-size-maximum");
        Assert.DoesNotContain(AppCommandCatalog.All, command => command.Id == "icon-size-jumbo");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "save-smart-folder");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "clear-recent-history");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "transfers");
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
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "clear-tag-filter");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "go-back");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "go-recycle-bin");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "restore-selected");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "empty-recycle-bin");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "profile-manage");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "profile-transfer");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "new-shortcut");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "customize-toolbar");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "toggle-toolbar-labels");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "git-panel");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "git-stage-selected");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "git-discard-selected");
        Assert.Contains(AppCommandCatalog.All, command => command.Id == "ftp-sftp-manager");
        Assert.Equal("Go home", AppCommandCatalog.Find("go-home")?.Label);
        Assert.Equal("Alt+Enter", AppCommandCatalog.Find("properties")?.Shortcut);
        Assert.Equal("Large files and duplicates", AppCommandCatalog.Find("disk-cleanup")?.Label);
        Assert.Equal("Save search as smart folder…", AppCommandCatalog.Find("save-smart-folder")?.Label);
        Assert.Equal("Organization", AppCommandCatalog.Find("save-smart-folder")?.Group);
        Assert.Equal("Search this folder", AppCommandCatalog.Find("search")?.Label);
        Assert.Equal("Filter this folder", AppCommandCatalog.Find("filter")?.Label);
        Assert.Equal("Set label", AppCommandCatalog.Find("color-label")?.Label);
        Assert.Equal("Show or hide sidebar", AppCommandCatalog.Find("toggle-side-menu")?.Label);
        Assert.Equal("FTP/SFTP manager", AppCommandCatalog.Find("ftp-sftp-manager")?.Label);
        Assert.Equal("Show transfers", AppCommandCatalog.Find("transfers")?.Label);
        Assert.Equal("Open or close second pane", AppCommandCatalog.Find("dual-pane")?.Label);
        Assert.Equal("Manage workspace profiles", AppCommandCatalog.Find("profile-manage")?.Label);
        Assert.Equal("Customize toolbar", AppCommandCatalog.Find("customize-toolbar")?.Label);
        Assert.Equal("Show or hide toolbar labels", AppCommandCatalog.Find("toggle-toolbar-labels")?.Label);
        Assert.Equal("Ctrl+Alt+C", AppCommandCatalog.Find("copy-to-pane")?.Shortcut);
        Assert.Equal("Delete", AppCommandCatalog.Find("delete")?.Label);
        Assert.Equal("Delete permanently", AppCommandCatalog.Find("delete-permanent")?.Label);
        Assert.Equal("Clear label filter", AppCommandCatalog.Find("clear-tag-filter")?.Label);
        Assert.Equal(AppCommandCatalog.All.Count, AppCommandCatalog.Filter("").Count);
        var git = AppCommandCatalog.Filter("git");
        Assert.Equal(10, git.Count);
        Assert.All(git, command => Assert.StartsWith("git-", command.Id, StringComparison.Ordinal));
        Assert.Equal("ftp-sftp-manager", Assert.Single(AppCommandCatalog.Filter("ftp")).Id);
        Assert.Equal("ftp-sftp-manager", Assert.Single(AppCommandCatalog.Filter("sftp")).Id);
        Assert.Equal(5, AppCommandCatalog.Filter("icon size").Count);
        Assert.Equal(7, AppCommandCatalog.Filter("profile").Count);
        Assert.Equal("toggle-side-menu", Assert.Single(AppCommandCatalog.Filter("sidebar")).Id);
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
    [InlineData("overflow-copy", "copy")]
    [InlineData("overflow-terminal", "terminal")]
    [InlineData("ctx-customize-toolbar", "customize-toolbar")]
    [InlineData("ctx-toggle-toolbar-labels", "toggle-toolbar-labels")]
    [InlineData("ctx-restore", "restore-selected")]
    [InlineData("ctx-empty-recycle-bin", "empty-recycle-bin")]
    [InlineData("ctx-git-stage", "git-stage-selected")]
    [InlineData("ctx-git-discard", "git-discard-selected")]
    [InlineData("ctx-git-panel", "git-panel")]
    [InlineData("view:details", "view:details")]
    public void CommandAliases_NormalizeSharedRouterIds(string id, string expected)
    {
        Assert.Equal(expected, CommandAliasCatalog.Normalize(id));
    }

    [Fact]
    public void SetPluginCommands_AppendsAndRaisesChanged()
    {
        var raised = 0;
        void Handler(object? sender, EventArgs e) => raised++;
        AppCommandCatalog.Changed += Handler;
        try
        {
            AppCommandCatalog.SetPluginCommands([new AppCommand("plugin:com.a:greet", "Greet", "Hello")]);

            Assert.Equal(1, raised);
            Assert.Equal("plugin:com.a:greet", AppCommandCatalog.All[^1].Id);
            Assert.Equal(AppCommandCatalog.BuiltIn.Count + 1, AppCommandCatalog.All.Count);
            Assert.NotNull(AppCommandCatalog.Find("plugin:com.a:greet"));
            Assert.Contains(AppCommandCatalog.Filter("greet"), command => command.Id == "plugin:com.a:greet");
        }
        finally
        {
            AppCommandCatalog.Changed -= Handler;
            AppCommandCatalog.SetPluginCommands([]);
        }
    }

    [Fact]
    public void SetPluginCommands_Empty_RestoresBuiltIn()
    {
        try
        {
            AppCommandCatalog.SetPluginCommands([new AppCommand("plugin:com.a:greet", "Greet", "Hello")]);
            AppCommandCatalog.SetPluginCommands([]);

            Assert.Equal(AppCommandCatalog.BuiltIn, AppCommandCatalog.All);
            Assert.Null(AppCommandCatalog.Find("plugin:com.a:greet"));
        }
        finally
        {
            AppCommandCatalog.SetPluginCommands([]);
        }
    }
}
