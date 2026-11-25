using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;

namespace SimpleFile.App;

// Pointer gestures on the window's chrome, as in Explorer and a browser: the mouse's side buttons go back and
// forward; a middle-click on a folder, a breadcrumb or a sidebar entry opens it in a new tab; files can be dropped
// onto a tab, a breadcrumb or a sidebar entry; and a tab can be dragged along its strip. The rules that need no
// window are in Core: PaneMouseGestures, StripReorder and DropDestination.
public sealed partial class MainWindow
{
    /// <summary>How far a pressed tab moves before the press is a drag and not a click.</summary>
    private const double TabDragThreshold = 8;

    // The tab being dragged along its strip, from the press on it until the release.
    private TabDrag? _tabDrag;

    // The tab, breadcrumb or sidebar row a drag is over, with the background it had before it was marked.
    private (FrameworkElement Element, Brush? Background)? _dropTargetChrome;

    private readonly record struct TabDrag(PaneTab Tab, Button Button, StackPanel Host, double StartX, bool Moving);

    private void AttachPaneGestureHandlers()
    {
        // Buttons and list rows handle the presses they are given, so this one asks for handled events too.
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnWindowPointerPressed), true);
        foreach (var list in SidebarPathLists())
        {
            list.AllowDrop = true;
            list.DragOver += OnSidebarDragOver;
            list.DragLeave += OnPathTargetDragLeave;
            list.Drop += OnSidebarDrop;
        }
    }

    /// <summary>The sidebar sections whose rows are folders: a row of the others (labels, smart folders) is not a place.</summary>
    private ListView[] SidebarPathLists() => [QuickAccessList, DriveList, FolderTreeList, BookmarksList, RecentsList];

    private async void OnWindowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        var buttons = e.GetCurrentPoint(RootGrid).Properties;
        var source = e.OriginalSource as DependencyObject;
        var step = PaneMouseGestures.HistoryStep(buttons.IsXButton1Pressed, buttons.IsXButton2Pressed);
        if (step != 0)
        {
            // The pane under the pointer, as its own Back and Forward buttons would; elsewhere, the active pane.
            e.Handled = true;
            var pane = PaneContaining(source) ?? ActiveUiPane;
            await RunUiActionAsync("Navigation", () => GoHistory(pane, step));
            return;
        }

        if (buttons.IsMiddleButtonPressed && MiddleClickFolder(source) is { } folder)
        {
            e.Handled = true;
            await RunUiActionAsync("Open in new tab", () => OpenFolderInNewTabAsync(folder.Pane, folder.Path));
        }
    }

    private PaneId? PaneContaining(DependencyObject? source)
    {
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, PrimaryPaneRoot))
            {
                return PaneId.Primary;
            }

            if (ReferenceEquals(element, SecondaryPaneRoot))
            {
                return PaneId.Secondary;
            }
        }

        return null;
    }

    /// <summary>
    /// The folder a middle-click on <paramref name="source"/> opens in a new tab: a folder's row in a file list, a
    /// breadcrumb, or a sidebar entry. Null for anything else, and for a tab, which a middle-click closes.
    /// </summary>
    private PanePath? MiddleClickFolder(DependencyObject? source)
    {
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            switch (element)
            {
                case FrameworkElement { Tag: PaneTab }:
                    return null;
                case Button { Tag: PanePath crumb }:
                    return crumb;
                case FileRowView { Row: { } row }:
                    return row.IsDir ? new PanePath(row.Pane, row.Path) : null;
                case ListViewItem { Content: FileRow row }:
                    return row.IsDir ? new PanePath(row.Pane, row.Path) : null;
                case ListViewItem item when ItemsControl.ItemsControlFromItemContainer(item) is ListView list
                    && Array.IndexOf(SidebarPathLists(), list) >= 0:
                    return SidebarPathOf(item.Content) is { Length: > 0 } path && _workspace is not null
                        ? new PanePath(_workspace.SidebarTarget, path)
                        : null;
            }
        }

        return null;
    }

    private static string? SidebarPathOf(object? item) => item switch
    {
        QuickAccessRow row => row.Path,
        DriveRow row => row.Path,
        FolderTreeItem row => row.Path,
        BookmarkItem row => row.Path,
        string recent => recent,
        _ => null,
    };

    private async Task OpenFolderInNewTabAsync(PaneId pane, string path)
    {
        if (_workspace is null)
        {
            return;
        }

        await SaveViewIconSizeNowAsync();
        await _workspace.OpenNewTabAsync(pane, path);
    }

    /// <summary>Lets files be dropped onto a tab or a breadcrumb, whose <c>Tag</c> names the folder.</summary>
    private void AttachPathDropTarget(FrameworkElement element)
    {
        element.AllowDrop = true;
        element.DragOver += OnPathTargetDragOver;
        element.DragLeave += OnPathTargetDragLeave;
        element.Drop += OnPathTargetDrop;
    }

    private string? DropPathOf(object? tag) => tag switch
    {
        PanePath crumb => crumb.Path,
        PaneTab tab => _workspace?.Pane(tab.Pane).Tabs.FirstOrDefault(candidate => candidate.Id == tab.TabId)?.Path,
        _ => null,
    };

    private void OnPathTargetDragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            HandlePathDragOver(e, DropPathOf(element.Tag), element);
        }
    }

    private void OnPathTargetDragLeave(object sender, DragEventArgs e) => ShowDropTargetChrome(null);

    private async void OnPathTargetDrop(object sender, DragEventArgs e)
    {
        var destination = sender is FrameworkElement element ? DropPathOf(element.Tag) : null;
        await RunUiActionAsync("Drop files", () => HandlePathDropAsync(e, destination));
    }

    private void OnSidebarDragOver(object sender, DragEventArgs e)
    {
        var (path, row) = SidebarEntryAt((ListView)sender, e);
        HandlePathDragOver(e, path, row);
    }

    private async void OnSidebarDrop(object sender, DragEventArgs e)
    {
        var (path, _) = SidebarEntryAt((ListView)sender, e);
        await RunUiActionAsync("Drop files", () => HandlePathDropAsync(e, path));
    }

    /// <summary>The sidebar entry under a drag: its folder, and the row to mark.</summary>
    private static (string? Path, FrameworkElement? Row) SidebarEntryAt(ListView list, DragEventArgs e)
    {
        var point = e.GetPosition(list);
        foreach (var (_, container) in DetailsFileListView.RealizedContainers(list))
        {
            var bounds = container.TransformToVisual(list)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(point))
            {
                return (SidebarPathOf(container.Content), container.ContentTemplateRoot as FrameworkElement ?? container);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// A drag over a tab, a breadcrumb or a sidebar entry: whether its files may go into
    /// <paramref name="destination"/>, by the rules of a drop into a pane, and the mark on <paramref name="target"/>.
    /// Only files are taken: a drag of text or of a link finds no target here.
    /// </summary>
    private void HandlePathDragOver(DragEventArgs e, string? destination, FrameworkElement? target)
    {
        var internalDrag = _dragPaths.Length > 0;
        var move = DropDestination.IsMove(internalDrag, (e.Modifiers & DragDropModifiers.Control) != 0);
        var valid = DropDestination.CanReceive(destination)
            && (internalDrag
                ? DropDestination.IsValidDrop(_dragPaths, destination!, move)
                : e.DataView.Contains(StandardDataFormats.StorageItems));
        e.AcceptedOperation = valid
            ? (move ? DataPackageOperation.Move : DataPackageOperation.Copy)
            : DataPackageOperation.None;
        if (valid && e.DragUIOverride is { } tip)
        {
            tip.Caption = $"{(move ? "Move" : "Copy")} to {destination}";
        }

        ShowDropTargetChrome(valid ? target : null);
        e.Handled = true;
    }

    private async Task HandlePathDropAsync(DragEventArgs e, string? destination)
    {
        ClearPaneDragHighlights();
        if (_workspace?.FileOps is null || !DropDestination.CanReceive(destination))
        {
            return;
        }

        var move = DropDestination.IsMove(_dragPaths.Length > 0, (e.Modifiers & DragDropModifiers.Control) != 0);
        List<string> sources;
        var deferral = e.GetDeferral();
        try
        {
            sources = await ReadDroppedPathsAsync(e);
        }
        finally
        {
            deferral.Complete();
        }

        if (sources.Count == 0)
        {
            return;
        }

        if (!DropDestination.IsValidDrop(sources, destination!, move))
        {
            SetStatusText("Cannot drop into that location");
            return;
        }

        var transfer = TransferWithConflictAsync(sources.ToArray(), destination!, move);
        _dragPaths = [];
        await transfer;
    }

    /// <summary>
    /// Marks <paramref name="element"/> as where a drop would go, or the tab position a dragged tab would take, and
    /// gives the element marked before its own background back. Null marks nothing.
    /// </summary>
    private void ShowDropTargetChrome(FrameworkElement? element)
    {
        if (ReferenceEquals(element, _dropTargetChrome?.Element))
        {
            return;
        }

        if (_dropTargetChrome is { } previous)
        {
            SetChromeBackground(previous.Element, previous.Background);
        }

        if (element is null)
        {
            _dropTargetChrome = null;
            return;
        }

        _dropTargetChrome = (element, ChromeBackground(element));
        SetChromeBackground(element, Brush("SfAccentSoftBrush"));
    }

    private static Brush? ChromeBackground(FrameworkElement element) => element switch
    {
        Border border => border.Background,
        Panel panel => panel.Background,
        Control control => control.Background,
        _ => null,
    };

    private static void SetChromeBackground(FrameworkElement element, Brush? background)
    {
        switch (element)
        {
            case Border border:
                border.Background = background;
                break;
            case Panel panel:
                panel.Background = background;
                break;
            case Control control:
                control.Background = background;
                break;
        }
    }

    /// <summary>
    /// Makes a tab a drop target for files and lets it be dragged along the strip. A button keeps the pointer from
    /// its press to its release, so the drag is followed on the button itself; the tab is moved when it is let go,
    /// to the position of the tab under the pointer, which is marked meanwhile.
    /// </summary>
    private void AttachTabGestures(Button select, Border chrome)
    {
        AttachPathDropTarget(chrome);
        select.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTabDragPressed), true);
        select.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnTabDragMoved), true);
        select.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTabDragReleased), true);
        // A button gives the pointer up in its own handling of the release, before a handler added here hears of
        // the release: the loss of the pointer is then where the drag ends.
        select.PointerCaptureLost += OnTabDragReleased;
    }

    private void OnTabDragPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { Tag: PaneTab tab } button
            && e.GetCurrentPoint(button).Properties.IsLeftButtonPressed
            && TabHostOf(tab.Pane) is { } host)
        {
            _tabDrag = new TabDrag(tab, button, host, e.GetCurrentPoint(host).Position.X, Moving: false);
        }
    }

    private void OnTabDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_tabDrag is not { } drag || !ReferenceEquals(sender, drag.Button))
        {
            return;
        }

        var point = e.GetCurrentPoint(drag.Host);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndTabDrag();
            return;
        }

        if (!drag.Moving && Math.Abs(point.Position.X - drag.StartX) < TabDragThreshold)
        {
            return;
        }

        _tabDrag = drag with { Moving = true };
        var chromes = TabChromes(drag.Host);
        if (TabChromeOf(drag.Button) is { } own)
        {
            own.Opacity = 0.6;
        }

        var index = StripReorder.TargetIndex(point.Position.X, TabEdges(drag.Host, chromes));
        ShowDropTargetChrome(index < chromes.Count && !ReferenceEquals(chromes[index], TabChromeOf(drag.Button)) ? chromes[index] : null);
    }

    private void OnTabDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_tabDrag is not { } drag)
        {
            return;
        }

        EndTabDrag();
        if (!drag.Moving || _workspace is null)
        {
            // A click: the button's own Click selects the tab.
            return;
        }

        var point = e.GetCurrentPoint(drag.Host);
        if (point.Properties.IsLeftButtonPressed)
        {
            // The pointer was taken away with the button still down: the drag is off, and nothing moves.
            return;
        }

        var index = StripReorder.TargetIndex(point.Position.X, TabEdges(drag.Host, TabChromes(drag.Host)));
        _workspace.MoveTab(drag.Tab.TabId, drag.Tab.Pane, index);
    }

    private void EndTabDrag()
    {
        if (_tabDrag is not { } drag)
        {
            return;
        }

        _tabDrag = null;
        ShowDropTargetChrome(null);
        if (TabChromeOf(drag.Button) is { } own)
        {
            own.Opacity = 1;
        }
    }

    private StackPanel? TabHostOf(PaneId pane) => pane == PaneId.Secondary ? SecondaryTabHost : PrimaryTabHost;

    /// <summary>A tab's outer element: the button sits in a row with the close button, inside the tab's border.</summary>
    private static Border? TabChromeOf(Button select) => (select.Parent as FrameworkElement)?.Parent as Border;

    private static List<Border> TabChromes(StackPanel host) =>
        host.Children.OfType<Border>().Where(chrome => chrome.Tag is PaneTab).ToList();

    private static List<(double Left, double Right)> TabEdges(StackPanel host, List<Border> chromes) =>
        chromes
            .Select(chrome =>
            {
                var left = chrome.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                return (left, left + chrome.ActualWidth);
            })
            .ToList();

    /// <summary>
    /// Ctrl+Shift+PageUp or PageDown on a tab moves it one place, the keyboard's way to what a drag does. The strip
    /// is rebuilt after the move, so the focus is put back on the moved tab.
    /// </summary>
    private bool TryMoveTabWithKeyboard(PaneTab tab, int delta)
    {
        if (_workspace is null)
        {
            return false;
        }

        var tabs = _workspace.Pane(tab.Pane).Tabs;
        var index = tabs.FindIndex(candidate => candidate.Id == tab.TabId);
        if (index < 0 || !_workspace.MoveTab(tab.TabId, tab.Pane, StripReorder.Step(index, tabs.Count, delta)))
        {
            return false;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            SyncFromWorkspace();
            TabHostOf(tab.Pane)?.Children.OfType<Border>()
                .Select(chrome => (chrome.Child as Panel)?.Children.OfType<Button>().FirstOrDefault())
                .FirstOrDefault(button => button?.Tag is PaneTab moved && moved == tab)
                ?.Focus(FocusState.Keyboard);
        });
        return true;
    }
}
