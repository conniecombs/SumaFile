using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SumaFile.Git.Views;

/// <summary>
/// The grip between the workbench's changes list and its diff preview (1.0.4's <c>DiffDivider</c>). Dragging it sets
/// the diff column's width, clamped to <see cref="MinDiffWidth"/>-<see cref="MaxDiffWidth"/> and to what leaves the
/// changes list its minimum; a double-tap resets it to <see cref="DefaultDiffWidth"/>. A <see cref="Grid"/> subclass,
/// so it can set the protected <see cref="UIElement.ProtectedCursor"/>.
/// </summary>
internal sealed class GitDiffSplitter : Grid
{
    public const double MinDiffWidth = 260;
    public const double MaxDiffWidth = 960;
    public const double DefaultDiffWidth = 420;

    private readonly Grid _content;
    private readonly ColumnDefinition _changesColumn;
    private readonly ColumnDefinition _dividerColumn;
    private readonly ColumnDefinition _diffColumn;
    private bool _dragging;

    public GitDiffSplitter(Grid content, ColumnDefinition changesColumn, ColumnDefinition dividerColumn, ColumnDefinition diffColumn)
    {
        _content = content;
        _changesColumn = changesColumn;
        _dividerColumn = dividerColumn;
        _diffColumn = diffColumn;

        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        AutomationProperties.SetName(this, "Resize Git diff preview");
        AutomationProperties.SetAutomationId(this, GitAutomationIds.DiffSplitter);
        ToolTipService.SetToolTip(this, "Drag to resize the diff preview. Double-click to reset it.");

        var line = new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Center };
        if (Xaml.Brush("SfBorderBrush") is { } border)
        {
            line.Fill = border;
        }

        Children.Add(line);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        DoubleTapped += OnDoubleTapped;
    }

    /// <summary>The diff width for a requested <paramref name="width"/> in a content area <paramref name="usableWidth"/> wide.</summary>
    public static double Clamp(double width, double usableWidth, double changesMinWidth)
    {
        var max = Math.Clamp(usableWidth - changesMinWidth, MinDiffWidth, MaxDiffWidth);
        return Math.Clamp(double.IsFinite(width) ? width : DefaultDiffWidth, MinDiffWidth, max);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _content.ActualWidth <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(_content).Position;
        var usable = Math.Max(520, _content.ActualWidth - _dividerColumn.ActualWidth);
        _diffColumn.Width = new GridLength(Clamp(usable - point.X, usable, _changesColumn.MinWidth));
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _diffColumn.Width = new GridLength(DefaultDiffWidth);
        e.Handled = true;
    }
}
