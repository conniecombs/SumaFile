using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed class StatusCenterTaskRow
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
}

public sealed partial class MainWindow
{
    private void OnTransferPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshStatusCenter);
    }

    private void OnStatusCenterFlyoutOpening(object? sender, object e)
    {
        RefreshStatusCenter();
    }

    private void OnStatusCenterTransfersClick(object sender, RoutedEventArgs e)
    {
        ShowTransferProgressWindow();
    }

    private async void OnStatusCenterHistoryClick(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Operation history", ShowOperationHistoryAsync);
    }

    private void OnStatusCenterCancelTransferClick(object sender, RoutedEventArgs e)
    {
        var operation = FirstCancellableTransfer();
        if (operation is null)
        {
            return;
        }

        OnFileProgressCancelRequested(operation);
        RefreshStatusCenter();
    }

    private void OnStatusCenterClearCompletedClick(object sender, RoutedEventArgs e)
    {
        OnTransferClearCompletedRequested();
        RefreshStatusCenter();
    }

    private void RefreshStatusCenter()
    {
        var snapshot = StatusCenterFormatter.Format(
            _transfer?.ActiveCount ?? 0,
            _transfer?.QueuedCount ?? 0,
            _transfer?.HasTerminalOperations == true,
            _workspace is not null && WorkspaceHasListingInProgress(_workspace),
            _workspace?.ErrorMessage ?? _workspace?.StatusMessage ?? _toolbar?.StatusText,
            _workspace?.OperationLog.Count ?? 0);

        StatusCenterBadgeText.Text = snapshot.BadgeText;
        StatusCenterHeaderText.Text = snapshot.HeaderText;
        StatusCenterDetailText.Text = snapshot.DetailText;
        StatusCenterTaskList.ItemsSource = StatusCenterRows(snapshot);
        StatusCenterCancelTransferButton.IsEnabled = FirstCancellableTransfer() is not null;
        StatusCenterClearCompletedButton.IsEnabled = _transfer?.HasTerminalOperations == true;
        StatusCenterButton.Opacity = snapshot.HasWork || snapshot.HasAttention ? 1.0 : 0.74;
        ToolTipService.SetToolTip(StatusCenterButton, $"{snapshot.HeaderText}: {snapshot.DetailText}");
    }

    private TransferOperationViewModel? FirstCancellableTransfer()
    {
        return _transfer?.Operations.FirstOrDefault(operation => operation.CanCancel);
    }

    private IReadOnlyList<StatusCenterTaskRow> StatusCenterRows(StatusCenterSnapshot snapshot)
    {
        var rows = new List<StatusCenterTaskRow>();
        if (_transfer is not null)
        {
            rows.AddRange(_transfer.Operations
                .Take(5)
                .Select(operation => new StatusCenterTaskRow
                {
                    Title = operation.Title,
                    Detail = operation.StatusDetailText,
                    Badge = operation.StatusLabel,
                }));
        }

        if (rows.Count < 5 && _workspace is not null)
        {
            rows.AddRange(_workspace.OperationLog
                .Take(5 - rows.Count)
                .Select(operation =>
                {
                    var display = OperationHistoryFormatter.Format(operation);
                    return new StatusCenterTaskRow
                    {
                        Title = display.RowText,
                        Detail = $"{display.DestinationSummary} - {operation.At.ToLocalTime():g}",
                        Badge = display.StatusLabel,
                    };
                }));
        }

        if (rows.Count == 0)
        {
            rows.Add(new StatusCenterTaskRow
            {
                Title = snapshot.HeaderText,
                Detail = snapshot.DetailText,
                Badge = snapshot.BadgeText,
            });
        }

        return rows;
    }
}
