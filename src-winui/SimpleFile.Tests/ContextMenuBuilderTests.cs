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

public class ContextMenuBuilderTests
{
    [Fact]
    public void ContextMenu_HidesDisabledItemsAndKeepsWinUiIds()
    {
        var empty = ContextMenuBuilder.Build(new ContextMenuRequest());
        Assert.Contains(empty, entry => entry.Id == "ctx-terminal");
        Assert.DoesNotContain(empty, entry => entry.Id == "ctx-open");
        Assert.DoesNotContain(empty, entry => entry.Id == "ctx-delete-menu");
        Assert.DoesNotContain(empty, entry => entry.Kind == ContextMenuKind.Divider && empty.Last() == entry);

        var selected = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            SelectedIsDirectory = false,
            SelectedIsArchive = true,
            ArchiveExtractFolderName = "pack",
            DualPaneEnabled = true,
            OtherPaneHasPath = true,
            HasClipboard = true,
            AllSelectedAreFiles = true,
        });

        var open = Assert.Single(selected, entry => entry.Id == "ctx-open");
        Assert.Equal("Enter", open.Shortcut);
        Assert.False(string.IsNullOrWhiteSpace(open.IconGlyph));
        Assert.Contains(selected, entry => entry.Id == "ctx-open-with");
        Assert.Contains(selected, entry => entry.Id == "ctx-copy-to-pane");
        Assert.Contains(selected, entry => entry.Id == "ctx-copy-path" && entry.Shortcut == "Ctrl+Shift+C");
        Assert.DoesNotContain(selected, entry => entry.Id == "ctx-open-tab");
        Assert.DoesNotContain(selected, entry => entry.Id == "ctx-open-other-pane");
        Assert.DoesNotContain(selected, entry => entry.Id == "ctx-bookmark");
        var delete = Assert.Single(selected, entry => entry.Id == "ctx-delete-menu");
        Assert.Equal("Delete:", delete.Label);
        Assert.Contains(delete.Children, entry => entry.Id == "ctx-delete-recycle" && entry.Label == "Move to Recycle Bin" && entry.Shortcut == "Delete");
        Assert.Contains(delete.Children, entry => entry.Id == "ctx-delete-permanent" && entry.Label == "Delete Permanently" && entry.Shortcut == "Shift+Delete");
        var extract = Assert.Single(selected, entry => entry.Id == "ctx-extract-menu");
        Assert.False(string.IsNullOrWhiteSpace(extract.IconGlyph));
        Assert.Contains(extract.Children, child => child.Id == "ctx-extract-folder" && child.Label.Contains("pack/", StringComparison.Ordinal));
        Assert.Contains(selected, entry => entry.Id == "ctx-info");
    }
    [Fact]
    public void ContextMenu_UsesCatalogIconsAndKeepsSubmenuChildrenQuiet()
    {
        var selected = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            SelectedIsArchive = true,
            ArchiveExtractFolderName = "pack",
            DualPaneEnabled = true,
            OtherPaneHasPath = true,
            HasClipboard = true,
        });

        Assert.Equal(ContextMenuIconCatalog.OpenFile, Assert.Single(selected, entry => entry.Id == "ctx-open").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.OpenWith, Assert.Single(selected, entry => entry.Id == "ctx-open-with").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Preview, Assert.Single(selected, entry => entry.Id == "ctx-preview").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.SelectAll, Assert.Single(selected, entry => entry.Id == "ctx-duplicates").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Edit, Assert.Single(selected, entry => entry.Id == "ctx-advanced-rename").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.MoveToFolder, Assert.Single(selected, entry => entry.Id == "ctx-move-to-pane").IconGlyph);

        var rename = Assert.Single(selected, entry => entry.Id == "ctx-rename");
        var advancedRename = Assert.Single(selected, entry => entry.Id == "ctx-advanced-rename");
        var copy = Assert.Single(selected, entry => entry.Id == "ctx-copy");
        var duplicates = Assert.Single(selected, entry => entry.Id == "ctx-duplicates");
        Assert.NotEqual(rename.IconGlyph, advancedRename.IconGlyph);
        Assert.NotEqual(copy.IconGlyph, duplicates.IconGlyph);

        var extract = Assert.Single(selected, entry => entry.Id == "ctx-extract-menu");
        Assert.Equal(ContextMenuIconCatalog.Import, extract.IconGlyph);
        Assert.All(extract.Children, entry => Assert.True(string.IsNullOrWhiteSpace(entry.IconGlyph)));

        var delete = Assert.Single(selected, entry => entry.Id == "ctx-delete-menu");
        Assert.Equal(ContextMenuIconCatalog.Delete, delete.IconGlyph);
        Assert.All(delete.Children, entry => Assert.True(string.IsNullOrWhiteSpace(entry.IconGlyph)));
    }
    [Fact]
    public void ContextMenu_OpenWithBuildsApplicationSubmenu()
    {
        var apps = new[]
        {
            OpenWithApplication.FromPath(@"C:\Program Files\Microsoft VS Code\Code.exe", "Visual Studio Code", "favorite"),
            OpenWithApplication.FromPath(@"C:\Program Files\Notepad++\notepad++.exe", "Notepad++", "suggested"),
        };

        var selected = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            AllSelectedAreFiles = true,
            SelectedExtension = ".txt",
            OpenWithApplications = apps,
        });

        var openWith = Assert.Single(selected, entry => entry.Id == "ctx-open-with");
        Assert.Equal("Open with", openWith.Label);
        Assert.Equal(ContextMenuIconCatalog.OpenWith, openWith.IconGlyph);

        var code = Assert.Single(openWith.Children, entry => entry.Id == "ctx-open-with-app-0");
        Assert.Equal("Visual Studio Code", code.Label);
        Assert.Equal(@"C:\Program Files\Microsoft VS Code\Code.exe", code.CommandParameter);
        Assert.Equal(ContextMenuIconCatalog.OpenWith, code.IconGlyph);
        Assert.Contains(openWith.Children, entry => entry.Kind == ContextMenuKind.Divider);

        var choose = Assert.Single(openWith.Children, entry => entry.Id == "ctx-open-with-choose");
        Assert.Equal("Choose another app...", choose.Label);
        Assert.Equal(ContextMenuIconCatalog.OpenWith, choose.IconGlyph);
    }
    [Fact]
    public void ContextMenu_CompareRequiresTwoFiles()
    {
        var oneFile = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            AllSelectedAreFiles = true,
        });
        Assert.DoesNotContain(oneFile, entry => entry.Id == "ctx-compare");

        var twoFiles = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 2,
            AllSelectedAreFiles = true,
        });
        Assert.Contains(twoFiles, entry => entry.Id == "ctx-compare");
    }
    [Fact]
    public void ContextMenu_FolderActionsForSingleDirectory()
    {
        var folder = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            SelectedIsDirectory = true,
            SelectedDirectoryPath = @"C:\Users\test\Desktop",
            FolderSelectionCount = 1,
            HasClipboard = true,
        });

        var openTab = Assert.Single(folder, entry => entry.Id == "ctx-open-tab");
        Assert.Equal("Ctrl+Enter", openTab.Shortcut);
        Assert.Equal(ContextMenuIconCatalog.NewTab, openTab.IconGlyph);
        Assert.Contains(folder, entry => entry.Id == "ctx-open-other-pane");
        Assert.Contains(folder, entry => entry.Id == "ctx-bookmark" && entry.Shortcut == "Ctrl+B");
        Assert.Contains(folder, entry => entry.Id == "ctx-copy-path");
        var paste = Assert.Single(folder, entry => entry.Id == "ctx-paste");
        Assert.Equal("Paste into folder", paste.Label);
        Assert.Equal(@"C:\Users\test\Desktop", paste.CommandParameter);
        Assert.DoesNotContain(folder, entry => entry.Id == "ctx-folder-metrics");
    }

    [Fact]
    public void ContextMenu_FolderMetricsRequiresMultipleFolders()
    {
        var oneFolder = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            SelectedIsDirectory = true,
            HasFolderSelection = true,
            FolderSelectionCount = 1,
        });
        Assert.DoesNotContain(oneFolder, entry => entry.Id == "ctx-folder-metrics");

        var twoFolders = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 2,
            HasFolderSelection = true,
            FolderSelectionCount = 2,
        });
        Assert.Contains(twoFolders, entry => entry.Id == "ctx-folder-metrics" && entry.Label == "Compare folder metrics");
    }

    [Fact]
    public void ContextMenu_RecycleBinShowsRestoreAndEmpty()
    {
        var menu = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            InRecycleBin = true,
            SelectionCount = 1,
        });
        Assert.Contains(menu, entry => entry.Id == "ctx-restore");
        Assert.Contains(menu, entry => entry.Id == "ctx-empty-recycle-bin");
        Assert.Contains(menu, entry => entry.Id == "ctx-delete-permanent");
        Assert.DoesNotContain(menu, entry => entry.Id == "ctx-delete-recycle");
        Assert.DoesNotContain(menu, entry => entry.Id == "ctx-rename");
    }
    [Fact]
    public void PaneMoreMenu_UsesPolishedLabelsAndSelectionGating()
    {
        var empty = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest());
        Assert.DoesNotContain(empty, entry => entry.Id == "ctx-rename");
        Assert.DoesNotContain(empty, entry => entry.Id == "ctx-delete-menu");
        Assert.Contains(empty, entry => entry.Id == "ctx-duplicates");
        Assert.Contains(empty, entry => entry.Id == "ctx-cleanup");
        Assert.Contains(empty, entry => entry.Id == "ctx-terminal" && entry.Shortcut == "F4");
        Assert.DoesNotContain(empty, entry => entry.Id == "ctx-close-dual-pane");
        Assert.DoesNotContain(empty, entry => entry.Kind == ContextMenuKind.Divider && empty.Last() == entry);

        var dualPane = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            DualPaneEnabled = true,
        });
        Assert.Contains(dualPane, entry => entry.Id == "ctx-close-left-pane" && entry.Label == "Close left pane");
        Assert.Contains(dualPane, entry => entry.Id == "ctx-close-dual-pane" && entry.Label == "Close right pane" && entry.Shortcut == "F6");

        var rightMenu = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            DualPaneEnabled = true,
            MenuPane = PaneId.Secondary,
        });
        Assert.DoesNotContain(rightMenu, entry => entry.Id == "ctx-close-left-pane");
        Assert.Contains(rightMenu, entry => entry.Id == "ctx-close-dual-pane");

        var archive = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            SelectionCount = 1,
            SelectedIsArchive = true,
        });

        Assert.Contains(archive, entry => entry.Id == "ctx-rename" && entry.Shortcut == "F2");
        var delete = Assert.Single(archive, entry => entry.Id == "ctx-delete-menu");
        Assert.Equal("Delete:", delete.Label);
        Assert.Contains(delete.Children, entry => entry.Id == "ctx-delete-recycle" && entry.Label == "Move to Recycle Bin");
        Assert.Contains(delete.Children, entry => entry.Id == "ctx-delete-permanent" && entry.Label == "Delete Permanently");
        Assert.Contains(archive, entry => entry.Id == "ctx-view-archive");
        Assert.Contains(archive, entry => entry.Id == "ctx-extract-to" && entry.Label == "Extract archive...");
        Assert.Contains(archive, entry => entry.Id == "ctx-compress" && entry.Label == "Create archive...");
    }
    [Fact]
    public void PaneMoreMenu_UsesPaneAndArchiveGlyphsFromCatalog()
    {
        var dualPane = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            DualPaneEnabled = true,
        });

        Assert.Equal(ContextMenuIconCatalog.ClosePane, Assert.Single(dualPane, entry => entry.Id == "ctx-close-left-pane").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.ClosePane, Assert.Single(dualPane, entry => entry.Id == "ctx-close-dual-pane").IconGlyph);

        var archive = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            SelectionCount = 1,
            SelectedIsArchive = true,
        });

        Assert.Equal(ContextMenuIconCatalog.Package, Assert.Single(archive, entry => entry.Id == "ctx-view-archive").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Import, Assert.Single(archive, entry => entry.Id == "ctx-extract-to").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Package, Assert.Single(archive, entry => entry.Id == "ctx-compress").IconGlyph);
    }
    [Fact]
    public void PaneMoreMenu_PrependsOverflowedToolbarCommands()
    {
        var overflowed = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            OverflowedToolbarIds =
            [
                ToolbarOverflowPlanner.Search,
                ToolbarOverflowPlanner.Filter,
                ToolbarOverflowPlanner.NewFolder,
                ToolbarOverflowPlanner.NewFile,
                ToolbarOverflowPlanner.DualPane,
                ToolbarOverflowPlanner.Profiles,
                ToolbarOverflowPlanner.ViewOptions,
                ToolbarOverflowPlanner.Settings,
            ],
        });

        Assert.Equal("overflow-search", overflowed[0].Id);
        Assert.Equal("Find in folder", overflowed[0].Label);
        Assert.Equal("overflow-filter", overflowed[1].Id);
        Assert.Equal("Filter list", overflowed[1].Label);
        Assert.Equal("overflow-new-folder", overflowed[2].Id);
        Assert.Equal("overflow-new-file", overflowed[3].Id);
        Assert.Equal("overflow-dual-pane", overflowed[4].Id);
        Assert.Equal("Open second pane", overflowed[4].Label);
        Assert.Equal("overflow-profiles", overflowed[5].Id);
        Assert.Equal("overflow-view", overflowed[6].Id);
        Assert.Equal("overflow-settings", overflowed[7].Id);
        Assert.Equal(ContextMenuIconCatalog.OpenPane, overflowed[4].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Switch, overflowed[5].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.ViewAll, overflowed[6].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Settings, overflowed[7].IconGlyph);
        Assert.Contains(overflowed[5].Children, child => child.Id == "profile:save");
        Assert.Contains(overflowed[5].Children, child => child.Id == "profile:manage");
        Assert.Contains(overflowed[6].Children, child => child.Id == "view:details");
        Assert.Contains(overflowed, entry => entry.Id == "ctx-duplicates");
        Assert.DoesNotContain(overflowed, entry => entry.Id == "ctx-close-dual-pane");

        var dualOpen = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            DualPaneEnabled = true,
            OverflowedToolbarIds = [ToolbarOverflowPlanner.DualPane],
        });
        Assert.DoesNotContain(dualOpen, entry => entry.Id == "overflow-dual-pane");
        Assert.Contains(dualOpen, entry => entry.Id == "ctx-close-dual-pane");
    }
}
