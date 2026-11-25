#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using Windows.Foundation;

namespace SimpleFile.App;

// The file panes' mouse gestures, made with the real mouse, so only in a run started with -PointerInput (see
// MainWindow.UiSmoke.Pointer.cs). Excluded from release builds.
public sealed partial class MainWindow
{
    /// <summary>
    /// In the Details view of a folder with blank space below its rows: a marquee dragged from the blank space
    /// selects the rows it touches, a click there clears the selection, and a file dragged over a folder's row shows
    /// that row as the destination and is moved into the folder when dropped. Then the gestures on the window's
    /// chrome: the mouse's side buttons, a middle-click on a folder, a breadcrumb, a bookmark and a tab, files
    /// dropped onto a tab, a breadcrumb and a bookmark, and a tab dragged along its strip.
    /// </summary>
    private async Task SmokePanePointerAsync(List<object> results, string root)
    {
        var workspace = _workspace!;
        var folder = Path.Combine(root, "pane-gestures");
        var inbox = Path.Combine(folder, "inbox");
        Directory.CreateDirectory(inbox);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }

        FileRowView? RowView(string name) =>
            PrimaryFiles.FirstOrDefault(row => row.Name == name) is { } row ? PrimaryDetailsFileList.RealizedRowView(row) : null;
        string Selected() => string.Join(", ", PrimaryDetailsFileList.SelectedRows.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal));

        bool Same(string? left, string right) => left is not null && PathRules.PathsEqual(left, right);
        Border? TabChrome(string tabId) =>
            PrimaryTabHost.Children.OfType<Border>().FirstOrDefault(chrome => chrome.Tag is PaneTab tab && tab.TabId == tabId);
        Button? TabButton(string tabId) => (TabChrome(tabId)?.Child as Panel)?.Children.OfType<Button>().FirstOrDefault();
        Button? Crumb(string path) =>
            PrimaryBreadcrumbHost.Children.OfType<Button>().FirstOrDefault(button => button.Tag is PanePath crumb && Same(crumb.Path, path));
        ListViewItem? BookmarkRow(string path) =>
            workspace.Bookmarks.FindIndex(bookmark => Same(bookmark.Path, path)) is >= 0 and var index
                ? BookmarksList.ContainerFromIndex(index) as ListViewItem
                : null;
        string TabOrder() => string.Join(" ", workspace.Primary.Tabs.Select(tab => tab.Id));
        // The pane shows `path` with `count` rows, and the row named `row` is on screen.
        Task ShowsAsync(string path, int count, string row) => SmokeRemoteWaitAsync(
            () => Same(workspace.Primary.Path, path) && !workspace.Primary.ListingInProgress && PrimaryFiles.Count == count && RowView(row) is { ActualHeight: > 0 },
            () => $"the pane to show {count} rows of {path} with {row}; it shows {PrimaryFiles.Count} of {workspace.Primary.Path}");

        var showedSidebar = false;
        var firstTab = workspace.Primary.ActiveTabId!;
        var cursor = SmokeMousePosition();
        try
        {
            workspace.SetFileListView(PaneId.Primary, "details");
            await workspace.NavigatePaneAsync(PaneId.Primary, folder);
            SyncFromWorkspace();
            await SmokeRemoteWaitAsync(
                () => PrimaryFiles.Count == 4 && PrimaryDetailsFileList.Visibility == Visibility.Visible && RowView("c.txt") is { ActualHeight: > 0 },
                () => $"the pane-gestures folder in the Details view, {PrimaryFiles.Count} rows are listed");
            await Task.Delay(300);

            // 1. A marquee from the blank space below the rows up to b.txt selects b.txt and c.txt.
            var last = RowView("c.txt")!;
            var lastBottom = last.TransformToVisual(PrimaryDetailsFileList).TransformPoint(new Point(0, last.ActualHeight)).Y;
            var blankDown = (lastBottom + 80) / PrimaryDetailsFileList.ActualHeight;
            SmokeAssert(blankDown < 0.95, "The Details list has no blank space below its rows for a marquee to start in");
            await SmokeDragAsync(PrimaryDetailsFileList, 0.6, blankDown, RowView("b.txt")!, 0.6, 0.5);
            await SmokeRemoteWaitAsync(() => Selected() == "b.txt, c.txt", () => $"the marquee to select b.txt and c.txt, selected: [{Selected()}]");
            SmokeAssert(PrimaryMarqueeRect.Visibility == Visibility.Collapsed, "The marquee rectangle is still shown after the drag ended");

            // 2. A click on the blank space clears the selection.
            await SmokeClickAsync(PrimaryDetailsFileList, acrossFraction: 0.6, downFraction: blankDown);
            await SmokeRemoteWaitAsync(() => Selected().Length == 0, () => $"a click on blank space to clear the selection, selected: [{Selected()}]");

            // 3. a.txt dragged over the inbox folder: its row is shown as the destination, and not the pane, which
            // is not where the file would go. Dropped, the file is moved into the folder and the mark is gone.
            var target = RowView("inbox")!;
            await SmokeDragAsync(RowView("a.txt")!, 0.1, 0.5, target, 0.1, 0.5, async () =>
            {
                await SmokeRemoteWaitAsync(() => target.IsDropTarget, () => "the inbox row to be shown as the drop's destination");
                SmokeAssert(PrimaryPaneRoot.BorderThickness.Top == 0, "The pane is highlighted although the drop goes into a folder's row");
            });
            await SmokeWaitWithinAsync(
                () => Task.FromResult(File.Exists(Path.Combine(inbox, "a.txt")) && !File.Exists(Path.Combine(folder, "a.txt")) && PrimaryFiles.Count == 3),
                TimeSpan.FromSeconds(30),
                () => $"the drop to move a.txt into inbox (there: {File.Exists(Path.Combine(inbox, "a.txt"))}, rows: {PrimaryFiles.Count})");
            SmokeAssert(PrimaryFiles.All(row => PrimaryDetailsFileList.RealizedRowView(row) is not { IsDropTarget: true }),
                "A row is still shown as the drop's destination after the drop");

            // 4. The mouse's side buttons go back and forward in the pane under the pointer.
            await workspace.NavigatePaneAsync(PaneId.Primary, inbox);
            SyncFromWorkspace();
            await ShowsAsync(inbox, 1, "a.txt");
            await SmokeClickAsync(PrimaryDetailsFileList, SmokeMouseButton.X1, acrossFraction: 0.6, downFraction: 0.7);
            await ShowsAsync(folder, 3, "inbox");
            await SmokeClickAsync(PrimaryDetailsFileList, SmokeMouseButton.X2, acrossFraction: 0.6, downFraction: 0.7);
            await ShowsAsync(inbox, 1, "a.txt");
            await SmokeClickAsync(PrimaryDetailsFileList, SmokeMouseButton.X1, acrossFraction: 0.6, downFraction: 0.7);
            await ShowsAsync(folder, 3, "inbox");

            // 5. A middle-click on a folder's row opens it in a new tab, and a middle-click on a tab closes it.
            await SmokeClickAsync(RowView("inbox")!, SmokeMouseButton.Middle, acrossFraction: 0.1);
            await SmokeRemoteWaitAsync(() => workspace.Primary.Tabs.Count == 2 && Same(workspace.Primary.Path, inbox),
                () => $"a middle-click on inbox to open it in a second tab: {workspace.Primary.Tabs.Count} tabs, showing {workspace.Primary.Path}");
            var opened = workspace.Primary.ActiveTabId!;
            await SmokeRemoteWaitAsync(() => TabButton(opened) is { ActualWidth: > 0 }, () => "the new tab's button");
            await SmokeClickAsync(TabButton(opened)!, SmokeMouseButton.Middle);
            await SmokeRemoteWaitAsync(() => workspace.Primary.Tabs.Count == 1, () => "a middle-click on the new tab to close it");
            await ShowsAsync(folder, 3, "inbox");

            // 6. A middle-click on a breadcrumb opens that folder in a new tab.
            await SmokeRemoteWaitAsync(() => Crumb(root) is { ActualWidth: > 0 }, () => "the breadcrumb of the folder above pane-gestures");
            await SmokeClickAsync(Crumb(root)!, SmokeMouseButton.Middle);
            await SmokeRemoteWaitAsync(() => workspace.Primary.Tabs.Count == 2 && Same(workspace.Primary.Path, root),
                () => $"a middle-click on the breadcrumb to open {root} in a second tab: {workspace.Primary.Tabs.Count} tabs, showing {workspace.Primary.Path}");
            await workspace.CloseTabAsync(workspace.Primary.ActiveTabId!, PaneId.Primary);
            SyncFromWorkspace();
            await ShowsAsync(folder, 3, "inbox");

            // 7. Files dropped onto a tab go into the tab's folder, and the tab is marked while they are over it.
            await workspace.OpenNewTabAsync(PaneId.Primary, inbox);
            var inboxTab = workspace.Primary.ActiveTabId!;
            await workspace.SwitchToTabAsync(firstTab, PaneId.Primary);
            SyncFromWorkspace();
            await ShowsAsync(folder, 3, "b.txt");
            await SmokeRemoteWaitAsync(() => TabChrome(inboxTab) is { ActualWidth: > 0 }, () => "the inbox tab");
            var tab = TabChrome(inboxTab)!;
            await SmokeDragAsync(RowView("b.txt")!, 0.1, 0.5, tab, 0.5, 0.5, () =>
                SmokeRemoteWaitAsync(() => ReferenceEquals(_dropTargetChrome?.Element, tab), () => "the inbox tab to be marked as the drop's destination"));
            await SmokeWaitWithinAsync(
                () => Task.FromResult(File.Exists(Path.Combine(inbox, "b.txt")) && !File.Exists(Path.Combine(folder, "b.txt"))),
                TimeSpan.FromSeconds(30),
                () => "the drop onto the inbox tab to move b.txt into inbox");
            SmokeAssert(_dropTargetChrome is null, "A tab is still marked as the drop's destination after the drop");
            await ShowsAsync(folder, 2, "c.txt");

            // 8. ...and onto a breadcrumb: from inside inbox, a.txt goes back up onto the pane-gestures breadcrumb.
            await workspace.SwitchToTabAsync(inboxTab, PaneId.Primary);
            SyncFromWorkspace();
            await ShowsAsync(inbox, 2, "a.txt");
            await SmokeRemoteWaitAsync(() => Crumb(folder) is { ActualWidth: > 0 }, () => "the pane-gestures breadcrumb");
            var crumb = Crumb(folder)!;
            await SmokeDragAsync(RowView("a.txt")!, 0.1, 0.5, crumb, 0.5, 0.5, () =>
                SmokeRemoteWaitAsync(() => ReferenceEquals(_dropTargetChrome?.Element, crumb), () => "the breadcrumb to be marked as the drop's destination"));
            await SmokeWaitWithinAsync(
                () => Task.FromResult(File.Exists(Path.Combine(folder, "a.txt")) && !File.Exists(Path.Combine(inbox, "a.txt"))),
                TimeSpan.FromSeconds(30),
                () => "the drop onto the breadcrumb to move a.txt up into pane-gestures");
            await ShowsAsync(inbox, 1, "b.txt");

            // 9. ...and onto a sidebar entry: c.txt goes onto the inbox bookmark, which a middle-click then opens
            // in a new tab.
            await workspace.SwitchToTabAsync(firstTab, PaneId.Primary);
            SyncFromWorkspace();
            await ShowsAsync(folder, 3, "c.txt");
            workspace.AddBookmark(inbox);
            if (!workspace.Settings.SidebarVisible)
            {
                await ToggleSidebarAsync();
                showedSidebar = true;
            }

            SyncFromWorkspace();
            await SmokeRemoteWaitAsync(() => BookmarkRow(inbox) is { ActualHeight: > 0 }, () => "the inbox bookmark in the sidebar");
            await Task.Delay(300);
            var bookmark = BookmarkRow(inbox)!;
            await SmokeDragAsync(RowView("c.txt")!, 0.1, 0.5, bookmark, 0.3, 0.5, () =>
                SmokeRemoteWaitAsync(() => _dropTargetChrome is not null, () => "the bookmark to be marked as the drop's destination"));
            await SmokeWaitWithinAsync(
                () => Task.FromResult(File.Exists(Path.Combine(inbox, "c.txt")) && !File.Exists(Path.Combine(folder, "c.txt"))),
                TimeSpan.FromSeconds(30),
                () => "the drop onto the bookmark to move c.txt into inbox");
            await ShowsAsync(folder, 2, "a.txt");
            await SmokeClickAsync(BookmarkRow(inbox)!, SmokeMouseButton.Middle, acrossFraction: 0.3);
            await SmokeRemoteWaitAsync(() => workspace.Primary.Tabs.Count == 3 && Same(workspace.Primary.Path, inbox),
                () => $"a middle-click on the bookmark to open inbox in a third tab: {workspace.Primary.Tabs.Count} tabs, showing {workspace.Primary.Path}");
            await workspace.CloseTabAsync(workspace.Primary.ActiveTabId!, PaneId.Primary);
            await workspace.SwitchToTabAsync(firstTab, PaneId.Primary);
            SyncFromWorkspace();
            await ShowsAsync(folder, 2, "a.txt");

            // 10. A tab dragged onto another takes its place, and stays the tab it was.
            SmokeAssert(TabOrder() == $"{firstTab} {inboxTab}", $"The tabs are in the order [{TabOrder()}] before the drag");
            await SmokeRemoteWaitAsync(() => TabButton(inboxTab) is { ActualWidth: > 0 } && TabChrome(firstTab) is { ActualWidth: > 0 }, () => "both tabs");
            await SmokeDragAsync(TabButton(inboxTab)!, TabChrome(firstTab)!);
            await SmokeRemoteWaitAsync(() => TabOrder() == $"{inboxTab} {firstTab}", () => $"the dragged tab to come first, the order is [{TabOrder()}]");
            SmokeAssert(workspace.Primary.ActiveTabId == firstTab && Same(workspace.Primary.Path, folder),
                "Dragging a tab that was not the active one made it the active one");
            await SmokeRemoteWaitAsync(
                () => PrimaryTabHost.Children.OfType<Border>().Select(chrome => chrome.Tag is PaneTab shown ? shown.TabId : "").Where(id => id.Length > 0)
                    .SequenceEqual([inboxTab, firstTab]),
                () => "the tab strip to show the new order");

            // 11. A column's header dragged onto another takes its place: Size onto Date.
            Button? Header(string id) => PrimaryColumnHeader.Children.OfType<Grid>().FirstOrDefault(cell => cell.Tag as string == id)?.Children.OfType<Button>().FirstOrDefault();
            await SmokeRemoteWaitAsync(() => Header("size") is { ActualWidth: > 0 } && Header("date") is { ActualWidth: > 0 }, () => $"the Size and Date headers, they are [{SmokeColumnOrder()}]");
            var columnsBefore = SmokeColumnOrder();
            await SmokeDragAsync(Header("size")!, Header("date")!);
            await SmokeRemoteWaitAsync(() => SmokeColumnOrder().StartsWith("name,date,size", StringComparison.Ordinal),
                () => $"the dragged Size column to come after Date, the headers are [{SmokeColumnOrder()}]");
            SmokeAssert(_dropTargetChrome is null, "A header is still marked after the column was dropped");
            MoveColumnBy(PaneId.Primary, "size", -1);
            await SmokeRemoteWaitAsync(() => SmokeColumnOrder() == columnsBefore, () => $"the columns back in the order [{columnsBefore}]");
            results.Add(new { scenario = "pane-pointer-gestures", passed = true });
        }
        finally
        {
            try
            {
                SmokeMouseMove(cursor.X, cursor.Y);
            }
            catch (InvalidOperationException)
            {
                // A failure here would hide the one that led to it.
            }

            // Back to one tab, no bookmark and the sidebar as it was.
            foreach (var extra in workspace.Primary.Tabs.Where(candidate => candidate.Id != firstTab).Select(candidate => candidate.Id).ToArray())
            {
                await workspace.CloseTabAsync(extra, PaneId.Primary);
            }

            workspace.RemoveBookmark(inbox);
            if (showedSidebar)
            {
                await ToggleSidebarAsync();
            }

            workspace.SetFileListView(PaneId.Primary, "list");
            await workspace.NavigatePaneAsync(PaneId.Primary, Path.Combine(root, "empty"));
            SyncFromWorkspace();
        }
    }
}
#endif
