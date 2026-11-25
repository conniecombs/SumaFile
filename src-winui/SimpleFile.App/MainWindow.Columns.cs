using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private async void OnSortColumn(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && sender is FrameworkElement { Tag: PaneSort sort })
        {
            await RunUiActionAsync(
                "Sort",
                async () =>
                {
                    _workspace.SetSort(sort.Pane, sort.Sort);
                    await _workspace.SaveWorkspaceLayoutAsync();
                });
        }
    }

    private void ApplyColumnHeader(Grid header, ColumnLayout columns, PaneId pane, ref string? renderedKey)
    {
        var visible = columns.VisibleColumns;
        var sortBy = _workspace?.SortByFor(pane) ?? "name";
        var sortAscending = _workspace?.SortAscendingFor(pane) ?? true;
        var key = string.Join('\u001f', visible.Select(column => column.Id))
            + $"|{sortBy}:{sortAscending}";
        if (!string.Equals(renderedKey, key, StringComparison.Ordinal))
        {
            renderedKey = key;
            header.ColumnDefinitions.Clear();
            header.Children.Clear();

            for (var index = 0; index < visible.Count; index++)
            {
                var column = visible[index];
                header.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(column.Width),
                });

                var cell = new Grid { Tag = column.Id };
                var button = new Button
                {
                    Style = ChromeStyle("SfColumnHeaderButtonStyle"),
                    Padding = column.Id == "name" ? new Thickness(38, 5, 8, 5) : new Thickness(10, 5, 8, 5),
                    Content = HeaderLabel(column, pane),
                    Tag = new PaneSort(pane, column.Sort),
                    ContextFlyout = CreateColumnsFlyout(pane, column.Id),
                };
                button.Click += OnSortColumn;
                AttachColumnHeaderDrag(button, column.Id, pane);
                ToolTipService.SetToolTip(button, $"Sort by {column.Label}");
                AutomationProperties.SetName(button, SortAutomationName(column, pane));
                cell.Children.Add(button);

                var thumb = new PaneResizeGrip
                {
                    Width = 8,
                    MinWidth = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 0, -4, 0),
                    Tag = new ColumnResizeTarget(column.Id, pane),
                };
                AutomationProperties.SetName(thumb, $"Resize {column.Label} column");
                thumb.Children.Add(new Rectangle
                {
                    Width = 1,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Fill = Brush("SfBorderBrush"),
                });
                thumb.PointerPressed += OnColumnThumbPressed;
                thumb.PointerMoved += OnColumnThumbMoved;
                thumb.PointerReleased += OnColumnThumbReleased;
                thumb.PointerCanceled += OnColumnThumbReleased;
                thumb.PointerCaptureLost += OnColumnThumbReleased;
                thumb.DoubleTapped += OnColumnThumbDoubleTapped;
                Canvas.SetZIndex(thumb, 1);
                cell.Children.Add(thumb);

                Grid.SetColumn(cell, index);
                header.Children.Add(cell);
            }
        }

        UpdateHeaderColumnWidths(header, columns);
    }

    /// <summary>
    /// The column picker on a header's right-click: a toggle per column, the presets as starting points, and the
    /// two size-to-fit items for the header that was clicked.
    /// </summary>
    private MenuFlyout CreateColumnsFlyout(PaneId pane, string columnId)
    {
        var flyout = new MenuFlyout();
        foreach (var item in ColumnMenuItems(pane))
        {
            flyout.Items.Add(item);
        }

        AppendLazyColumnItems(flyout, pane, columnId);
        flyout.Items.Add(new MenuFlyoutSeparator());
        // What dragging the header does, for the keyboard.
        var moveLeft = new MenuFlyoutItem { Text = "Move Column Left" };
        AutomationProperties.SetAutomationId(moveLeft, "Columns.MoveLeft");
        moveLeft.Click += (_, _) => MoveColumnBy(pane, columnId, -1);
        var moveRight = new MenuFlyoutItem { Text = "Move Column Right" };
        AutomationProperties.SetAutomationId(moveRight, "Columns.MoveRight");
        moveRight.Click += (_, _) => MoveColumnBy(pane, columnId, 1);
        flyout.Opening += (_, _) =>
        {
            var layout = _workspace?.ColumnsFor(pane);
            moveLeft.IsEnabled = layout?.CanMove(columnId, -1) == true;
            moveRight.IsEnabled = layout?.CanMove(columnId, 1) == true;
        };
        flyout.Items.Add(moveLeft);
        flyout.Items.Add(moveRight);
        flyout.Items.Add(new MenuFlyoutSeparator());
        var sizeColumn = new MenuFlyoutItem { Text = "Size Column to Fit" };
        sizeColumn.Click += (_, _) => SizeColumnToFit(columnId, pane, save: true);
        var sizeAll = new MenuFlyoutItem { Text = "Size All Columns to Fit" };
        sizeAll.Click += (_, _) => SizeAllColumnsToFit(pane);
        flyout.Items.Add(sizeColumn);
        flyout.Items.Add(sizeAll);
        return flyout;
    }

    // The column header being dragged along the header row, from the press on it until the release.
    private ColumnHeaderDrag? _columnHeaderDrag;

    private readonly record struct ColumnHeaderDrag(PaneId Pane, string ColumnId, Button Button, Grid Header, double StartX, bool Moving);

    /// <summary>
    /// Lets a column be dragged to another place in the header row: it takes the place of the column it is let go
    /// on, which is marked meanwhile. The name column stays first. Like a tab, a header is a button, which keeps the
    /// pointer from its press to its release and gives it up before a handler added here hears of the release.
    /// </summary>
    private void AttachColumnHeaderDrag(Button button, string columnId, PaneId pane)
    {
        if (columnId == "name")
        {
            return;
        }

        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed
                && (button.Parent as FrameworkElement)?.Parent is Grid header)
            {
                _columnHeaderDrag = new ColumnHeaderDrag(pane, columnId, button, header, e.GetCurrentPoint(header).Position.X, Moving: false);
            }
        }), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnColumnHeaderDragMoved), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnColumnHeaderDragReleased), true);
        button.PointerCaptureLost += OnColumnHeaderDragReleased;
    }

    private void OnColumnHeaderDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_columnHeaderDrag is not { } drag || !ReferenceEquals(sender, drag.Button))
        {
            return;
        }

        var point = e.GetCurrentPoint(drag.Header);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndColumnHeaderDrag();
            return;
        }

        if (!drag.Moving && Math.Abs(point.Position.X - drag.StartX) < TabDragThreshold)
        {
            return;
        }

        _columnHeaderDrag = drag with { Moving = true };
        drag.Button.Opacity = 0.6;
        var target = ColumnHeaderCellAt(drag.Header, point.Position.X);
        ShowDropTargetChrome(target is not null && !ReferenceEquals(target, drag.Button.Parent) && target.Tag as string != "name"
            ? target.Children.OfType<Button>().FirstOrDefault()
            : null);
    }

    private void OnColumnHeaderDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_columnHeaderDrag is not { } drag)
        {
            return;
        }

        EndColumnHeaderDrag();
        var point = e.GetCurrentPoint(drag.Header);
        if (!drag.Moving || _workspace is not { } workspace || point.Properties.IsLeftButtonPressed)
        {
            // A click, which sorts; or the pointer was taken away with the button still down.
            return;
        }

        // The place of the column under the pointer in the saved order: a narrow pane shows its columns in another.
        var targetId = ColumnHeaderCellAt(drag.Header, point.Position.X)?.Tag as string;
        var index = targetId is null ? -1 : workspace.ColumnsFor(drag.Pane).VisibleIds.IndexOf(targetId);
        if (index >= 0)
        {
            ApplyColumnMove(drag.Pane, drag.ColumnId, index);
        }
    }

    private void EndColumnHeaderDrag()
    {
        if (_columnHeaderDrag is not { } drag)
        {
            return;
        }

        _columnHeaderDrag = null;
        drag.Button.Opacity = 1;
        ShowDropTargetChrome(null);
    }

    /// <summary>The header cell at <paramref name="x"/> across the header row; the first or the last past its ends.</summary>
    private static Grid? ColumnHeaderCellAt(Grid header, double x)
    {
        var cells = header.Children.OfType<Grid>().Where(cell => cell.Tag is string).ToList();
        if (cells.Count == 0)
        {
            return null;
        }

        var edges = cells
            .Select(cell =>
            {
                var left = cell.TransformToVisual(header).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                return (left, left + cell.ActualWidth);
            })
            .ToList();
        return cells[StripReorder.TargetIndex(x, edges)];
    }

    private void MoveColumnBy(PaneId pane, string columnId, int delta)
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        var ids = workspace.ColumnsFor(pane).VisibleIds;
        var index = ids.IndexOf(columnId);
        if (index >= 0)
        {
            ApplyColumnMove(pane, columnId, StripReorder.Step(index, ids.Count, delta));
        }
    }

    /// <summary>Moves the column and, when it moved, shows and saves the new order as a change of columns is saved.</summary>
    private void ApplyColumnMove(PaneId pane, string columnId, int index)
    {
        if (_workspace is { } workspace && workspace.MoveColumn(pane, columnId, index))
        {
            SyncFromWorkspace();
            _ = RunUiActionAsync("Columns", () => workspace.SaveUiSettingsAsync());
        }
    }

    /// <summary>The same picker as a "Columns" submenu at the end of the list background's menu, in the Details view.</summary>
    private void AppendColumnsMenu(MenuFlyout flyout, PaneId pane)
    {
        if (_workspace is null || EffectiveFileListViewForPane(pane) != "details")
        {
            return;
        }

        var columns = new MenuFlyoutSubItem { Text = "Columns" };
        foreach (var item in ColumnMenuItems(pane))
        {
            columns.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(columns);
    }

    /// <summary>Fresh menu items each time: a flyout item can belong to one menu only.</summary>
    private IEnumerable<MenuFlyoutItemBase> ColumnMenuItems(PaneId pane)
    {
        var workspace = _workspace;
        if (workspace is null)
        {
            yield break;
        }

        var layout = workspace.ColumnsFor(pane);
        foreach (var column in layout.Columns.Where(column => column.Id != "name"))
        {
            var toggle = new ToggleMenuFlyoutItem { Text = column.Label, IsChecked = layout.IsVisible(column.Id) };
            AutomationProperties.SetAutomationId(toggle, $"Columns.{column.Id}");
            var (id, shown) = (column.Id, toggle.IsChecked);
            toggle.Click += (_, _) =>
            {
                workspace.SetColumnVisible(pane, id, !shown);
                SyncFromWorkspace();
                _ = RunUiActionAsync("Columns", () => workspace.SaveUiSettingsAsync());
            };
            yield return toggle;
        }

        yield return new MenuFlyoutSeparator();
        var presets = new MenuFlyoutSubItem { Text = "Presets" };
        foreach (var preset in ColumnLayout.Presets.Keys)
        {
            var item = new MenuFlyoutItem { Text = char.ToUpperInvariant(preset[0]) + preset[1..] };
            var key = preset;
            item.Click += (_, _) =>
            {
                workspace.ApplyColumnPreset(pane, key);
                SyncFromWorkspace();
                _ = RunUiActionAsync("Columns", () => workspace.SaveUiSettingsAsync());
            };
            presets.Items.Add(item);
        }

        yield return presets;
    }

    private static void UpdateHeaderColumnWidths(Grid header, ColumnLayout columns)
    {
        var visible = columns.VisibleColumns;
        for (var index = 0; index < visible.Count && index < header.ColumnDefinitions.Count; index++)
        {
            header.ColumnDefinitions[index].Width = new GridLength(visible[index].Width);
        }

        header.Width = Math.Max(1, columns.VisibleWidth + header.Padding.Left + header.Padding.Right);
    }

    private string SortAutomationName(FileListColumn column, PaneId pane)
    {
        var label = column.Id == "date" ? "Date modified" : column.Label;
        if (_workspace is null || !string.Equals(_workspace.SortByFor(pane), column.Sort, StringComparison.OrdinalIgnoreCase))
        {
            return $"Sort by {label}";
        }

        return _workspace.SortAscendingFor(pane)
            ? $"Sort by {label}, currently sorted ascending"
            : $"Sort by {label}, currently sorted descending";
    }

    private string HeaderLabel(FileListColumn column, PaneId pane)
    {
        var label = column.Id == "date" ? "Date modified" : column.Label;
        if (_workspace is null || !string.Equals(_workspace.SortByFor(pane), column.Sort, StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        return _workspace.SortAscendingFor(pane) ? $"{label} ↑" : $"{label} ↓";
    }

    [ThreadStatic]
    private static TextBlock? s_measureBlock;

    private void SizeColumnToFit(string columnId, PaneId pane, bool save, bool applyLayout = true)
    {
        var columns = _workspace?.ColumnsFor(pane) ?? ColumnLayoutHost.For(pane);
        var column = columns.Find(columnId);
        if (column is null)
        {
            return;
        }

        var rows = pane == PaneId.Secondary ? SecondaryFiles : PrimaryFiles;
        var extra = columnId == "name" ? FileListViewHost.IconSizeFor(pane) + 38 : 24;
        var headerText = column.Id == "date" ? "Date modified" : column.Label;
        var width = MeasureTextWidth(headerText, 11, semiBold: true) + extra;
        var fontSize = columnId == "name" ? 13d : 12d;
        var limit = Math.Min(rows.Count, EntryPresentation.DefaultProgressiveViewportCount);
        for (var i = 0; i < limit; i++)
        {
            width = Math.Max(width, MeasureTextWidth(rows[i].ColumnText(columnId), fontSize, semiBold: false) + extra);
        }

        columns.Resize(columnId, width + 12);
        if (applyLayout)
        {
            ApplyColumnWidths();
        }

        if (save && _workspace is not null)
        {
            _ = RunUiActionAsync("Resize columns", () => _workspace.SaveUiSettingsAsync());
        }
    }

    private void SizeAllColumnsToFit(PaneId pane)
    {
        var columns = _workspace?.ColumnsFor(pane) ?? ColumnLayoutHost.For(pane);
        foreach (var column in columns.VisibleColumns)
        {
            SizeColumnToFit(column.Id, pane, save: false, applyLayout: false);
        }

        ApplyColumnWidths();
        if (_workspace is not null)
        {
            _ = RunUiActionAsync("Resize columns", () => _workspace.SaveUiSettingsAsync());
        }
    }

    private static double MeasureTextWidth(string text, double fontSize, bool semiBold)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var block = s_measureBlock ??= new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
        };
        block.Text = text;
        block.FontSize = fontSize;
        block.FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal;
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    private void OnPaneRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_workspace is null || e.NewSize.Width <= 0 || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 0.5)
        {
            return;
        }

        QueuePaneSizeColumnRefresh();
    }

    private void QueuePaneSizeColumnRefresh()
    {
        if (_workspace is null)
        {
            return;
        }

        _paneSizeColumnRefreshTimer ??= CreatePaneSizeColumnRefreshTimer();
        _paneSizeColumnRefreshTimer.Stop();
        _paneSizeColumnRefreshTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreatePaneSizeColumnRefreshTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_workspace is not null)
            {
                ApplyFileListViewPresentation();
                ApplyColumnWidths();
                QueueDetailsScrollRefresh();
            }
        };
        return timer;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root) =>
        FindDescendant<ScrollViewer>(root);

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        if (root is T match)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, index));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
