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
    public void MainWindow_SplitsLargeShellSectionsIntoUserControls()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var mainWindowXaml = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml"));
        var mainWindowCode = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));
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
        var mainWindow = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "MainWindow.xaml.cs"));
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
        var commands = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Commands.cs"));
        var workspace = File.ReadAllText(Path.Combine(coreRoot, "ExplorerWorkspace.cs"));
        var profiles = File.ReadAllText(Path.Combine(coreRoot, "WorkspaceProfile.cs"));

        Assert.Contains("WorkspaceProfileButton", toolbar);
        Assert.Contains("WorkspaceProfilesHost", toolbar);
        Assert.Contains("OnWorkspaceProfilesFlyoutOpening", toolbar);
        Assert.Contains("WorkspaceProfileManageButton", toolbar);
        Assert.Contains("RefreshWorkspaceProfilesHostAsync", commands);
        Assert.Contains("AppendWorkspaceProfileOverflowMenuAsync", commands);
        Assert.Contains("profile:save", commands);
        Assert.Contains("ApplyWorkspaceProfileAsync", commands);
        Assert.Contains("SaveWorkspaceProfileAsync", workspace);
        Assert.Contains("DuplicateWorkspaceProfileAsync", workspace);
        Assert.Contains("ExportWorkspaceProfileAsync", workspace);
        Assert.Contains("workspace-profiles", profiles);
        Assert.Contains("builtin-transfer", profiles);
        Assert.DoesNotContain("AppendSavedLayoutOverflowMenuAsync", commands);
        Assert.DoesNotContain("layout:save", commands);
    }

    [Fact]
    public void PreviewPane_UsesPathBackedPdfAndMediaControls()
    {
        var root = FindRepoRoot();
        var appRoot = Path.Combine(root, "SimpleFile.App");
        var pane = File.ReadAllText(Path.Combine(appRoot, "PreviewPaneView.xaml"));
        var presenter = File.ReadAllText(Path.Combine(appRoot, "PreviewPresenter.cs"));
        var commands = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Commands.cs"));
        var backendPreview = File.ReadAllText(Path.Combine(root, "..", "crates", "simplefile-core", "src", "preview.rs"));

        Assert.Contains("<WebView2", pane);
        Assert.Contains("<MediaPlayerElement", pane);
        Assert.Contains("TryRenderPdfPreview", presenter);
        Assert.Contains("TryRenderMediaPreview", presenter);
        Assert.Contains("TryCreatePathBackedPreview", commands);
        Assert.DoesNotContain("const PDF_MAX", backendPreview);
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
        var presenter = File.ReadAllText(Path.Combine(root, "SimpleFile.App", "PreviewPresenter.cs"));
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
