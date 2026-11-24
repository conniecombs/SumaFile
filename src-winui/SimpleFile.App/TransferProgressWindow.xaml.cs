using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using Windows.ApplicationModel.DataTransfer;

namespace SimpleFile.App;

public sealed partial class TransferProgressWindow : Window
{
    private readonly HashSet<string> _expandedOperationIds = new(StringComparer.Ordinal);
    private TransferManagerViewModel? _manager;
    private ISettingsBackend? _settings;
    private bool _syncingContainer;

    public TransferProgressWindow()
    {
        InitializeComponent();
        // Escape closes the window; transfers keep running in the background.
        SecondaryWindowChrome.Apply(this, "Transfers", 800, 620, mica: false, cancel: () => CloseRequested?.Invoke());

        Closed += (_, _) =>
        {
            IsClosed = true;
            DetachManager();
        };
    }

    public bool IsClosed { get; private set; }

    public event Action<TransferOperationViewModel>? CancelRequested;
    public event Action? ClearCompletedRequested;
    public event Action? CloseRequested;

    public void Start(TransferManagerViewModel manager, ISettingsBackend? settings = null)
    {
        _settings = settings;
        if (!ReferenceEquals(_manager, manager))
        {
            DetachManager();
            _expandedOperationIds.Clear();
            _manager = manager;
            _manager.Operations.CollectionChanged += OnOperationsCollectionChanged;
        }

        Root.DataContext = manager;
        TransferList.ItemsSource = manager.Operations;
        UpdateDetailsHeights();
    }

    private async void OnDefaultsClicked(object sender, RoutedEventArgs e)
    {
        if (_settings is not null) await TransferOptionsDialog.ShowAsync(Root.XamlRoot, _settings);
    }

    private async void OnTaskOptionsClicked(object sender, RoutedEventArgs e)
    {
        // A plugin or archive-engine row never reads transfer.overrides.v1: its settings live in its plugin.
        if (_settings is not null && sender is Button { Tag: TransferOperationViewModel { IsExternal: false } operation })
            await TransferOptionsDialog.ShowAsync(Root.XamlRoot, _settings, operation);
    }

    // A folder heading or a file line in Details: its Tag is the full path, which the line may show shortened.
    private void OnActivePathRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string { Length: > 0 } path } line)
        {
            return;
        }

        var copy = new MenuFlyoutItem { Text = "Copy path" };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
        };
        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        menu.ShowAt(line, new FlyoutShowOptions { Position = e.GetPosition(line) });
        e.Handled = true;
    }

    private void OnCancelOperationClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TransferOperationViewModel operation })
        {
            CancelRequested?.Invoke(operation);
        }
    }

    private void OnClearCompletedClicked(object sender, RoutedEventArgs e)
    {
        ClearCompletedRequested?.Invoke();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }

    private void DetachManager()
    {
        if (_manager is not null)
        {
            _manager.Operations.CollectionChanged -= OnOperationsCollectionChanged;
            _manager = null;
        }
    }

    private void OnOperationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_manager is not null && _expandedOperationIds.Count > 0)
        {
            var liveIds = _manager.Operations.Select(operation => operation.Id).ToHashSet(StringComparer.Ordinal);
            _expandedOperationIds.RemoveWhere(id => !liveIds.Contains(id));
        }

        UpdateDetailsHeights();
        DispatcherQueue.TryEnqueue(UpdateDetailsHeights);
    }

    private void OnTransferContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container
            || !TryGetDetailsElements(container, out var expander, out _, out var scrollViewer))
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            _syncingContainer = true;
            try
            {
                expander.IsExpanded = false;
                scrollViewer.Height = double.NaN;
            }
            finally
            {
                _syncingContainer = false;
            }

            return;
        }

        if (args.Item is TransferOperationViewModel operation)
        {
            var shouldExpand = _expandedOperationIds.Contains(operation.Id);
            if (expander.IsExpanded != shouldExpand)
            {
                _syncingContainer = true;
                try
                {
                    expander.IsExpanded = shouldExpand;
                }
                finally
                {
                    _syncingContainer = false;
                }
            }

            if (!shouldExpand)
            {
                scrollViewer.Height = double.NaN;
            }
        }
    }

    private void OnDetailsExpanderExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (!_syncingContainer && sender.DataContext is TransferOperationViewModel operation)
        {
            _expandedOperationIds.Add(operation.Id);
        }

        UpdateDetailsHeights();
        DispatcherQueue.TryEnqueue(UpdateDetailsHeights);
    }

    private void OnDetailsExpanderCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        if (!_syncingContainer && sender.DataContext is TransferOperationViewModel operation)
        {
            _expandedOperationIds.Remove(operation.Id);
        }

        if (TryGetScrollViewer(sender) is { } scrollViewer)
        {
            scrollViewer.Height = double.NaN;
        }

        UpdateDetailsHeights();
        DispatcherQueue.TryEnqueue(UpdateDetailsHeights);
    }

    private void OnTransferListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDetailsHeights();

    private void OnTransferCardSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDetailsHeights();

    private void OnDetailsExpanderSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDetailsHeights();

    private void UpdateDetailsHeights()
    {
        if (IsClosed)
        {
            return;
        }

        var viewportHeight = TransferList.ActualHeight - TransferList.Padding.Top - TransferList.Padding.Bottom;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            return;
        }

        var itemCount = TransferList.Items.Count;
        if (itemCount == 0)
        {
            return;
        }

        var expandedScrollViewers = new List<ScrollViewer>();
        var expandedChromeHeights = new List<double>();
        var collapsedRowHeights = new List<double>();
        var unrealizedCount = 0;

        for (var index = 0; index < itemCount; index++)
        {
            if (TransferList.ContainerFromIndex(index) is not ListViewItem container)
            {
                unrealizedCount++;
                continue;
            }

            if (container.ActualHeight <= 0
                || !TryGetDetailsElements(container, out var expander, out var contentGrid, out var scrollViewer))
            {
                continue;
            }

            var outerHeight = container.ActualHeight + container.Margin.Top + container.Margin.Bottom;
            if (expander.IsExpanded)
            {
                if (!IsDetailsArranged(expander, contentGrid))
                {
                    // Wait for the newly expanding card's arrange pass so already-open rows do not jump.
                    return;
                }

                var chromeHeight = outerHeight - scrollViewer.ActualHeight;
                if (chromeHeight <= 0)
                {
                    return;
                }

                expandedScrollViewers.Add(scrollViewer);
                expandedChromeHeights.Add(chromeHeight);
            }
            else
            {
                collapsedRowHeights.Add(outerHeight);
            }
        }

        if (expandedScrollViewers.Count == 0)
        {
            return;
        }

        if (unrealizedCount > 0)
        {
            collapsedRowHeights.Add(TransferWindowLayout.CollapsedSiblingReserveCap);
        }

        var targetHeight = TransferWindowLayout.DetailsScrollHeight(
            viewportHeight,
            expandedChromeHeights,
            collapsedRowHeights);
        if (double.IsNaN(targetHeight))
        {
            return;
        }

        foreach (var scrollViewer in expandedScrollViewers)
        {
            if (!TransferWindowLayout.HeightMatches(scrollViewer.Height, targetHeight))
            {
                scrollViewer.Height = targetHeight;
            }
        }
    }

    private static bool TryGetDetailsElements(
        ListViewItem container,
        [NotNullWhen(true)] out Expander? expander,
        [NotNullWhen(true)] out Grid? contentGrid,
        [NotNullWhen(true)] out ScrollViewer? scrollViewer)
    {
        expander = null;
        contentGrid = null;
        scrollViewer = null;

        if (container.ContentTemplateRoot is not Border { Child: Grid cardGrid })
        {
            return false;
        }

        foreach (var child in cardGrid.Children)
        {
            if (child is Expander foundExpander)
            {
                expander = foundExpander;
                break;
            }
        }

        if (expander?.Content is not Grid foundContentGrid)
        {
            expander = null;
            return false;
        }

        contentGrid = foundContentGrid;
        scrollViewer = TryGetScrollViewer(expander);
        if (scrollViewer is null)
        {
            expander = null;
            contentGrid = null;
            return false;
        }

        return true;
    }

    private static ScrollViewer? TryGetScrollViewer(Expander expander)
    {
        if (expander.Content is not Grid contentGrid)
        {
            return null;
        }

        foreach (var child in contentGrid.Children)
        {
            if (child is ScrollViewer foundScrollViewer)
            {
                return foundScrollViewer;
            }
        }

        return null;
    }

    private static bool IsDetailsArranged(Expander expander, Grid contentGrid)
    {
        if (contentGrid.ActualHeight <= 0 || expander.ActualHeight <= contentGrid.ActualHeight)
        {
            return false;
        }

        DependencyObject? current = contentGrid;
        while (current is not null && !ReferenceEquals(current, expander))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed }
                || current is FrameworkElement { ActualHeight: <= 0 })
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return ReferenceEquals(current, expander);
    }
}
