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
    public void DetailsColumns_UsePaneOwnedHorizontalScrollAndRefreshAfterPaneResize()
    {
        var root = FindRepoRoot();
        var mainWindow = ReadMainWindowSource(Path.Combine(root, "SimpleFile.App"));
        var primaryPane = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "PrimaryPaneView.xaml"));
        var secondaryPane = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "SecondaryPaneView.xaml"));

        Assert.Contains("x:Name=\"PrimaryDetailsScroller\"", primaryPane);
        Assert.Contains("x:Name=\"PrimaryFileSurface\"", primaryPane);
        Assert.Contains("x:Name=\"PrimaryColumnHeader\"", primaryPane);
        Assert.Contains("x:Name=\"PrimaryFileList\"", primaryPane);
        Assert.Contains("x:Name=\"SecondaryDetailsScroller\"", secondaryPane);
        Assert.Contains("x:Name=\"SecondaryFileSurface\"", secondaryPane);
        Assert.Contains("PrimaryPaneRoot.SizeChanged += OnPaneRootSizeChanged", mainWindow);
        Assert.Contains("SecondaryPaneRoot.SizeChanged += OnPaneRootSizeChanged", mainWindow);
        Assert.Contains("QueuePaneSizeColumnRefresh", mainWindow);
        Assert.Contains("DispatcherQueue.CreateTimer()", mainWindow);
        Assert.Contains("ApplyDetailsSurface(PaneId.Primary", mainWindow);
        Assert.Contains("ApplyDetailsSurface(PaneId.Secondary", mainWindow);
        Assert.Contains("ClampDetailsHorizontalScroll(PaneId.Primary)", mainWindow);
        Assert.Contains("ClampDetailsHorizontalScroll(PaneId.Secondary)", mainWindow);
        Assert.Contains("PrimaryDetailsScroller.UpdateLayout()", mainWindow);
        Assert.Contains("SecondaryDetailsScroller.UpdateLayout()", mainWindow);
        Assert.Contains("ScrollViewer.SetHorizontalScrollMode", mainWindow);
        Assert.Contains("ScrollMode.Disabled", mainWindow);
        Assert.Contains("scroller.ScrollableWidth", mainWindow);
        Assert.DoesNotContain("HookFileListColumnScroll", mainWindow);
        Assert.DoesNotContain("PrimaryColumnHeaderScroller", mainWindow);
        Assert.DoesNotContain("SecondaryColumnHeaderScroller", mainWindow);
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
            "PrimaryPaneView",
            "SecondaryPaneView",
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
        Assert.Contains("ViewProfilesHost", toolbar);
        Assert.Contains("OnViewSaveProfileClicked", toolbar);
        Assert.Contains("OnWorkspaceProfilesFlyoutOpening", toolbar);
        Assert.Contains("WorkspaceProfileManageButton", toolbar);
        Assert.Contains("RefreshWorkspaceProfilesHostAsync", commands);
        Assert.Contains("RefreshViewProfilesHostAsync", commands);
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

        Assert.Contains("ViewUseGloballyButton", toolbar);
        Assert.Contains("ViewUseForFolderButton", toolbar);
        Assert.Contains("ViewUseForDescendantsButton", toolbar);
        Assert.Contains("Current view options", toolbar);
        Assert.Contains("Save view defaults", toolbar);
        Assert.Contains("SaveFolderViewSettingsFromFlyoutAsync", commands);
        Assert.Contains("ActiveToolbarSearchTextBox", commands);
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
        Assert.Contains("presenter.IsResizable = true;", settingsCode);
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
        Assert.Contains("AppWindow.Resize(new SizeInt32(1180, 740))", workbenchWindow);
        Assert.Contains("presenter.IsResizable = true;", workbenchWindow);
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
        Assert.DoesNotContain("Content=\"Light\"", settingsXaml);
        Assert.DoesNotContain("Tag=\"Light\"", settingsXaml);

        Assert.DoesNotContain("\"light\" => ElementTheme.Light", commands);
        Assert.Contains("RootGrid.ActualThemeChanged += OnRootActualThemeChanged;", mainWindowCode);
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
        Assert.Contains("TryRenderVideoPosterPreview", presenter);
        Assert.Contains("PreviewCapabilities.ShouldRenderHtml", presenter);
        Assert.Contains("PreviewCapabilities.ShouldPreviewVideo", presenter);
        Assert.Contains("VideoThumbnailExtractor", presenter);
        Assert.Contains("SetVideoFramePreference", presenter);
        Assert.Contains("MetadataSections", presenter);
        Assert.Contains("VideoThumbnailExtractor", thumbnailHost);
        Assert.Contains("PhotoFolder.IsImage", thumbnailHost);
        Assert.Contains("MediaFolder.IsVideo", thumbnailHost);
        Assert.Contains("MediaFolder.IsMediaFolder", mainWindow);
        Assert.Contains("TryCreatePathBackedPreview", commands);
        Assert.Contains("InspectionDetails", presenter);
        Assert.Contains("InspectionDetails", commands);
        Assert.Contains("FolderMetricRows", inspectionDetails);
        Assert.Contains("ChecksumsText", inspectionDetails);
        Assert.DoesNotContain("const PDF_MAX", backendPreview);

        var renderer = File.ReadAllText(Path.Combine(appRoot, "PreviewHtmlRenderer.cs"));
        var capabilities = File.ReadAllText(Path.Combine(coreRoot, "PreviewCapabilities.cs"));
        var pathSupport = File.ReadAllText(Path.Combine(coreRoot, "PreviewPathSupport.cs"));

        Assert.Contains("Content-Security-Policy", renderer);
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


    private static string ReadExplorerWorkspaceSource(string coreRoot)
    {
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(coreRoot, "ExplorerWorkspace*.cs")
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
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
