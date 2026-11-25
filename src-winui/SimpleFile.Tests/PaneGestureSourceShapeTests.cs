using System.IO;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Source-shape checks of the pointer gestures of the file panes (audit U3: the marquee in every view and the row a
/// drag would drop into) and of the window's chrome (audit U4: the mouse's side buttons, middle-click, drops onto
/// tabs, breadcrumbs and sidebar entries, and dragging a tab). The rules themselves are tested in
/// <see cref="MarqueeSelectionTests"/>, <see cref="DropDestinationTests"/> and <see cref="PaneMouseGestureTests"/>;
/// the UI smoke makes the gestures with the real mouse when it is started with <c>-PointerInput</c>.
/// </summary>
public class PaneGestureSourceShapeTests
{
    private static string App(string name) => File.ReadAllText(Path.Combine(UiAuditSprint3Tests.AppRoot(), name));

    [Fact]
    public void Marquee_WorksInTheDetailsViewToo()
    {
        var marquee = App("MainWindow.Marquee.cs");
        var begin = UiAuditSprint3Tests.Slice(marquee, "private void BeginMarquee(", "private void OnMarqueePointerMoved(");

        // It used to return at once when the Details view was showing.
        Assert.DoesNotMatch(@"if \(details\.Visibility == Visibility\.Visible\)\s*\{\s*return;", begin);
        Assert.Contains("var inDetails = details.Visibility == Visibility.Visible;", begin);
        // A press on a row selects it or drags it: the marquee starts on blank space, in either kind of list.
        Assert.Contains("if (inDetails ? details.IsInsideRow(source) : IsInsideListViewItem(source, list)) return;", begin);
        Assert.Contains("details.ClearSelection();", begin);
        Assert.Contains("inDetails ? details.SelectedRows : list.SelectedItems", begin);

        var update = UiAuditSprint3Tests.Slice(marquee, "private void UpdateMarqueeSelection(", "private static bool IsInsideListViewItem(");
        Assert.Contains("details.RowsIntersecting(marqueeBounds, canvas)", update);
        Assert.Contains("details.SelectRows(MarqueeSelection.Resolve(", update);
        // One rule for both kinds of list.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(update, @"MarqueeSelection\.Resolve\(").Count);
        Assert.Contains("MarqueeSelection.Intersects(", update);
        Assert.Contains("MarqueeSelection.Bounds(", marquee);
        Assert.DoesNotContain("RectIntersects", marquee);
    }

    [Fact]
    public void DetailsList_ChangesAMarqueeSelectionRowByRow()
    {
        var details = App("DetailsFileListView.cs");
        var select = UiAuditSprint3Tests.Slice(details, "public void SelectRows(", "public FileRowView? RealizedRowView(");

        // Through the list's own selection, so SelectionChanged reaches the window as it does for a click; the
        // bulk SetSelection suppresses it.
        Assert.Contains("_list.SelectedItems.Remove(selected);", select);
        Assert.Contains("_list.SelectedItems.Add(row);", select);
        Assert.DoesNotContain("_suppressSelectionChanged", select);
        Assert.DoesNotContain("SetSelection(", select);
        Assert.Contains("public IReadOnlyList<FileRow> RowsIntersecting(Rect bounds, UIElement relativeTo)", details);
        Assert.Contains("public bool IsInsideRow(DependencyObject? source)", details);
    }

    [Fact]
    public void ADrag_ShowsTheFolderRowItWouldDropInto()
    {
        var transfer = App("MainWindow.Transfer.cs");
        var over = UiAuditSprint3Tests.Slice(transfer, "private void HandleFileDragOver(", "private async Task HandleFileDropAsync(");

        // The row when the drop goes into the folder under the pointer, the pane when it goes into the pane's own.
        Assert.Contains("SetPaneDragHighlight(pane, valid && !target.OntoFolder);", over);
        Assert.Contains("ShowDropTargetRow(pane, valid && target.OntoFolder ? hovered : null);", over);

        // Every way a drag ends takes the mark away: leaving, dropping, and the end of a drag from either list.
        var clear = UiAuditSprint3Tests.Slice(transfer, "private void ClearPaneDragHighlights()", "private void OnPrimaryFileDragOver(");
        Assert.Contains("ShowDropTargetRow(PaneId.Primary, null);", clear);
        foreach (var handler in new[]
                 {
                     "private void OnFileDragLeave(", "private async Task HandleFileDropAsync(",
                     "private void OnFileDragItemsCompleted(", "private void OnDetailsRowsDragCompleted(",
                 })
        {
            var start = transfer.IndexOf(handler, System.StringComparison.Ordinal);
            Assert.True(start >= 0, $"{handler} was not found.");
            Assert.Contains("ClearPaneDragHighlights();", transfer[start..(start + 400)]);
        }

        var show = UiAuditSprint3Tests.Slice(transfer, "private void ShowDropTargetRow(", "private FileRowView? RealizedRowView(");
        Assert.Contains("_dropTargetRowView?.SetDropTarget(false);", show);
        Assert.Contains("view?.SetDropTarget(true);", show);
    }

    [Fact]
    public void TheWindow_TakesTheMousesSideAndMiddleButtonsWhereverTheyArePressed()
    {
        var gestures = App("MainWindow.PaneGestures.cs");

        // Buttons and list rows handle the presses they get, so the window asks for handled events too.
        Assert.Contains("RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnWindowPointerPressed), true);", gestures);
        Assert.Contains("AttachPaneGestureHandlers();", App("MainWindow.xaml.cs"));
        var pressed = UiAuditSprint3Tests.Slice(gestures, "private async void OnWindowPointerPressed(", "private PaneId? PaneContaining(");
        Assert.Contains("PaneMouseGestures.HistoryStep(buttons.IsXButton1Pressed, buttons.IsXButton2Pressed)", pressed);
        Assert.Contains("var pane = PaneContaining(source) ?? ActiveUiPane;", pressed);
        Assert.Contains("GoHistory(pane, step)", pressed);
        Assert.Contains("buttons.IsMiddleButtonPressed && MiddleClickFolder(source) is { } folder", pressed);
        Assert.Contains("OpenFolderInNewTabAsync(folder.Pane, folder.Path)", pressed);

        var folder = UiAuditSprint3Tests.Slice(gestures, "private PanePath? MiddleClickFolder(", "private static string? SidebarPathOf(");
        // A tab is closed by a middle-click, as before: the walk up stops there and opens nothing.
        Assert.True(folder.IndexOf("case FrameworkElement { Tag: PaneTab }:", System.StringComparison.Ordinal)
            < folder.IndexOf("case Button { Tag: PanePath crumb }:", System.StringComparison.Ordinal));
        Assert.Contains("return row.IsDir ? new PanePath(row.Pane, row.Path) : null;", folder);
        Assert.Contains("new PanePath(_workspace.SidebarTarget, path)", folder);
        Assert.Contains("select.PointerPressed += OnTabPointerPressed;", App("MainWindow.Chrome.cs"));
    }

    [Fact]
    public void Tabs_Breadcrumbs_AndSidebarEntries_TakeDroppedFilesByTheRulesOfAPane()
    {
        var gestures = App("MainWindow.PaneGestures.cs");
        var chrome = App("MainWindow.Chrome.cs");

        Assert.Contains("AttachPathDropTarget(button);", UiAuditSprint3Tests.Slice(chrome, "private void RebuildBreadcrumbs(", "private void RebuildTabs("));
        Assert.Contains("AttachTabGestures(select, chrome);", UiAuditSprint3Tests.Slice(chrome, "private void RebuildTabs(", "private MenuFlyout CreateTabContextFlyout("));
        Assert.Contains("AttachPathDropTarget(chrome);", gestures);
        Assert.Contains("private ListView[] SidebarPathLists() => [QuickAccessList, DriveList, FolderTreeList, BookmarksList, RecentsList];", gestures);
        Assert.Contains("list.DragOver += OnSidebarDragOver;", gestures);
        Assert.Contains("list.Drop += OnSidebarDrop;", gestures);

        var over = UiAuditSprint3Tests.Slice(gestures, "private void HandlePathDragOver(", "private async Task HandlePathDropAsync(");
        Assert.Contains("DropDestination.CanReceive(destination)", over);
        Assert.Contains("DropDestination.IsValidDrop(_dragPaths, destination!, move)", over);
        // A drag from another program is taken only when it carries files.
        Assert.Contains("e.DataView.Contains(StandardDataFormats.StorageItems)", over);
        Assert.Contains("ShowDropTargetChrome(valid ? target : null);", over);

        var drop = UiAuditSprint3Tests.Slice(gestures, "private async Task HandlePathDropAsync(", "private void ShowDropTargetChrome(");
        Assert.Contains("ClearPaneDragHighlights();", drop);
        Assert.Contains("var deferral = e.GetDeferral();", drop);
        Assert.Contains("deferral.Complete();", drop);
        Assert.Contains("SetStatusText(\"Cannot drop into that location\");", drop);
        Assert.Contains("TransferWithConflictAsync(sources.ToArray(), destination!, move)", drop);

        // One rule for move or copy, in the panes as well.
        var transfer = App("MainWindow.Transfer.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(transfer, @"DropDestination\.IsMove\(").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(gestures, @"DropDestination\.IsMove\(").Count);
        Assert.Contains("ShowDropTargetChrome(null);", UiAuditSprint3Tests.Slice(transfer, "private void ClearPaneDragHighlights()", "private void OnPrimaryFileDragOver("));
    }

    [Fact]
    public void ATab_IsReorderedByADragOrFromTheKeyboard()
    {
        var gestures = App("MainWindow.PaneGestures.cs");

        // A button keeps the pointer from press to release, so the drag is followed on the button, with handled
        // events too; the strip is rebuilt only by the move itself, when the tab is let go.
        foreach (var handler in new[] { "PointerPressedEvent, new PointerEventHandler(OnTabDragPressed)", "PointerMovedEvent, new PointerEventHandler(OnTabDragMoved)", "PointerReleasedEvent, new PointerEventHandler(OnTabDragReleased)" })
        {
            Assert.Contains("select.AddHandler(UIElement." + handler + ", true);", gestures);
        }

        // The button gives the pointer up before a handler added to it hears of the release, so the drag also ends
        // where the pointer is lost; lost with the mouse button still down, nothing moves.
        Assert.Contains("select.PointerCaptureLost += OnTabDragReleased;", gestures);
        var moved = UiAuditSprint3Tests.Slice(gestures, "private void OnTabDragMoved(", "private void OnTabDragReleased(");
        Assert.Contains("Math.Abs(point.Position.X - drag.StartX) < TabDragThreshold", moved);
        Assert.DoesNotContain("MoveTab(", moved);
        var released = UiAuditSprint3Tests.Slice(gestures, "private void OnTabDragReleased(", "private void EndTabDrag()");
        Assert.Contains("if (!drag.Moving || _workspace is null)", released);
        Assert.Contains("_workspace.MoveTab(drag.Tab.TabId, drag.Tab.Pane, index);", released);
        Assert.True(released.IndexOf("if (point.Properties.IsLeftButtonPressed)", System.StringComparison.Ordinal) is > 0 and var pressed
            && pressed < released.IndexOf("_workspace.MoveTab(", System.StringComparison.Ordinal));
        Assert.Contains("StripReorder.TargetIndex(", released);

        var keys = UiAuditSprint3Tests.Slice(App("MainWindow.FileListEvents.cs"), "private async void OnTabKeyDown(", "private async void OnQuickAccessClick(");
        // Not Ctrl+Shift+Left and Right, which switch panes: the browsers' Ctrl+Shift+PageUp and PageDown.
        Assert.Contains("if (e.Key is VirtualKey.PageUp or VirtualKey.PageDown)", keys);
        Assert.Contains("if (IsKeyDown(VirtualKey.Control) && IsKeyDown(VirtualKey.Shift))", keys);
        Assert.Contains("TryMoveTabWithKeyboard(tab, e.Key == VirtualKey.PageDown ? 1 : -1);", keys);
        Assert.Contains("new(\"pane.focusPrimary\", [\"Alt+1\", \"Ctrl+Shift+Left\"]", File.ReadAllText(Path.Combine(UiAuditSprint3Tests.FindRepoRoot(), "SimpleFile.Core", "KeyboardShortcutMap.cs")));
        Assert.Contains("StripReorder.Step(index, tabs.Count, delta)", gestures);
    }

    [Fact]
    public void TheOmnibar_AndThePathBoxes_ShareOneSuggestionListThatTheKeyboardWorks()
    {
        var path = App("MainWindow.Path.cs");
        var omnibar = App("MainWindow.Omnibar.cs");

        // The omnibar asks while it has the focus and the user is typing, not when its text is synced.
        var changed = UiAuditSprint3Tests.Slice(omnibar, "private void OnOmnibarTextChanged(", "private void FocusOmnibar(");
        Assert.Contains("if (_syncingOmnibar || _applyingWorkspace || !_omnibarFocused)", changed);
        Assert.Contains("_ = UpdateOmnibarSuggestionsAsync();", changed);
        var update = UiAuditSprint3Tests.Slice(path, "private async Task UpdateOmnibarSuggestionsAsync()", "private async Task<IEnumerable<string>?> ListFolderCandidatesAsync(");
        Assert.Contains("OmnibarSuggestions.Request(OmnibarInput.Text, _omnibarMode, _workspace.Pane(pane).Path)", update);
        Assert.Contains("await Task.Delay(PathSuggestDebounceMilliseconds);", update);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(update, @"token != Volatile\.Read\(ref _pathSuggestToken\)").Count);
        Assert.Contains("OmnibarSuggestions.CommandSuggestions(request.Query)", update);
        Assert.Contains("OmnibarSuggestions.FolderSuggestions(candidates, request.Prefix)", update);
        Assert.Contains("HideSuggestions(OmnibarInput);", UiAuditSprint3Tests.Slice(omnibar, "private void OnOmnibarLostFocus(", "private bool IsOmnibarModeButtonFocused()"));

        // One list, and its keys are taken before the box sees them.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(path, @"new Flyout\b"));
        Assert.Contains("box.PreviewKeyDown += OnSuggestBoxPreviewKeyDown;", App("MainWindow.xaml.cs"));
        Assert.Contains("foreach (var box in new[] { OmnibarInput, PrimaryPathInput, SecondaryPathInput })", App("MainWindow.xaml.cs"));
        var keys = UiAuditSprint3Tests.Slice(path, "private void OnSuggestBoxPreviewKeyDown(", "private async Task HandlePathKey(");
        Assert.Contains("if (!_suggestionsOpen || !ReferenceEquals(sender, _suggestBox)", keys);
        Assert.Contains("OmnibarSuggestions.MoveSelection(list.SelectedIndex, _suggestions.Count, e.Key == VirtualKey.Down ? 1 : -1)", keys);
        Assert.Contains("case VirtualKey.Tab when !IsKeyDown(VirtualKey.Shift):", keys);
        // Enter takes a line only when one is chosen; otherwise it still runs what is typed.
        Assert.Contains("case VirtualKey.Enter when list.SelectedIndex >= 0:", keys);
        Assert.Contains("case VirtualKey.Escape:", keys);

        // Hidden is closed at once, not when the flyout says so later: Enter after Tab must run the typed path, not
        // take the line of a list that is gone.
        var hide = UiAuditSprint3Tests.Slice(path, "private void HidePathSuggestions()", "private void HideSuggestions(TextBox box)");
        Assert.True(hide.IndexOf("_suggestionsOpen = false;", System.StringComparison.Ordinal) is > 0 and var closed
            && closed < hide.IndexOf("_pathSuggestFlyout?.Hide();", System.StringComparison.Ordinal));
        Assert.Contains("_suggestions = [];", hide);
        Assert.Contains("_suggestionsOpen = true;", UiAuditSprint3Tests.Slice(path, "private void ShowSuggestions(", "private void HidePathSuggestions()"));

        var accept = UiAuditSprint3Tests.Slice(path, "private void AcceptSuggestion(int index)", "private void OnSuggestBoxPreviewKeyDown(");
        Assert.Contains("await RunAppCommandAsync(suggestion.Value);", accept);
        Assert.Contains("OmnibarSuggestions.AcceptedFolderText(suggestion.Value)", accept);
        Assert.Contains("box.Focus(FocusState.Programmatic);", accept);
    }

    [Fact]
    public void ADetailsColumn_IsMovedByDraggingItsHeaderOrFromItsMenu()
    {
        var columns = App("MainWindow.Columns.cs");

        Assert.Contains("AttachColumnHeaderDrag(button, column.Id, pane);", UiAuditSprint3Tests.Slice(columns, "private void ApplyColumnHeader(", "private MenuFlyout CreateColumnsFlyout("));
        var attach = UiAuditSprint3Tests.Slice(columns, "private void AttachColumnHeaderDrag(", "private void OnColumnHeaderDragMoved(");
        // The name column stays first, so its header is not dragged.
        Assert.Contains("if (columnId == \"name\")", attach);
        Assert.Contains("button.PointerCaptureLost += OnColumnHeaderDragReleased;", attach);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(attach, @"button\.AddHandler\(UIElement\.Pointer\w+Event, new PointerEventHandler\(").Count);

        var released = UiAuditSprint3Tests.Slice(columns, "private void OnColumnHeaderDragReleased(", "private void EndColumnHeaderDrag()");
        Assert.Contains("point.Properties.IsLeftButtonPressed", released);
        // By the column under the pointer, not by its place on screen: a narrow pane shows another order.
        Assert.Contains("workspace.ColumnsFor(drag.Pane).VisibleIds.IndexOf(targetId)", released);
        Assert.Contains("ApplyColumnMove(drag.Pane, drag.ColumnId, index);", released);

        var apply = UiAuditSprint3Tests.Slice(columns, "private void ApplyColumnMove(", "/// <summary>The same picker as a \"Columns\" submenu");
        Assert.Contains("workspace.MoveColumn(pane, columnId, index)", apply);
        Assert.Contains("workspace.SaveUiSettingsAsync()", apply);

        var menu = UiAuditSprint3Tests.Slice(columns, "private MenuFlyout CreateColumnsFlyout(", "private ColumnHeaderDrag? _columnHeaderDrag;");
        Assert.Contains("Text = \"Move Column Left\"", menu);
        Assert.Contains("Text = \"Move Column Right\"", menu);
        Assert.Contains("moveLeft.IsEnabled = layout?.CanMove(columnId, -1) == true;", menu);
        Assert.Contains("moveRight.IsEnabled = layout?.CanMove(columnId, 1) == true;", menu);
    }

    [Fact]
    public void ARowView_DrawsTheDropMarkAndDropsItWhenItIsRecycled()
    {
        var xaml = App("FileRowView.xaml");
        var outline = UiAuditSprint3Tests.Slice(xaml, "x:Name=\"DropTargetOutline\"", "/>");
        Assert.Contains("BorderBrush=\"{ThemeResource SfAccentBrush}\"", outline);
        Assert.Contains("IsHitTestVisible=\"False\"", outline);
        Assert.Contains("Visibility=\"Collapsed\"", outline);
        // Behind the row's cells.
        Assert.True(xaml.IndexOf("x:Name=\"DropTargetOutline\"", System.StringComparison.Ordinal)
            < xaml.IndexOf("x:Name=\"RowGrid\"", System.StringComparison.Ordinal));

        var view = App("FileRowView.xaml.cs");
        var rowChanged = UiAuditSprint3Tests.Slice(view, "private static void OnRowChanged(", "private void OnLoaded(");
        Assert.Contains("view.SetDropTarget(false);", rowChanged);
    }
}
