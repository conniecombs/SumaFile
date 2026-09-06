using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SimpleFile.App;

public sealed partial class GitWorkbenchView : UserControl
{
    private bool _diffDividerDragging;

    public GitWorkbenchView()
    {
        InitializeComponent();
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler? FetchRequested;
    public event EventHandler? PullRequested;
    public event EventHandler? PushRequested;
    public event EventHandler? CommitRequested;
    public event EventHandler? StageRequested;
    public event EventHandler? UnstageRequested;
    public event EventHandler? DiscardRequested;
    public event EventHandler? DiffRequested;
    public event EventHandler? HostToggleRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? SelectionChanged;

    public GitWorkbenchViewModel? ViewModel { get; private set; }

    public GitChangeRow[] SelectedChanges =>
        ChangesList.SelectedItems.OfType<GitChangeRow>().ToArray();

    public void Start(GitWorkbenchViewModel viewModel)
    {
        ViewModel = viewModel;
        Root.DataContext = viewModel;
    }

    public void ClearSelection()
    {
        ChangesList.SelectedItems.Clear();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnFetchClicked(object sender, RoutedEventArgs e) => FetchRequested?.Invoke(this, EventArgs.Empty);

    private void OnPullClicked(object sender, RoutedEventArgs e) => PullRequested?.Invoke(this, EventArgs.Empty);

    private void OnPushClicked(object sender, RoutedEventArgs e) => PushRequested?.Invoke(this, EventArgs.Empty);

    private void OnCommitClicked(object sender, RoutedEventArgs e) => CommitRequested?.Invoke(this, EventArgs.Empty);

    private void OnStageClicked(object sender, RoutedEventArgs e) => StageRequested?.Invoke(this, EventArgs.Empty);

    private void OnUnstageClicked(object sender, RoutedEventArgs e) => UnstageRequested?.Invoke(this, EventArgs.Empty);

    private void OnDiscardClicked(object sender, RoutedEventArgs e) => DiscardRequested?.Invoke(this, EventArgs.Empty);

    private void OnDiffClicked(object sender, RoutedEventArgs e) => DiffRequested?.Invoke(this, EventArgs.Empty);

    private void OnHostToggleClicked(object sender, RoutedEventArgs e) => HostToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClicked(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnChangesSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectionChanged?.Invoke(this, EventArgs.Empty);

    private void OnDiffDividerPressed(object sender, PointerRoutedEventArgs e)
    {
        _diffDividerDragging = true;
        DiffDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDiffDividerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_diffDividerDragging || ContentGrid.ActualWidth <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(ContentGrid).Position;
        var usableWidth = Math.Max(520, ContentGrid.ActualWidth - DiffDividerColumn.ActualWidth);
        var previewWidth = usableWidth - point.X;
        var maxPreviewWidth = Math.Max(260, usableWidth - ChangesColumn.MinWidth);
        DiffPreviewColumn.Width = new GridLength(Math.Clamp(previewWidth, DiffPreviewColumn.MinWidth, maxPreviewWidth));
        e.Handled = true;
    }

    private void OnDiffDividerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_diffDividerDragging)
        {
            return;
        }

        _diffDividerDragging = false;
        DiffDivider.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnDiffDividerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        DiffPreviewColumn.Width = new GridLength(420);
        e.Handled = true;
    }
}
