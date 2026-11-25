using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;

namespace SimpleFile.App;

// Rubber-band selection in a file pane, in every view. It starts with a press on the blank space around the rows
// (a press on a row selects it or starts dragging it) and selects the rows on screen that the rectangle touches;
// with Ctrl held it reverses them in the selection it started from. The rules are MarqueeSelection's, in Core.
public sealed partial class MainWindow
{
    private bool _isMarqueeDragging;
    private Windows.Foundation.Point _marqueeStartPoint;
    private PaneId _marqueePane;

    // The Details view keeps its rows in its own control; the other views are the pane's plain list.
    private bool _marqueeInDetails;
    private readonly HashSet<object> _marqueeInitialSelection = [];

    private void OnPrimaryMarqueePressed(object sender, PointerRoutedEventArgs e)
        => BeginMarquee(sender, e, PaneId.Primary, PrimaryFileList, PrimaryMarqueeCanvas);

    private void OnSecondaryMarqueePressed(object sender, PointerRoutedEventArgs e)
        => BeginMarquee(sender, e, PaneId.Secondary, SecondaryFileList, SecondaryMarqueeCanvas);

    private void BeginMarquee(object sender, PointerRoutedEventArgs e, PaneId pane, ListView list, Canvas canvas)
    {
        var details = pane == PaneId.Secondary ? SecondaryDetailsFileList : PrimaryDetailsFileList;
        var inDetails = details.Visibility == Visibility.Visible;

        var props = e.GetCurrentPoint((UIElement)sender).Properties;
        if (!props.IsLeftButtonPressed) return;

        var source = e.OriginalSource as DependencyObject;
        if (inDetails ? details.IsInsideRow(source) : IsInsideListViewItem(source, list)) return;

        _marqueePane = pane;
        _marqueeInDetails = inDetails;
        _marqueeStartPoint = e.GetCurrentPoint(canvas).Position;
        _isMarqueeDragging = true;

        if (sender is UIElement container)
        {
            container.CapturePointer(e.Pointer);
        }

        _marqueeInitialSelection.Clear();
        var modifiers = e.KeyModifiers;
        if ((modifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
        {
            IEnumerable<object> selected = inDetails ? details.SelectedRows : list.SelectedItems;
            foreach (var item in selected)
            {
                _marqueeInitialSelection.Add(item);
            }
        }
        else if (inDetails)
        {
            details.ClearSelection();
        }
        else
        {
            list.SelectedItems.Clear();
        }

        var rect = pane == PaneId.Secondary ? SecondaryMarqueeRect : PrimaryMarqueeRect;
        Canvas.SetLeft(rect, _marqueeStartPoint.X);
        Canvas.SetTop(rect, _marqueeStartPoint.Y);
        rect.Width = 0;
        rect.Height = 0;
        rect.Visibility = Visibility.Visible;

        e.Handled = true;
    }

    private void OnMarqueePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMarqueeDragging) return;

        var canvas = _marqueePane == PaneId.Secondary ? SecondaryMarqueeCanvas : PrimaryMarqueeCanvas;
        var rect = _marqueePane == PaneId.Secondary ? SecondaryMarqueeRect : PrimaryMarqueeRect;
        var list = _marqueePane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;

        var currentPoint = e.GetCurrentPoint(canvas).Position;
        var (x, y, width, height) = MarqueeSelection.Bounds(_marqueeStartPoint.X, _marqueeStartPoint.Y, currentPoint.X, currentPoint.Y);

        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = width;
        rect.Height = height;

        var marqueeBounds = new Windows.Foundation.Rect(x, y, width, height);
        UpdateMarqueeSelection(list, canvas, marqueeBounds, e.KeyModifiers);

        e.Handled = true;
    }

    private void OnMarqueePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMarqueeDragging) return;
        FinishMarquee(sender as UIElement, e.Pointer);
        e.Handled = true;
    }

    private void OnMarqueePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMarqueeDragging) return;
        FinishMarquee(sender as UIElement, e.Pointer);
    }

    private void FinishMarquee(UIElement? container, Pointer pointer)
    {
        _isMarqueeDragging = false;

        var rect = _marqueePane == PaneId.Secondary ? SecondaryMarqueeRect : PrimaryMarqueeRect;
        rect.Visibility = Visibility.Collapsed;
        rect.Width = 0;
        rect.Height = 0;

        if (container is not null)
        {
            container.ReleasePointerCapture(pointer);
        }

        _marqueeInitialSelection.Clear();
    }

    private void UpdateMarqueeSelection(ListView list, Canvas canvas, Windows.Foundation.Rect marqueeBounds, Windows.System.VirtualKeyModifiers modifiers)
    {
        var toggle = (modifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        if (_marqueeInDetails)
        {
            var details = _marqueePane == PaneId.Secondary ? SecondaryDetailsFileList : PrimaryDetailsFileList;
            details.SelectRows(MarqueeSelection.Resolve(
                _marqueeInitialSelection.OfType<FileRow>(),
                details.RowsIntersecting(marqueeBounds, canvas),
                toggle));
            return;
        }

        var touched = new List<object>();
        foreach (var (index, container) in DetailsFileListView.RealizedContainers(list))
        {
            var itemBounds = container.TransformToVisual(canvas).TransformBounds(
                new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (MarqueeSelection.Intersects(
                    marqueeBounds.X, marqueeBounds.Y, marqueeBounds.Width, marqueeBounds.Height,
                    itemBounds.X, itemBounds.Y, itemBounds.Width, itemBounds.Height))
            {
                touched.Add(list.Items[index]);
            }
        }

        var desired = MarqueeSelection.Resolve(_marqueeInitialSelection, touched, toggle);
        var toRemove = new List<object>();
        foreach (var current in list.SelectedItems)
        {
            if (!desired.Contains(current))
            {
                toRemove.Add(current);
            }
        }

        foreach (var item in toRemove)
        {
            list.SelectedItems.Remove(item);
        }

        foreach (var item in desired)
        {
            if (!list.SelectedItems.Contains(item))
            {
                list.SelectedItems.Add(item);
            }
        }
    }

    private static bool IsInsideListViewItem(DependencyObject? source, ListView list)
    {
        while (source is not null)
        {
            if (source is ListViewItem) return true;
            if (source == list) return false;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
