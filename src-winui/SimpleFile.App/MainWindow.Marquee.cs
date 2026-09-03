using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private bool _isMarqueeDragging;
    private Windows.Foundation.Point _marqueeStartPoint;
    private PaneId _marqueePane;
    private readonly HashSet<object> _marqueeInitialSelection = [];

    private void OnPrimaryMarqueePressed(object sender, PointerRoutedEventArgs e)
        => BeginMarquee(sender, e, PaneId.Primary, PrimaryFileList, PrimaryMarqueeCanvas);

    private void OnSecondaryMarqueePressed(object sender, PointerRoutedEventArgs e)
        => BeginMarquee(sender, e, PaneId.Secondary, SecondaryFileList, SecondaryMarqueeCanvas);

    private void BeginMarquee(object sender, PointerRoutedEventArgs e, PaneId pane, ListView list, Canvas canvas)
    {
        var props = e.GetCurrentPoint((UIElement)sender).Properties;
        if (!props.IsLeftButtonPressed) return;

        if (IsInsideListViewItem(e.OriginalSource as DependencyObject, list)) return;

        _marqueePane = pane;
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
            foreach (var item in list.SelectedItems)
            {
                _marqueeInitialSelection.Add(item);
            }
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

        double x = Math.Min(_marqueeStartPoint.X, currentPoint.X);
        double y = Math.Min(_marqueeStartPoint.Y, currentPoint.Y);
        double width = Math.Abs(currentPoint.X - _marqueeStartPoint.X);
        double height = Math.Abs(currentPoint.Y - _marqueeStartPoint.Y);

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
        bool ctrlPressed = (modifiers & Windows.System.VirtualKeyModifiers.Control) != 0;

        for (int i = 0; i < list.Items.Count; i++)
        {
            var rawItem = list.Items[i];
            if (list.ContainerFromIndex(i) is not ListViewItem container) continue;

            var transform = container.TransformToVisual(canvas);
            var itemBounds = transform.TransformBounds(
                new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));

            bool intersects = RectIntersects(marqueeBounds, itemBounds);

            if (ctrlPressed)
            {
                bool wasOriginallySelected = _marqueeInitialSelection.Contains(rawItem);
                bool shouldBeSelected = intersects ? !wasOriginallySelected : wasOriginallySelected;
                SetItemSelected(list, rawItem, shouldBeSelected);
            }
            else
            {
                SetItemSelected(list, rawItem, intersects);
            }
        }
    }

    private static bool RectIntersects(Windows.Foundation.Rect a, Windows.Foundation.Rect b)
    {
        return !(a.Right < b.Left || a.Left > b.Right || a.Bottom < b.Top || a.Top > b.Bottom);
    }

    private static void SetItemSelected(ListView list, object item, bool selected)
    {
        if (selected)
        {
            if (!list.SelectedItems.Contains(item))
            {
                list.SelectedItems.Add(item);
            }
        }
        else
        {
            list.SelectedItems.Remove(item);
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
