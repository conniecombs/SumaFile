using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace SimpleFile.App;

public sealed partial class SecondaryPaneView : UserControl
{
    public SecondaryPaneView()
    {
        InitializeComponent();
    }

    public Grid Root => SecondaryPaneRoot;
    public Border ActivePaneRail => SecondaryActivePaneRail;
    public Grid PaneHeader => SecondaryPaneHeader;
    public StackPanel PaneCaption => SecondaryPaneCaption;
    public Rectangle PaneCaptionRail => SecondaryPaneCaptionRail;
    public TextBlock PaneCaptionText => SecondaryPaneCaptionText;
    public StackPanel TabHost => SecondaryTabHost;
    public Grid PathBar => SecondaryPathBar;
    public ScrollViewer BreadcrumbScroller => SecondaryBreadcrumbScroller;
    public StackPanel BreadcrumbHost => SecondaryBreadcrumbHost;
    public TextBox PathInput => SecondaryPathInput;
    public Button EditPathButton => SecondaryEditPathButton;
    public ScrollViewer ColumnHeaderScroller => SecondaryColumnHeaderScroller;
    public Grid ColumnHeader => SecondaryColumnHeader;
    public ListView FileList => SecondaryFileList;
    public Canvas MarqueeCanvas => SecondaryMarqueeCanvas;
    public Rectangle MarqueeRect => SecondaryMarqueeRect;
    public StackPanel EmptyState => SecondaryEmptyState;
    public TextBlock EmptyTitle => SecondaryEmptyTitle;
    public TextBlock EmptyHint => SecondaryEmptyHint;

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

    private void OnSecondaryPathKeyDown(object sender, KeyRoutedEventArgs e) => PathKeyDown?.Invoke(sender, e);

    private void OnSecondaryPathLostFocus(object sender, RoutedEventArgs e) => PathLostFocus?.Invoke(sender, e);

    private void OnSecondaryPathTextChanged(object sender, TextChangedEventArgs e) => PathTextChanged?.Invoke(sender, e);

    private void OnEditSecondaryPath(object sender, RoutedEventArgs e) => EditPath?.Invoke(sender, e);

    private void OnSecondaryMarqueePressed(object sender, PointerRoutedEventArgs e) => MarqueePressed?.Invoke(sender, e);

    private void OnMarqueePointerMoved(object sender, PointerRoutedEventArgs e) => MarqueePointerMoved?.Invoke(sender, e);

    private void OnMarqueePointerReleased(object sender, PointerRoutedEventArgs e) => MarqueePointerReleased?.Invoke(sender, e);

    private void OnMarqueePointerCanceled(object sender, PointerRoutedEventArgs e) => MarqueePointerCanceled?.Invoke(sender, e);

    private void OnSecondaryFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        FileDoubleTapped?.Invoke(sender, e);

    private void OnFileDragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        FileDragItemsStarting?.Invoke(sender, e);

    private void OnFileDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e) =>
        FileDragItemsCompleted?.Invoke(sender, e);

    private void OnFileDragLeave(object sender, DragEventArgs e) => FileDragLeave?.Invoke(sender, e);

    private void OnSecondaryFileDragOver(object sender, DragEventArgs e) => FileDragOver?.Invoke(sender, e);

    private void OnSecondaryFileDrop(object sender, DragEventArgs e) => FileDrop?.Invoke(sender, e);

    private void OnSecondaryFileKeyDown(object sender, KeyRoutedEventArgs e) => FileKeyDown?.Invoke(sender, e);

    private void OnSecondaryFileRightTapped(object sender, RightTappedRoutedEventArgs e) =>
        FileRightTapped?.Invoke(sender, e);

    private void OnSecondarySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FileSelectionChanged?.Invoke(sender, e);

    private void OnSecondaryFileListContextOpening(object sender, object e) =>
        FileListContextOpening?.Invoke(sender, e);

    private void OnFileRowContextRequested(object sender, ContextRequestedEventArgs e) =>
        FileRowContextRequested?.Invoke(sender, e);
}
