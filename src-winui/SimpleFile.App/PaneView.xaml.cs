using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimpleFile.Core;
using Windows.Foundation;

namespace SimpleFile.App;

/// <summary>
/// One file pane (tabs, path bar, context bar, list/details views, marquee, empty state).
/// The window hosts two instances distinguished only by <see cref="Pane"/>.
/// </summary>
public sealed partial class PaneView : UserControl
{
    public static readonly DependencyProperty PaneProperty = DependencyProperty.Register(
        nameof(Pane),
        typeof(PaneId),
        typeof(PaneView),
        new PropertyMetadata(PaneId.Primary, OnPaneChanged));

    public PaneView()
    {
        InitializeComponent();
        ApplyPane(PaneId.Primary);
        PartDetailsFileList.SelectionChanged += (_, e) => DetailsSelectionChanged?.Invoke(this, e);
        PartDetailsFileList.RowInvoked += (_, e) => DetailsRowInvoked?.Invoke(this, e);
        PartDetailsFileList.RowContextRequested += (_, e) => DetailsRowContextRequested?.Invoke(this, e);
        PartDetailsFileList.BackgroundContextRequested += (_, e) => DetailsBackgroundContextRequested?.Invoke(this, e);
        PartDetailsFileList.RowsDragStarting += (_, e) => DetailsRowsDragStarting?.Invoke(this, e);
        PartDetailsFileList.RowsDragCompleted += (_, e) => DetailsRowsDragCompleted?.Invoke(this, e);
    }

    public PaneId Pane
    {
        get => (PaneId)GetValue(PaneProperty);
        set => SetValue(PaneProperty, value);
    }

    private static void OnPaneChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is PaneView view && args.NewValue is PaneId pane)
        {
            view.ApplyPane(pane);
        }
    }

    private void ApplyPane(PaneId pane)
    {
        var side = pane == PaneId.Secondary ? "Right" : "Left";
        PartDetailsFileList.Pane = pane;
        PartPaneCaptionText.Text = side;
        AutomationProperties.SetName(PartDetailsFileList, $"{side} pane detail files");
        AutomationProperties.SetName(PartFileList, $"{side} pane files");
        if (pane == PaneId.Secondary)
        {
            // The right pane stays hidden until the window enables dual-pane mode.
            PartPaneRoot.Visibility = Visibility.Collapsed;
        }
    }

    public Grid Root => PartPaneRoot;
    public PaneContextBar ContextBar => PartContextBar;
    public Border ActivePaneRail => PartActivePaneRail;
    public Grid PaneHeader => PartPaneHeader;
    public StackPanel PaneCaption => PartPaneCaption;
    public Rectangle PaneCaptionRail => PartPaneCaptionRail;
    public TextBlock PaneCaptionText => PartPaneCaptionText;
    public StackPanel TabHost => PartTabHost;
    public Grid PathBar => PartPathBar;
    public ScrollViewer BreadcrumbScroller => PartBreadcrumbScroller;
    public StackPanel BreadcrumbHost => PartBreadcrumbHost;
    public TextBox PathInput => PartPathInput;
    public Button EditPathButton => PartEditPathButton;
    public ScrollBar DetailsHorizontalScrollBar => PartDetailsHorizontalScrollBar;
    public Grid FileSurface => PartFileSurface;
    public Grid FileViewport => PartFileViewport;
    public Grid ColumnHeader => PartColumnHeader;
    public DetailsFileListView DetailsFileList => PartDetailsFileList;
    public ListView FileList => PartFileList;
    public Canvas MarqueeCanvas => PartMarqueeCanvas;
    public Rectangle MarqueeRect => PartMarqueeRect;
    public StackPanel EmptyState => PartEmptyState;
    public TextBlock EmptyTitle => PartEmptyTitle;
    public TextBlock EmptyHint => PartEmptyHint;

    public event KeyEventHandler? PathKeyDown;
    public event RoutedEventHandler? PathLostFocus;
    public event TextChangedEventHandler? PathTextChanged;
    public event RoutedEventHandler? EditPath;
    public event PointerEventHandler? MarqueePressed;
    public event PointerEventHandler? MarqueePointerMoved;
    public event PointerEventHandler? MarqueePointerReleased;
    public event PointerEventHandler? MarqueePointerCanceled;
    public event DoubleTappedEventHandler? FileDoubleTapped;
    public event DragItemsStartingEventHandler? FileDragItemsStarting;
    public event TypedEventHandler<ListViewBase, DragItemsCompletedEventArgs>? FileDragItemsCompleted;
    public event DragEventHandler? FileDragLeave;
    public event DragEventHandler? FileDragOver;
    public event DragEventHandler? FileDrop;
    public event KeyEventHandler? FileKeyDown;
    public event RightTappedEventHandler? FileRightTapped;
    public event SelectionChangedEventHandler? FileSelectionChanged;
    public event EventHandler<object>? FileListContextOpening;
    public event EventHandler<ContextRequestedEventArgs>? FileRowContextRequested;
    public event RangeBaseValueChangedEventHandler? DetailsHorizontalScrollChanged;
    public event EventHandler<DetailsFileSelectionChangedEventArgs>? DetailsSelectionChanged;
    public event EventHandler<DetailsFileRowEventArgs>? DetailsRowInvoked;
    public event EventHandler<DetailsFileRowContextEventArgs>? DetailsRowContextRequested;
    public event EventHandler<DetailsFileBackgroundContextEventArgs>? DetailsBackgroundContextRequested;
    public event EventHandler<DetailsFileRowsDragStartingEventArgs>? DetailsRowsDragStarting;
    public event EventHandler? DetailsRowsDragCompleted;
    public event KeyEventHandler? DetailsFileKeyDown;

    private void OnPathKeyDown(object sender, KeyRoutedEventArgs e) => PathKeyDown?.Invoke(sender, e);

    private void OnPathLostFocus(object sender, RoutedEventArgs e) => PathLostFocus?.Invoke(sender, e);

    private void OnPathTextChanged(object sender, TextChangedEventArgs e) => PathTextChanged?.Invoke(sender, e);

    private void OnEditPath(object sender, RoutedEventArgs e) => EditPath?.Invoke(sender, e);

    private void OnPathBarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PartPathInput.Visibility == Visibility.Visible)
        {
            return;
        }

        for (var current = e.OriginalSource as DependencyObject;
            current is not null && !ReferenceEquals(current, PartPathBar);
            current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBox)
            {
                return;
            }
        }

        e.Handled = true;
        EditPath?.Invoke(this, new RoutedEventArgs());
    }

    private void OnDetailsHorizontalScrollChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        DetailsHorizontalScrollChanged?.Invoke(sender, e);

    private void OnDetailsFileKeyDown(object sender, KeyRoutedEventArgs e) =>
        DetailsFileKeyDown?.Invoke(sender, e);

    private void OnMarqueePressed(object sender, PointerRoutedEventArgs e) => MarqueePressed?.Invoke(sender, e);

    private void OnMarqueePointerMoved(object sender, PointerRoutedEventArgs e) => MarqueePointerMoved?.Invoke(sender, e);

    private void OnMarqueePointerReleased(object sender, PointerRoutedEventArgs e) => MarqueePointerReleased?.Invoke(sender, e);

    private void OnMarqueePointerCanceled(object sender, PointerRoutedEventArgs e) => MarqueePointerCanceled?.Invoke(sender, e);

    private void OnFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        FileDoubleTapped?.Invoke(sender, e);

    private void OnFileDragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        FileDragItemsStarting?.Invoke(sender, e);

    private void OnFileDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e) =>
        FileDragItemsCompleted?.Invoke(sender, e);

    private void OnFileDragLeave(object sender, DragEventArgs e) => FileDragLeave?.Invoke(sender, e);

    private void OnFileDragOver(object sender, DragEventArgs e) => FileDragOver?.Invoke(sender, e);

    private void OnFileDrop(object sender, DragEventArgs e) => FileDrop?.Invoke(sender, e);

    private void OnFileKeyDown(object sender, KeyRoutedEventArgs e) => FileKeyDown?.Invoke(sender, e);

    private void OnFileRightTapped(object sender, RightTappedRoutedEventArgs e) =>
        FileRightTapped?.Invoke(sender, e);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FileSelectionChanged?.Invoke(sender, e);

    private void OnFileListContextOpening(object sender, object e) =>
        FileListContextOpening?.Invoke(sender, e);

    private void OnFileRowContextRequested(object sender, ContextRequestedEventArgs e) =>
        FileRowContextRequested?.Invoke(sender, e);
}
