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
    private INotifyCollectionChanged? _observableItems;
    private IEnumerable? _itemsSource;
    private IReadOnlyList<FileListColumn> _columns = [];
    private int _iconSize = UiSettings.NormalizeIconSize((int?)null);
    private double _horizontalOffset;
    private double _viewportWidth = 1;
    private int _anchorIndex = -1;
    private bool _suppressSelectionChanged;

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
        _list.Loaded += (_, _) => ApplyContainerStyle();
        Content = _list;
    }

    public event EventHandler<DetailsFileSelectionChangedEventArgs>? SelectionChanged;

    public event EventHandler<DetailsFileRowEventArgs>? RowInvoked;

    public event EventHandler<DetailsFileRowContextEventArgs>? RowContextRequested;

    public event EventHandler<DetailsFileRowsDragStartingEventArgs>? RowsDragStarting;

    public event EventHandler? RowsDragCompleted;

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

    public void SelectPath(string? path)
    {
        var row = path is null
            ? null
            : _rows.FirstOrDefault(candidate => PathRules.PathsEqual(candidate.Path, path));
        SetSelection(row is null ? [] : [row]);
        if (row is not null)
        {
            _anchorIndex = _rows.IndexOf(row);
            _list.ScrollIntoView(row);
        }
    }

    public bool ContainsSelection(FileRow row) =>
        _selectedRows.Any(selected => PathRules.PathsEqual(selected.Path, row.Path));

    public FileRow? RowFromPoint(Point point)
    {
        foreach (var row in _rows)
        {
            if (_list.ContainerFromItem(row) is not ListViewItem container)
            {
                continue;
            }

            var bounds = container.TransformToVisual(this)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(point))
            {
                return row;
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

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var selectedPaths = SelectedPathSet();
        ApplyRowsChange(e);
        RestoreSelectionByPaths(selectedPaths);
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
        _selectedRows.Select(row => row.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void SetSelection(IEnumerable<FileRow> rows)
    {
        var next = rows
            .Where(row => _rows.Any(candidate => PathRules.PathsEqual(candidate.Path, row.Path)))
            .DistinctBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _suppressSelectionChanged = true;
        try
        {
            _list.SelectedItems.Clear();
            foreach (var row in next)
            {
                _list.SelectedItems.Add(row);
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
        _selectedRows.AddRange(_list.SelectedItems.OfType<FileRow>());
        _anchorIndex = _selectedRows.Count > 0 ? _rows.IndexOf(_selectedRows[^1]) : -1;
    }

    private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = RowFromSource(e.OriginalSource) ?? SelectedRow;
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
        if (row is null)
        {
            return;
        }

        Focus(FocusState.Programmatic);
        if (!ContainsSelection(row))
        {
            SetSelection([row]);
        }

        Point? position = null;
        if (e.TryGetPosition(this, out var requestPosition))
        {
            position = requestPosition;
        }

        RowContextRequested?.Invoke(
            this,
            new DetailsFileRowContextEventArgs(row, position, AnchorForRow(row) ?? _list));
        e.Handled = true;
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
        foreach (var row in _rows)
        {
            if (_list.ContainerFromItem(row) is ListViewItem item)
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
