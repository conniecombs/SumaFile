using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Storage;

namespace SimpleFile.App;

/// <summary>
/// Lazy columns in the window: the rows on screen are reported to the workspace (debounced), a "…" cell's click
/// reads one value, a duration the service cannot read is asked of the Windows property system, and a fill of a
/// whole folder shows its progress in the status line.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Windows property reads at a time; each opens the file through the shell.</summary>
    private static readonly SemaphoreSlim WindowsPropertyReads = new(2, 2);

    private void ConfigureLazyColumns(ExplorerWorkspace workspace)
    {
        LazyColumnHost.Configure((pane, path, kind) => workspace.RequestFact(pane, path, kind));
        workspace.FactFallback = ReadFactWithWindowsAsync;
    }

    /// <summary>Reports the rows on screen of each Details pane shortly after the last scroll or row change.</summary>
    private void QueueViewportReport()
    {
        if (_workspace is null)
        {
            return;
        }

        var timer = _viewportReportTimer;
        if (timer is null)
        {
            timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(ViewportReportDelayMilliseconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => ReportViewports();
            _viewportReportTimer = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private void CancelViewportReport()
    {
        _viewportReportTimer?.Stop();
    }

    private void ReportViewports()
    {
        if (_workspace is null)
        {
            return;
        }

        var panes = _workspace.DualPaneEnabled ? new[] { PaneId.Primary, PaneId.Secondary } : [PaneId.Primary];
        foreach (var pane in panes)
        {
            if (EffectiveFileListViewForPane(pane) != "details")
            {
                continue;
            }

            var details = pane == PaneId.Secondary ? SecondaryDetailsFileList : PrimaryDetailsFileList;
            if (details.VisibleRange() is { } range)
            {
                _workspace.ReportViewport(pane, range.First, range.Last);
            }
        }
    }

    /// <summary>The fill's progress in the status line, once per change: "Reading pages… 1,240 of 4,000".</summary>
    private void ShowLazyColumnFillStatus()
    {
        var fill = _workspace?.LazyColumnFill;
        if (fill is null || ReferenceEquals(fill, _shownLazyColumnFill))
        {
            return;
        }

        _shownLazyColumnFill = fill;
        var label = LazyColumnLabel(fill.Kind);
        if (!fill.Done)
        {
            SetStatusText($"Reading {label}… {fill.Fill.Done:N0} of {fill.Fill.Total:N0}");
        }
        else if (fill.Cancelled)
        {
            SetStatusText($"Stopped reading {label} after {fill.Fill.Done:N0} of {fill.Fill.Total:N0}");
        }
        else if (fill.Fill.Skipped > 0)
        {
            SetStatusText($"Read {label} for {fill.Fill.Done:N0} items; {fill.Fill.Skipped:N0} more were not read");
        }
        else
        {
            SetStatusText($"Read {label} for {fill.Fill.Done:N0} items");
        }
    }

    private static string LazyColumnLabel(string kind) => kind switch
    {
        LazyColumns.FolderSize => "folder sizes",
        LazyColumns.ItemCount => "item counts",
        LazyColumns.Dimensions => "dimensions",
        LazyColumns.Duration => "durations",
        LazyColumns.DateTaken => "dates taken",
        LazyColumns.Camera => "cameras",
        LazyColumns.Pages => "page counts",
        _ => kind,
    };

    /// <summary>
    /// A lazy column's own items in its header menu: read it for the whole folder, stop a fill, and whether it reads as
    /// rows appear or waits to be asked.
    /// </summary>
    private void AppendLazyColumnItems(MenuFlyout flyout, PaneId pane, string columnId)
    {
        var workspace = _workspace;
        var column = workspace?.ColumnsFor(pane).Find(columnId);
        if (workspace is null || column?.FactKind is not { } kind)
        {
            return;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var label = LazyColumnLabel(kind);
        var calculate = new MenuFlyoutItem { Text = $"Calculate {label} for this folder" };
        calculate.Click += (_, _) => workspace.ReadColumnForFolder(pane, kind);
        flyout.Items.Add(calculate);
        if (workspace.LazyColumnFill is { Done: false } fill && fill.Pane == workspace.Normalize(pane))
        {
            var stop = new MenuFlyoutItem { Text = "Stop calculating" };
            stop.Click += (_, _) => workspace.CancelColumnFill(pane);
            flyout.Items.Add(stop);
        }

        var automatic = new ToggleMenuFlyoutItem
        {
            Text = "Read as rows appear",
            IsChecked = workspace.LazyColumnModeFor(kind) == LazyColumns.Automatic,
        };
        automatic.Click += (_, _) =>
        {
            workspace.SetLazyColumnMode(kind, automatic.IsChecked ? LazyColumns.Automatic : LazyColumns.OnRequest);
            QueueViewportReport();
            _ = workspace.SaveWorkspaceLayoutAsync();
        };
        flyout.Items.Add(automatic);
    }

    /// <summary>
    /// The Windows property system's answer for a duration the service's readers do not cover (AVI, WMV, MPEG, ASF),
    /// read through the shell at most two at a time. Null when it has none either.
    /// </summary>
    private static async Task<EntryFact?> ReadFactWithWindowsAsync(string path, string kind, CancellationToken cancellationToken)
    {
        if (kind != LazyColumns.Duration)
        {
            return null;
        }

        await WindowsPropertyReads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
            var duration = (await file.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false)).Duration;
            if (duration <= TimeSpan.Zero)
            {
                duration = (await file.Properties.GetMusicPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false)).Duration;
            }

            if (duration <= TimeSpan.Zero)
            {
                return null;
            }

            return new EntryFact
            {
                Path = path,
                Kind = kind,
                State = EntryFact.Ready,
                Text = LazyColumns.FormatDuration(duration),
                Sort = Math.Floor(duration.TotalSeconds),
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            WindowsPropertyReads.Release();
        }
    }
}
