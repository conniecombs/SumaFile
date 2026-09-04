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
                    ContextFlyout = CreateColumnHeaderFlyout(column.Id, pane),
                };
                button.Click += OnSortColumn;
                ToolTipService.SetToolTip(button, $"Sort by {column.Label}");
                AutomationProperties.SetName(button, $"Sort by {column.Label}");
                cell.Children.Add(button);

                var thumb = new PaneResizeGrip
                {
                    Width = 8,
                    MinWidth = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 0, -4, 0),
                    Tag = new ColumnResizeTarget(column.Id, pane),
                };
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

    private MenuFlyout CreateColumnHeaderFlyout(string columnId, PaneId pane)
    {
        var flyout = new MenuFlyout();
        var sizeColumn = new MenuFlyoutItem { Text = "Size Column to Fit" };
        sizeColumn.Click += (_, _) => SizeColumnToFit(columnId, pane, save: true);
        var sizeAll = new MenuFlyoutItem { Text = "Size All Columns to Fit" };
        sizeAll.Click += (_, _) => SizeAllColumnsToFit(pane);
        flyout.Items.Add(sizeColumn);
        flyout.Items.Add(sizeAll);
        return flyout;
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
        foreach (var row in rows)
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

    private void HookFileListColumnScroll(ListView list)
    {
        list.Loaded -= OnFileListLoadedForColumnScroll;
        list.Loaded += OnFileListLoadedForColumnScroll;
        AttachFileListColumnScroll(list);
    }

    private void OnFileListLoadedForColumnScroll(object sender, RoutedEventArgs e)
    {
        if (sender is ListView list)
        {
            AttachFileListColumnScroll(list);
        }
    }

    private void AttachFileListColumnScroll(ListView list)
    {
        var scroller = FindDescendantScrollViewer(list);
        if (scroller is null)
        {
            return;
        }

        if (ReferenceEquals(list, PrimaryFileList))
        {
            if (!ReferenceEquals(_primaryFileListScroller, scroller))
            {
                if (_primaryFileListScroller is not null)
                {
                    _primaryFileListScroller.ViewChanged -= OnPrimaryFileListViewChanged;
                }

                _primaryFileListScroller = scroller;
                scroller.ViewChanged += OnPrimaryFileListViewChanged;
            }

            SyncHeaderScroll(PrimaryColumnHeaderScroller, scroller);
        }
        else if (ReferenceEquals(list, SecondaryFileList))
        {
            if (!ReferenceEquals(_secondaryFileListScroller, scroller))
            {
                if (_secondaryFileListScroller is not null)
                {
                    _secondaryFileListScroller.ViewChanged -= OnSecondaryFileListViewChanged;
                }

                _secondaryFileListScroller = scroller;
                scroller.ViewChanged += OnSecondaryFileListViewChanged;
            }

            SyncHeaderScroll(SecondaryColumnHeaderScroller, scroller);
        }
    }

    private void OnPrimaryFileListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => SyncHeaderScroll(PrimaryColumnHeaderScroller, sender as ScrollViewer);

    private void OnSecondaryFileListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => SyncHeaderScroll(SecondaryColumnHeaderScroller, sender as ScrollViewer);

    private static void SyncHeaderScroll(ScrollViewer header, ScrollViewer? list)
    {
        if (list is null)
        {
            return;
        }

        if (Math.Abs(header.HorizontalOffset - list.HorizontalOffset) > 0.5)
        {
            header.ChangeView(list.HorizontalOffset, null, null, disableAnimation: true);
        }
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
            $"gitIntegration={_workspace.Settings.EnableGitIntegration}",
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
        var cts = new CancellationTokenSource();
        _columnEnrichmentCts = cts;
        var token = Interlocked.Increment(ref _columnEnrichmentToken);
        _ = EnrichColumnsAsync(panes, needsSizes, token, cts);
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
                var needsGit = columns.IsVisible("git") && workspace.Settings.EnableGitIntegration;
                var needsItems = columns.IsVisible("items");
                var paneNeedsSizes = needsSizes;

                if (needsGit)
                {
                    await workspace.ApplyGitStatusesAsync(pane, cancellationToken).ConfigureAwait(true);
                }

                if (cancellationToken.IsCancellationRequested || token != _columnEnrichmentToken)
                {
                    return;
                }

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
