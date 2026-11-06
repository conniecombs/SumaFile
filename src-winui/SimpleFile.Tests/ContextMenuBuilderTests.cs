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
        Assert.Contains(empty, entry => entry.Id == "ctx-tools-menu");
        Assert.Contains(Flatten(empty), entry => entry.Id == "ctx-terminal");
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
        var selectedFlat = Flatten(selected);
        Assert.Equal("Enter", open.Shortcut);
        Assert.False(string.IsNullOrWhiteSpace(open.IconGlyph));
        Assert.Contains(selected, entry => entry.Id == "ctx-open-with");
        Assert.Contains(selected, entry => entry.Id == "ctx-send-to-menu");
        Assert.Contains(selectedFlat, entry => entry.Id == "ctx-copy-to-pane");
        Assert.Contains(selectedFlat, entry => entry.Id == "ctx-copy-path" && entry.Shortcut == "Ctrl+Shift+C");
        Assert.DoesNotContain(selectedFlat, entry => entry.Id == "ctx-open-tab");
        Assert.DoesNotContain(selectedFlat, entry => entry.Id == "ctx-open-other-pane");
        Assert.DoesNotContain(selectedFlat, entry => entry.Id == "ctx-bookmark");
        var delete = Assert.Single(selected, entry => entry.Id == "ctx-delete-menu");
        Assert.Equal("Delete:", delete.Label);
        Assert.Contains(delete.Children, entry => entry.Id == "ctx-delete-recycle" && entry.Label == "Move to Recycle Bin" && entry.Shortcut == "Delete");
        Assert.Contains(delete.Children, entry => entry.Id == "ctx-delete-permanent" && entry.Label == "Delete Permanently" && entry.Shortcut == "Shift+Delete");
        var archive = Assert.Single(selected, entry => entry.Id == "ctx-archive-menu");
        Assert.False(string.IsNullOrWhiteSpace(archive.IconGlyph));
        Assert.Contains(archive.Children, child => child.Id == "ctx-extract-folder" && child.Label.Contains("pack/", StringComparison.Ordinal));
        Assert.Contains(selected, entry => entry.Id == "ctx-info");
    }

    [Fact]
    public void ContextMenu_SendToLabelsNameOtherPaneDestination()
    {
        var fromPrimary = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 2,
            DualPaneEnabled = true,
            MenuPane = PaneId.Primary,
            OtherPaneHasPath = true,
            OtherPanePath = @"D:\Sorted",
        });
        var primarySendTo = Assert.Single(fromPrimary, entry => entry.Id == "ctx-send-to-menu");
        Assert.Contains(primarySendTo.Children, entry => entry.Id == "ctx-copy-to-pane" && entry.Label == @"Copy to right pane: D:\Sorted");
        Assert.Contains(primarySendTo.Children, entry => entry.Id == "ctx-move-to-pane" && entry.Label == @"Move to right pane: D:\Sorted");

        var fromSecondary = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            DualPaneEnabled = true,
            MenuPane = PaneId.Secondary,
            OtherPaneHasPath = true,
            OtherPanePath = @"R:\Inbox",
        });
        var secondarySendTo = Assert.Single(fromSecondary, entry => entry.Id == "ctx-send-to-menu");
        Assert.Contains(secondarySendTo.Children, entry => entry.Id == "ctx-copy-to-pane" && entry.Label == @"Copy to left pane: R:\Inbox");
        Assert.Contains(secondarySendTo.Children, entry => entry.Id == "ctx-move-to-pane" && entry.Label == @"Move to left pane: R:\Inbox");
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

        var selectedFlat = Flatten(selected);
        Assert.Equal(ContextMenuIconCatalog.OpenFile, Assert.Single(selected, entry => entry.Id == "ctx-open").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.OpenWith, Assert.Single(selected, entry => entry.Id == "ctx-open-with").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Preview, Assert.Single(selected, entry => entry.Id == "ctx-preview").IconGlyph);
        var sendTo = Assert.Single(selected, entry => entry.Id == "ctx-send-to-menu");
        var tools = Assert.Single(selected, entry => entry.Id == "ctx-tools-menu");
        Assert.Equal(ContextMenuIconCatalog.MoveToFolder, sendTo.IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.AreaChart, tools.IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Package, Assert.Single(selected, entry => entry.Id == "ctx-archive-menu").IconGlyph);
        Assert.All(sendTo.Children, entry => Assert.True(string.IsNullOrWhiteSpace(entry.IconGlyph)));
        Assert.All(tools.Children, entry => Assert.True(string.IsNullOrWhiteSpace(entry.IconGlyph)));

        var rename = Assert.Single(selected, entry => entry.Id == "ctx-rename");
        var advancedRename = Assert.Single(selectedFlat, entry => entry.Id == "ctx-advanced-rename");
        var copy = Assert.Single(selected, entry => entry.Id == "ctx-copy");
        var duplicates = Assert.Single(selectedFlat, entry => entry.Id == "ctx-duplicates");
        Assert.NotEqual(rename.IconGlyph, advancedRename.IconGlyph);
        Assert.NotEqual(copy.IconGlyph, duplicates.IconGlyph);

        var archive = Assert.Single(selected, entry => entry.Id == "ctx-archive-menu");
        Assert.Equal(ContextMenuIconCatalog.Package, archive.IconGlyph);
        Assert.All(archive.Children, entry => Assert.True(string.IsNullOrWhiteSpace(entry.IconGlyph)));

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
        Assert.DoesNotContain(Flatten(oneFile), entry => entry.Id == "ctx-compare");

        var twoFiles = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 2,
            AllSelectedAreFiles = true,
        });
        Assert.Contains(Flatten(twoFiles), entry => entry.Id == "ctx-compare");
    }

    [Fact]
    public void ContextMenu_GitMenuRespectsSettingAndSelection()
    {
        var disabled = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            GitEnabled = false,
            InGitRepository = true,
            SelectionHasGitStatus = true,
        });
        Assert.DoesNotContain(disabled, entry => entry.Id == "ctx-git-menu");

        var noRepo = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            GitEnabled = true,
            InGitRepository = false,
        });
        var noRepoGit = Assert.Single(noRepo, entry => entry.Id == "ctx-git-menu");
        Assert.Contains(noRepoGit.Children, entry => entry.Id == "ctx-git-panel");
        Assert.DoesNotContain(noRepoGit.Children, entry => entry.Id == "ctx-git-pull");

        var changedSelection = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            AllSelectedAreFiles = true,
            GitEnabled = true,
            InGitRepository = true,
            SelectionHasGitStatus = true,
        });
        var git = Assert.Single(changedSelection, entry => entry.Id == "ctx-git-menu");
        Assert.Contains(git.Children, entry => entry.Id == "ctx-git-diff");
        Assert.Contains(git.Children, entry => entry.Id == "ctx-git-stage");
        Assert.Contains(git.Children, entry => entry.Id == "ctx-git-unstage");
        Assert.Contains(git.Children, entry => entry.Id == "ctx-git-discard");
        Assert.Contains(git.Children, entry => entry.Id == "ctx-git-fetch");
        Assert.Contains(git.Children, entry => entry.Id == "ctx-git-pull");
        Assert.Contains(git.Children, entry => entry.Id == "ctx-git-push");
        Assert.Contains(git.Children, entry => entry.Id == "ctx-git-commit");
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
        var folderFlat = Flatten(folder);
        Assert.Equal("Ctrl+Enter", openTab.Shortcut);
        Assert.Equal(ContextMenuIconCatalog.NewTab, openTab.IconGlyph);
        Assert.Contains(folder, entry => entry.Id == "ctx-open-other-pane");
        Assert.Contains(folder, entry => entry.Id == "ctx-send-to-menu");
        Assert.Contains(folderFlat, entry => entry.Id == "ctx-bookmark" && entry.Shortcut == "Ctrl+B");
        Assert.Contains(folderFlat, entry => entry.Id == "ctx-copy-path");
        var paste = Assert.Single(folder, entry => entry.Id == "ctx-paste");
        Assert.Equal("Paste into folder", paste.Label);
        Assert.Equal(@"C:\Users\test\Desktop", paste.CommandParameter);
        Assert.DoesNotContain(folderFlat, entry => entry.Id == "ctx-folder-metrics");
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
        Assert.DoesNotContain(Flatten(oneFolder), entry => entry.Id == "ctx-folder-metrics");

        var twoFolders = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 2,
            HasFolderSelection = true,
            FolderSelectionCount = 2,
        });
        Assert.Contains(Flatten(twoFolders), entry => entry.Id == "ctx-folder-metrics" && entry.Label == "Compare folder metrics");
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
    public void ContextMenu_EmptySelectionShowsBackgroundMenu()
    {
        var background = ContextMenuBuilder.Build(new ContextMenuRequest { HasClipboard = true });

        var newMenu = Assert.Single(background, entry => entry.Id == "overflow-new");
        Assert.Equal("New", newMenu.Label);
        Assert.Equal(newMenu, background[0]);
        Assert.Contains(newMenu.Children, child => child.Id == "new:folder" && child.Shortcut == "Ctrl+Shift+N");
        Assert.Contains(newMenu.Children, child => child.Id == "new:text");
        Assert.Contains(newMenu.Children, child => child.Id == "new:empty");
        Assert.Contains(newMenu.Children, child => child.Id == "new:shortcut" && child.Label == "Shortcut...");

        var paste = Assert.Single(background, entry => entry.Id == "ctx-paste");
        Assert.Equal("Paste", paste.Label);
        Assert.Null(paste.CommandParameter);

        var refresh = Assert.Single(background, entry => entry.Id == "ctx-refresh");
        Assert.Equal("Refresh", refresh.Label);
        Assert.Equal("F5", refresh.Shortcut);
        Assert.False(string.IsNullOrWhiteSpace(refresh.IconGlyph));
        Assert.Equal("refresh", CommandAliasCatalog.Normalize("ctx-refresh"));

        Assert.Contains(background, entry => entry.Id == "ctx-tools-menu");
        Assert.DoesNotContain(Flatten(background), entry => entry.Id == "ctx-copy");
        Assert.DoesNotContain(Flatten(background), entry => entry.Id == "ctx-rename");
        Assert.DoesNotContain(Flatten(background), entry => entry.Id == "ctx-info");

        var properties = Assert.Single(background, entry => entry.Id == "ctx-folder-info");
        Assert.Equal("Properties", properties.Label);
        Assert.Equal(properties, background[^1]);
        Assert.Equal(ContextMenuKind.Divider, background[^2].Kind);
        Assert.False(string.IsNullOrWhiteSpace(properties.IconGlyph));
        Assert.Equal("folder-properties", CommandAliasCatalog.Normalize("ctx-folder-info"));
        Assert.Equal("Current folder properties", AppCommandCatalog.Find("folder-properties")?.Label);
        Assert.Equal("Inspection", AppCommandCatalog.Find("folder-properties")?.Group);
    }

    [Fact]
    public void ContextMenu_SingleSelectionOffersCreateShortcut()
    {
        var single = ContextMenuBuilder.Build(new ContextMenuRequest { SelectionCount = 1, AllSelectedAreFiles = true });
        var createShortcut = Assert.Single(single, entry => entry.Id == "ctx-create-shortcut");
        Assert.Equal("Create shortcut", createShortcut.Label);
        Assert.Equal(ContextMenuIconCatalog.Link, createShortcut.IconGlyph);
        var renameIndex = single.ToList().FindIndex(entry => entry.Id == "ctx-rename");
        Assert.Equal(renameIndex + 1, single.ToList().IndexOf(createShortcut));

        var two = ContextMenuBuilder.Build(new ContextMenuRequest { SelectionCount = 2, AllSelectedAreFiles = true });
        Assert.DoesNotContain(Flatten(two), entry => entry.Id == "ctx-create-shortcut");

        var folder = ContextMenuBuilder.Build(new ContextMenuRequest { SelectionCount = 1, SelectedIsDirectory = true });
        Assert.Contains(folder, entry => entry.Id == "ctx-create-shortcut");

        var recycleBin = ContextMenuBuilder.Build(new ContextMenuRequest { SelectionCount = 1, InRecycleBin = true });
        Assert.DoesNotContain(Flatten(recycleBin), entry => entry.Id == "ctx-create-shortcut");

        Assert.Equal("shortcut-for-selected", CommandAliasCatalog.Normalize("ctx-create-shortcut"));
        Assert.Equal("Create shortcut to selection", AppCommandCatalog.Find("shortcut-for-selected")?.Label);
        Assert.Equal("File", AppCommandCatalog.Find("shortcut-for-selected")?.Group);
    }

    [Fact]
    public void ContextMenu_BackgroundMenuPromotesTerminalAboveTools()
    {
        var background = ContextMenuBuilder.Build(new ContextMenuRequest()).ToList();
        var terminal = Assert.Single(background, entry => entry.Id == "ctx-terminal");
        Assert.Equal("Open terminal here", terminal.Label);
        Assert.Equal("F4", terminal.Shortcut);
        Assert.False(string.IsNullOrWhiteSpace(terminal.IconGlyph));
        Assert.Equal(background.FindIndex(entry => entry.Id == "ctx-refresh") + 1, background.IndexOf(terminal));

        var tools = Assert.Single(background, entry => entry.Id == "ctx-tools-menu");
        Assert.DoesNotContain(tools.Children, child => child.Id == "ctx-terminal");
        Assert.DoesNotContain(tools.Children, child => child.Id == "ctx-powershell-admin");

        var selected = ContextMenuBuilder.Build(new ContextMenuRequest { SelectionCount = 1, AllSelectedAreFiles = true });
        var selectedTools = Assert.Single(selected, entry => entry.Id == "ctx-tools-menu");
        Assert.Contains(selectedTools.Children, child => child.Id == "ctx-terminal");
        Assert.DoesNotContain(selected, entry => entry.Id == "ctx-terminal");
    }

    [Fact]
    public void NewSubmenu_ChildrenCarryIcons()
    {
        var background = ContextMenuBuilder.Build(new ContextMenuRequest());
        var newMenu = Assert.Single(background, entry => entry.Id == "overflow-new");

        Assert.Equal(ContextMenuIconCatalog.NewFolder, newMenu.Children.Single(child => child.Id == "new:folder").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Document, newMenu.Children.Single(child => child.Id == "new:text").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Document, newMenu.Children.Single(child => child.Id == "new:empty").IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Link, newMenu.Children.Single(child => child.Id == "new:shortcut").IconGlyph);
    }

    [Fact]
    public void ContextMenu_EmptySelectionHidesPasteWithoutClipboard()
    {
        var background = ContextMenuBuilder.Build(new ContextMenuRequest());

        Assert.DoesNotContain(background, entry => entry.Id == "ctx-paste");
        Assert.Contains(background, entry => entry.Id == "overflow-new");
        Assert.Contains(background, entry => entry.Id == "ctx-refresh");
    }

    [Fact]
    public void ContextMenu_SelectionDoesNotShowBackgroundItems()
    {
        var selected = ContextMenuBuilder.Build(new ContextMenuRequest
        {
            SelectionCount = 1,
            AllSelectedAreFiles = true,
        });

        Assert.DoesNotContain(Flatten(selected), entry => entry.Id == "overflow-new");
        Assert.DoesNotContain(Flatten(selected), entry => entry.Id == "ctx-refresh");
        Assert.DoesNotContain(Flatten(selected), entry => entry.Id == "ctx-folder-info");
    }

    [Fact]
    public void PaneMoreMenu_UsesPolishedLabelsAndSelectionGating()
    {
        var empty = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest());
        var emptyFlat = Flatten(empty);
        Assert.Contains(empty, entry => entry.Id == "ctx-toolbar-menu");
        Assert.Contains(emptyFlat, entry => entry.Id == "ctx-toggle-toolbar-labels");
        Assert.Contains(emptyFlat, entry => entry.Id == "ctx-customize-toolbar");
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
    public void PaneMoreMenu_OffersToolbarLabelToggleAndCustomizeShortcut()
    {
        var iconOnly = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            ToolbarDisplayMode = ToolbarActionCatalog.IconOnlyDisplayMode,
        });
        var iconOnlyToolbar = Assert.Single(iconOnly, entry => entry.Id == "ctx-toolbar-menu");

        Assert.Equal("Toolbar", iconOnlyToolbar.Label);
        Assert.Equal(ContextMenuIconCatalog.Settings, iconOnlyToolbar.IconGlyph);
        Assert.Contains(iconOnlyToolbar.Children, entry => entry.Id == "ctx-toggle-toolbar-labels" && entry.Label == "Show button labels");
        Assert.Contains(iconOnlyToolbar.Children, entry => entry.Id == "ctx-customize-toolbar" && entry.Label == "Customize toolbar...");

        var withLabels = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            ToolbarDisplayMode = ToolbarActionCatalog.IconAndLabelDisplayMode,
        });
        var labeledToolbar = Assert.Single(withLabels, entry => entry.Id == "ctx-toolbar-menu");

        Assert.Contains(labeledToolbar.Children, entry => entry.Id == "ctx-toggle-toolbar-labels" && entry.Label == "Hide button labels");
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
            ToolbarActionOrder = ["new", "dual-pane", "profiles", "view-options", "settings"],
            OverflowedToolbarIds =
            [
                ToolbarOverflowPlanner.New,
                ToolbarOverflowPlanner.DualPane,
                ToolbarOverflowPlanner.Profiles,
                ToolbarOverflowPlanner.ViewOptions,
                ToolbarOverflowPlanner.Settings,
            ],
        });

        var newMenu = overflowed[0];
        Assert.Equal("overflow-new", newMenu.Id);
        Assert.Equal("New", newMenu.Label);
        Assert.Contains(newMenu.Children, child => child.Id == "new:folder" && child.Shortcut == "Ctrl+Shift+N");
        Assert.Contains(newMenu.Children, child => child.Id == "new:text" && child.Label == "Text document" && child.Shortcut == "Ctrl+N");
        Assert.Contains(newMenu.Children, child => child.Id == "new:empty" && child.Label == "Blank file...");
        Assert.Contains(newMenu.Children, child => child.Id == "new:shortcut" && child.Label == "Shortcut...");
        Assert.Equal("overflow-dual-pane", overflowed[1].Id);
        Assert.Equal("Open second pane", overflowed[1].Label);
        Assert.Equal("overflow-profiles", overflowed[2].Id);
        Assert.Equal("overflow-view", overflowed[3].Id);
        Assert.Equal("overflow-settings", overflowed[4].Id);
        Assert.Equal(ContextMenuIconCatalog.NewFolder, overflowed[0].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.OpenPane, overflowed[1].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Switch, overflowed[2].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.ViewAll, overflowed[3].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Settings, overflowed[4].IconGlyph);
        Assert.Contains(overflowed[2].Children, child => child.Id == "profile:save");
        Assert.Contains(overflowed[2].Children, child => child.Id == "profile:manage");
        Assert.Contains(overflowed[3].Children, child => child.Id == "view:details");
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

    [Fact]
    public void PaneMoreMenu_PrependsCustomOverflowedToolbarCommandsInLayoutOrder()
    {
        var overflowed = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            OverflowedToolbarIds = ["copy", "terminal", ToolbarOverflowPlanner.Settings],
            ToolbarActionOrder = ["terminal", "copy", ToolbarOverflowPlanner.Settings],
        });

        Assert.Equal("overflow-terminal", overflowed[0].Id);
        Assert.Equal("Open terminal", overflowed[0].Label);
        Assert.Equal("F4", overflowed[0].Shortcut);
        Assert.Equal("overflow-copy", overflowed[1].Id);
        Assert.Equal("Copy", overflowed[1].Label);
        Assert.Equal("Ctrl+C", overflowed[1].Shortcut);
        Assert.Equal("overflow-settings", overflowed[2].Id);
    }

    [Fact]
    public void PaneMoreMenu_OverflowedCrossPaneCommandsNameDestination()
    {
        var overflowed = ContextMenuBuilder.BuildPaneMoreMenu(new ContextMenuRequest
        {
            DualPaneEnabled = true,
            MenuPane = PaneId.Primary,
            OtherPaneHasPath = true,
            OtherPanePath = @"D:\Sorted",
            OverflowedToolbarIds = ["copy-to-pane", "move-to-pane"],
            ToolbarActionOrder = ["copy-to-pane", "move-to-pane"],
        });

        Assert.Contains(overflowed, entry => entry.Id == "overflow-copy-to-pane" && entry.Label == @"Copy to right pane: D:\Sorted");
        Assert.Contains(overflowed, entry => entry.Id == "overflow-move-to-pane" && entry.Label == @"Move to right pane: D:\Sorted");
    }

    private static IReadOnlyList<ContextMenuEntry> Flatten(IReadOnlyList<ContextMenuEntry> entries)
    {
        var result = new List<ContextMenuEntry>();
        foreach (var entry in entries)
        {
            result.Add(entry);
            result.AddRange(Flatten(entry.Children));
        }

        return result;
    }
}
