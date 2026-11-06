using System;
using System.Text.RegularExpressions;
using Xunit;
using static SimpleFile.Tests.UiAuditSprint3Tests;

namespace SimpleFile.Tests;

/// <summary>1.0.4 app-shell guards (docs/superpowers/specs/2026-11-06-ux-recommendations.md).</summary>
public class UxShellTests
{
    // Change 1: bare Tab / Space / Backspace act on the file list only when it has focus.
    [Fact]
    public void FileListScopedKeys_YieldWhenTheFileListIsNotFocused()
    {
        var shortcuts = Read("MainWindow.Shortcuts.cs");
        var guard = Slice(shortcuts, "private bool CanRunShortcutNow(", "private void AddFixedTabJumpAccelerators()");

        Assert.Matches(new Regex(@"KeyboardShortcutMap\.IsFileListScopedKey\(commandId\)\s*&&\s*!IsFileListFocused\(\)"), guard);
        Assert.Contains("IsSuppressedWhileEditingText", guard);
        Assert.Contains("commandId == \"pane.switch\" && _workspace?.DualPaneEnabled != true", guard);

        var focus = Slice(shortcuts, "private bool IsFileListFocused()", "private void AddFixedTabJumpAccelerators()");
        Assert.Contains("FocusManager.GetFocusedElement(root)", focus);
        Assert.Contains("VisualTreeHelper.GetParent(current)", focus);
        Assert.Contains("PrimaryDetailsFileList", focus);
        Assert.Contains("SecondaryFileList", focus);
    }

    [Fact]
    public void ShortcutKeyTable_MapsTheOemKeysUsedByNewDefaults()
    {
        var map = Slice(Read("MainWindow.Shortcuts.cs"), "private static bool TryMapVirtualKey(", "return virtualKey != default;");

        Assert.Matches(new Regex(@"""Oem3""[^\n]*=> \(VirtualKey\)192"), map);
        Assert.Matches(new Regex(@"""OemComma""[^\n]*=> \(VirtualKey\)188"), map);
        Assert.Matches(new Regex(@"""Oem2""[^\n]*=> \(VirtualKey\)191"), map);
        Assert.Contains("\"Backspace\" => VirtualKey.Back", map);
    }

    // Change 2: the omnibar returns to Path mode when a search or filter ends.
    [Fact]
    public void Omnibar_ReturnsToPathModeWhenSearchOrFilterEnds()
    {
        var cleared = Slice(Read("MainWindow.Search.cs"), "private void OnSearchCleared(", "private void OnSearchPropertyChanged(");
        Assert.Contains("SetOmnibarMode(OmnibarMode.Navigate, focus: false)", cleared);

        var escape = Slice(Read("MainWindow.Commands.cs"), "private async void OnRootPreviewKeyDown(", "private bool IsTextInputFocused()");
        Assert.True(Regex.Matches(escape, @"SetOmnibarMode\(OmnibarMode\.Navigate, focus: false\)").Count >= 2,
            "Escape must leave Search and Filter mode");

        var omnibar = Read("MainWindow.Omnibar.cs");
        var sync = Slice(omnibar, "private void SyncOmnibarFromWorkspace(", "private bool ResetOmnibarModeAfterNavigation()");
        Assert.Contains("ResetOmnibarModeAfterNavigation()", sync);
        var reset = Slice(omnibar, "private bool ResetOmnibarModeAfterNavigation()", "private void ApplyOmnibarModeButtons()");
        Assert.Contains("OmnibarMode.Search or OmnibarMode.Filter", reset);
    }

    [Fact]
    public void Omnibar_PrefixHintIsPermanentAndParserGetsTheFolderCheck()
    {
        const string hint = "Path, or >command ?search ~filter";
        var toolbar = Read("PrimaryToolbarView.xaml");
        Assert.Contains($"PlaceholderText=\"{hint}\"", toolbar);
        Assert.Contains($"ToolTipService.ToolTip=\"{hint}\"", toolbar);
        Assert.Contains("AutomationProperties.Name=\"Address and search\"", toolbar);

        var omnibar = Read("MainWindow.Omnibar.cs");
        Assert.Contains($"OmnibarPrefixHint = \"{hint}\"", omnibar);
        Assert.Contains("_ => OmnibarPrefixHint", omnibar);
        Assert.Contains("ToolTipService.SetToolTip(OmnibarInput, OmnibarPrefixHint)", omnibar);
        Assert.Contains("System.IO.Directory.Exists", omnibar);
        Assert.Contains("intent.StatusMessage", omnibar);
    }

    // Change 4: cross-pane toolbar buttons are disabled without a second pane.
    [Fact]
    public void CrossPaneToolbarButtons_AreDisabledInSinglePaneMode()
    {
        var surface = Slice(Read("MainWindow.CommandSurface.cs"), "private void ConfigureToolbarButton(", "private static object CreateToolbarButtonContent(");

        Assert.Contains("button.IsEnabled = dualPaneEnabled && otherPaneHasPath", surface);
        Assert.Contains("Open the second pane (F6) to", surface);
    }

    // Add: tab context menu, hidden close glyph on a pane's last tab in dual-pane mode.
    [Fact]
    public void Tabs_HaveAContextMenuAndHideTheLastCloseGlyphInDualPane()
    {
        var chrome = Read("MainWindow.Chrome.cs");
        var tabs = Slice(chrome, "private void RebuildTabs(", "private Button CreateTabCloseButton(");

        Assert.Contains("select.ContextFlyout = CreateTabContextFlyout(tabId, tab.Path)", tabs);
        foreach (var item in new[] { "Close", "Close other tabs", "Close tabs to the right", "Duplicate tab", "Open in other pane", "Copy path" })
        {
            Assert.Contains($"CreateTabMenuItem(\"{item}\"", tabs);
        }

        Assert.Contains("pane.Tabs.Count == 1 && _workspace?.DualPaneEnabled == true", tabs);
        Assert.Contains("if (!hideLastClose)", tabs);
        Assert.Contains("ToolTipService.SetToolTip(add, \"New tab\")", tabs);
        Assert.DoesNotContain("\"New Tab\"", chrome);
    }

    // Change 10 / Change 9: one noun per concept in the sidebar.
    [Fact]
    public void Sidebar_UsesLabelsConnectionsAndSidebarCopy()
    {
        var sidebar = Read("SidebarView.xaml");
        Assert.Contains("Text=\"Labels\"", sidebar);
        Assert.Contains("Text=\"No labels yet\"", sidebar);
        Assert.Contains("Text=\"Connections\"", sidebar);
        Assert.Contains("AutomationProperties.Name=\"Manage connections\"", sidebar);
        Assert.Contains("Text=\"No connections. Open the manager to add one.\"", sidebar);
        Assert.Contains("AutomationProperties.Name=\"Collapse This PC\"", sidebar);
        Assert.Contains("Text=\"Run a search, then save it here to run it again later.\"", sidebar);
        Assert.Matches(new Regex(@"AccessibilityView=""Raw""[\s\S]{0,300}Glyph=""&#xE8A7;"""), sidebar);
        foreach (var stale in new[] { "Text=\"Tags\"", "FTP/SFTP", "No color labels yet", "My PC\"", "side menu" })
        {
            Assert.DoesNotContain(stale, sidebar);
        }

        Assert.DoesNotContain("side menu", Read("PrimaryToolbarView.xaml"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("side menu", Read("MainWindow.Chrome.cs"), StringComparison.OrdinalIgnoreCase);

        foreach (var collapse in new[] { "OnToggleQuickAccess", "OnToggleMyPc", "OnToggleTags" })
        {
            var start = sidebar.IndexOf($"Click=\"{collapse}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, collapse);
            var end = sidebar.IndexOf('>', start);
            Assert.Contains("ToolTipService.ToolTip=", sidebar[start..end]);
        }
    }

    // Remove: dual-pane row and Profiles block in View options; "Remember this view for".
    [Fact]
    public void ViewOptions_AreTrimmedAndIconSizeSnapsToTheOptions()
    {
        var toolbar = Read("PrimaryToolbarView.xaml");
        var view = Slice(toolbar, "x:Name=\"PrimaryViewButton\"", "x:Name=\"PrimarySettingsButton\"");
        Assert.DoesNotContain("ViewDualPaneButton", view);
        Assert.DoesNotContain("Profiles", view);
        Assert.Contains("Header=\"Display style\"", view);
        Assert.Contains("Text=\"Icon size\"", view);
        Assert.DoesNotContain("Current display style", view);
        Assert.DoesNotContain("Current icon size", view);

        var commands = Read("MainWindow.Commands.cs");
        var opening = Slice(commands, "private void OnViewOptionsFlyoutOpening(", "private void SelectViewStyle(");
        Assert.Contains("UiSettings.IconSizeOptions", opening);
        Assert.DoesNotContain("ViewProfilesHost", opening);
        var slider = Slice(commands, "private void OnViewIconSizeSliderChanged(", "private void QueueViewIconSizeSave()");
        Assert.Contains("options[index].Size", slider);

        var code = Read("PrimaryToolbarView.xaml.cs");
        var remember = Slice(code, "private void OnViewRememberClicked(", "private void OnSettingsClicked(");
        Assert.Contains("ViewUseForFolderClicked?.Invoke", remember);
        Assert.Contains("ViewUseForDescendantsClicked?.Invoke", remember);
        Assert.Contains("ViewUseGloballyClicked?.Invoke", remember);
    }

    // Add: smart folders.
    [Fact]
    public void SmartFolders_SaveFromTheSearchBannerAndConfirmRemoval()
    {
        var places = Read("MainWindow.Places.cs");
        Assert.Matches(new Regex(@"private (async )?Task SaveSearchAsSmartFolderAsync\(\)"), places);
        Assert.Contains("RunUiActionAsync(\"Smart folder\", SaveSearchAsSmartFolderAsync)", Slice(places, "private async void OnSaveSmartFolder(", "private async Task SaveSearchAsSmartFolderAsync()"));
        Assert.Contains("ContextBar.SaveSmartFolder +=", places);
        var delete = Slice(places, "private async void OnDeleteSmartFolderClicked(", "workspace.DeleteSmartFolderAsync(");
        Assert.Contains("Remove smart folder '", delete);
        Assert.Contains("PrimaryButtonText = \"Remove\"", delete);
        Assert.Contains("CloseButtonText = \"Cancel\"", delete);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", delete);
        Assert.Contains("DialogHost.ShowAsync(confirm)", delete);

        Assert.Contains("Content=\"Save as smart folder\"", Read("PaneContextBar.xaml"));
        Assert.Contains("public event EventHandler? SaveSmartFolder;", Read("PaneContextBar.xaml.cs"));
    }

    // Change 12: the status-bar preview button is a strict toggle of PreviewVisible.
    [Fact]
    public void PreviewToggle_IsAStrictToggle()
    {
        var toggle = Slice(Read("MainWindow.Commands.cs"), "private async void OnTogglePreview(", "private void ApplyPreviewVisibility()");

        Assert.Contains("var showing = PreviewPanel.Visibility == Visibility.Visible;", toggle);
        Assert.Contains("workspace.Settings.PreviewVisible = !showing;", toggle);
        Assert.DoesNotContain("PreviewRail.Visibility", toggle);
    }

    // Add: inline rename in the Details view.
    [Fact]
    public void InlineRename_EditsTheDetailsRowInPlace()
    {
        var events = Read("MainWindow.FileListEvents.cs");
        var begin = Slice(events, "private bool TryBeginInlineRename()", "private void CancelInlineRename()");
        Assert.Contains("\"details\"", begin);
        Assert.Contains("selected.Count != 1", begin);
        Assert.Contains("BeginRename(CommitInlineRenameAsync)", begin);
        var commit = events[events.IndexOf("private async Task CommitInlineRenameAsync(", StringComparison.Ordinal)..];
        Assert.Contains("AdvancedRename.IsValidFileName(newName)", commit);
        Assert.Contains("RenameSelectedAsync(row.Path, newName", commit);

        Assert.Contains("x:Name=\"RenameBox\"", Read("FileRowView.xaml"));
        var row = Read("FileRowView.xaml.cs");
        Assert.Contains("NewItemTemplate.RenameSelectionLength(row.Name, row.IsDir)", row);
        Assert.Contains("VirtualKey.Escape", row);
        Assert.Contains("OnRenameBoxLostFocus", row);

        Assert.Contains("public FileRowView? RowViewFor(FileRow row)", Read("DetailsFileListView.cs"));
        Assert.Contains("CancelInlineRename()", Read("MainWindow.Commands.cs"));
    }
}
