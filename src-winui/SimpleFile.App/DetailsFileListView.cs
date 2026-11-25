using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using SimpleFile.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace SimpleFile.App;

public sealed class DetailsFileSelectionChangedEventArgs(
    IReadOnlyList<FileRow> addedRows,
    IReadOnlyList<FileRow> removedRows)
    : EventArgs
{
    public IReadOnlyList<FileRow> AddedRows { get; } = addedRows;

    public IReadOnlyList<FileRow> RemovedRows { get; } = removedRows;
}

public sealed class DetailsFileRowEventArgs(FileRow row) : EventArgs
{
    public FileRow Row { get; } = row;
}

public sealed class DetailsFileRowContextEventArgs(
    FileRow row,
    Point? position,
    FrameworkElement anchor)
    : EventArgs
{
    public FileRow Row { get; } = row;

    public Point? Position { get; } = position;

    public FrameworkElement Anchor { get; } = anchor;
}

public sealed class DetailsFileBackgroundContextEventArgs(
    Point? position,
    FrameworkElement anchor)
    : EventArgs
{
    public Point? Position { get; } = position;

    public FrameworkElement Anchor { get; } = anchor;
}

public sealed class DetailsFileRowsDragStartingEventArgs(
    IReadOnlyList<FileRow> rows,
    DataPackage data)
    : EventArgs
{
    public IReadOnlyList<FileRow> Rows { get; } = rows;

    public DataPackage Data { get; } = data;

    public bool Cancel { get; set; }
}

public sealed class DetailsFileListView : UserControl
{
    public static readonly DependencyProperty PaneProperty = DependencyProperty.Register(
        nameof(Pane),
        typeof(PaneId),
        typeof(DetailsFileListView),
        new PropertyMetadata(PaneId.Primary));

    private readonly ListView _list;
    private readonly List<FileRow> _rows = [];
    private readonly List<FileRow> _selectedRows = [];
    private readonly HashSet<string> _selectedPathSet = new(StringComparer.OrdinalIgnoreCase);
    private INotifyCollectionChanged? _observableItems;
    private IEnumerable? _itemsSource;
    private IReadOnlyList<FileListColumn> _columns = [];
    private int _iconSize = UiSettings.NormalizeIconSize((int?)null);
    private double _horizontalOffset;
    private double _viewportWidth = 1;
    private int _anchorIndex = -1;
    private bool _suppressSelectionChanged;
    private int _updateDepth;
    private bool _pendingUpdateChanged;
    private HashSet<string>? _pendingUpdateSelectedPaths;
    private ScrollViewer? _scrollViewer;

    public DetailsFileListView()
    {
        IsTabStop = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _list = new ListView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SelectionMode = ListViewSelectionMode.Extended,
            IsMultiSelectCheckBoxEnabled = false,
            SingleSelectionFollowsFocus = false,
            IsItemClickEnabled = false,
            CanDragItems = true,
            CanReorderItems = false,
            AllowDrop = true,
            ItemTemplate = CreateItemTemplate(),
            ItemsPanel = CreateItemsPanelTemplate(),
            Padding = new Thickness(10, 4, 0, 6),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(_list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);

        _list.SelectionChanged += OnListSelectionChanged;
        _list.DoubleTapped += OnListDoubleTapped;
        _list.RightTapped += OnListRightTapped;
        _list.ContextRequested += OnListContextRequested;
        _list.DragItemsStarting += OnListDragItemsStarting;
        _list.DragItemsCompleted += (_, _) => RowsDragCompleted?.Invoke(this, EventArgs.Empty);
        _list.ContainerContentChanging += OnListContainerContentChanging;
        _list.Loaded += (_, _) =>
        {
            ApplyContainerStyle();
            AttachScrollViewerActivityHook();
        };
        Content = _list;
    }

    public event EventHandler<DetailsFileSelectionChangedEventArgs>? SelectionChanged;

    public event EventHandler<DetailsFileRowEventArgs>? RowInvoked;

    public event EventHandler<DetailsFileRowContextEventArgs>? RowContextRequested;

    /// <summary>Raised for a context gesture on blank space, after the selection is cleared.</summary>
    public event EventHandler<DetailsFileBackgroundContextEventArgs>? BackgroundContextRequested;

    public event EventHandler<DetailsFileRowsDragStartingEventArgs>? RowsDragStarting;

    public event EventHandler? RowsDragCompleted;

    /// <summary>The rows on screen changed: a scroll, or the rows themselves. See <see cref="VisibleRange"/>.</summary>
    public event EventHandler? ViewportChanged;

    public PaneId Pane
    {
        get => (PaneId)GetValue(PaneProperty);
        set => SetValue(PaneProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => _itemsSource;
        set
        {
            if (ReferenceEquals(_itemsSource, value))
            {
                return;
            }

            var selectedPaths = SelectedPathSet();
            if (_observableItems is not null)
            {
                _observableItems.CollectionChanged -= OnItemsChanged;
            }

            _itemsSource = value;
            _observableItems = value as INotifyCollectionChanged;
            if (_observableItems is not null)
            {
                _observableItems.CollectionChanged += OnItemsChanged;
            }

            _list.ItemsSource = value;
            RefreshRowsFromItemsSource();
            RestoreSelectionByPaths(selectedPaths);
        }
    }

    public IReadOnlyList<FileRow> SelectedRows => _selectedRows.ToArray();

    public FileRow? SelectedRow => _selectedRows.LastOrDefault();

    public int ItemCount => _rows.Count;

    /// <summary>
    /// The realized row view for <paramref name="row"/>, scrolling it into view first. Used by
    /// inline rename; null when the row is not in this list or could not be realized.
    /// </summary>
    public FileRowView? RowViewFor(FileRow row)
    {
        if (!_rows.Contains(row))
        {
            return null;
        }

        if (_list.ContainerFromItem(row) is not ListViewItem)
        {
            _list.ScrollIntoView(row);
            _list.UpdateLayout();
        }

        return _list.ContainerFromItem(row) is ListViewItem item
            ? FindDescendant<FileRowView>(item)
            : null;
    }

    public void ApplyDetailsLayout(
        ColumnLayout columns,
        int iconSize,
        double viewportWidth,
        double horizontalOffset)
    {
        _columns = columns.VisibleColumns
            .Select(column => new FileListColumn(
                column.Id,
                column.Label,
                column.Sort,
                column.Width,
                column.MinWidth,
                column.MaxWidth))
            .ToArray();
        _iconSize = UiSettings.NormalizeIconSize(iconSize);
        _viewportWidth = Math.Max(1, viewportWidth);
        _horizontalOffset = Math.Max(0, horizontalOffset);
        _list.Width = _viewportWidth;

        UpdateRealizedContainers();
        _list.InvalidateMeasure();
        InvalidateMeasure();
    }

    public void ClearSelection()
    {
        _list.SelectedItems.Clear();
    }

    public void SelectAll()
    {
        _list.SelectAll();
    }

    public void SelectPath(string? path, bool replaceSelection = false)
    {
        // Workspace syncs run on every background change; they must not collapse a
        // multi-selection the user made in the view, nor jump the list to an unchanged selection.
        // A deliberate move to one item (type-ahead) passes replaceSelection.
        var several = _selectedRows.Count > 1;
        if (several && !replaceSelection)
        {
            return;
        }

        var row = path is null
            ? null
            : _rows.FirstOrDefault(candidate => PathRules.PathsEqual(candidate.Path, path));
        if (row is not null && !several && ContainsSelection(row))
        {
            return;
        }

        SetSelection(row is null ? [] : [row]);
        if (row is not null)
        {
            _anchorIndex = _rows.IndexOf(row);
            _list.ScrollIntoView(row);
        }
    }

    public bool ContainsSelection(FileRow row) =>
        _selectedPathSet.Contains(row.Path);

    internal static IEnumerable<(int Index, ListViewItem Container)> RealizedContainers(ListViewBase list)
    {
        var count = list.Items.Count;
        if (count == 0)
        {
            yield break;
        }

        if (list.ItemsPanelRoot is ItemsStackPanel stack
            && stack.FirstVisibleIndex >= 0
            && stack.LastVisibleIndex >= stack.FirstVisibleIndex)
        {
            var last = Math.Min(stack.LastVisibleIndex, count - 1);
            for (var index = stack.FirstVisibleIndex; index <= last; index++)
            {
                if (list.ContainerFromIndex(index) is ListViewItem container)
                {
                    yield return (index, container);
                }
            }

            yield break;
        }

        if (list.ItemsPanelRoot is ItemsWrapGrid wrap
            && wrap.FirstVisibleIndex >= 0
            && wrap.LastVisibleIndex >= wrap.FirstVisibleIndex)
        {
            var last = Math.Min(wrap.LastVisibleIndex, count - 1);
            for (var index = wrap.FirstVisibleIndex; index <= last; index++)
            {
                if (list.ContainerFromIndex(index) is ListViewItem container)
                {
                    yield return (index, container);
                }
            }

            yield break;
        }

        if (list.ItemsPanelRoot is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is ListViewItem container)
                {
                    var index = list.IndexFromContainer(container);
                    if (index >= 0 && index < count)
                    {
                        yield return (index, container);
                    }
                }
            }
        }
    }

    /// <summary>Whether <paramref name="source"/>, the original source of a pointer event, lies in one of the rows.</summary>
    public bool IsInsideRow(DependencyObject? source)
    {
        for (var element = source; element is not null && !ReferenceEquals(element, this); element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is ListViewItem)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The rows on screen whose bounds, as <paramref name="relativeTo"/> sees them, meet <paramref name="bounds"/>.</summary>
    public IReadOnlyList<FileRow> RowsIntersecting(Rect bounds, UIElement relativeTo)
    {
        var rows = new List<FileRow>();
        foreach (var (index, container) in RealizedContainers(_list))
        {
            var row = container.TransformToVisual(relativeTo)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (index < _rows.Count
                && MarqueeSelection.Intersects(bounds.X, bounds.Y, bounds.Width, bounds.Height, row.X, row.Y, row.Width, row.Height))
            {
                rows.Add(_rows[index]);
            }
        }

        return rows;
    }

    /// <summary>
    /// Makes <paramref name="rows"/> the selection by removing and adding only the rows that differ, so
    /// <see cref="SelectionChanged"/> reports each change as it does for a click. For a marquee, which changes the
    /// selection a little on every pointer move.
    /// </summary>
    public void SelectRows(IReadOnlySet<FileRow> rows)
    {
        foreach (var selected in _selectedRows.Where(row => !rows.Contains(row)).ToArray())
        {
            _list.SelectedItems.Remove(selected);
        }

        foreach (var row in rows)
        {
            if (!_selectedRows.Contains(row))
            {
                _list.SelectedItems.Add(row);
            }
        }
    }

    /// <summary>The view of <paramref name="row"/> while it is on screen. Unlike <see cref="RowViewFor"/>, it scrolls nothing.</summary>
    public FileRowView? RealizedRowView(FileRow row) =>
        _list.ContainerFromItem(row) is ListViewItem item ? FindDescendant<FileRowView>(item) : null;

    public FileRow? RowFromPoint(Point point)
    {
        foreach (var (index, container) in RealizedContainers(_list))
        {
            var bounds = container.TransformToVisual(this)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(point))
            {
                return index < _rows.Count ? _rows[index] : container.Content as FileRow;
            }
        }

        return null;
    }

    public Rect? RowBounds(FileRow row, UIElement relativeTo)
    {
        if (_list.ContainerFromItem(row) is not ListViewItem container)
        {
            return null;
        }

        return container.TransformToVisual(relativeTo)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
    }

    private static DataTemplate CreateItemTemplate()
    {
        const string xaml = """
            <DataTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:local="using:SimpleFile.App">
                <local:FileRowView Row="{Binding}" />
            </DataTemplate>
            """;
        return (DataTemplate)XamlReader.Load(xaml);
    }

    private static ItemsPanelTemplate CreateItemsPanelTemplate()
    {
        const string xaml = """
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ItemsStackPanel Orientation="Vertical" />
            </ItemsPanelTemplate>
            """;
        return (ItemsPanelTemplate)XamlReader.Load(xaml);
    }

    private void ApplyContainerStyle()
    {
        if (Application.Current.Resources.TryGetValue("SfFileDetailsItemStyle", out var style)
            && style is Style itemStyle
            && !ReferenceEquals(_list.ItemContainerStyle, itemStyle))
        {
            _list.ItemContainerStyle = itemStyle;
        }
    }

    private void AttachScrollViewerActivityHook()
    {
        var scrollViewer = FindDescendant<ScrollViewer>(_list);
        if (ReferenceEquals(_scrollViewer, scrollViewer))
        {
            return;
        }

        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
        }

        _scrollViewer = scrollViewer;
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged += OnScrollViewerViewChanged;
        }
    }

    private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ForegroundTaskScheduler.Shared.RecordActivity(ForegroundActivityKind.Scrolling);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The first and last row index realized on screen, or null before the list has laid out.</summary>
    public (int First, int Last)? VisibleRange()
    {
        if (_list.ItemsPanelRoot is not ItemsStackPanel panel || _rows.Count == 0)
        {
            return null;
        }

        var first = panel.FirstVisibleIndex;
        var last = panel.LastVisibleIndex;
        if (first < 0 || last < first)
        {
            return null;
        }

        return (first, Math.Min(last, _rows.Count - 1));
    }

    /// <summary>Tells the viewport listeners after the rows changed, once the list has laid the new rows out.</summary>
    private void NotifyViewportAfterLayout()
    {
        if (ViewportChanged is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ViewportChanged?.Invoke(this, EventArgs.Empty));
    }

    public void BeginUpdate()
    {
        if (_updateDepth == 0)
        {
            _pendingUpdateSelectedPaths = SelectedPathSet();
            _pendingUpdateChanged = false;
        }

        _updateDepth++;
    }

    public void EndUpdate()
    {
        if (_updateDepth <= 0)
        {
            return;
        }

        _updateDepth--;
        if (_updateDepth > 0)
        {
            return;
        }

        var selectedPaths = _pendingUpdateSelectedPaths;
        var changed = _pendingUpdateChanged;
        _pendingUpdateSelectedPaths = null;
        _pendingUpdateChanged = false;

        if (changed)
        {
            RefreshRowsFromItemsSource();
            RestoreSelectionByPaths(selectedPaths ?? []);
            NotifyViewportAfterLayout();
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_updateDepth > 0)
        {
            _pendingUpdateChanged = true;
            return;
        }

        var selectedPaths = SelectedPathSet();
        ApplyRowsChange(e);
        RestoreSelectionByPaths(selectedPaths);
        NotifyViewportAfterLayout();
    }

    private void ApplyRowsChange(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            var insertIndex = e.NewStartingIndex >= 0 ? e.NewStartingIndex : _rows.Count;
            foreach (var row in e.NewItems.OfType<FileRow>())
            {
                _rows.Insert(Math.Min(insertIndex, _rows.Count), row);
                insertIndex++;
            }
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (var row in e.OldItems.OfType<FileRow>())
            {
                var index = _rows.FindIndex(candidate => PathRules.PathsEqual(candidate.Path, row.Path));
                if (index >= 0)
                {
                    _rows.RemoveAt(index);
                }
            }
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Replace
            && e.NewItems is not null
            && e.NewStartingIndex >= 0)
        {
            var index = e.NewStartingIndex;
            foreach (var row in e.NewItems.OfType<FileRow>())
            {
                if (index < _rows.Count)
                {
                    _rows[index] = row;
                }
                else
                {
                    _rows.Add(row);
                }

                index++;
            }
            return;
        }

        RefreshRowsFromItemsSource();
    }

    private void RefreshRowsFromItemsSource()
    {
        _rows.Clear();
        if (_itemsSource is not null)
        {
            _rows.AddRange(_itemsSource.OfType<FileRow>());
        }
    }

    private void RestoreSelectionByPaths(HashSet<string> selectedPaths)
    {
        if (selectedPaths.Count == 0)
        {
            _suppressSelectionChanged = true;
            try
            {
                _list.SelectedItems.Clear();
                _selectedRows.Clear();
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
            return;
        }

        SetSelection(_rows.Where(row => selectedPaths.Contains(row.Path)));
    }

    private HashSet<string> SelectedPathSet() =>
        new(_selectedPathSet, StringComparer.OrdinalIgnoreCase);

    private void SetSelection(IEnumerable<FileRow> rows)
    {
        var wantedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.Path))
            {
                wantedPaths.Add(row.Path);
            }
        }

        var next = new List<FileRow>(Math.Min(wantedPaths.Count, _rows.Count));
        if (wantedPaths.Count > 0)
        {
            foreach (var candidate in _rows)
            {
                if (wantedPaths.Remove(candidate.Path))
                {
                    next.Add(candidate);
                    if (wantedPaths.Count == 0)
                    {
                        break;
                    }
                }
            }
        }

        _suppressSelectionChanged = true;
        try
        {
            if (next.Count == _rows.Count && _rows.Count > 0)
            {
                _list.SelectAll();
            }
            else
            {
                _list.SelectedItems.Clear();
                foreach (var row in next)
                {
                    _list.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        SyncSelectedRowsFromList();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSelectedRowsFromList();
        if (_suppressSelectionChanged)
        {
            return;
        }

        SelectionChanged?.Invoke(
            this,
            new DetailsFileSelectionChangedEventArgs(
                e.AddedItems.OfType<FileRow>().ToArray(),
                e.RemovedItems.OfType<FileRow>().ToArray()));
    }

    private void SyncSelectedRowsFromList()
    {
        _selectedRows.Clear();
        _selectedPathSet.Clear();
        foreach (var row in _list.SelectedItems.OfType<FileRow>())
        {
            _selectedRows.Add(row);
            _selectedPathSet.Add(row.Path);
        }

        if (_selectedRows.Count == 0)
        {
            _anchorIndex = -1;
        }
        else if (_selectedRows.Count == _rows.Count)
        {
            _anchorIndex = _rows.Count - 1;
        }
        else
        {
            _anchorIndex = _rows.IndexOf(_selectedRows[^1]);
        }
    }

    private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = RowFromSource(e.OriginalSource);
        if (row is null)
        {
            return;
        }

        RowInvoked?.Invoke(this, new DetailsFileRowEventArgs(row));
        e.Handled = true;
    }

    private void OnListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var row = RowFromSource(e.OriginalSource);
        if (row is null)
        {
            RaiseBackgroundContext(e.GetPosition(this), FocusState.Pointer);
            e.Handled = true;
            return;
        }

        Focus(FocusState.Pointer);
        if (!ContainsSelection(row))
        {
            SetSelection([row]);
        }

        var anchor = AnchorForRow(row) ?? _list;
        RowContextRequested?.Invoke(
            this,
            new DetailsFileRowContextEventArgs(row, e.GetPosition(this), anchor));
        e.Handled = true;
    }

    private void OnListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var row = RowFromSource(e.OriginalSource) ?? SelectedRow;
        Point? position = null;
        if (e.TryGetPosition(this, out var requestPosition))
        {
            position = requestPosition;
        }

        if (row is null)
        {
            RaiseBackgroundContext(position, FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        Focus(FocusState.Programmatic);
        if (!ContainsSelection(row))
        {
            SetSelection([row]);
        }

        RowContextRequested?.Invoke(
            this,
            new DetailsFileRowContextEventArgs(row, position, AnchorForRow(row) ?? _list));
        e.Handled = true;
    }

    private void RaiseBackgroundContext(Point? position, FocusState focusState)
    {
        Focus(focusState);
        ClearSelection();
        BackgroundContextRequested?.Invoke(this, new DetailsFileBackgroundContextEventArgs(position, _list));
    }

    private void OnListDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var rows = e.Items.OfType<FileRow>().ToArray();
        if (rows.Length == 0)
        {
            rows = SelectedRows.ToArray();
        }

        var eventArgs = new DetailsFileRowsDragStartingEventArgs(rows, e.Data);
        RowsDragStarting?.Invoke(this, eventArgs);
        e.Cancel = eventArgs.Cancel;
    }

    private void OnListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem item)
        {
            return;
        }

        if (args.Item is FileRow namedRow)
        {
            // Narrator focuses the container, so the row name must live there.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, namedRow.AutomationName);
        }

        item.Width = _viewportWidth;
        item.MinWidth = 0;
        item.HorizontalAlignment = HorizontalAlignment.Left;
        if (FindDescendant<FileRowView>(item) is { } rowView)
        {
            rowView.Width = _viewportWidth;
            rowView.ApplyDetailsPresentation(_columns, _iconSize, _horizontalOffset);
        }
    }

    private void UpdateRealizedContainers()
    {
        foreach (var (_, item) in RealizedContainers(_list))
        {
            item.Width = _viewportWidth;
            item.MinWidth = 0;
            if (FindDescendant<FileRowView>(item) is { } rowView)
            {
                rowView.Width = _viewportWidth;
                rowView.ApplyDetailsPresentation(_columns, _iconSize, _horizontalOffset);
            }
        }
    }

    private FileRow? RowFromSource(object? source)
    {
        if (source is not DependencyObject dependencyObject)
        {
            return null;
        }

        if (source is FrameworkElement { DataContext: FileRow row })
        {
            return row;
        }

        if (FindAncestor<FileRowView>(dependencyObject) is { Row: { } rowViewRow })
        {
            return rowViewRow;
        }

        if (FindAncestor<ListViewItem>(dependencyObject) is { Content: FileRow itemRow })
        {
            return itemRow;
        }

        return null;
    }

    private FrameworkElement? AnchorForRow(FileRow row) =>
        _list.ContainerFromItem(row) as FrameworkElement;

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject start) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(start);
        for (var index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(start, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
