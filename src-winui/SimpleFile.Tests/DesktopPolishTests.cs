using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class DesktopPolishTests
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
        Assert.Equal("Go home", AppCommandCatalog.Find("go-home")?.Label);
        Assert.Equal("Disk cleanup", AppCommandCatalog.Find("disk-cleanup")?.Label);
        Assert.Equal("Open or close second pane", AppCommandCatalog.Find("dual-pane")?.Label);
        Assert.Equal("Move to Recycle Bin", AppCommandCatalog.Find("delete")?.Label);
        Assert.Equal("Delete Permanently", AppCommandCatalog.Find("delete-permanent")?.Label);
        Assert.Equal(AppCommandCatalog.All.Count, AppCommandCatalog.Filter("").Count);
        var git = AppCommandCatalog.Filter("git");
        Assert.Equal(2, git.Count);
        Assert.All(git, command => Assert.StartsWith("git-", command.Id, StringComparison.Ordinal));
        Assert.Equal(7, AppCommandCatalog.Filter("icon size").Count);
        Assert.Equal("toggle-side-menu", Assert.Single(AppCommandCatalog.Filter("side menu")).Id);
        Assert.Equal("settings", AppCommandCatalog.Find("settings")?.Id);
        Assert.Null(AppCommandCatalog.Find("missing"));
    }

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
    public void OpenWithPreferences_ComposesPinnedRecentAndDiscoveredAppsInOrder()
    {
        var preferences = new OpenWithPreferences();
        var pinned = OpenWithApplication.FromPath(@"C:\Program Files\Pinned\App.exe", "Pinned App", "custom");
        var recent = OpenWithApplication.FromPath(@"C:\Program Files\Recent\App.exe", "Recent App", "custom");
        var discovered = OpenWithApplication.FromPath(@"C:\Program Files\Suggested\App.exe", "Suggested App", "suggested");

        preferences.RecordRecent("TXT", recent);
        preferences.PinForExtension("txt", pinned);
        var apps = preferences.ComposeMenuApplications(".txt", [recent, discovered]);

        Assert.Collection(
            apps,
            app =>
            {
                Assert.Equal("Pinned App", app.DisplayName);
                Assert.True(app.IsFavorite);
                Assert.False(app.IsRecent);
            },
            app =>
            {
                Assert.Equal("Recent App", app.DisplayName);
                Assert.False(app.IsFavorite);
                Assert.True(app.IsRecent);
            },
            app =>
            {
                Assert.Equal("Suggested App", app.DisplayName);
                Assert.Equal("suggested", app.Source);
            });
    }

    [Fact]
    public void OpenWithPreferences_CapsRecentsAndRoundTripsJson()
    {
        var preferences = new OpenWithPreferences();
        for (var index = 0; index < OpenWithPreferences.MaxRecentApplications + 2; index++)
        {
            preferences.RecordRecent(
                ".log",
                OpenWithApplication.FromPath($@"C:\Program Files\App{index}\app.exe", $"App {index}", "custom"));
        }

        var roundTripped = OpenWithPreferences.FromJson(preferences.ToJson());
        var apps = roundTripped.ComposeMenuApplications("log", []);

        Assert.Equal(OpenWithPreferences.MaxRecentApplications, apps.Count);
        Assert.Equal("App 9", apps[0].DisplayName);
        Assert.Equal("App 2", apps[^1].DisplayName);
        Assert.All(apps, app => Assert.True(app.IsRecent));
    }

    [Fact]
    public void OpenWithPreferences_UnpinsFavoritesForExtension()
    {
        var preferences = new OpenWithPreferences();
        var favorite = OpenWithApplication.FromPath(@"C:\Program Files\Pinned\App.exe", "Pinned App", "custom");

        preferences.PinForExtension(".md", favorite);
        Assert.Contains(preferences.ComposeMenuApplications("md", []), app => app.IsFavorite);

        preferences.UnpinForExtension("md", favorite);

        Assert.DoesNotContain(preferences.ComposeMenuApplications("md", []), app => app.IsFavorite);
        Assert.False(preferences.FavoritesByExtension.ContainsKey(".md"));
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
    public void ToolbarOverflowPlanner_HidesLowestPriorityFirstAndStaysStable()
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ToolbarOverflowPlanner.Filter] = ToolbarOverflowPlanner.FilterOverflowWidthFor(1200),
            [ToolbarOverflowPlanner.Search] = ToolbarOverflowPlanner.SearchOverflowWidthFor(1200),
            [ToolbarOverflowPlanner.Settings] = 32,
            [ToolbarOverflowPlanner.DualPane] = 32,
            [ToolbarOverflowPlanner.ViewOptions] = 32,
            [ToolbarOverflowPlanner.NewFile] = 32,
            [ToolbarOverflowPlanner.NewFolder] = 32,
        };
        var reserved = 360;

        Assert.Equal(
            [
                ToolbarOverflowPlanner.Filter,
                ToolbarOverflowPlanner.Search,
                ToolbarOverflowPlanner.Settings,
                ToolbarOverflowPlanner.DualPane,
                ToolbarOverflowPlanner.ViewOptions,
                ToolbarOverflowPlanner.NewFile,
                ToolbarOverflowPlanner.NewFolder,
            ],
            ToolbarOverflowPlanner.PrimaryHideOrder);

        var wide = ToolbarOverflowPlanner.OverflowIds(1200, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.Empty(wide);

        var medium = ToolbarOverflowPlanner.OverflowIds(700, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.Contains(ToolbarOverflowPlanner.Filter, medium);
        Assert.Contains(ToolbarOverflowPlanner.Search, medium);
        Assert.DoesNotContain(ToolbarOverflowPlanner.NewFolder, medium);

        var narrow = ToolbarOverflowPlanner.OverflowIds(340, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.True(narrow.IsSupersetOf(medium));
        Assert.Contains(ToolbarOverflowPlanner.NewFolder, narrow);

        var again = ToolbarOverflowPlanner.OverflowIds(340, reserved, widths, ToolbarOverflowPlanner.PrimaryHideOrder);
        Assert.True(narrow.SetEquals(again));
    }

    [Theory]
    [InlineData(700, 260, 128)]
    [InlineData(1600, 384, 160)]
    [InlineData(2400, 480, 200)]
    public void ToolbarOverflowPlanner_ScalesSearchAndFilterWithinCaps(
        double availableWidth,
        double expectedSearchWidth,
        double expectedFilterWidth)
    {
        Assert.Equal(expectedSearchWidth, ToolbarOverflowPlanner.SearchWidthFor(availableWidth), 3);
        Assert.Equal(expectedFilterWidth, ToolbarOverflowPlanner.FilterWidthFor(availableWidth), 3);
    }

    [Fact]
    public void TransferProgressFormatter_ShowsRichCopyState()
    {
        var context = new TransferProgressContext(false, 6, @"R:\Repos", @"V:\Stuff");
        var update = new ProgressUpdate
        {
            OperationType = "copy",
            Current = 1100UL * 1024 * 1024,
            Total = 1500UL * 1024 * 1024,
            CurrentFiles = 7,
            TotalFiles = 12,
            CurrentItem = @"R:\Repos\SimpleFile-Windows\src-winui\SimpleFile.App\file.bin",
            Status = "running",
        };

        var display = TransferProgressFormatter.Format(context, update, 50 * 1024 * 1024, 2.5);

        Assert.Equal("Copying 6 items", display.Title);
        Assert.Equal("1.07 GB of 1.46 GB", display.Summary);
        Assert.Equal("73%", display.Percent);
        Assert.Equal("7 of 12 files", display.FileSummary);
        Assert.Equal("2.5 files/s avg", display.FileRate);
        Assert.InRange(display.FileProgressPercent, 58.3, 58.4);
        Assert.Equal("file.bin", display.CurrentItemName);
        Assert.Equal(@"From: R:\Repos", display.From);
        Assert.Equal(@"To: V:\Stuff", display.To);
        Assert.Equal("50 MB/s", display.Speed);
        Assert.Contains("remaining", display.Eta, StringComparison.Ordinal);
        Assert.False(display.IsIndeterminate);
    }

    [Fact]
    public void TransferProgressFormatter_ClampsOverCompleteProgress()
    {
        var context = new TransferProgressContext(true, 1, @"R:\Repos", @"V:\Stuff");
        var update = new ProgressUpdate
        {
            OperationType = "move",
            Current = 125,
            Total = 100,
            CurrentFiles = 4,
            TotalFiles = 3,
            CurrentItem = @"R:\Repos\file.txt",
            Status = "running",
        };

        var display = TransferProgressFormatter.Format(context, update, bytesPerSecond: null, averageFilesPerSecond: null);

        Assert.Equal("Moving 1 item", display.Title);
        Assert.Equal(100, display.ProgressPercent);
        Assert.Equal("100%", display.Percent);
        Assert.Equal("100 B of 100 B", display.Summary);
        Assert.Equal(100, display.FileProgressPercent);
        Assert.Equal("3 of 3 files", display.FileSummary);
    }

    [Fact]
    public void TransferProgressFormatter_CompletedZeroTotals_DoNotLookStuck()
    {
        var context = new TransferProgressContext(false, 1, @"R:\Empty", @"V:\Stuff");
        var update = new ProgressUpdate
        {
            OperationType = "copy",
            Status = "completed",
        };

        var display = TransferProgressFormatter.Format(context, update, bytesPerSecond: null, averageFilesPerSecond: null);

        Assert.Equal("Copy complete", display.Title);
        Assert.Equal("0 B transferred", display.Summary);
        Assert.Equal("0 files", display.FileSummary);
        Assert.Equal("Files complete", display.FileRate);
        Assert.False(display.IsIndeterminate);
        Assert.False(display.FileProgressIsIndeterminate);
    }

    [Fact]
    public void TransferProgressFormatter_ErrorWithoutItem_DoesNotShowPreparing()
    {
        var context = new TransferProgressContext(false, 1, @"R:\Source", @"V:\Stuff");
        var update = new ProgressUpdate
        {
            OperationType = "copy",
            Status = "error",
            Error = "Failed to preserve file timestamps: Access is denied. (os error 5)",
        };

        var display = TransferProgressFormatter.Format(context, update, bytesPerSecond: null, averageFilesPerSecond: null);

        Assert.Equal("Copy failed", display.Title);
        Assert.Equal(update.Error, display.Summary);
        Assert.Equal("Transfer failed", display.CurrentItemName);
        Assert.Equal("", display.CurrentItemPath);
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
                ToolbarOverflowPlanner.ViewOptions,
                ToolbarOverflowPlanner.Settings,
            ],
        });

        Assert.Equal("overflow-search", overflowed[0].Id);
        Assert.Equal("overflow-filter", overflowed[1].Id);
        Assert.Equal("overflow-new-folder", overflowed[2].Id);
        Assert.Equal("overflow-new-file", overflowed[3].Id);
        Assert.Equal("overflow-dual-pane", overflowed[4].Id);
        Assert.Equal("Open second pane", overflowed[4].Label);
        Assert.Equal("overflow-view", overflowed[5].Id);
        Assert.Equal("overflow-settings", overflowed[6].Id);
        Assert.Equal(ContextMenuIconCatalog.OpenPane, overflowed[4].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.ViewAll, overflowed[5].IconGlyph);
        Assert.Equal(ContextMenuIconCatalog.Settings, overflowed[6].IconGlyph);
        Assert.Contains(overflowed[5].Children, child => child.Id == "view:details");
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
    public void ServiceJob_DoesNotKillOpenedDocumentsWithTheProcessTree()
    {
        var root = FindRepoRoot();
        var session = File.ReadAllText(Path.Combine(root, "SimpleFile.Core", "BackendSession.cs"));
        var job = File.ReadAllText(Path.Combine(root, "SimpleFile.Core", "JobObject.cs"));

        Assert.DoesNotContain("entireProcessTree: true", session);
        Assert.Contains("entireProcessTree: false", session);
        Assert.Contains("JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK", job);
        Assert.Contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE", job);
    }

    [Fact]
    public void FilePanes_AcceptClicksAcrossPaneAndRowWhitespace()
    {
        var root = FindRepoRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.xaml.cs"));
        var fileRowView = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileRowView.xaml"));

        Assert.Contains("AttachPaneActivationHandlers();", mainWindow);
        Assert.Contains(
            "PrimaryPaneRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPrimaryPanePressed), true);",
            mainWindow);
        Assert.Contains(
            "SecondaryPaneRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSecondaryPanePressed), true);",
            mainWindow);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", fileRowView);
        Assert.Contains("Background=\"Transparent\"", fileRowView);
    }

    [Fact]
    public void DetailsColumns_UseIndependentPixelWidthsLikeExplorer()
    {
        var root = FindRepoRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.xaml.cs"))
            + File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.Transfer.cs"));
        var fileRowView = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileRowView.xaml.cs"));

        Assert.Contains("ColumnResizeTarget", mainWindow);
        Assert.Contains("Size Column to Fit", mainWindow);
        Assert.Contains("Size All Columns to Fit", mainWindow);
        Assert.Contains("OnColumnThumbDoubleTapped", mainWindow);
        Assert.DoesNotContain("resizeId = column.Id == \"name\"", mainWindow);
        Assert.DoesNotContain(
            "? new GridLength(1, GridUnitType.Star)",
            fileRowView.Replace("\r\n", "\n"));
        Assert.Contains("Width = new GridLength(column.Width)", fileRowView);
    }

    [Fact]
    public void TileLayout_StacksLargeIconsAndKeepsContainerWidthDynamic()
    {
        var root = FindRepoRoot();
        var metrics = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileTileLayoutMetrics.cs"));
        var fileRowView = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileRowView.xaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "App.xaml"));

        Assert.Contains("StackedIconThreshold = 128", metrics);
        Assert.Contains("ContainerHeightFor", metrics);
        Assert.Contains("UsesStackedLayout", fileRowView);
        Assert.Contains("RebuildStackedTileLayout(iconSize)", fileRowView);
        Assert.Contains("TileItemStyleFor(iconSize)", mainWindow);
        Assert.Contains("ContainerWidthFor(normalized)", mainWindow);
        Assert.Contains("ApplyTileItemsPanelMetrics(list, iconSize)", mainWindow);
        Assert.Contains("FindDescendant<ItemsWrapGrid>(list)", mainWindow);
        Assert.Contains("panel.ItemWidth", mainWindow);
        Assert.Contains("panel.ItemHeight", mainWindow);
        Assert.Contains("HorizontalContentAlignment\" Value=\"Left", app);
        Assert.DoesNotContain("<Setter Property=\"Width\" Value=\"236\" />", app);
    }

    [Fact]
    public void OpeningFiles_FlushesPendingIconSizePersistence()
    {
        var root = FindRepoRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.xaml.cs"));
        var commands = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.Commands.cs"));

        Assert.Contains("await SaveViewIconSizeNowAsync();", mainWindow);
        Assert.Contains("SaveViewIconSizeAsync(token, delay: false)", commands);
        Assert.Contains("_viewIconSizeSaveGate.WaitAsync()", commands);
        Assert.Contains("Volatile.Read(ref _viewIconSizeSaveToken)", commands);
    }

    [Fact]
    public void ColumnLayout_ClampsResizeAndAppliesPresets()
    {
        var columns = new ColumnLayout();
        columns.Resize("size", 10);
        Assert.Equal(36, columns.WidthOf("size"));
        columns.Resize("size", 800);
        Assert.Equal(800, columns.WidthOf("size"));
        columns.Resize("name", 40);
        Assert.Equal(80, columns.WidthOf("name"));
        columns.Resize("name", 500);
        Assert.Equal(500, columns.WidthOf("name"));
        Assert.Equal(columns.VisibleColumns.Sum(column => column.Width), columns.VisibleWidth);
        columns.ApplyPreset("developer");
        Assert.Contains("git", columns.VisibleIds);
        Assert.Equal(["name", "size", "date", "extension", "git", "symlink", "path"], columns.VisibleColumns.Select(column => column.Id));
        columns.RestoreWidths(new Dictionary<string, double> { ["name"] = 300 });
        Assert.Equal(300, columns.WidthOf("name"));
    }

    [Fact]
    public void DropDestination_ResolvesFolderHoverAndRejectsSelf()
    {
        var ontoFolder = DropDestination.Resolve(@"C:\Users\test", @"C:\Users\test\Desktop", hoveredIsDirectory: true);
        Assert.Equal(@"C:\Users\test\Desktop", ontoFolder.Destination);
        Assert.True(ontoFolder.OntoFolder);

        var ontoPane = DropDestination.Resolve(@"C:\Users\test", @"C:\Users\test\notes.txt", hoveredIsDirectory: false);
        Assert.Equal(@"C:\Users\test", ontoPane.Destination);

        Assert.False(DropDestination.IsValidDrop([@"C:\Users\test\Desktop"], @"C:\Users\test\Desktop"));
        Assert.False(DropDestination.IsValidDrop([@"C:\Users\test\Desktop"], @"C:\Users\test\Desktop\nested"));
        Assert.False(DropDestination.IsValidDrop([@"C:\Users\test\notes.txt"], @"C:\Users\test"));
        Assert.True(DropDestination.IsValidDrop([@"C:\Users\test\notes.txt"], @"C:\Users\test\Desktop"));

        var conflicts = DropDestination.ConflictingNames(
            [@"C:\src\notes.txt", @"C:\src\other.txt"],
            ["notes.txt", "readme.md"]);
        Assert.Equal(["notes.txt"], conflicts);

        var transferConflicts = DropDestination.ConflictingTransferNames(
            [@"C:\one\notes.txt", @"C:\two\notes.txt", @"C:\src\readme.md"],
            ["readme.md"]);
        Assert.Equal(["notes.txt", "readme.md"], transferConflicts);
    }

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
    public void DrivePresentation_DescribesNetworkStateForSidebar()
    {
        var offline = new DriveInfo
        {
            Name = "Projects (Z:)",
            Path = @"Z:\",
            DriveType = "network",
            DriveStatus = "offline",
            StatusDetail = "The operation timed out.",
            RemotePath = @"\\nas\projects",
        };
        Assert.Equal("Offline", DrivePresentation.Badge(offline));
        Assert.Equal("Timed out · Retry to reconnect", DrivePresentation.Description(offline));
        Assert.False(DrivePresentation.IsAvailable(offline));

        var connected = new DriveInfo
        {
            Path = @"Y:\",
            DriveType = "network",
            DriveStatus = "available",
            RemotePath = @"\\nas\media",
        };
        Assert.Equal("", DrivePresentation.Badge(connected));
        Assert.Equal(@"\\nas\media", DrivePresentation.Description(connected));
        Assert.True(DrivePresentation.IsAvailable(connected));
    }

    [Fact]
    public void KeyboardShortcuts_IncludePaletteAndAllowOverrides()
    {
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "commandPalette.open" && item.Keys == "Ctrl+Shift+P");
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "pane.switch" && item.Keys == "Tab");
        Assert.Contains(KeyboardShortcutMap.Defaults, item => item.Id == "pane.toggleDual" && item.Label == "Open or close second pane");
        var remapped = KeyboardShortcutMap.ApplyOverrides(new Dictionary<string, string>
        {
            ["search.focus"] = "Ctrl+K",
        });
        Assert.Equal("Ctrl+K", remapped.Single(item => item.Id == "search.focus").Keys);
        Assert.Equal("F5", remapped.Single(item => item.Id == "directory.refresh").Keys);
    }

    [Fact]
    public void ArchivePaths_RecognizeCompoundExtensions()
    {
        Assert.True(ArchivePaths.IsArchiveFile(@"C:\pack.tar.gz"));
        Assert.True(ArchivePaths.IsArchiveFile(@"D:\a.tgz"));
        Assert.True(ArchivePaths.IsArchiveFile("bundle.rar"));
        Assert.False(ArchivePaths.IsArchiveFile("notes.txt"));
        Assert.Equal("pack", ArchivePaths.ExtractFolderName("pack.tar.gz"));
        Assert.Equal("pack", ArchivePaths.ExtractFolderName(@"C:\downloads\pack.tar.gz"));
        Assert.Equal("backup", ArchivePaths.ExtractFolderName("backup.tgz"));
        Assert.Equal("report.v1", ArchivePaths.ExtractFolderName("report.v1.txt"));
        Assert.Equal("bundle", ArchivePaths.ExtractFolderName("bundle.zip"));
        Assert.Equal("report.v1.tar.gz", ArchivePaths.WithArchiveExtension("report.v1.zip", "tar.gz"));
        Assert.Equal("report.v1.rar", ArchivePaths.WithArchiveExtension("report.v1", "rar"));
        Assert.Equal("Archive.zip", ArchivePaths.WithArchiveExtension("", "zip"));
    }

    [Fact]
    public void SearchOptionsFactory_CreatesRunCopyWithoutMutatingTemplate()
    {
        var template = new SearchOptions
        {
            Query = "invoice",
            SearchPath = "",
            CaseSensitive = true,
            IncludeHidden = true,
            FileTypes = ["pdf", "docx"],
            MaxResults = 200,
            MaxDepth = 4,
            SearchId = "saved-template",
            ContentSearch = true,
            MinSize = 1024,
            MaxSize = 4096,
            DateAfter = "2026-01-01",
            DateBefore = "2026-12-31",
        };

        var run = SearchOptionsFactory.ForRun(template, "run-42", @"C:\Work");

        Assert.Equal(@"C:\Work", run.SearchPath);
        Assert.Equal("run-42", run.SearchId);
        Assert.Equal("saved-template", template.SearchId);
        Assert.Equal("", template.SearchPath);
        Assert.NotSame(template.FileTypes, run.FileTypes);
        Assert.Equal(template.FileTypes, run.FileTypes);
    }

    [Fact]
    public void UiSettings_NormalizesAppearanceAndStartLocation()
    {
        Assert.Equal("light", UiSettings.NormalizeTheme("Light"));
        Assert.Equal("system", UiSettings.NormalizeTheme("system"));
        Assert.Equal("dark", UiSettings.NormalizeTheme("nope"));
        Assert.Equal("last", UiSettings.NormalizeStartLocation("Last"));
        Assert.Equal("custom", UiSettings.NormalizeStartLocation("custom"));
        Assert.Equal("home", UiSettings.NormalizeStartLocation(null));
        Assert.Equal("details", UiSettings.NormalizeDefaultView("Details"));
        Assert.Equal("tiles", UiSettings.NormalizeDefaultView("tiles"));
        Assert.Equal("details", UiSettings.NormalizeDefaultView("nope"));
        Assert.Equal(16, UiSettings.NormalizeIconSize((int?)null));
        Assert.Equal(32, UiSettings.NormalizeIconSize("33"));
        Assert.Equal(120, UiSettings.NormalizeIconSize(120));
        Assert.Equal(16, UiSettings.NormalizeIconSize(3));
        Assert.Equal(256, UiSettings.NormalizeIconSize(900));
        Assert.Equal(UiSettings.SidebarDefaultWidth, UiSettings.NormalizeSidebarWidth(double.NaN));
        Assert.Equal(UiSettings.SidebarMinWidth, UiSettings.NormalizeSidebarWidth(120));
        Assert.Equal(312, UiSettings.NormalizeSidebarWidth("312"));
        Assert.Equal(UiSettings.SidebarMaxWidth, UiSettings.NormalizeSidebarWidth(900));
        Assert.Equal(UiSettings.PreviewDefaultWidth, UiSettings.NormalizePreviewWidth(double.NaN));
        Assert.Equal(UiSettings.PreviewMinWidth, UiSettings.NormalizePreviewWidth(80));
        Assert.Equal(420, UiSettings.NormalizePreviewWidth("420"));
        Assert.Equal(UiSettings.PreviewMaxWidth, UiSettings.NormalizePreviewWidth(2000));
        Assert.Equal(UiSettings.DualPaneDefaultPercent, UiSettings.NormalizeDualPanePrimaryPercent(double.PositiveInfinity));
        Assert.Equal(UiSettings.DualPaneMinPercent, UiSettings.NormalizeDualPanePrimaryPercent(5));
        Assert.Equal(62, UiSettings.NormalizeDualPanePrimaryPercent("62"));
        Assert.Equal(UiSettings.DualPaneMaxPercent, UiSettings.NormalizeDualPanePrimaryPercent(99));
        Assert.Equal(0, UiSettings.NormalizeDualPanePrimaryWidth(0));
        Assert.Equal(0, UiSettings.NormalizeDualPanePrimaryWidth(double.NaN));
        Assert.Equal(UiSettings.FilePaneMinWidth, UiSettings.NormalizeDualPanePrimaryWidth(40));
        Assert.Equal(420, UiSettings.NormalizeDualPanePrimaryWidth("420"));
        Assert.Equal(400, UiSettings.ResolveDualPanePrimaryWidth(400, 50, 1000));
        Assert.Equal(500, UiSettings.ResolveDualPanePrimaryWidth(0, 50, 1000));
        Assert.Equal(UiSettings.FilePaneMinWidth, UiSettings.ResolveDualPanePrimaryWidth(10, 50, 1000));
        Assert.Equal(1000 - UiSettings.FilePaneMinWidth - UiSettings.DualPaneDividerWidth,
            UiSettings.ResolveDualPanePrimaryWidth(9000, 50, 1000));
    }

    [Fact]
    public async Task UndoStack_UndoThenRedo_InvokesInOrder()
    {
        var log = new List<string>();
        var stack = new UndoStack();
        stack.Push(new UndoEntry
        {
            Description = "Copy 1 item(s)",
            Undo = _ => { log.Add("undo"); return Task.CompletedTask; },
            Redo = _ => { log.Add("redo"); return Task.CompletedTask; },
        });

        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
        await stack.UndoAsync();
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
        await stack.RedoAsync();
        Assert.Equal(["undo", "redo"], log);
        Assert.Equal("Copy 1 item(s)", stack.History.Single());
    }

    [Fact]
    public async Task UndoStack_UndoCancellationKeepsEntry()
    {
        var stack = new UndoStack();
        stack.Push(new UndoEntry
        {
            Description = "Copy 1 item(s)",
            Undo = _ => throw new OperationCanceledException(),
            Redo = _ => Task.CompletedTask,
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => stack.UndoAsync());

        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Equal("Copy 1 item(s)", stack.NextUndoDescription);
    }

    [Fact]
    public async Task UndoStack_RedoFailureKeepsEntry()
    {
        var stack = new UndoStack();
        stack.Push(new UndoEntry
        {
            Description = "Move 1 item(s)",
            Undo = _ => Task.CompletedTask,
            Redo = _ => throw new InvalidOperationException("redo failed"),
        });

        await stack.UndoAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stack.RedoAsync());

        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
        Assert.Equal("Move 1 item(s)", stack.NextRedoDescription);
    }

    [Fact]
    public void ResolveStartPath_UsesHomeLastAndCustom()
    {
        var backend = FakeExplorerBackend.Typical();
        var workspace = new ExplorerWorkspace(backend);
        workspace.ApplyUiSettings(new UiSettings { StartLocation = "home" });
        // HomePath is empty until Initialize; ResolveStartPath still returns HomePath/primary.
        Assert.Equal("", workspace.ResolveStartPath());

        workspace.ApplyUiSettings(new UiSettings { StartLocation = "custom", CustomPath = @"D:\Work" });
        Assert.Equal(@"D:\Work", workspace.ResolveStartPath());

        workspace.ApplyUiSettings(new UiSettings { StartLocation = "last", LastPath = @"D:\Last" });
        Assert.Equal(@"D:\Last", workspace.ResolveStartPath());
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "SimpleFile.App", "MainWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src-winui source root.");
    }
}
