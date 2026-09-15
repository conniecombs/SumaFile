using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

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

    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _rowsHost;
    private readonly List<FileRow> _rows = [];
    private readonly List<FileRow> _selectedRows = [];
    private readonly Dictionary<FileRow, Border> _containers = new();
    private readonly Dictionary<FileRow, FileRowView> _rowViews = new();
    private IReadOnlyList<FileListColumn> _columns = [];
    private INotifyCollectionChanged? _observableItems;
    private IEnumerable? _itemsSource;
    private int _iconSize = UiSettings.NormalizeIconSize((int?)null);
    private double _horizontalOffset;
    private double _viewportWidth = 1;
    private int _anchorIndex = -1;
    private bool _suppressSelectionChanged;

    public DetailsFileListView()
    {
        IsTabStop = true;
        Background = new SolidColorBrush(Colors.Transparent);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _rowsHost = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _scrollViewer = new ScrollViewer
        {
            Content = _rowsHost,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
        };
        Content = _scrollViewer;

        KeyDown += OnKeyDown;
        ActualThemeChanged += (_, _) => RefreshSelectionVisuals();
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

            RebuildRows();
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
        _rowsHost.Width = _viewportWidth;

        foreach (var (row, container) in _containers)
        {
            container.Width = _viewportWidth;
            if (_rowViews.TryGetValue(row, out var rowView))
            {
                ApplyRowLayout(rowView);
            }
        }

        _rowsHost.InvalidateMeasure();
        _scrollViewer.InvalidateMeasure();
        InvalidateMeasure();
    }

    public void ClearSelection()
    {
        SetSelection([], notify: true);
    }

    public void SelectAll()
    {
        SetSelection(_rows, notify: true);
    }

    public void SelectPath(string? path)
    {
        var row = path is null
            ? null
            : _rows.FirstOrDefault(candidate => PathRules.PathsEqual(candidate.Path, path));
        SetSelection(row is null ? [] : [row], notify: true);
        if (row is not null)
        {
            _anchorIndex = _rows.IndexOf(row);
            BringRowIntoView(row);
        }
    }

    public bool ContainsSelection(FileRow row) =>
        _selectedRows.Any(selected => PathRules.PathsEqual(selected.Path, row.Path));

    public FileRow? RowFromPoint(Point point)
    {
        foreach (var (row, container) in _containers)
        {
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
        if (!_containers.TryGetValue(row, out var container))
        {
            return null;
        }

        return container.TransformToVisual(relativeTo)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildRows();
    }

    private void RebuildRows()
    {
        var selectedPaths = _selectedRows.Select(row => row.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        _containers.Clear();
        _rowViews.Clear();
        _rowsHost.Children.Clear();

        if (_itemsSource is not null)
        {
            foreach (var row in _itemsSource.OfType<FileRow>())
            {
                _rows.Add(row);
                AddRow(row);
            }
        }

        _suppressSelectionChanged = true;
        try
        {
            _selectedRows.Clear();
            _selectedRows.AddRange(_rows.Where(row => selectedPaths.Contains(row.Path)));
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        _anchorIndex = _selectedRows.Count > 0 ? _rows.IndexOf(_selectedRows[^1]) : -1;
        RefreshSelectionVisuals();
    }

    private void AddRow(FileRow row)
    {
        var rowView = new FileRowView { Row = row };
        ApplyRowLayout(rowView);
        rowView.ContextRequested += (_, args) => RaiseContextRequested(row, rowView, args);

        var container = new Border
        {
            Child = rowView,
            Tag = row,
            MinHeight = 30,
            Width = _viewportWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = TransparentBrush(),
            CanDrag = true,
        };
        container.PointerPressed += OnRowPointerPressed;
        container.DoubleTapped += OnRowDoubleTapped;
        container.RightTapped += OnRowRightTapped;
        container.DragStarting += OnRowDragStarting;
        container.DropCompleted += (_, _) => RowsDragCompleted?.Invoke(this, EventArgs.Empty);
        _rowViews[row] = rowView;
        _containers[row] = container;
        _rowsHost.Children.Add(container);
    }

    private void ApplyRowLayout(FileRowView rowView)
    {
        rowView.Width = _viewportWidth;
        rowView.HorizontalAlignment = HorizontalAlignment.Left;
        rowView.ApplyDetailsPresentation(_columns, _iconSize, _horizontalOffset);
    }

    private void OnRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FileRow row })
        {
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        Focus(FocusState.Pointer);
        SelectFromPointer(row, e.KeyModifiers);
        e.Handled = true;
    }

    private void SelectFromPointer(FileRow row, VirtualKeyModifiers modifiers)
    {
        var index = _rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        if (modifiers.HasFlag(VirtualKeyModifiers.Shift) && _anchorIndex >= 0)
        {
            var start = Math.Min(_anchorIndex, index);
            var end = Math.Max(_anchorIndex, index);
            SetSelection(_rows.Skip(start).Take(end - start + 1), notify: true);
            return;
        }

        _anchorIndex = index;
        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            var next = _selectedRows.ToList();
            var existing = next.FindIndex(selected => PathRules.PathsEqual(selected.Path, row.Path));
            if (existing >= 0)
            {
                next.RemoveAt(existing);
            }
            else
            {
                next.Add(row);
            }

            SetSelection(next, notify: true);
            return;
        }

        SetSelection([row], notify: true);
    }

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FileRow row })
        {
            RowInvoked?.Invoke(this, new DetailsFileRowEventArgs(row));
            e.Handled = true;
        }
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FileRow row } element)
        {
            return;
        }

        Focus(FocusState.Pointer);
        if (!ContainsSelection(row))
        {
            _anchorIndex = _rows.IndexOf(row);
            SetSelection([row], notify: true);
        }

        var position = e.GetPosition(this);
        RowContextRequested?.Invoke(this, new DetailsFileRowContextEventArgs(row, position, element));
        e.Handled = true;
    }

    private void RaiseContextRequested(FileRow row, FrameworkElement anchor, ContextRequestedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        if (!ContainsSelection(row))
        {
            _anchorIndex = _rows.IndexOf(row);
            SetSelection([row], notify: true);
        }

        Point? position = null;
        if (e.TryGetPosition(this, out var requestPosition))
        {
            position = requestPosition;
        }

        RowContextRequested?.Invoke(this, new DetailsFileRowContextEventArgs(row, position, anchor));
        e.Handled = true;
    }

    private void OnRowDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is FrameworkElement { Tag: FileRow row } && !ContainsSelection(row))
        {
            SetSelection([row], notify: true);
        }

        var selected = SelectedRows;
        var eventArgs = new DetailsFileRowsDragStartingEventArgs(selected, args.Data);
        RowsDragStarting?.Invoke(this, eventArgs);
        args.Cancel = eventArgs.Cancel;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var selectedIndex = SelectedRow is { } selected ? _rows.IndexOf(selected) : -1;
        var nextIndex = e.Key switch
        {
            VirtualKey.Up => Math.Max(0, selectedIndex <= 0 ? 0 : selectedIndex - 1),
            VirtualKey.Down => Math.Min(_rows.Count - 1, selectedIndex < 0 ? 0 : selectedIndex + 1),
            VirtualKey.Home => 0,
            VirtualKey.End => _rows.Count - 1,
            _ => -1,
        };
        if (nextIndex < 0)
        {
            return;
        }

        e.Handled = true;
        var next = _rows[nextIndex];
        if (IsKeyDown(VirtualKey.Shift) && _anchorIndex >= 0)
        {
            var start = Math.Min(_anchorIndex, nextIndex);
            var end = Math.Max(_anchorIndex, nextIndex);
            SetSelection(_rows.Skip(start).Take(end - start + 1), notify: true);
        }
        else
        {
            _anchorIndex = nextIndex;
            SetSelection([next], notify: true);
        }

        BringRowIntoView(next);
    }

    private void BringRowIntoView(FileRow row)
    {
        if (_containers.TryGetValue(row, out var container))
        {
            container.StartBringIntoView();
        }
    }

    private void SetSelection(IEnumerable<FileRow> rows, bool notify)
    {
        var next = rows
            .Where(row => _rows.Any(candidate => PathRules.PathsEqual(candidate.Path, row.Path)))
            .DistinctBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var added = next
            .Where(row => !_selectedRows.Any(selected => PathRules.PathsEqual(selected.Path, row.Path)))
            .ToArray();
        var removed = _selectedRows
            .Where(row => !next.Any(selected => PathRules.PathsEqual(selected.Path, row.Path)))
            .ToArray();
        if (added.Length == 0 && removed.Length == 0)
        {
            return;
        }

        _selectedRows.Clear();
        _selectedRows.AddRange(next);
        RefreshSelectionVisuals();
        if (notify && !_suppressSelectionChanged)
        {
            SelectionChanged?.Invoke(this, new DetailsFileSelectionChangedEventArgs(added, removed));
        }
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var (row, container) in _containers)
        {
            container.Background = ContainsSelection(row)
                ? ThemeResourceLookup.Brush(this, "SfBgSelectedBrush") ?? TransparentBrush()
                : TransparentBrush();
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private static SolidColorBrush TransparentBrush() => new(Colors.Transparent);
}
