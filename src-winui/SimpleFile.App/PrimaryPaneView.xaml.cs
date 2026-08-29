using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace SimpleFile.App;

public sealed partial class PrimaryPaneView : UserControl
{
    public PrimaryPaneView()
    {
        InitializeComponent();
    }

    public Grid Root => PrimaryPaneRoot;
    public Border ActivePaneRail => PrimaryActivePaneRail;
    public Grid PaneHeader => PrimaryPaneHeader;
    public StackPanel PaneCaption => PrimaryPaneCaption;
    public Rectangle PaneCaptionRail => PrimaryPaneCaptionRail;
    public TextBlock PaneCaptionText => PrimaryPaneCaptionText;
    public StackPanel TabHost => PrimaryTabHost;
    public Grid PathBar => PrimaryPathBar;
    public ScrollViewer BreadcrumbScroller => PrimaryBreadcrumbScroller;
    public StackPanel BreadcrumbHost => PrimaryBreadcrumbHost;
    public TextBox PathInput => PrimaryPathInput;
    public Button EditPathButton => PrimaryEditPathButton;
    public ScrollViewer ColumnHeaderScroller => PrimaryColumnHeaderScroller;
    public Grid ColumnHeader => PrimaryColumnHeader;
    public ListView FileList => PrimaryFileList;
    public Canvas MarqueeCanvas => PrimaryMarqueeCanvas;
    public Rectangle MarqueeRect => PrimaryMarqueeRect;
    public StackPanel EmptyState => PrimaryEmptyState;
    public TextBlock EmptyTitle => PrimaryEmptyTitle;
    public TextBlock EmptyHint => PrimaryEmptyHint;

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

    private void OnPrimaryPathKeyDown(object sender, KeyRoutedEventArgs e) => PathKeyDown?.Invoke(sender, e);

    private void OnPrimaryPathLostFocus(object sender, RoutedEventArgs e) => PathLostFocus?.Invoke(sender, e);

    private void OnPrimaryPathTextChanged(object sender, TextChangedEventArgs e) => PathTextChanged?.Invoke(sender, e);

    private void OnEditPrimaryPath(object sender, RoutedEventArgs e) => EditPath?.Invoke(sender, e);

    private void OnPrimaryMarqueePressed(object sender, PointerRoutedEventArgs e) => MarqueePressed?.Invoke(sender, e);

    private void OnMarqueePointerMoved(object sender, PointerRoutedEventArgs e) => MarqueePointerMoved?.Invoke(sender, e);

    private void OnMarqueePointerReleased(object sender, PointerRoutedEventArgs e) => MarqueePointerReleased?.Invoke(sender, e);

    private void OnMarqueePointerCanceled(object sender, PointerRoutedEventArgs e) => MarqueePointerCanceled?.Invoke(sender, e);

    private void OnPrimaryFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        FileDoubleTapped?.Invoke(sender, e);

    private void OnFileDragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        FileDragItemsStarting?.Invoke(sender, e);

    private void OnFileDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e) =>
        FileDragItemsCompleted?.Invoke(sender, e);

    private void OnFileDragLeave(object sender, DragEventArgs e) => FileDragLeave?.Invoke(sender, e);

    private void OnPrimaryFileDragOver(object sender, DragEventArgs e) => FileDragOver?.Invoke(sender, e);

    private void OnPrimaryFileDrop(object sender, DragEventArgs e) => FileDrop?.Invoke(sender, e);

    private void OnPrimaryFileKeyDown(object sender, KeyRoutedEventArgs e) => FileKeyDown?.Invoke(sender, e);

    private void OnPrimaryFileRightTapped(object sender, RightTappedRoutedEventArgs e) =>
        FileRightTapped?.Invoke(sender, e);

    private void OnPrimarySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FileSelectionChanged?.Invoke(sender, e);

    private void OnPrimaryFileListContextOpening(object sender, object e) =>
        FileListContextOpening?.Invoke(sender, e);

    private void OnFileRowContextRequested(object sender, ContextRequestedEventArgs e) =>
        FileRowContextRequested?.Invoke(sender, e);
}
