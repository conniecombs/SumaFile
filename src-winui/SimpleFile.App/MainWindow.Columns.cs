using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

                var cell = new Grid();
                var button = new Button
                {
                    Style = ChromeStyle("SfColumnHeaderButtonStyle"),
                    Padding = column.Id == "name" ? new Thickness(38, 5, 8, 5) : new Thickness(10, 5, 8, 5),
                    Content = HeaderLabel(column, pane),
                    Tag = new PaneSort(pane, column.Sort),
                    ContextFlyout = CreateColumnsFlyout(pane, column.Id),
                };
                button.Click += OnSortColumn;
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

        flyout.Items.Add(new MenuFlyoutSeparator());
        var sizeColumn = new MenuFlyoutItem { Text = "Size Column to Fit" };
        sizeColumn.Click += (_, _) => SizeColumnToFit(columnId, pane, save: true);
        var sizeAll = new MenuFlyoutItem { Text = "Size All Columns to Fit" };
        sizeAll.Click += (_, _) => SizeAllColumnsToFit(pane);
        flyout.Items.Add(sizeColumn);
        flyout.Items.Add(sizeAll);
        return flyout;
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

    private void SizeColumnToFit(string columnId, PaneId pane, bool save)
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
        foreach (var row in rows.Take(EntryPresentation.DefaultProgressiveViewportCount))
        {
            width = Math.Max(width, MeasureTextWidth(row.ColumnText(columnId), fontSize, semiBold: false) + extra);
        }

        columns.Resize(columnId, width + 12);
        ApplyColumnWidths();
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
            SizeColumnToFit(column.Id, pane, save: false);
        }

        if (_workspace is not null)
        {
            _ = RunUiActionAsync("Resize columns", () => _workspace.SaveUiSettingsAsync());
        }
    }

    private static double MeasureTextWidth(string text, double fontSize, bool semiBold)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.NoWrap,
        };
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

    private void QueueColumnEnrichment()
    {
        if (_workspace?.FileOps is null)
        {
            return;
        }

        var panes = _workspace.DualPaneEnabled
            ? new[] { PaneId.Primary, PaneId.Secondary }
            : new[] { PaneId.Primary };
        var needsSizes = _workspace.Settings.ShowFolderSizes;
        var signatureParts = new List<string>
        {
            $"sizes={needsSizes}",
        };
        foreach (var pane in panes)
        {
            var columns = _workspace.ColumnsFor(pane);
            signatureParts.Add($"{pane}:visible={string.Join(',', columns.VisibleIds)}");
            signatureParts.Add(ColumnEnrichmentSignatureFor(pane));
        }

        var signature = string.Join('|', signatureParts);
        if (string.Equals(signature, _columnEnrichmentSignature, StringComparison.Ordinal))
        {
            return;
        }

        _columnEnrichmentSignature = signature;
        _columnEnrichmentCts?.Cancel();
        _columnEnrichmentTimer?.Stop();
        var token = Interlocked.Increment(ref _columnEnrichmentToken);
        QueueColumnEnrichmentTimer(panes, needsSizes, token);
    }

    private void QueueColumnEnrichmentTimer(IReadOnlyList<PaneId> panes, bool needsSizes, int token)
    {
        _pendingColumnEnrichmentPanes = panes;
        _pendingColumnEnrichmentNeedsSizes = needsSizes;
        _pendingColumnEnrichmentToken = token;
        var timer = _columnEnrichmentTimer ??= CreateColumnEnrichmentTimer();
        timer.Stop();
        timer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateColumnEnrichmentTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(ColumnEnrichmentIdleDelayMilliseconds);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var cts = _columnEnrichmentCts;
            if (cts is null || cts.IsCancellationRequested)
            {
                cts = new CancellationTokenSource();
                _columnEnrichmentCts = cts;
            }

            _ = EnrichColumnsAsync(
                _pendingColumnEnrichmentPanes,
                _pendingColumnEnrichmentNeedsSizes,
                _pendingColumnEnrichmentToken,
                cts);
        };
        return timer;
    }

    private void CancelColumnEnrichment()
    {
        _columnEnrichmentTimer?.Stop();
        _columnEnrichmentCts?.Cancel();
        _columnEnrichmentSignature = null;
        _pendingColumnEnrichmentPanes = [];
        _pendingColumnEnrichmentNeedsSizes = false;
        _pendingColumnEnrichmentToken = 0;
    }

    private string ColumnEnrichmentSignatureFor(PaneId pane)
    {
        if (_workspace is null)
        {
            return "";
        }

        var state = _workspace.Pane(pane);
        var entries = string.Join(
            '\u001e',
            state.Entries.Take(64).Select(entry => $"{entry.Path}:{entry.Modified}:{entry.IsDir}"));
        return $"{pane}:{state.Path}:{state.NavigationToken}:{state.ListingInProgress}:{state.Entries.Count}:{entries}";
    }

    private async Task EnrichColumnsAsync(
        IReadOnlyList<PaneId> panes,
        bool needsSizes,
        int token,
        CancellationTokenSource cts)
    {
        using var preemptionRegistration = (_workspace?.ForegroundScheduler ?? ForegroundTaskScheduler.Shared)
            .RegisterPreemptibleBackground(
                cts,
                preemptOnNavigation: true,
                preemptOnScrolling: true,
                preemptOnTyping: true);
        try
        {
            var workspace = _workspace;
            var cancellationToken = cts.Token;
            if (workspace is null)
            {
                return;
            }

            foreach (var pane in panes)
            {
                var state = workspace.Pane(pane);
                if (cancellationToken.IsCancellationRequested
                    || token != _columnEnrichmentToken
                    || state.ListingInProgress)
                {
                    return;
                }

                if (state.PathIsNetwork)
                {
                    continue;
                }

                var columns = workspace.ColumnsFor(pane);
                var needsItems = columns.IsVisible("items");
                var paneNeedsSizes = needsSizes;
                if (paneNeedsSizes || needsItems)
                {
                    await workspace.FillFolderMetricsAsync(pane, paneNeedsSizes, needsItems, cancellationToken).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_columnEnrichmentCts, cts))
            {
                _columnEnrichmentCts = null;
            }

            cts.Dispose();
        }
    }
}
