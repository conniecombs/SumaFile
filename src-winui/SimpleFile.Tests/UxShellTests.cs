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

        // Only the bare keys are scoped: Alt+Left (Back) keeps working outside the file list.
        Assert.Contains("var bareKey = (modifiers & ~VirtualKeyModifiers.Shift) == VirtualKeyModifiers.None;", guard);
        Assert.Matches(new Regex(@"if \(bareKey\s*&&\s*KeyboardShortcutMap\.IsFileListScopedKey\(commandId\)\s*&&\s*!IsFileListFocused\(\)"), guard);
        Assert.Contains("private bool CanRunShortcutNow(string commandId, VirtualKeyModifiers modifiers)", shortcuts);
        Assert.Contains("CanRunShortcutNow(commandId, modifiers)", Slice(shortcuts, "private KeyboardAccelerator CreateCommandAccelerator(", "private bool CanRunShortcutNow("));
        Assert.Contains("var scopedChord = !bareKey && KeyboardShortcutMap.IsFileListScopedKey(commandId);", guard);
        Assert.Matches(new Regex(@"IsSuppressedWhileEditingText\(commandId\)\s*&&\s*!scopedChord"), guard);
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
        Assert.Contains("\"Slash\" => VirtualKey.Divide", map);

        // Ctrl+/ keeps working on the numpad: an Oem2 chord also registers VirtualKey.Divide.
        var apply = Slice(Read("MainWindow.Shortcuts.cs"), "private void ApplyKeyboardShortcuts()", "private void ApplyShortcutHints()");
        Assert.Matches(new Regex(@"string\.Equals\(gesture\.Key, ""Oem2"", StringComparison\.Ordinal\)"), apply);
        Assert.Contains("CreateCommandAccelerator(assignment.Id, VirtualKey.Divide, modifiers)", apply);
        Assert.Contains("registered.Add(", apply);
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

    // Change 4: cross-pane toolbar buttons look unavailable without a second pane and say why.
    // They stay enabled because WinUI never shows a tooltip on a disabled Button.
    [Fact]
    public void CrossPaneToolbarButtons_ExplainThemselvesInSinglePaneMode()
    {
        var source = Read("MainWindow.CommandSurface.cs");
        var surface = Slice(source, "private void ConfigureToolbarButton(", "private static object CreateToolbarButtonContent(");

        Assert.DoesNotContain("button.IsEnabled = ", surface);
        Assert.Contains("CrossPaneUnavailableText(action.CommandId ?? action.Id) is { } unavailable", surface);
        Assert.Contains("ToolTipService.SetToolTip(button, unavailable)", surface);
        Assert.Contains("dualPaneEnabled && otherPaneHasPath", surface);
        Assert.Contains("Open the second pane (F6) to {verb.ToLowerInvariant()} there", surface);

        var click = Slice(source, "private async void OnGeneratedToolbarCommandClick(", "private void ApplyPrimaryToolbarCommandVisibility(");
        Assert.Contains("SetStatusText(unavailable)", click);
        Assert.True(click.IndexOf("CrossPaneUnavailableText(commandId)", StringComparison.Ordinal)
            < click.IndexOf("RunAppCommandAsync(commandId)", StringComparison.Ordinal));
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

    // Change 10 / Change 9: one noun per concept in the sidebar. Connections is the remote plugin's section now.
    [Fact]
    public void Sidebar_UsesLabelsAndSidebarCopy()
    {
        var sidebar = Read("SidebarView.xaml");
        Assert.Contains("Text=\"Labels\"", sidebar);
        Assert.Contains("Text=\"No labels yet\"", sidebar);
        Assert.Contains("AutomationProperties.Name=\"Collapse This PC\"", sidebar);
        Assert.Contains("Text=\"Run a search, then save it here to run it again later.\"", sidebar);
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
        Assert.Contains("FolderViewScope.Folder", remember);
        Assert.Contains("FolderViewScope.Descendants", remember);
        Assert.Contains("FolderViewScope.Global", remember);
        Assert.Contains("ViewRememberRequested?.Invoke", remember);

        // The Remember button still saves through the same folder-view defaults path.
        Assert.Contains("PrimaryToolbarPanel.ViewRememberRequested += OnViewRememberRequested;", Read("MainWindow.Controls.cs"));
        var save = Slice(commands, "private async void OnViewRememberRequested(", "private async Task SaveFolderViewSettingsFromFlyoutAsync(");
        Assert.Contains("SaveFolderViewSettingsFromFlyoutAsync(scope)", save);
    }

    // Integration: the detached View-flyout stand-ins for the removed rows are gone from App source.
    [Fact]
    public void ViewOptions_RemovedRowsLeaveNoStandInsInAppSource()
    {
        var stale = new[]
        {
            "ViewUseGlobally", "ViewUseForFolder", "ViewUseForDescendants",
            "ViewDualPaneToggleButton", "ViewDualPaneClicked", "OnViewDualPaneClicked",
            "ViewProfilesList", "ViewProfilesHost", "ViewSaveProfile", "RefreshViewProfilesHostAsync",
        };
        foreach (var file in System.IO.Directory.EnumerateFiles(AppRoot(), "*.*", System.IO.SearchOption.AllDirectories))
        {
            if (!(file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".xaml", StringComparison.Ordinal))
                || file.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = System.IO.File.ReadAllText(file);
            foreach (var name in stale)
            {
                Assert.False(text.Contains(name, StringComparison.Ordinal), $"{System.IO.Path.GetFileName(file)} still references {name}.");
            }
        }
    }

    // Final review C1: the Windows UI smoke must drive the 1.0.4 UI, not removed labels.
    [Fact]
    public void UiSmoke_DrivesTheCurrentUiLabels()
    {
        var smoke = Read("MainWindow.UiPolishSmoke.cs") + Read("MainWindow.UiSmoke.Transfers.cs") + Read("MainWindow.UiSmoke.cs");
        foreach (var stale in new[]
        {
            "Content?.ToString() == \"Keep both\"",
            "\"Maximum simultaneous files\"",
            "\"Bandwidth limit (MiB/s; 0 = unlimited)\"",
            "\"Empty unpinned preview did not collapse\"",
            "Side menu", "Disk cleanup",
        })
        {
            Assert.DoesNotContain(stale, smoke);
        }

        var options = Read("TransferOptionsDialog.cs");
        foreach (var header in new[] { "\"Files at once\"", "\"Bandwidth limit (MB/s, 0 = unlimited)\"" })
        {
            Assert.Contains(header, options);
            Assert.Contains(header, smoke);
        }

        Assert.Contains("SmokeDescendants<RadioButtons>(conflict).Single(group => group.Name == \"ChoiceButtons\")", smoke);
        Assert.Contains("x:Name=\"ChoiceButtons\"", Read("ConflictDialog.xaml"));
        Assert.Contains("SmokeExpandTransferDetailsAsync(view)", smoke);
        Assert.Contains("PreviewSubtitle.Text == \"Select a file\"", smoke);
        Assert.Contains("\"Select a file\"", Read("PreviewPresenter.cs"));
    }

    // Plugin phase 2 verification: a ContentDialog's popup is listed as open before its first layout
    // pass applies the template and adds the content, so the smoke must wait for the loaded dialog and
    // the controls it drives, not only for the popup ("Sequence contains no matching element" otherwise).
    [Fact]
    public void UiSmoke_WaitsForTheTransferOptionsControlsBeforeDrivingThem()
    {
        var wait = Slice(Read("MainWindow.UiSmoke.Transfers.cs"),
            "var optionsTask = TransferOptionsDialog.ShowAsync(", "\"transfer options dialog controls\");");
        Assert.Contains("dialog is { IsLoaded: true }", wait);
        Assert.Contains("SmokeDescendants<ComboBox>(dialog).Any(c => Equals(c.Header, \"Mode\"))", wait);
        Assert.Contains("SmokeDescendants<Button>(dialog).Any(b => b.Name == \"PrimaryButton\")", wait);
    }

    // Final review I5: the backend is "SumaFile's file service" in user-facing copy.
    [Fact]
    public void ServiceMessages_NameSumaFilesFileService()
    {
        var window = Read("MainWindow.xaml.cs");
        Assert.DoesNotContain("IPC service", window);
        Assert.DoesNotContain("background service", window);
        Assert.Contains("\"SumaFile's file service disconnected\"", window);
        Assert.Contains("\"SumaFile's file service reconnected\"", window);
        Assert.Contains("\"Could not start or reach SumaFile's file service.\"", window);
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

        Assert.Contains("workspace.Settings.PreviewVisible = !workspace.Settings.PreviewVisible;", toggle);
        Assert.Contains("_previewManuallyExpanded = workspace.Settings.PreviewVisible;", toggle);
        Assert.Contains("ApplyPreviewVisibility();", toggle);
        Assert.Contains("SaveUiSettingsAsync()", toggle);
        // No special case for the rail: the button flips the setting every time.
        Assert.DoesNotContain("PreviewRail", toggle);
        Assert.DoesNotContain("PreviewPanel.Visibility", toggle);
        Assert.DoesNotContain("if (", toggle.Replace("if (workspace is null)", ""));
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
