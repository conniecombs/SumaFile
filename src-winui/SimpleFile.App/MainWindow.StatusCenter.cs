using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed class StatusCenterTaskRow
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";

    /// <summary>The live transfer behind the row, so the progress bar follows its percent.</summary>
    public TransferOperationViewModel? Operation { get; init; }
    public Visibility ProgressVisibility { get; init; } = Visibility.Collapsed;
    public Visibility UndoVisibility { get; init; } = Visibility.Collapsed;
    public ICommand? UndoCommand { get; init; }
}

/// <summary>Minimal command for buttons inside the Status Center row template.</summary>
internal sealed class StatusCenterCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

public sealed partial class MainWindow
{
    // A completed transfer offers Undo in the Status Center for this long.
    private static readonly TimeSpan TransferUndoWindow = TimeSpan.FromSeconds(10);
    private TransferOperationViewModel? _undoableTransfer;
    // The undo entry pushed for that transfer; its Undo buttons work only while it is still the newest.
    private UndoEntry? _undoableTransferEntry;
    private DispatcherQueueTimer? _undoableTransferTimer;
    private string? _undoReceiptMessage;
    private bool _undoReceiptHooked;
    private bool _statusCenterTemplateApplied;

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
            _workspace?.ErrorMessage ?? _workspace?.StatusMessage,
            _workspace?.OperationLog.Count ?? 0);

        EnsureStatusCenterRowTemplate();
        if (_undoableTransferEntry is not null && !IsTransferUndoCurrent())
        {
            // A later change (rename, delete, another transfer) is now on top: retire the transfer's Undo.
            ClearTransferUndoable();
            return;
        }

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
                    Operation = operation,
                    ProgressVisibility = operation.IsTerminal ? Visibility.Collapsed : Visibility.Visible,
                    UndoVisibility = IsTransferUndoable(operation) ? Visibility.Visible : Visibility.Collapsed,
                    UndoCommand = new StatusCenterCommand(UndoFromStatusCenter),
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

    /// <summary>
    /// Replaces the Status Center row template once (built-in types and literal values only, so
    /// XamlReader needs no app resources) with one that adds a progress bar bound to
    /// the transfer's percent and an Undo button for a just-completed transfer.
    /// </summary>
    private void EnsureStatusCenterRowTemplate()
    {
        if (_statusCenterTemplateApplied)
        {
            return;
        }

        _statusCenterTemplateApplied = true;
        const string xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid Padding="0,4" ColumnSpacing="8">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <FontIcon
                        VerticalAlignment="Top"
                        FontFamily="Segoe Fluent Icons"
                        FontSize="12"
                        Opacity="0.72"
                        Glyph="&#xE9D9;" />
                    <StackPanel Grid.Column="1" Spacing="2">
                        <TextBlock
                            Text="{Binding Title}"
                            TextTrimming="CharacterEllipsis" />
                        <TextBlock
                            FontSize="12"
                            Opacity="0.72"
                            Text="{Binding Detail}"
                            TextTrimming="CharacterEllipsis"
                            ToolTipService.ToolTip="{Binding Detail}" />
                        <ProgressBar
                            Height="4"
                            Margin="0,4,0,0"
                            Minimum="0"
                            Maximum="100"
                            AutomationProperties.Name="Transfer progress"
                            IsIndeterminate="{Binding Operation.IsIndeterminate}"
                            Value="{Binding Operation.ProgressPercent}"
                            Visibility="{Binding ProgressVisibility}" />
                    </StackPanel>
                    <StackPanel Grid.Column="2" Spacing="4" HorizontalAlignment="Right">
                        <TextBlock
                            HorizontalAlignment="Right"
                            FontSize="12"
                            Opacity="0.72"
                            Text="{Binding Badge}" />
                        <Button
                            Padding="8,2"
                            MinHeight="0"
                            FontSize="12"
                            Content="Undo"
                            AutomationProperties.Name="Undo this transfer"
                            Command="{Binding UndoCommand}"
                            Visibility="{Binding UndoVisibility}" />
                    </StackPanel>
                </Grid>
            </DataTemplate>
            """;
        try
        {
            StatusCenterTaskList.ItemTemplate = (DataTemplate)XamlReader.Load(xaml);
        }
        catch (Exception)
        {
            // Keep the template from MainWindow.xaml if the richer one cannot load.
        }
    }

    private bool IsTransferUndoable(TransferOperationViewModel operation) =>
        ReferenceEquals(operation, _undoableTransfer)
        && operation.Status == TransferOperationStatus.Completed
        && IsTransferUndoCurrent();

    /// <summary>True only while the transfer's own undo entry is still the newest one on the undo list.</summary>
    private bool IsTransferUndoCurrent() =>
        _workspace is { } workspace && IsNewestUndoEntry(workspace.Undo, _undoableTransferEntry);

    internal static bool IsNewestUndoEntry(UndoStack undo, UndoEntry? entry) =>
        entry is not null && undo.Entries is { Count: > 0 } entries && ReferenceEquals(entries[^1], entry);

    /// <summary>The transfer just pushed an undo entry: offer Undo on its Status Center row for a short while.</summary>
    private void MarkTransferUndoable(TransferOperationViewModel operation, UndoStack undo)
    {
        _undoableTransfer = operation;
        // Entries runs oldest to newest, so the entry just pushed is the last one.
        _undoableTransferEntry = undo.Entries is { Count: > 0 } entries ? entries[^1] : null;
        if (_undoableTransferTimer is null)
        {
            _undoableTransferTimer = DispatcherQueue.CreateTimer();
            _undoableTransferTimer.Interval = TransferUndoWindow;
            _undoableTransferTimer.IsRepeating = false;
            _undoableTransferTimer.Tick += (timer, _) =>
            {
                timer.Stop();
                ClearTransferUndoable();
            };
        }

        _undoableTransferTimer.Stop();
        _undoableTransferTimer.Start();
        RefreshStatusCenter();
    }

    private void ClearTransferUndoable()
    {
        _undoableTransferTimer?.Stop();
        if (_undoableTransfer is null && _undoableTransferEntry is null && _undoReceiptMessage is null)
        {
            return;
        }

        _undoableTransfer = null;
        _undoableTransferEntry = null;
        if (_undoReceiptMessage is not null)
        {
            _undoReceiptMessage = null;
            MessageBar.ActionButton = null;
        }

        RefreshStatusCenter();
    }

    private async void UndoFromStatusCenter()
    {
        StatusCenterButton.Flyout?.Hide();
        await UndoTransferIfCurrentAsync();
    }

    /// <summary>
    /// Undo buttons for a transfer undo that transfer only: if anything else was pushed since,
    /// nothing is undone and the buttons are retired.
    /// </summary>
    private async Task UndoTransferIfCurrentAsync()
    {
        if (!IsTransferUndoCurrent())
        {
            ClearTransferUndoable();
            SetStatusText("That transfer is no longer the latest change, so it was not undone.");
            return;
        }

        await RunUiActionAsync("Undo", UndoLastAsync);
    }

    /// <summary>Completion receipt in the message bar with an Undo action.</summary>
    private void ShowUndoReceipt(string title, string receipt)
    {
        var undo = new Button { Content = "Undo" };
        undo.Click += async (_, _) =>
        {
            MessageBar.IsOpen = false;
            await UndoTransferIfCurrentAsync();
        };

        _undoReceiptMessage = receipt;
        ShowMessage(title, receipt, InfoBarSeverity.Success);
        MessageBar.ActionButton = undo;
        if (!_undoReceiptHooked)
        {
            _undoReceiptHooked = true;
            // Any later message replaces the receipt, so its Undo button must not linger.
            MessageBar.RegisterPropertyChangedCallback(InfoBar.MessageProperty, (_, _) =>
            {
                if (_undoReceiptMessage is not null
                    && !string.Equals(MessageBar.Message, _undoReceiptMessage, StringComparison.Ordinal))
                {
                    _undoReceiptMessage = null;
                    MessageBar.ActionButton = null;
                }
            });
        }
    }
}
