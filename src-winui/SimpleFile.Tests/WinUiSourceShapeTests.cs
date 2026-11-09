using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class WinUiSourceShapeTests
{
    [Fact]
    public void ServiceJob_DoesNotKillOpenedDocumentsWithTheProcessTree()
    {
        var root = FindRepoRoot();
        var session = File.ReadAllText(Path.Combine(root, "SimpleFile.Core", "BackendSession.cs"));

        Assert.DoesNotContain("entireProcessTree: true", session);
        Assert.Contains("entireProcessTree: false", session);
        Assert.Equal(
            JobObject.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JobObject.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
            JobObject.DefaultLimitFlags);
        Assert.Equal(0u, JobObject.DefaultLimitFlags & JobObject.JOB_OBJECT_LIMIT_BREAKAWAY_OK);
    }

    [Fact]
    public void FilePanes_AcceptClicksAcrossPaneAndRowWhitespace()
    {
        var root = FindRepoRoot();
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));
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
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));
        var fileRowView = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileRowView.xaml.cs"));

        Assert.Contains("ColumnResizeTarget", mainWindow);
        Assert.Contains("Size Column to Fit", mainWindow);
        Assert.Contains("Size All Columns to Fit", mainWindow);
        Assert.Contains("OnColumnThumbDoubleTapped", mainWindow);
        Assert.Contains("ColumnLayoutHost.For(", fileRowView);
        Assert.Contains("ColumnLayoutHost.Attach(_workspace.PrimaryColumns, _workspace.SecondaryColumns)", mainWindow);
        Assert.Contains("ColumnsFor(_columnDragPane)", mainWindow);
        Assert.DoesNotContain("resizeId = column.Id == \"name\"", mainWindow);
        Assert.DoesNotContain(
            "? new GridLength(1, GridUnitType.Star)",
            fileRowView.Replace("\r\n", "\n"));
        Assert.Contains("Width = new GridLength(column.Width)", fileRowView);
    }

    [Fact]
    public void MainWindow_CoalescesWorkspaceSyncDuringDirectoryListings()
    {
        var root = FindRepoRoot();
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));

        Assert.Contains("WorkspaceSyncCoalesceMilliseconds = 75", mainWindow);
        Assert.Contains("DispatcherQueue.TryEnqueue(QueueWorkspaceSync)", mainWindow);
        Assert.Contains("WorkspaceHasListingInProgress(_workspace)", mainWindow);
        Assert.Contains("DispatcherQueue.CreateTimer()", mainWindow);
        Assert.Contains("timer.Interval = TimeSpan.FromMilliseconds(WorkspaceSyncCoalesceMilliseconds)", mainWindow);
        Assert.Contains("_workspaceSyncPending", mainWindow);
        Assert.Contains("StopWorkspaceSyncTimer();", mainWindow);
        Assert.DoesNotContain("DispatcherQueue.TryEnqueue(SyncFromWorkspace)", mainWindow);
    }

    [Fact]
    public void RemoteUi_LivesOnlyInTheRemotePlugin()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");

        Assert.False(File.Exists(Path.Combine(appRoot, "RemoteManagerWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(appRoot, "RemoteManagerWindow.xaml.cs")));
        Assert.False(File.Exists(Path.Combine(appRoot, "MainWindow.Remote.cs")));
        Assert.False(File.Exists(Path.Combine(root, "SimpleFile.Core", "RemoteManagerViewModel.cs")));

        var commandRouting = File.ReadAllText(Path.Combine(appRoot, "MainWindow.CommandRouting.cs"));
        Assert.DoesNotContain("ftp-sftp-manager", commandRouting);

        var sidebar = File.ReadAllText(Path.Combine(appRoot, "SidebarView.xaml"));
        Assert.DoesNotContain("Connections", sidebar);
        Assert.DoesNotContain("RemoteProfile", sidebar);

        Assert.DoesNotContain("SettingsRemoteManagerButton", File.ReadAllText(Path.Combine(appRoot, "SettingsWindow.xaml")));

        var transferOptions = File.ReadAllText(Path.Combine(appRoot, "TransferOptionsDialog.cs"));
        var showAsync = Regex.Match(transferOptions, @"public static async Task ShowAsync\(XamlRoot root, ISettingsBackend backend[^)]*\)");
        Assert.True(showAsync.Success, "TransferOptionsDialog.ShowAsync(XamlRoot, ISettingsBackend, ...) was not found");
        Assert.DoesNotContain("remote", showAsync.Value, StringComparison.OrdinalIgnoreCase);

        var protocol = File.ReadAllText(Path.Combine(root, "SimpleFile.Ipc", "Protocol.Generated.cs"));
        Assert.DoesNotContain("\"remote_", protocol);

        // No App, Core or Ipc source names the removed core remote surface.
        var sources = new[] { "SimpleFile.App", "SimpleFile.Core", "SimpleFile.Ipc" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            foreach (var name in new[] { "RemoteManagerWindow", "remote_list_profiles", "ShowRemoteManagerWindow" })
            {
                Assert.False(
                    text.Contains(name, StringComparison.Ordinal)
                        && !IsRemotePluginSmoke(path, name),
                    $"{Path.GetRelativePath(root, path)} still contains {name}");
            }
        }
    }

    // The UI smoke checks the plugin's own window type by its full name, SumaFile.Remote.Views.RemoteManagerWindow.
    private static bool IsRemotePluginSmoke(string path, string name) =>
        name == "RemoteManagerWindow"
        && Path.GetFileName(path) == "MainWindow.UiSmoke.Remote.cs";

    [Fact]
    public void MainWindow_DefersColumnEnrichmentUntilIdle()
    {
        var root = FindRepoRoot();
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));

        Assert.Contains("ColumnEnrichmentIdleDelayMilliseconds = 650", mainWindow);
        Assert.Contains("CreateColumnEnrichmentTimer", mainWindow);
        Assert.Contains("timer.Interval = TimeSpan.FromMilliseconds(ColumnEnrichmentIdleDelayMilliseconds)", mainWindow);
        Assert.Contains("QueueColumnEnrichmentTimer", mainWindow);
        Assert.Contains("CancelColumnEnrichment();", mainWindow);
        Assert.Contains("private void CancelColumnEnrichment()", mainWindow);
        Assert.DoesNotContain("_ = EnrichColumnsAsync(panes, needsSizes, token, cts);", mainWindow);
    }

    [Fact]
    public void MainWindow_RefreshesUnknownDrivesOnlyOnDemand()
    {
        var root = FindRepoRoot();
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));

        Assert.Contains("InitializeAsync(deferInitialNavigation: true)", mainWindow);
        Assert.Contains("QueueDeferredStartupNavigation(_workspace)", mainWindow);
        Assert.Contains("RunDeferredStartupNavigationAsync", mainWindow);
        Assert.Contains("RefreshDriveAsync(row.Path, quiet: true", mainWindow);
        Assert.DoesNotContain("QueueStartupDriveRefresh(_workspace)", mainWindow);
        Assert.DoesNotContain("RefreshStartupDrivesAsync", mainWindow);
        Assert.DoesNotContain("MainWindow.StartupDriveRefresh", mainWindow);
        Assert.DoesNotContain("Task.Delay(TimeSpan.FromMilliseconds(150))", mainWindow);
    }

    [Fact]
    public void MainWindow_WiresOmnibarStatusCenterAndTagNavigation()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var mainWindow = ReadMainWindowSource(appRoot);
        var mainWindowXaml = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        var toolbar = File.ReadAllText(Path.Combine(appRoot, "PrimaryToolbarView.xaml"));
        var sidebar = File.ReadAllText(Path.Combine(appRoot, "SidebarView.xaml"));

        Assert.Contains("x:Name=\"OmnibarBox\"", toolbar);
        Assert.Contains("OnOmnibarModeRequested", mainWindow);
        Assert.Contains("OmnibarIntentParser.Parse", mainWindow);
        Assert.Contains("FocusOmnibar(OmnibarMode.Search)", mainWindow);
        Assert.Contains("FocusOmnibar(OmnibarMode.Navigate)", mainWindow);

        Assert.Contains("x:Name=\"StatusCenterButton\"", mainWindowXaml);
        Assert.Contains("x:Name=\"StatusCenterTaskList\"", mainWindowXaml);
        Assert.Contains("x:Name=\"StatusCenterCancelTransferButton\"", mainWindowXaml);
        Assert.Contains("x:Name=\"StatusCenterClearCompletedButton\"", mainWindowXaml);
        Assert.Contains("StatusCenterFormatter.Format", mainWindow);
        Assert.Contains("StatusCenterRows(snapshot)", mainWindow);
        Assert.Contains("OnStatusCenterTransfersClick", mainWindow);
        Assert.Contains("OnStatusCenterHistoryClick", mainWindow);
        Assert.Contains("OnStatusCenterCancelTransferClick", mainWindow);
        Assert.Contains("OnStatusCenterClearCompletedClick", mainWindow);
        Assert.Contains("FirstCancellableTransfer()", mainWindow);

        Assert.Contains("x:Name=\"TagsSection\"", sidebar);
        Assert.Contains("x:Name=\"TagsCollapseButton\"", sidebar);
        Assert.DoesNotContain("ClearTagFilterButton", sidebar);
        Assert.Contains("OnToggleTags", mainWindow);
        Assert.Contains("_workspace.Settings.TagsCollapsed", mainWindow);
        Assert.Contains("x:Name=\"ShowTagsSwitch\"", File.ReadAllText(Path.Combine(root, "SimpleFile.App", "SettingsWindow.xaml")));
        Assert.Contains("OnTagClicked", mainWindow);
        Assert.Contains("_workspace.SetTagFilter(tag.Id)", mainWindow);
        Assert.Contains("clear-tag-filter", File.ReadAllText(Path.Combine(root, "SimpleFile.Core", "AppCommandCatalog.cs")));
    }

    [Fact]
    public void DetailsColumns_UsePaneOwnedHorizontalScrollBarAndRefreshAfterPaneResize()
    {
        var root = FindRepoRoot();
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));
        var paneView = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "PaneView.xaml"));

        Assert.Contains("x:Name=\"PartColumnHeaderViewport\"", paneView);
        Assert.Contains("x:Name=\"PartFileSurface\"", paneView);
        Assert.Contains("x:Name=\"PartColumnHeader\"", paneView);
        Assert.Contains("x:Name=\"PartDetailsFileList\"", paneView);
        Assert.Contains("x:Name=\"PartFileList\"", paneView);
        Assert.Contains("x:Name=\"PartDetailsHorizontalScrollBar\"", paneView);
        Assert.Contains("PrimaryPaneRoot.SizeChanged += OnPaneRootSizeChanged", mainWindow);
        Assert.Contains("SecondaryPaneRoot.SizeChanged += OnPaneRootSizeChanged", mainWindow);
        Assert.Contains("PrimaryPane.DetailsHorizontalScrollChanged += OnPrimaryDetailsHorizontalScrollChanged", mainWindow);
        Assert.Contains("SecondaryPane.DetailsHorizontalScrollChanged += OnSecondaryDetailsHorizontalScrollChanged", mainWindow);
        Assert.Contains("EffectiveFileListViewForPane", mainWindow);
        Assert.Contains("return _workspace.ViewFor(pane);", mainWindow);
        Assert.DoesNotContain("viewportWidth < stableDetailsWidth ? \"list\" : \"details\"", mainWindow);
        Assert.Contains("FileViewportWidthForPane", mainWindow);
        Assert.Contains("ApplyFileListSurfaceVisibility(PaneId.Primary", mainWindow);
        Assert.Contains("ApplyFileListSurfaceVisibility(PaneId.Secondary", mainWindow);
        Assert.Contains("QueuePaneSizeColumnRefresh", mainWindow);
        Assert.Contains("DispatcherQueue.CreateTimer()", mainWindow);
        Assert.Contains("ApplyFileListViewPresentation();", mainWindow);
        Assert.Contains("ApplyDetailsSurface(PaneId.Primary", mainWindow);
        Assert.Contains("ApplyDetailsSurface(PaneId.Secondary", mainWindow);
        Assert.Contains("detailsList.ApplyDetailsLayout", mainWindow);
        Assert.Contains("ApplyDetailsHorizontalOffset", mainWindow);
        Assert.Contains("FileListHorizontalScrollHost.Apply", mainWindow);
        Assert.Contains("PrimaryFileSurface.UpdateLayout()", mainWindow);
        Assert.Contains("SecondaryFileSurface.UpdateLayout()", mainWindow);
        Assert.Contains("PrimaryDetailsFileList.UpdateLayout()", mainWindow);
        Assert.Contains("SecondaryDetailsFileList.UpdateLayout()", mainWindow);
        Assert.Contains("ResetHiddenListHorizontalScroll(PrimaryFileList)", mainWindow);
        Assert.Contains("ResetHiddenListHorizontalScroll(SecondaryFileList)", mainWindow);
        Assert.Contains("scroller.ChangeView(0, null, null, disableAnimation: true)", mainWindow);
        Assert.Contains("ScrollViewer.SetHorizontalScrollMode", mainWindow);
        Assert.Contains("ScrollMode.Disabled", mainWindow);
        Assert.Contains("ScrollBar scrollBar", mainWindow);
        Assert.DoesNotContain("HookFileListColumnScroll", mainWindow);
        Assert.DoesNotContain("PrimaryDetailsScroller", mainWindow);
        Assert.DoesNotContain("SecondaryDetailsScroller", mainWindow);
        Assert.DoesNotContain("PrimaryColumnHeaderScroller", mainWindow);
        Assert.DoesNotContain("SecondaryColumnHeaderScroller", mainWindow);
    }

    [Fact]
    public void DetailsFileList_UsesVirtualizedListViewForSelectionAndRows()
    {
        var root = FindRepoRoot();
        var details = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "DetailsFileListView.cs"));
        var fileRowView = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileRowView.xaml.cs"));
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));

        Assert.Contains("public sealed class DetailsFileListView : UserControl", details);
        Assert.Contains("private readonly ListView _list", details);
        Assert.Contains("ContainerContentChanging", details);
        Assert.Contains("ItemsStackPanel", details);
        Assert.Contains("public void ApplyDetailsLayout", details);
        Assert.Contains("public void ApplyDetailsPresentation", fileRowView);
        Assert.Contains("_explicitColumns", fileRowView);
        Assert.Contains("_explicitHorizontalOffset", fileRowView);
        Assert.Contains("RowFromPoint(Point point)", details);
        Assert.Contains("SelectedRows => _selectedRows.ToArray()", details);
        Assert.Contains("RowsDragStarting", details);
        Assert.DoesNotContain("private readonly StackPanel _rowsHost", details);
        Assert.DoesNotContain("_rowsHost.Children.Clear()", details);
        Assert.DoesNotContain("new FileRowView { Row = row }", details);
        Assert.Contains("PrimaryDetailsFileList.ItemsSource = PrimaryFiles", mainWindow);
        Assert.Contains("SecondaryDetailsFileList.ItemsSource = SecondaryFiles", mainWindow);
        Assert.Contains("SelectedRowsForPane(PaneId pane)", mainWindow);
        Assert.Contains("details.Visibility == Visibility.Visible", mainWindow);
        Assert.Contains("OpenSelectedFile(PaneId pane)", mainWindow);
        Assert.Contains("HoveredFileRow(DragEventArgs e, PaneId pane)", mainWindow);
        Assert.Contains("details.RowFromPoint(e.GetPosition(details))", mainWindow);
    }

    [Fact]
    public void TileLayout_StacksLargeIconsAndKeepsContainerWidthDynamic()
    {
        var root = FindRepoRoot();
        var metrics = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileTileLayoutMetrics.cs"));
        var fileRowView = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileRowView.xaml.cs"));
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));
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
    public void MainWindow_SplitsLargeShellSectionsIntoUserControls()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var mainWindowXaml = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        var mainWindowCode = ReadMainWindowSource(appRoot);
        var bridge = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Controls.cs"));

        foreach (var control in new[]
        {
            "SidebarView",
            "PrimaryToolbarView",
            "PaneView",
            "PreviewPaneView",
        })
        {
            Assert.Contains($"<local:{control}", mainWindowXaml);
            Assert.Contains(
                $"x:Class=\"SimpleFile.App.{control}\"",
                File.ReadAllText(Path.Combine(appRoot, $"{control}.xaml")));
        }

        Assert.Contains("AttachControlEvents();", mainWindowCode);
        Assert.Contains("private Grid PrimaryPaneRoot => PrimaryPane.Root;", bridge);
        Assert.Contains("PrimaryToolbarPanel.ToggleSidebar += OnToggleSidebar;", bridge);
        Assert.DoesNotContain("x:Name=\"QuickAccessList\"", mainWindowXaml);
        Assert.DoesNotContain("x:Name=\"PreviewTitle\"", mainWindowXaml);
    }

    [Fact]
    public void OpeningFiles_FlushesPendingIconSizePersistence()
    {
        var root = FindRepoRoot();
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));
        var commands = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.Commands.cs"));

        Assert.Contains("await SaveViewIconSizeNowAsync();", mainWindow);
        Assert.Contains("SaveViewIconSizeAsync(token, delay: false)", commands);
        Assert.Contains("_viewIconSizeSaveGate.WaitAsync()", commands);
        Assert.Contains("Volatile.Read(ref _viewIconSizeSaveToken)", commands);
    }

    [Fact]
    public void WorkspaceProfiles_AreDiscoverableFromToolbarViewAndOverflow()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var coreRoot = Path.Combine(root, "SimpleFile.Core");
        var toolbar = File.ReadAllText(Path.Combine(appRoot, "PrimaryToolbarView.xaml"));
        var commands = ReadMainWindowSource(appRoot);
        var workspace = ReadExplorerWorkspaceSource(coreRoot);
        var profiles = File.ReadAllText(Path.Combine(coreRoot, "WorkspaceProfile.cs"));

        Assert.Contains("WorkspaceProfileButton", toolbar);
        Assert.Contains("WorkspaceProfilesHost", toolbar);
        // 1.0.4: profiles live only behind the Profiles button; the View flyout no longer repeats them.
        Assert.DoesNotContain("ViewProfilesHost", toolbar);
        Assert.DoesNotContain("OnViewSaveProfileClicked", toolbar);
        Assert.Contains("OnWorkspaceProfilesFlyoutOpening", toolbar);
        Assert.Contains("WorkspaceProfileManageButton", toolbar);
        Assert.Contains("RefreshWorkspaceProfilesHostAsync", commands);
        Assert.DoesNotContain("RefreshViewProfilesHostAsync", commands);
        Assert.Contains("AppendWorkspaceProfileOverflowMenuAsync", commands);
        Assert.Contains("profile:save", commands);
        Assert.Contains("ApplyWorkspaceProfileAsync", commands);
        Assert.Contains("SaveWorkspaceProfileAsync", workspace);
        Assert.Contains("DuplicateWorkspaceProfileAsync", workspace);
        Assert.Contains("ExportWorkspaceProfileAsync", workspace);
        Assert.Contains("workspace-profiles", profiles);
        Assert.Contains("builtin-transfer", profiles);
        Assert.DoesNotContain("SavedLayoutsHost", toolbar);
        Assert.DoesNotContain("ViewSaveLayout", toolbar);
        Assert.DoesNotContain("AppendSavedLayoutOverflowMenuAsync", commands);
        Assert.DoesNotContain("RefreshSavedLayoutsHostAsync", commands);
        Assert.DoesNotContain("layout:save", commands);
    }

    [Fact]
    public void PrimaryToolbar_NewActionIsTemplateMenu()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var coreRoot = Path.Combine(root, "SimpleFile.Core");
        var toolbar = File.ReadAllText(Path.Combine(appRoot, "PrimaryToolbarView.xaml"));
        var toolbarCode = File.ReadAllText(Path.Combine(appRoot, "PrimaryToolbarView.xaml.cs"));
        var mainWindow = ReadMainWindowSource(appRoot);
        var commandRouting = File.ReadAllText(Path.Combine(appRoot, "MainWindow.CommandRouting.cs"));
        var overflow = File.ReadAllText(Path.Combine(coreRoot, "ContextMenuBuilder.cs"));

        Assert.Contains("PrimaryNewButton", toolbar);
        Assert.Contains("Tag=\"new:folder\"", toolbar);
        Assert.Contains("Tag=\"new:text\"", toolbar);
        Assert.Contains("Tag=\"new:empty\"", toolbar);
        Assert.Contains("Tag=\"new:shortcut\"", toolbar);
        Assert.DoesNotContain("Tag=\"new:markdown\"", toolbar);
        Assert.DoesNotContain("Tag=\"new:json\"", toolbar);
        Assert.DoesNotContain("PrimaryNewFileButton", toolbar);
        Assert.DoesNotContain("PrimaryNewFolderButton", toolbar);
        Assert.Contains("PrimaryNewItemRequested", toolbarCode);
        Assert.Contains("RunNewItemCommandAsync", mainWindow);
        Assert.Contains("NewItemTemplate.TextFile", commandRouting);
        Assert.Contains("PromptAndCreateShortcut", commandRouting);
        Assert.Contains("overflow-new", overflow);
    }

    [Fact]
    public void FolderViewSettings_AreDiscoverableFromViewOptions()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var coreRoot = Path.Combine(root, "SimpleFile.Core");
        var toolbar = File.ReadAllText(Path.Combine(appRoot, "PrimaryToolbarView.xaml"));
        var commands = ReadMainWindowSource(appRoot);
        var workspace = ReadExplorerWorkspaceSource(coreRoot);
        var settingsStore = File.ReadAllText(Path.Combine(coreRoot, "WorkspaceSettingsStore.cs"));
        var folderViews = File.ReadAllText(Path.Combine(coreRoot, "FolderViewSettings.cs"));

        // 1.0.4: one "Remember this view for" dropdown replaces the three "Use ..." buttons.
        Assert.Contains("ViewRememberScopeBox", toolbar);
        Assert.Contains("Header=\"Remember this view for\"", toolbar);
        Assert.Contains("Content=\"Everywhere\"", toolbar);
        Assert.Contains("Content=\"This folder\"", toolbar);
        Assert.Contains("Content=\"This folder and subfolders\"", toolbar);
        Assert.Contains("Click=\"OnViewRememberClicked\"", toolbar);
        Assert.DoesNotContain("ViewUseGloballyButton", toolbar);
        Assert.DoesNotContain("Save view defaults", toolbar);
        Assert.Contains("Current view options", toolbar);
        Assert.Contains("SaveFolderViewSettingsFromFlyoutAsync", commands);
        Assert.Contains("ActiveSearchQuery", commands);
        Assert.DoesNotContain("SearchTextBoxFor", commands);
        Assert.Contains("EffectiveFolderViewRuleFor", workspace);
        Assert.Contains("FolderViewSettingsDocument.SettingsKey", settingsStore);
        Assert.Contains("StableKeyFromPath", folderViews);
    }

    [Fact]
    public void ShortcutEditor_UsesRuntimeShortcutRegistry()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var coreRoot = Path.Combine(root, "SimpleFile.Core");
        var settingsXaml = File.ReadAllText(Path.Combine(appRoot, "SettingsWindow.xaml"));
        var settingsCode = ReadSettingsWindowSource(appRoot);
        var dialogService = File.ReadAllText(Path.Combine(appRoot, "FileOperationDialogService.cs"));
        var mainWindowXaml = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        var mainWindowCode = ReadMainWindowSource(appRoot);
        var shortcutBinder = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Shortcuts.cs"));
        var settingsStore = File.ReadAllText(Path.Combine(coreRoot, "WorkspaceSettingsStore.cs"));
        var shortcutMap = File.ReadAllText(Path.Combine(coreRoot, "KeyboardShortcutMap.cs"));

        Assert.Contains("ShortcutRecorderBox", settingsXaml);
        Assert.Contains("ShortcutImportButton", settingsXaml);
        Assert.Contains("ShortcutExportButton", settingsXaml);
        Assert.Contains("OnShortcutRecorderKeyDown", settingsCode);
        Assert.Contains("KeyboardShortcutExportDocument.FromJson", settingsCode);
        Assert.Contains("ApplyKeyboardShortcuts();", mainWindowCode);
        Assert.Contains("KeyboardShortcutMap.EffectiveShortcuts", shortcutBinder);
        Assert.Contains("KeyboardShortcutMap.SettingsKey", settingsStore);
        Assert.Contains("TryGetReservedWindowsWarning", shortcutMap);
        Assert.Contains("<Window", settingsXaml);
        Assert.Contains("x:Class=\"SimpleFile.App.SettingsWindow\"", settingsXaml);
        Assert.Contains("<NavigationView", settingsXaml);
        Assert.Contains("SettingsNavigation", settingsCode);
        Assert.Contains("SecondaryWindowChrome.Apply(this, \"Settings\"", settingsCode);
        Assert.Contains("new SettingsWindow", dialogService);
        Assert.Contains("LoadSettingsAsync(fileOps, workspace.Settings", dialogService);
        Assert.Contains("SettingsWindow.SettingsState.cs", Directory.EnumerateFiles(appRoot, "SettingsWindow*.cs").Select(Path.GetFileName));
        Assert.Contains("SettingsWindow.Tools.cs", Directory.EnumerateFiles(appRoot, "SettingsWindow*.cs").Select(Path.GetFileName));
        Assert.DoesNotContain("GetSettingOrDefaultAsync", settingsCode);
        Assert.DoesNotContain("ContentDialogMaxWidth", settingsXaml);
        Assert.DoesNotContain("Width=\"760\"", settingsXaml);
        Assert.Contains("HorizontalScrollMode=\"Disabled\"", settingsXaml);
        Assert.Contains("<ColumnDefinition Width=\"300\" />", settingsXaml);
        Assert.DoesNotContain("Remapping is not available yet", settingsXaml);
        Assert.DoesNotContain("<Grid.KeyboardAccelerators>", mainWindowXaml);
    }

    [Fact]
    public void GitIntegration_UsesDockableWorkbenchAndCompleteDisableSwitch()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var coreRoot = Path.Combine(root, "SimpleFile.Core");
        var ipcRoot = Path.Combine(root, "SimpleFile.Ipc");
        var mainWindowXaml = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        var mainWindowCode = ReadMainWindowSource(appRoot);
        var workbenchXaml = File.ReadAllText(Path.Combine(appRoot, "GitWorkbenchView.xaml"));
        var workbenchCode = File.ReadAllText(Path.Combine(appRoot, "GitWorkbenchView.xaml.cs"));
        var workbenchModel = File.ReadAllText(Path.Combine(appRoot, "GitWorkbenchViewModel.cs"));
        var workbenchWindow = File.ReadAllText(Path.Combine(appRoot, "GitWorkbenchWindow.xaml.cs"));
        var settingsXaml = File.ReadAllText(Path.Combine(appRoot, "SettingsWindow.xaml"));
        var workspace = ReadExplorerWorkspaceSource(coreRoot);
        var ipc = File.ReadAllText(Path.Combine(ipcRoot, "ISimpleFileIpc.cs"));

        Assert.Contains("GitPanel", mainWindowXaml);
        Assert.Contains("GitToggleButton", mainWindowXaml);
        Assert.Contains("GitWorkbench", mainWindowXaml);
        Assert.Contains("GitWorkbenchViewModel", mainWindowCode);
        Assert.Contains("GitWorkbenchWindow", mainWindowCode);
        Assert.Contains("DetachGitWorkbenchAsync", mainWindowCode);
        Assert.Contains("DockGitWorkbenchAsync", mainWindowCode);
        Assert.Contains("PreviewSelectedGitDiffAsync", mainWindowCode);
        Assert.Contains("CloseGitWorkbenchWindow", mainWindowCode);
        Assert.Contains("SetHostMode(detached: true)", mainWindowCode);
        Assert.Contains("SetHostMode(detached: false)", mainWindowCode);
        Assert.Contains("SecondaryWindowChrome.Apply(this, \"Git Workbench\", 1180, 740)", workbenchWindow);
        Assert.Contains("presenter.IsResizable = true;", File.ReadAllText(Path.Combine(appRoot, "SecondaryWindowChrome.cs")));
        Assert.Contains("DiffPreviewColumn", workbenchXaml);
        Assert.Contains("Resize Git diff preview", workbenchXaml);
        Assert.Contains("HostActionText", workbenchXaml);
        Assert.Contains("SelectedChanges", workbenchCode);
        Assert.Contains("CanDiff", workbenchModel);
        Assert.Contains("SetDiffLoading", workbenchModel);
        Assert.DoesNotContain("ShowGitDiffDialogAsync", mainWindowCode);
        Assert.Contains("OnGitStageRequested", mainWindowCode);
        Assert.Contains("GitStageSelectedAsync", mainWindowCode);
        Assert.Contains("GetGitRepositoryStatusAsync", mainWindowCode);
        Assert.Contains("EnsureGitIntegrationEnabled", mainWindowCode);
        Assert.Contains("Git integration is disabled in Settings.", mainWindowCode);
        Assert.Contains("command.Group, \"Git\"", mainWindowCode);
        Assert.Contains("ClearGitStatuses()", workspace);
        Assert.Contains("EnableGitSwitch", settingsXaml);
        Assert.Contains("Task<GitCommandResult> GitStagePathsAsync", ipc);
        Assert.Contains("Task<GitCommandResult> GitPullAsync", ipc);
    }

    [Fact]
    public void ThemeChrome_FollowsWindowsDefaultAndAvoidsStaticSfBrushes()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var settingsXaml = File.ReadAllText(Path.Combine(appRoot, "SettingsWindow.xaml"));
        var mainWindowCode = ReadMainWindowSource(appRoot);
        var commands = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Commands.cs"));
        var fileRows = File.ReadAllText(Path.Combine(appRoot, "FileRowView.xaml.cs"));
        var preview = ReadPreviewPresenterSource(appRoot);
        var themeResources = File.ReadAllText(Path.Combine(appRoot, "ThemeResourceLookup.cs"));

        Assert.Contains("<ComboBoxItem Content=\"Windows default\" Tag=\"System\" />", settingsXaml);
        Assert.Contains("<ComboBoxItem Content=\"Dark\" Tag=\"Dark\" />", settingsXaml);
        Assert.Contains("<ComboBoxItem Content=\"Light\" Tag=\"Light\" />", settingsXaml);

        Assert.Contains("\"light\" => ElementTheme.Light", commands);
        Assert.Contains("RootGrid.ActualThemeChanged += OnRootActualThemeChanged;", mainWindowCode);
        Assert.Contains("private ElementTheme? _appliedTheme", mainWindowCode);
        Assert.Contains("if (_appliedTheme == next && RootGrid.RequestedTheme == next)", commands);
        Assert.Contains("RefreshGeneratedThemeResources", commands);
        Assert.Contains("ThemeResourceLookup.Brush(RootGrid, key)", mainWindowCode);
        Assert.Contains("ThemeResourceLookup.Brush(this, key)", fileRows);
        Assert.Contains("ThemeResourceLookup.Brush(_metadataRows, key)", preview);
        Assert.Contains("appResources.ThemeDictionaries.TryGetValue(themeKey", themeResources);

        var staleBrushReferences = Directory
            .EnumerateFiles(appRoot, "*.xaml")
            .SelectMany(file => File.ReadLines(file).Select((line, index) => new { file, line, index }))
            .Where(item => Regex.IsMatch(item.line, "\\{StaticResource Sf[A-Za-z]+Brush\\}"))
            .Select(item => $"{Path.GetFileName(item.file)}:{item.index + 1}: {item.line.Trim()}")
            .ToList();
        Assert.Empty(staleBrushReferences);
    }

    [Fact]
    public void CreateArchiveDialog_UsesCapabilitiesAndDoesNotOfferRarCreation()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var dialogXaml = File.ReadAllText(Path.Combine(appRoot, "CreateArchiveDialog.xaml"));
        var dialogCode = File.ReadAllText(Path.Combine(appRoot, "CreateArchiveDialog.xaml.cs"));
        var dialogService = File.ReadAllText(Path.Combine(appRoot, "FileOperationDialogService.cs"));

        Assert.DoesNotContain("RAR (.rar)", dialogXaml);
        Assert.Contains("SetArchiveFormats", dialogCode);
        Assert.Contains("capabilities.Where(format => format.CanCreate)", dialogCode);
        Assert.Contains("GetArchiveCapabilitiesAsync", dialogService);
        // 7z is offered only when the service reports a plugin engine that can create it.
        Assert.DoesNotContain("Format = \"7z\"", dialogCode);
    }

    [Fact]
    public void SettingsArchiveSupport_IsBuiltFromCapabilities()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var settingsXaml = File.ReadAllText(Path.Combine(appRoot, "SettingsWindow.xaml"));
        var tools = File.ReadAllText(Path.Combine(appRoot, "SettingsWindow.Tools.cs"));

        Assert.DoesNotContain("7z, and RAR extraction are handled by SumaFile", settingsXaml);
        Assert.Contains("ZIP, TAR and TAR.GZ are built in. RAR archives can be listed and extracted but not created.", settingsXaml);
        Assert.Contains("x:Name=\"ArchivePluginFormatsText\"", settingsXaml);
        Assert.Contains("GetArchiveCapabilitiesAsync", tools);
        Assert.Contains("ArchivePluginFormatsText.Text", tools);
        Assert.Contains("archives: {", tools);
        Assert.Contains("\"unavailable\"", tools);
    }

    [Fact]
    public void MainWindow_RefreshesArchiveCapabilitiesOnPluginChangesAndReconnect()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var plugins = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Plugins.cs"));
        var transfers = File.ReadAllText(Path.Combine(appRoot, "MainWindow.ArchiveTransfers.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));

        var changedStart = plugins.IndexOf("private void OnPluginsChanged(", StringComparison.Ordinal);
        Assert.True(changedStart >= 0, "OnPluginsChanged not found");
        var changedEnd = plugins.IndexOf("\n    private ", changedStart + 1, StringComparison.Ordinal);
        var changedBody = changedEnd < 0 ? plugins[changedStart..] : plugins[changedStart..changedEnd];
        Assert.Contains("RefreshArchiveCapabilitiesAsync()", changedBody);

        Assert.Contains("private async Task RefreshArchiveCapabilitiesAsync()", transfers);
        Assert.Contains("GetArchiveCapabilitiesAsync(", transfers);
        Assert.Contains("ArchivePaths.SetEngineFormats(", transfers);
        Assert.Contains("RefreshArchivePanesAsync(", transfers);
        Assert.Contains("AppendPluginLog(", transfers);

        // At startup, once the workspace has loaded.
        var connectStart = mainWindow.IndexOf("private async Task ConnectAsync(", StringComparison.Ordinal);
        Assert.True(connectStart >= 0, "ConnectAsync not found");
        var connectEnd = mainWindow.IndexOf("\n    private ", connectStart + 1, StringComparison.Ordinal);
        var connect = connectEnd < 0 ? mainWindow[connectStart..] : mainWindow[connectStart..connectEnd];
        var initialized = connect.IndexOf("OnPluginWorkspaceInitialized();", StringComparison.Ordinal);
        Assert.True(initialized >= 0, "OnPluginWorkspaceInitialized call not found");
        Assert.True(
            connect.IndexOf("RefreshArchiveCapabilitiesAsync()", initialized, StringComparison.Ordinal) > initialized,
            "the startup refresh must follow OnPluginWorkspaceInitialized");

        // On reconnect, right after the IPC swap and before the pane refresh.
        var start = mainWindow.IndexOf("private async Task ReconnectBackendAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ReconnectBackendAsync not found");
        var end = mainWindow.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end < 0 ? mainWindow[start..] : mainWindow[start..end];
        var replaceIpc = body.IndexOf("ReplaceIpc(", StringComparison.Ordinal);
        var refresh = body.IndexOf("RefreshArchiveCapabilitiesAsync()", StringComparison.Ordinal);
        var panes = body.IndexOf("RefreshAffectedPanesAsync(", StringComparison.Ordinal);
        Assert.True(replaceIpc >= 0 && replaceIpc < refresh, "the capability refresh must follow ReplaceIpc");
        Assert.True(refresh < panes, "the capability refresh must precede RefreshAffectedPanesAsync");
    }

    [Fact]
    public void PluginFormatArchiveOperations_UseTransfersRows()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var dialogs = File.ReadAllText(Path.Combine(appRoot, "FileOperationDialogService.cs"));
        var transfer = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Transfer.cs"));
        var archiveTransfers = File.ReadAllText(Path.Combine(appRoot, "MainWindow.ArchiveTransfers.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));

        static string Body(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{signature} not found");
            var end = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
            var publicEnd = source.IndexOf("\n    public ", start + 1, StringComparison.Ordinal);
            if (publicEnd >= 0 && (end < 0 || publicEnd < end))
            {
                end = publicEnd;
            }

            return end < 0 ? source[start..] : source[start..end];
        }

        static void AssertEngineRouteBeforeModal(string body, string trackedCall, string modalCall, string where)
        {
            var engineCheck = body.IndexOf("ArchivePaths.IsEngineArchive(", StringComparison.Ordinal);
            var enqueue = body.IndexOf("nqueueArchiveTransfer(", StringComparison.Ordinal);
            var tracked = body.IndexOf(trackedCall, StringComparison.Ordinal);
            var modal = body.IndexOf(modalCall, StringComparison.Ordinal);
            Assert.True(engineCheck >= 0, $"{where} does not check ArchivePaths.IsEngineArchive");
            Assert.True(enqueue > engineCheck, $"{where} does not enqueue a Transfers row for engine formats");
            Assert.True(tracked > engineCheck, $"{where} does not use {trackedCall}");
            Assert.True(modal >= 0, $"{where} lost the modal path for built-in formats");
        }

        // The dialog service takes the delegate and routes engine formats through it.
        Assert.Contains("Func<ArchiveTransferRequest, TransferOperationViewModel?> enqueueArchiveTransfer", dialogs);
        AssertEngineRouteBeforeModal(
            Body(dialogs, "public async Task CreateArchiveAsync("),
            "CreateArchiveTrackedAsync(",
            "fileOps.CreateArchiveAsync(",
            "CreateArchiveAsync");
        AssertEngineRouteBeforeModal(
            Body(dialogs, "private async Task ShowExtractDialogAsync("),
            "ExtractArchiveTrackedAsync(",
            "fileOps.ExtractArchiveAsync(",
            "ShowExtractDialogAsync");
        Assert.Contains("$\"Creating {", dialogs);
        Assert.Contains("$\"Extracting {", dialogs);

        // The context-menu extract does the same.
        AssertEngineRouteBeforeModal(
            Body(transfer, "private async Task ExtractSelectedArchiveAsync("),
            "ExtractArchiveTrackedAsync(",
            "fileOps.ExtractArchiveAsync(",
            "ExtractSelectedArchiveAsync");

        // The row: an external Transfers row with byte progress, the operation id and a pane refresh on success.
        Assert.Contains("internal sealed record ArchiveTransferRequest(", archiveTransfers);
        var enqueueBody = Body(archiveTransfers, "internal TransferOperationViewModel? EnqueueArchiveTransfer(");
        Assert.Contains(".EnqueueExternal(", enqueueBody);
        Assert.Contains("new Progress<ProgressUpdate>(", enqueueBody);
        Assert.Contains(".ApplyProgress", enqueueBody);
        Assert.Contains(".SetOperationId(", enqueueBody);
        Assert.Contains("RefreshAffectedPanesAsync(", enqueueBody);

        // MainWindow passes the delegate to the dialog service.
        var ctor = mainWindow.IndexOf("new FileOperationDialogService(", StringComparison.Ordinal);
        Assert.True(ctor >= 0, "FileOperationDialogService construction not found");
        var ctorEnd = mainWindow.IndexOf(");", ctor, StringComparison.Ordinal);
        Assert.Contains("EnqueueArchiveTransfer", mainWindow[ctor..ctorEnd]);
    }

    [Fact]
    public void PreviewPane_UsesPathBackedPdfAndMediaControls()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var coreRoot = Path.Combine(root, "SimpleFile.Core");
        var pane = File.ReadAllText(Path.Combine(appRoot, "PreviewPaneView.xaml"));
        var presenter = ReadPreviewPresenterSource(appRoot);
        var commands = ReadMainWindowSource(appRoot);
        var mainWindow = commands;
        var thumbnailHost = File.ReadAllText(Path.Combine(appRoot, "FileListThumbnailHost.cs"));
        var inspectionDetails = File.ReadAllText(Path.Combine(coreRoot, "InspectionDetails.cs"));
        var backendPreview = File.ReadAllText(Path.Combine(root, "..", "crates", "simplefile-core", "src", "preview.rs"));

        foreach (var file in new[]
        {
            "PreviewPresenter.Rendering.cs",
            "PreviewPresenter.Image.cs",
            "PreviewPresenter.Media.cs",
            "PreviewPresenter.Video.cs",
            "PreviewPresenter.Html.cs",
            "PreviewPresenter.Metadata.cs",
            "PreviewPresenter.Comparison.cs",
        })
        {
            Assert.True(File.Exists(Path.Combine(appRoot, file)), $"{file} should keep PreviewPresenter split by concern.");
        }

        Assert.Contains("<WebView2", pane);
        Assert.Contains("<MediaPlayerElement", pane);
        Assert.Contains("PreviewRenderHtmlCheckBox", pane);
        Assert.Contains("PreviewVideoPlaybackCheckBox", pane);
        Assert.Contains("PreviewVideoFramePresetOptions", pane);
        Assert.Contains("TryRenderTextPreview", presenter);
        Assert.Contains("TryRenderImagePreviewAsync", presenter);
        Assert.Contains("TryRenderPdfPreview", presenter);
        Assert.Contains("TryRenderMediaPreview", presenter);
        Assert.Contains("TryRenderHtmlPreview", presenter);
        Assert.Contains("EnsureCoreWebView2Async", presenter);
        Assert.Contains("TryRenderVideoPosterPreview", presenter);
        Assert.Contains("PreviewCapabilities.ShouldRenderHtml", presenter);
        Assert.Contains("PreviewCapabilities.ShouldPreviewVideo", presenter);
        Assert.Contains("VideoThumbnailExtractor", presenter);
        Assert.Contains("SetVideoFramePreference", presenter);
        Assert.Contains("MetadataSections", presenter);
        Assert.Contains("VideoThumbnailExtractor", thumbnailHost);
        Assert.Contains("PhotoFolder.IsImage", thumbnailHost);
        Assert.Contains("MediaFolder.IsVideo", thumbnailHost);
        Assert.Contains("MaxInFlightThumbnails", thumbnailHost);
        Assert.Contains("InFlight.Count >= MaxInFlightThumbnails", thumbnailHost);
        Assert.Contains("MediaFolder.IsMediaFolder", mainWindow);
        Assert.Contains("TryCreatePathBackedPreview", commands);
        Assert.Contains("InspectionDetails", presenter);
        Assert.Contains("InspectionDetails", commands);
        Assert.Contains("FolderMetricRows", inspectionDetails);
        Assert.Contains("ChecksumsText", inspectionDetails);
        Assert.DoesNotContain("const PDF_MAX", backendPreview);

        var renderer = File.ReadAllText(Path.Combine(coreRoot, "PreviewHtmlRenderer.cs"));
        var capabilities = File.ReadAllText(Path.Combine(coreRoot, "PreviewCapabilities.cs"));
        var pathSupport = File.ReadAllText(Path.Combine(coreRoot, "PreviewPathSupport.cs"));

        Assert.Contains("Content-Security-Policy", renderer);
        Assert.Contains("MarkdownPipelineBuilder", renderer);
        Assert.Contains("UseAdvancedExtensions", renderer);
        Assert.Contains("DisableHtml", renderer);
        Assert.Contains("RenderMarkdown", renderer);
        Assert.Contains("SanitizeHtmlFragment", renderer);
        Assert.Contains("RenderableHtml", capabilities);
        Assert.Contains("PreviewVideoPlaybackEnabled", capabilities);
        Assert.Contains("IsImagePreviewType", pathSupport);
    }

    [Fact]
    public void ManualParityRows_HaveFocusedGuardsForPreviewPropertiesPathAndCancellation()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var testsRoot = Path.Combine(root, "SimpleFile.Tests");
        var pathEdit = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Path.cs"));
        var commands = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Commands.cs"));
        var commandRouting = File.ReadAllText(Path.Combine(appRoot, "MainWindow.CommandRouting.cs"));
        var shortcuts = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Shortcuts.cs"));
        var inspectionDialogs = File.ReadAllText(Path.Combine(appRoot, "MainWindow.InspectionDialogs.cs"));
        var previewPane = File.ReadAllText(Path.Combine(appRoot, "PreviewPaneView.xaml"));
        var previewPresenter = ReadPreviewPresenterSource(appRoot);
        var fileOperationTests = File.ReadAllText(Path.Combine(testsRoot, "FileOperationServiceTests.cs"));
        var viewModelTests = File.ReadAllText(Path.Combine(testsRoot, "ViewModelCutoverTests.cs"));
        var pathTests = File.ReadAllText(Path.Combine(testsRoot, "PathCompletionTests.cs"));
        var inspectionTests = File.ReadAllText(Path.Combine(testsRoot, "InspectionDetailsTests.cs"));

        Assert.Contains("HandlePathKey", pathEdit);
        Assert.Contains("VirtualKey.Escape", pathEdit);
        Assert.Contains("VirtualKey.Enter", pathEdit);
        Assert.Contains("NavigatePaneAsync(pane, path)", pathEdit);
        Assert.Contains("Suggest_DeduplicatesAndCapsPathEditCandidates", pathTests);

        Assert.Contains("ShowPropertiesAsync", inspectionDialogs);
        Assert.Contains("GetEntryInfoAsync", inspectionDialogs);
        Assert.Contains("GetFileMetadataAsync", inspectionDialogs);
        Assert.Contains("ComputeChecksumAsync", inspectionDialogs);
        Assert.Contains("PropertiesRows_LabelsRecycleBinSymlinkAsOriginalLocation", inspectionTests);

        Assert.Contains("<WebView2", previewPane);
        Assert.Contains("<MediaPlayerElement", previewPane);
        Assert.Contains("PreviewRenderHtmlCheckBox", previewPane);
        Assert.Contains("PreviewVideoPlaybackCheckBox", previewPane);
        Assert.Contains("TryRenderTextPreview", previewPresenter);
        Assert.Contains("TryRenderImagePreviewAsync", previewPresenter);
        Assert.Contains("TryRenderPdfPreview", previewPresenter);
        Assert.Contains("TryRenderMediaPreview", previewPresenter);
        Assert.Contains("TryRenderHtmlPreview", previewPresenter);
        Assert.Contains("MetadataSections", previewPresenter);
        Assert.Contains("MetadataSections_GroupsAudioForPreviewPane", inspectionTests);
        Assert.Contains("MetadataSections_GroupsDataAndArchiveDetails", inspectionTests);
        Assert.Contains("MetadataSections_GroupsDocumentCountsAndTruncation", inspectionTests);
        Assert.Contains("OnTogglePreview", commands);
        Assert.Contains("TogglePreviewPaneAsync", shortcuts);
        Assert.Contains("[\"preview\"]", commandRouting);
        Assert.Contains("ShowQuickLookAsync", inspectionDialogs);
        Assert.Contains("[\"quick-look\"]", commandRouting);

        Assert.Contains("CancelOperationAsync_CallsNamedIpcCancel", fileOperationTests);
        Assert.Contains("CalculateFolderSizeAsync_CancellationSendsBackendCancel", fileOperationTests);
        Assert.Contains("SearchViewModel_CancelActiveAsyncCancelsBackendSearch", viewModelTests);
        Assert.Contains("TransferViewModel_CancelAsyncAwaitsBackendCancelCompletion", viewModelTests);
    }

    [Fact]
    public void GodObjectRefactors_KeepLargeWorkflowsBehindFocusedSplitPoints()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var coreRoot = Path.Combine(root, "SimpleFile.Core");
        var repoRoot = Path.GetFullPath(Path.Combine(root, ".."));
        var commands = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Commands.cs"));
        var commandRouting = File.ReadAllText(Path.Combine(appRoot, "MainWindow.CommandRouting.cs"));
        var dialogService = File.ReadAllText(Path.Combine(appRoot, "FileOperationDialogService.cs"));
        var scanDialogHost = File.ReadAllText(Path.Combine(appRoot, "FileOperationDialogService.Scans.cs"));
        var workspace = File.ReadAllText(Path.Combine(coreRoot, "ExplorerWorkspace.cs"));
        var profileService = File.ReadAllText(Path.Combine(coreRoot, "WorkspaceProfileService.cs"));
        var legacyLayoutDocument = File.ReadAllText(Path.Combine(coreRoot, "SavedWorkspaceLayout.cs"));
        var facades = File.ReadAllText(Path.Combine(coreRoot, "FileOperationFacades.cs"));
        var fileOperationService = File.ReadAllText(Path.Combine(coreRoot, "FileOperationService.cs"));
        var rustFileOps = File.ReadAllText(Path.Combine(repoRoot, "crates", "simplefile-core", "src", "file_ops.rs"));
        var rustProgress = File.ReadAllText(Path.Combine(repoRoot, "crates", "simplefile-service", "src", "progress.rs"));

        Assert.Contains("CommandAliasCatalog.Normalize", commandRouting);
        Assert.Contains("CreateAppCommandHandlers", commandRouting);
        Assert.DoesNotContain("private async Task RunAppCommandAsync", commands);

        Assert.Contains("interface IScanDialog", scanDialogHost);
        Assert.Contains("RunScanDialogAsync", scanDialogHost);
        Assert.Contains("RunScanDialogAsync", dialogService);

        Assert.Contains("WorkspaceProfileService _profiles", workspace);
        Assert.DoesNotContain("SavedWorkspaceLayoutService", workspace);
        Assert.DoesNotContain("_savedLayouts", workspace);
        Assert.True(File.ReadAllLines(Path.Combine(coreRoot, "ExplorerWorkspace.cs")).Length <= 200);
        foreach (var controllerFile in new[]
                 {
                     "ClosedTabController.cs",
                     "DriveController.cs",
                     "DualPaneTabController.cs",
                     "FolderTreeController.cs",
                     "FolderViewController.cs",
                     "OperationLogController.cs",
                     "PaneEnrichmentController.cs",
                     "PlacesController.cs",
                     "WorkspaceFileOpsController.cs",
                     "WorkspaceLayoutController.cs",
                     "WorkspaceNavigationController.cs",
                     "WorkspaceSelectionController.cs",
                     "WorkspaceTagController.cs",
                 })
        {
            Assert.True(
                File.Exists(Path.Combine(coreRoot, "WorkspaceControllers", controllerFile)),
                $"Expected {controllerFile} in SimpleFile.Core/WorkspaceControllers/.");
        }

        Assert.Contains("internal sealed class WorkspaceProfileService", profileService);
        Assert.Contains("SavedWorkspaceLayoutsDocument", legacyLayoutDocument);
        Assert.Contains("WorkspaceProfilesDocument.FromLegacyLayouts", profileService);
        Assert.False(File.Exists(Path.Combine(coreRoot, "SavedWorkspaceLayoutService.cs")));

        Assert.Contains("interface ISettingsBackend", facades);
        Assert.DoesNotContain("interface IFileOperationBackend", facades);
        Assert.DoesNotContain("interface ITagBackend", facades);
        Assert.DoesNotContain("interface ISmartFolderBackend", facades);
        Assert.Contains("ISettingsBackend", fileOperationService);
        Assert.Contains("RunJournaledScanAsync", fileOperationService);

        Assert.Contains("mod folder_metrics;", rustFileOps);
        Assert.Contains("mod metadata_preserve;", rustFileOps);
        Assert.Contains("mod registry;", rustProgress);
        Assert.Contains("mod plan;", rustProgress);
        Assert.Contains("mod reporting;", rustProgress);
        Assert.Contains("mod execute;", rustProgress);
        Assert.Contains("pub use registry::OperationRegistry;", rustProgress);
        Assert.True(File.Exists(Path.Combine(repoRoot, "crates", "simplefile-service", "src", "progress", "plan.rs")));
        Assert.True(File.Exists(Path.Combine(repoRoot, "crates", "simplefile-service", "src", "progress", "reporting.rs")));
        Assert.True(File.Exists(Path.Combine(repoRoot, "crates", "simplefile-service", "src", "progress", "execute.rs")));
    }

    [Fact]
    public void DuplicateChecker_OffersPermanentDeleteFallbackWhenTrashIsUnavailable()
    {
        var root = FindRepoRoot();
        var dialogService = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "FileOperationDialogService.cs"));

        Assert.Contains(
            "catch (IpcException ipcException) when (FileOperationService.IsTrashUnavailable(ipcException))",
            dialogService);
        Assert.Contains(
            "PromptPermanentDeleteAfterTrashUnavailableAsync(trash, workspace, ipcException, token)",
            dialogService);
    }

    [Fact]
    public void AdvancedRename_PushesUndoEntryAfterBatchRename()
    {
        var root = FindRepoRoot();
        var transfer = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.Transfer.cs"));

        Assert.Contains("var renamed = await fileOps.BatchRenameAsync(requests, utilityCts.Token);", transfer);
        Assert.Contains("workspace.Undo.PushRename(requests.Select(request => request.Path).ToArray(), renamed, fileOps);", transfer);
    }

    [Fact]
    public void ReplaceTransfers_DoNotRegisterUndoBeforeReceiptsExist()
    {
        var root = FindRepoRoot();
        var transfer = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.Transfer.cs"));

        Assert.Contains("var canRegisterUndo = !string.Equals(action, \"replace\", StringComparison.OrdinalIgnoreCase);", transfer);
        Assert.Contains("if (!canRegisterUndo || committedAll.Count == 0", transfer);
    }

    [Fact]
    public void ServiceSession_SpawnsThumbnailPreviewJobs()
    {
        var root = FindRepoRoot();
        var session = File.ReadAllText(Path.Combine(root, "..", "crates", "simplefile-service", "src", "session", "mod.rs"));
        var jobs = File.ReadAllText(Path.Combine(root, "..", "crates", "simplefile-service", "src", "session", "jobs.rs"));
        var normalizedSession = session.Replace("\r\n", "\n");

        Assert.Contains("spawn_generate_thumbnail(", normalizedSession);
        Assert.Contains("spawn_generate_thumbnails(", normalizedSession);
        Assert.DoesNotContain("Dispatch::GenerateThumbnail { id, path, size } => {\n                generate_thumbnail_and_reply(", normalizedSession);
        Assert.DoesNotContain("Dispatch::GenerateThumbnails { id, paths, size } => {\n                generate_thumbnails_and_reply(", normalizedSession);
        Assert.Contains("pub(super) fn spawn_generate_thumbnail", jobs);
        Assert.Contains("pub(super) fn spawn_generate_thumbnails", jobs);
        Assert.Contains("tokio::spawn(async move", jobs);
    }

    [Fact]
    public void CompareDialog_RendersBinaryComparisonRows()
    {
        var root = FindRepoRoot();
        var presenter = ReadPreviewPresenterSource(Path.Combine(root, "SimpleFile.App"));
        var models = File.ReadAllText(Path.Combine(root, "SimpleFile.Ipc", "Models.cs"));
        var schema = File.ReadAllText(Path.Combine(root, "..", "ipc", "schema", "v1", "types.json"));

        Assert.Contains("comparison.ComparisonType", presenter);
        Assert.Contains("BinaryComparisonRows", presenter);
        Assert.Contains("BinaryDiffRow", models);
        Assert.Contains("\"binary_rows\"", schema);
    }

    [Fact]
    public void SidebarShellIcons_PassDirectoryIntentToShellIconImage()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var shellIcon = File.ReadAllText(Path.Combine(appRoot, "ShellIconLoader.cs"));
        var sidebar = File.ReadAllText(Path.Combine(appRoot, "SidebarView.xaml"));

        Assert.Contains("nameof(IsDirectory)", shellIcon);
        Assert.Contains("public bool IsDirectory", shellIcon);
        Assert.Contains("ShellIconLoader.ForPath(Path, IconSize, IsDirectory)", shellIcon);
        Assert.Contains("isDirectory || IsLikelyDirectoryPath(path)", shellIcon);
        Assert.Contains("treatAsDirectory ? \"dir\" : \"file\"", shellIcon);
        Assert.Contains("ShouldRejectGenericIconIndex", shellIcon);
        Assert.Contains("info.iIcon == 0 && ShouldRejectGenericIconIndex(path, isDirectory)", shellIcon);

        var shellIconCount = CountOccurrences(sidebar, "<local:ShellIconImage");
        Assert.Equal(4, shellIconCount);
        Assert.Equal(shellIconCount, CountOccurrences(sidebar, "IsDirectory=\"True\""));
    }


    [Fact]
    public void TaskOptionsButton_IsBoundToCanChangeOptions()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "TransferProgressWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "TransferProgressWindow.xaml.cs"));

        var button = Regex.Match(xaml, "<Button[^>]*Click=\"OnTaskOptionsClicked\"[^>]*/>", RegexOptions.Singleline);
        Assert.True(button.Success, "The per-task Options button is missing.");
        Assert.Contains("IsEnabled=\"{Binding CanChangeOptions}\"", button.Value);
        Assert.DoesNotContain("IsEnabled=\"{Binding CanCancel}\"", button.Value);

        // A plugin or archive-engine row has no per-task overrides, so the click does nothing for it.
        var handler = MethodBody(code, "void OnTaskOptionsClicked(");
        Assert.Contains("IsExternal", handler);
        Assert.True(
            handler.IndexOf("IsExternal", StringComparison.Ordinal) < handler.IndexOf("TransferOptionsDialog.ShowAsync(", StringComparison.Ordinal),
            "OnTaskOptionsClicked must return for an external row before it opens the dialog.");
    }

    [Fact]
    public void OnClosed_ClosesPluginWindowsBeforeTheSessionCleanup()
    {
        var root = FindRepoRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.xaml.cs"));
        var onClosed = MethodBody(mainWindow, "async void OnClosed(object sender, WindowEventArgs args)");

        var close = onClosed.IndexOf("ClosePluginWindows();", StringComparison.Ordinal);
        var cleanup = onClosed.IndexOf("await CleanupSessionAsync(", StringComparison.Ordinal);
        Assert.True(close >= 0, "OnClosed does not close the plugin windows.");
        Assert.True(cleanup >= 0, "OnClosed does not clean up the session.");
        Assert.True(close < cleanup, "Plugin windows must close before the session cleanup awaits.");

        var plugins = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.Plugins.cs"));
        Assert.Contains(".CloseAllNow()", MethodBody(plugins, "void ClosePluginWindows()"));
        // WindowsFor hands the manager a new host for every runtime, never a sealed one.
        Assert.Contains("new PluginWindowHost(", MethodBody(plugins, "IPluginWindowHost WindowsFor(string pluginId)"));
    }

    [Fact]
    public void PluginWindowHost_AttachAfterTheSeal_ClosesTheWindow()
    {
        var root = FindRepoRoot();
        var host = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "PluginWindowHost.cs"));
        Assert.Contains("PluginWindowSet<Window>", host);
        Assert.Contains("\"A plugin window must be a Microsoft.UI.Xaml.Window.\"", host);

        // Attach: a refused window (the host is sealed) closes at once, before any chrome, and is not tracked.
        var attach = MethodBody(host, "public void Attach(object window, PluginWindowOptions options)");
        var track = attach.IndexOf(".TryTrack(", StringComparison.Ordinal);
        var chrome = attach.IndexOf("SecondaryWindowChrome.Apply(", StringComparison.Ordinal);
        Assert.True(track >= 0 && chrome > track, "Attach must ask TryTrack before it applies the chrome.");
        var refused = attach[track..chrome];
        Assert.Contains("Close", refused);
        Assert.Contains("return;", refused);

        // CloseAllAsync and CloseAllNow seal first, on the calling thread, before anything is enqueued or closed.
        var closeAll = MethodBody(host, "public Task CloseAllAsync()");
        var seal = closeAll.IndexOf(".Seal()", StringComparison.Ordinal);
        Assert.True(seal >= 0, "CloseAllAsync does not seal the host.");
        Assert.True(seal < closeAll.IndexOf("TryEnqueue(", StringComparison.Ordinal), "CloseAllAsync must seal before TryEnqueue.");
        Assert.True(closeAll.IndexOf("Close", seal, StringComparison.Ordinal) > seal);
        Assert.DoesNotContain("Close", closeAll[..seal]);

        var closeNow = MethodBody(host, "internal void CloseAllNow()");
        var sealNow = closeNow.IndexOf(".Seal()", StringComparison.Ordinal);
        Assert.True(sealNow >= 0, "CloseAllNow does not seal the host.");
        Assert.DoesNotContain("Close", closeNow[..sealNow]);
    }

    [Fact]
    public void PluginWindowHost_HidesOpenDialogsBeforeClosingAWindow()
    {
        // Carry-over (d): a dialog open on a plugin window holds the app-wide DialogHost gate. The host hides it before
        // it closes the window, so Disable, rescan, load failure and main-window close all release the gate.
        var root = FindRepoRoot();
        var host = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "PluginWindowHost.cs"));

        var closeEach = MethodBody(host, "private void CloseEach(");
        var hide = closeEach.IndexOf("DialogHost.HideDialogsOn(", StringComparison.Ordinal);
        var close = closeEach.IndexOf("TryClose(", StringComparison.Ordinal);
        Assert.True(hide >= 0, "CloseEach does not hide the window's open dialogs.");
        Assert.True(close > hide, "CloseEach must hide the window's dialogs before it closes the window.");
        Assert.Contains("XamlRoot", closeEach[hide..close]);
        Assert.Contains("window.Content?.XamlRoot", host);
        Assert.Contains("window.Close()", MethodBody(host, "private static void TryClose(Window window)"));

        // CloseAllNow (the main window's OnClosed) goes through the same CloseEach.
        Assert.Contains("CloseEach(", MethodBody(host, "internal void CloseAllNow()"));
        Assert.Contains("CloseEach(", MethodBody(host, "public Task CloseAllAsync()"));
    }

    [Fact]
    public void PluginWindowHost_HidesOpenDialogsWhenAWindowClosesOnItsOwn()
    {
        // Carry-over (d), user-initiated close: a plugin window closed by its title-bar X, Alt+F4 or the plugin's own
        // Close() never goes through CloseEach, and WinUI does not complete a dialog's ShowAsync when its window
        // closes. Attach's Closed handler hides the window's dialogs too, so the app-wide gate is released either way.
        var root = FindRepoRoot();
        var host = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "PluginWindowHost.cs"));

        var attach = MethodBody(host, "public void Attach(object window, PluginWindowOptions options)");
        var closed = attach.IndexOf("pluginWindow.Closed +=", StringComparison.Ordinal);
        Assert.True(closed >= 0, "Attach does not handle the plugin window's Closed event.");
        var handler = attach[closed..];
        var end = handler.IndexOf("};", StringComparison.Ordinal);
        Assert.True(end > 0, "Attach's Closed handler must be a block lambda that hides the window's dialogs.");
        handler = handler[..end];
        Assert.Contains("DialogHost.HideDialogsOn(XamlRootOf(pluginWindow))", handler);
        Assert.Contains("_windows.Remove(pluginWindow)", handler);
    }

    [Fact]
    public void DialogHost_ShowAsync_CompletesWhenTheHostHidesTheDialog()
    {
        // WinUI may never complete ShowAsync for a dialog whose window closed; the gate must not wait on it.
        var root = FindRepoRoot();
        var host = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "DialogHost.cs"));

        var show = MethodBody(host, "public static async Task<ContentDialogResult> ShowAsync(");
        Assert.Contains("Task.WhenAny(", show);
        Assert.Contains("Gate.Release()", show);
        Assert.Contains("Interlocked.Decrement(ref _openCount)", show);
        Assert.Contains("ContentDialogResult.None", show);

        var hide = MethodBody(host, "internal static int HideDialogsOn(XamlRoot? root)");
        Assert.Contains(".Hide()", hide);
        Assert.Contains("TrySetResult(", hide);
        AssertEveryHideIsGuarded(hide, "HideDialogsOn", ".Hide()", expected: 1);
    }

    [Fact]
    public void DialogHost_RunWithProgress_GuardsEveryHide()
    {
        var root = FindRepoRoot();
        var host = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "DialogHost.cs"));
        var body = MethodBody(host, "public static async Task<T> RunWithProgressAsync<T>(");

        AssertEveryHideIsGuarded(body, "RunWithProgressAsync", "dialog.Hide()", expected: 2);
        Assert.Equal(2, CountOccurrences(body, "dialog.XamlRoot is null"));
    }

    /// <summary>
    /// Every <paramref name="call"/> in <paramref name="body"/> sits inside a <c>try</c> block, and there are
    /// <paramref name="expected"/> of them.
    /// </summary>
    internal static void AssertEveryHideIsGuarded(string body, string method, string call, int expected)
    {
        var found = 0;
        for (var index = body.IndexOf(call, StringComparison.Ordinal); index >= 0; index = body.IndexOf(call, index + 1, StringComparison.Ordinal))
        {
            found++;
            Assert.True(IsInsideTry(body, index), $"{method}: the {call} at offset {index} is not inside a try block.");
        }

        Assert.Equal(expected, found);
    }

    /// <summary>True when <paramref name="index"/> lies inside the braces of some <c>try</c> block of <paramref name="body"/>.</summary>
    private static bool IsInsideTry(string body, int index)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(body[..index], @"\btry\s*\{"))
        {
            var depth = 0;
            for (var position = match.Index + match.Length - 1; position < index; position++)
            {
                if (body[position] == '{') depth++;
                else if (body[position] == '}') depth--;
            }

            if (depth > 0)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void InitializePlugins_RendersOnceInsideTheBudgetedSpan_ThenSubscribesAndStartsBackends()
    {
        // Spec §5.2: the plugins-* startup span holds the manager, the hand-over of the prepared scan and one contribution
        // render. Changed is subscribed only after that render (the scan's own Changed would queue a second one into the
        // span), and startup backends start only after the mark and the subscription (no launch competes with the span,
        // and no Changed of theirs is missed).
        var root = FindRepoRoot();
        var plugins = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.Plugins.cs"));
        var body = MethodBody(plugins, "private async Task InitializePluginsAsync(");

        int At(string text)
        {
            var index = body.IndexOf(text, StringComparison.Ordinal);
            Assert.True(index >= 0, $"InitializePluginsAsync has no '{text}'.");
            return index;
        }

        string[] ordered =
        [
            "timer.Lap(\"plugins-begin\")",
            "new PluginManager(",
            "timer.Lap(\"plugins-manager-built\")",
            "await pending.Scan",
            "timer.Lap(\"plugins-scan-awaited\")",
            "DiscoverAsync(prepared, startBackends: false,",
            "timer.Lap(\"plugins-evaluated\")",
            "ApplyPluginContributions();",
            "timer.Mark(\"plugins-discovered\"",
            "manager.Changed += OnPluginsChanged;",
            "manager.StartStartupBackends();",
            "[info] Discovered: ",
        ];
        var positions = ordered.Select(At).ToList();
        for (var index = 1; index < ordered.Length; index++)
        {
            Assert.True(positions[index - 1] < positions[index], $"'{ordered[index - 1]}' must come before '{ordered[index]}'.");
        }

        Assert.Equal(1, CountOccurrences(body, "ApplyPluginContributions();"));
        Assert.Equal(1, CountOccurrences(plugins, "Changed += OnPluginsChanged"));
        Assert.Equal(1, CountOccurrences(plugins, "StartStartupBackends()"));
        // Sub-stages are laps: their file writes happen with the closing mark, outside the span they split.
        Assert.Equal(1, CountOccurrences(body, "timer.Mark("));
    }

    [Fact]
    public void ActivateVisiblePluginSections_OnlyQueuesBodyLoads()
    {
        // Carry-over #82: a body load runs plugin code, so it never runs inline inside a host layout pass.
        var root = FindRepoRoot();
        var sidebar = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.PluginSidebar.cs"));
        var body = MethodBody(sidebar, "private void ActivateVisiblePluginSections(");

        Assert.Contains("_pluginBodyLoads.Request(PluginSidebarLayout.BodyLoadKey(", body);
        Assert.Equal(1, CountOccurrences(body, "LoadPluginSectionBodyAsync("));
        Assert.Equal(1, CountOccurrences(body, "() => LoadPluginSectionBodyAsync("));
    }

    [Fact]
    public void PluginWorkspaceView_NavigateAsync_AlwaysQueues()
    {
        // A plugin calling NavigateAsync from the UI thread must not re-enter a workspace sync inline.
        var root = FindRepoRoot();
        var view = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "PluginWorkspaceView.cs"));
        var body = MethodBody(view, "public Task NavigateAsync(");

        Assert.Contains("PluginUiDispatch.EnqueueAsync(", body);
        Assert.DoesNotContain("RunAsync(", body);
    }

    [Fact]
    public void TogglePluginSection_ReplacesTheCollapsedListInsteadOfMutatingIt()
    {
        // A settings save on the thread pool reads the list; the toggle swaps in a new one instead of editing it.
        var root = FindRepoRoot();
        var sidebar = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.PluginSidebar.cs"));
        var body = MethodBody(sidebar, "private void TogglePluginSection(");

        Assert.Contains("CollapsedPluginSidebarSections = [", body);
        Assert.DoesNotMatch(@"CollapsedPluginSidebarSections\s*\.\s*(Add|Remove)\(", body);
        Assert.DoesNotMatch(@"\b(collapsed|list)\s*\.\s*(Add|Remove)\(", body);
    }

    [Fact]
    public void ConnectAsync_WarmsPluginRenderingBeforeAwaitingTheService()
    {
        // Carry-over (h): the first contribution render's JIT runs while the file service starts. Normal priority,
        // queued before the first await, so it runs before the backend-started continuation (at Low it ran only after
        // ready; see the phase 4 Task 4 report).
        var root = FindRepoRoot();
        var window = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.xaml.cs"));
        var body = MethodBody(window, "private async Task ConnectAsync(");
        var prepare = body.IndexOf("PreparePluginScan()", StringComparison.Ordinal);
        var started = body.IndexOf("timer.Mark(\"backend-started\"", StringComparison.Ordinal);
        Assert.True(prepare >= 0 && started > prepare, "PreparePluginScan() must come before the backend-started mark.");

        var between = body[prepare..started];
        var enqueue = between.IndexOf("TryEnqueue(DispatcherQueuePriority.Normal,", StringComparison.Ordinal);
        Assert.True(enqueue >= 0, "No normal-priority TryEnqueue between PreparePluginScan() and backend-started.");
        var call = between.IndexOf(';', enqueue);
        Assert.Contains("WarmPluginRendering(", between[enqueue..call]);
        Assert.True(
            between.IndexOf("await ", StringComparison.Ordinal) < 0
            || between.IndexOf("await ", StringComparison.Ordinal) > enqueue,
            "The warm-up must be queued before the first await.");

        var plugins = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));
        var warm = MethodBody(plugins, "private void WarmPluginRendering(");
        Assert.DoesNotContain("PluginSectionsHost", warm);
        Assert.DoesNotContain("_plugins", warm);
        Assert.DoesNotContain("_pluginSectionViews", warm);
    }

    [Fact]
    public void PrimaryToolbar_RendersOnlyTheVisibleItems()
    {
        // A saved toolbar keeps an absent plugin's item; only the catalog-known items are drawn.
        var root = FindRepoRoot();
        var surface = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.CommandSurface.cs"));
        var body = MethodBody(surface, "private void ApplyCommandSurfaceLayout(");

        Assert.Contains("in layout.VisibleItems()", body);
        Assert.DoesNotContain("layout.PrimaryToolbar", body);
    }

    [Fact]
    public void SettingsToolbarEditor_CarriesTheOfferedItems()
    {
        var root = FindRepoRoot();
        var settings = ReadSettingsWindowSource(Path.Combine(root, "SimpleFile.App"));

        Assert.Contains("OfferedPrimaryItems =", MethodBody(settings, "private CommandSurfaceLayout CurrentCommandSurfaceLayout("));
        Assert.Contains("OfferedPrimaryItems =", MethodBody(settings, "private static CommandSurfaceLayout CloneCommandSurfaceLayout("));

        // Save merges through the Core rule, against the layout the editor loaded, so an offer made while Settings was
        // open keeps its item (CommandSurfaceLayoutTests.MergeOffersFrom_*).
        Assert.Contains("_toolbarBaseline = CloneCommandSurfaceLayout(", MethodBody(settings, "private void ApplyCommandSurfaceLayout("));
        var applyTo = MethodBody(settings, "public void ApplyTo(UiSettings settings)");
        Assert.Contains(".MergeOffersFrom(current, _toolbarBaseline)", applyTo);
        Assert.DoesNotContain(".. current.OfferedPrimaryItems", applyTo);
    }

    /// <summary>The body of the first method whose declaration contains <paramref name="signature"/>, braces included.</summary>
    [Fact]
    public void RowDecorationCoordinator_StartsEverySourceCallOnTheThreadPool()
    {
        // Carry-over #82: a provider's first activation must never run inline inside a host pass, so every call into
        // the decoration source starts on the thread pool.
        var root = FindRepoRoot();
        var coordinator = File.ReadAllText(Path.Combine(root, "SimpleFile.Core", "RowDecorationCoordinator.cs"));
        var calls = Regex.Matches(coordinator, @"\.DecorateAsync\(");

        Assert.NotEmpty(calls);
        foreach (Match call in calls)
        {
            var lineStart = coordinator.LastIndexOf('\n', call.Index) + 1;
            var before = coordinator[lineStart..call.Index];
            Assert.Matches(@"Task\.Run\(\s*\(\)\s*=>\s*\w+$", before);
        }
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[open..(index + 1)];
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }

    private static string ReadExplorerWorkspaceSource(string coreRoot)
    {
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(coreRoot, "ExplorerWorkspace*.cs")
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
    }

    [Fact]
    public void PluginLocations_NeverResolveFromLocalAppData()
    {
        // User requirement 2026-11-09: every plugin file stays in the installation folder, and nothing falls back to
        // %LOCALAPPDATA%\SumaFile.
        var root = FindRepoRoot();
        string[] sources =
        [
            Path.Combine(root, "SimpleFile.PluginHost", "Discovery", "PluginDiscovery.cs"),
            Path.Combine(root, "SimpleFile.PluginHost", "Trust", "PluginApprovalStore.cs"),
            Path.Combine(root, "SimpleFile.App", "MainWindow.Plugins.cs"),
            Path.Combine(root, "SimpleFile.App", "SettingsWindow.Plugins.cs"),
        ];

        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            Assert.False(text.Contains("LocalApplicationData", StringComparison.Ordinal), $"{source} resolves LocalApplicationData");
            Assert.False(text.Contains("UserRoot", StringComparison.Ordinal), $"{source} still names UserRoot");
        }
    }

    [Fact]
    public void PluginUiSources_NeverResolveTempOrUserFolders()
    {
        // Plugin UI code runs in SumaFile's process: a temp or user folder it resolved itself would be outside the
        // installation folder. Backends get TMP and TEMP inside their data folder instead.
        var pluginsFolder = Path.Combine(Path.GetDirectoryName(FindRepoRoot())!, "plugins");
        var sources = Directory.GetDirectories(pluginsFolder)
            .Select(plugin => Path.Combine(plugin, "ui"))
            .Where(Directory.Exists)
            .SelectMany(ui => Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (var forbidden in new[] { "GetTempPath", "GetTempFileName", "SpecialFolder." })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{source} uses {forbidden}");
            }
        }
    }

    /// <summary>
    /// Every plugin copy target replaces only the plugin folders it copies, so a file a rebuild dropped does not
    /// linger. The Debug output's <c>plugins</c> folder also holds user-installed plugins and, beside it, the
    /// app-owned files, so neither it nor anything else is ever removed whole.
    /// </summary>
    [Fact]
    public void PluginCopyTargets_ReplaceOnlyTheirOwnFolders()
    {
        var root = FindRepoRoot();
        var app = System.Xml.Linq.XDocument.Load(Path.Combine(root, "SimpleFile.App", "SimpleFile.App.csproj"));
        var tests = System.Xml.Linq.XDocument.Load(Path.Combine(root, "SimpleFile.Tests", "SimpleFile.Tests.csproj"));

        var bundled = CopyTargetSteps(app, "CopyBundledPlugins");
        var bundledRemove = Assert.Single(bundled, step => step.Name == "RemoveDir");
        Assert.Matches(@"^@\(\w+->'\$\(OutDir\)plugins\\%\(Filename\)%\(Extension\)'\)$", bundledRemove.Directories);
        var folders = Assert.Single(
            CopyTargetElements(app, "CopyBundledPlugins"),
            element => element.Name.LocalName == bundledRemove.Directories[2..bundledRemove.Directories.IndexOf("->", StringComparison.Ordinal)]);
        Assert.Equal(
            "$([System.IO.Directory]::GetDirectories('$(BundledPluginsStagingDir)'))",
            (string?)folders.Attribute("Include"));
        AssertRemovesBeforeEveryCopy(bundled, "CopyBundledPlugins");

        var remote = CopyTargetSteps(tests, "CopyRemotePluginFixture");
        var remoteRemove = Assert.Single(remote, step => step.Name == "RemoveDir");
        Assert.Equal(@"$(RemoteFixtureDest)plugins\com.sumafile.remote", remoteRemove.Directories);
        Assert.Equal(@"Exists('$(_RepoTarget)plugins\com.sumafile.remote')", remoteRemove.Condition);
        AssertRemovesBeforeEveryCopy(remote, "CopyRemotePluginFixture");

        foreach (var step in bundled.Concat(remote).Where(step => step.Name == "RemoveDir"))
        {
            foreach (var whole in new[] { "$(OutDir)plugins", @"$(OutDir)plugins\", "$(OutDir)", "$(RemoteFixtureDest)", @"$(RemoteFixtureDest)plugins", @"$(RemoteFixtureDest)plugins\" })
            {
                Assert.NotEqual(whole, step.Directories);
            }
        }
    }

    private static List<System.Xml.Linq.XElement> CopyTargetElements(System.Xml.Linq.XDocument project, string target) =>
        project.Descendants()
            .Single(element => element.Name.LocalName == "Target" && (string?)element.Attribute("Name") == target)
            .Descendants()
            .ToList();

    private static List<(string Name, string Directories, string? Condition)> CopyTargetSteps(
        System.Xml.Linq.XDocument project, string target) =>
        project.Descendants()
            .Single(element => element.Name.LocalName == "Target" && (string?)element.Attribute("Name") == target)
            .Elements()
            .Where(element => element.Name.LocalName is "RemoveDir" or "Copy")
            .Select(element => (element.Name.LocalName, (string?)element.Attribute("Directories") ?? "", (string?)element.Attribute("Condition")))
            .ToList();

    private static void AssertRemovesBeforeEveryCopy(List<(string Name, string Directories, string? Condition)> steps, string target)
    {
        var firstCopy = steps.FindIndex(step => step.Name == "Copy");
        var remove = steps.FindIndex(step => step.Name == "RemoveDir");
        Assert.True(firstCopy >= 0, $"{target} copies nothing.");
        Assert.True(remove >= 0 && remove < firstCopy, $"{target} must remove its folder before its first Copy.");
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

    [Fact]
    public void MainWindow_ReconnectDoesNotAwaitArchiveReregistration()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.xaml.cs"));
        var start = source.IndexOf("private async Task ReconnectBackendAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ReconnectBackendAsync not found");
        var end = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end < 0 ? source[start..] : source[start..end];

        Assert.Contains("ReregisterArchiveEnginesAsync(", body);
        Assert.Matches(@"_\s*=\s*ObserveAsync\(\s*_plugins\.ReregisterArchiveEnginesAsync\(", body);
        Assert.DoesNotMatch(@"\bawait\b[^;]*ReregisterArchiveEnginesAsync\(", body);

        // Started right after the IPC swap, before the drive and pane refreshes (which never wait for it).
        var replaceIpc = body.IndexOf("ReplaceIpc(", StringComparison.Ordinal);
        var reregister = body.IndexOf("ReregisterArchiveEnginesAsync(", StringComparison.Ordinal);
        var refreshDrives = body.IndexOf("RefreshDrivesAsync(", StringComparison.Ordinal);
        Assert.True(replaceIpc >= 0 && replaceIpc < reregister, "re-registration must start after ReplaceIpc");
        Assert.True(reregister < refreshDrives, "re-registration must start before the drive refresh");
    }

    private static string ReadMainWindowSource(string appRoot)
    {
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(appRoot, "MainWindow*.cs")
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
    }

    private static string ReadPreviewPresenterSource(string appRoot)
    {
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(appRoot, "PreviewPresenter*.cs")
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
    }

    private static string ReadSettingsWindowSource(string appRoot)
    {
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(appRoot, "SettingsWindow*.cs")
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count += 1;
            index += value.Length;
        }

        return count;
    }
}
