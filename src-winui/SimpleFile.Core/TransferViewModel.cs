using CommunityToolkit.Mvvm.ComponentModel;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// ViewModel encapsulating file transfer (copy/move) progress state that was
/// previously managed across MainWindow.xaml.cs and MainWindow.Transfer.cs.
/// Tracks the active operation ID, cancellation, and progress updates.
/// </summary>
public sealed partial class TransferViewModel : ObservableObject
{
    private readonly ExplorerWorkspace _workspace;

    [ObservableProperty]
    private string? _currentOperationId;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private bool _isCancelling;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressDetail = "";

    private CancellationTokenSource? _transferCts;

    /// <summary>
    /// Raised when a transfer operation completes, fails, or is cancelled.
    /// </summary>
    public event EventHandler<TransferCompletedEventArgs>? Completed;

    /// <summary>
    /// Raised when a progress update arrives from the backend.
    /// The host can forward this to a TransferProgressWindow.
    /// </summary>
    public event EventHandler<ProgressUpdate>? ProgressReceived;

    public TransferViewModel(ExplorerWorkspace workspace)
    {
        _workspace = workspace;
    }

    public bool HasActiveTransfer => CurrentOperationId is not null || _transferCts is not null;

    /// <summary>
    /// Creates a new CancellationTokenSource for the current transfer.
    /// Returns the token for the caller to use.
    /// </summary>
    public CancellationTokenSource BeginTransfer()
    {
        _transferCts?.Cancel();
        var cts = new CancellationTokenSource();
        _transferCts = cts;
        IsTransferring = true;
        IsCancelling = false;
        ProgressPercent = 0;
        ProgressDetail = "";
        StatusText = "";
        return cts;
    }

    /// <summary>
    /// Registers the operation ID once the backend acknowledges the transfer.
    /// </summary>
    public void SetOperationId(string operationId)
    {
        CurrentOperationId = operationId;
        IsTransferring = true;
    }

    public void ClearCurrentOperation()
    {
        CurrentOperationId = null;
        IsTransferring = _transferCts is not null;
    }

    /// <summary>
    /// Updates progress from a backend progress event.
    /// </summary>
    public void OnProgress(ProgressUpdate update)
    {
        if (CurrentOperationId is not null
            && !string.Equals(update.OperationId, CurrentOperationId, StringComparison.Ordinal))
        {
            return;
        }

        ProgressReceived?.Invoke(this, update);

        if (update.Total > 0)
        {
            ProgressPercent = 100.0 * update.Current / update.Total;
        }

        if (update.Status is "completed" or "cancelled" or "error")
        {
            CurrentOperationId = null;
            IsTransferring = false;
            IsCancelling = false;
            Completed?.Invoke(this, new TransferCompletedEventArgs(update.Status));
        }
    }

    /// <summary>
    /// Cancels the active transfer via both the local CTS and backend cancel.
    /// </summary>
    public async Task CancelAsync()
    {
        var operationId = CurrentOperationId;
        _transferCts?.Cancel();
        IsCancelling = true;

        if (string.IsNullOrEmpty(operationId) || _workspace.FileOps is null)
        {
            return;
        }

        try
        {
            await _workspace.FileOps.CancelOperationAsync(operationId);
        }
        catch (OperationCanceledException)
        {
            // Expected during teardown.
        }
    }

    public bool FinishTransfer(CancellationTokenSource transferCts)
    {
        if (!ReferenceEquals(_transferCts, transferCts))
        {
            return false;
        }

        _transferCts = null;
        IsTransferring = CurrentOperationId is not null;
        IsCancelling = false;
        transferCts.Dispose();
        return true;
    }

    /// <summary>
    /// Clears all transfer state without cancelling. Used during cleanup.
    /// </summary>
    public void Reset()
    {
        _transferCts?.Cancel();
        _transferCts = null;
        CurrentOperationId = null;
        IsTransferring = false;
        IsCancelling = false;
        ProgressPercent = 0;
        ProgressDetail = "";
        StatusText = "";
    }

    /// <summary>
    /// Describes the source location(s) for a transfer operation.
    /// </summary>
    public static string DescribeSource(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return "";
        }

        var parents = sources
            .Select(SourceParent)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parents.Length == 1 ? parents[0] : "Multiple locations";
    }

    private static string SourceParent(string source)
    {
        var trimmed = source.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return source;
        }

        var parent = System.IO.Path.GetDirectoryName(trimmed);
        return string.IsNullOrWhiteSpace(parent) ? source : parent;
    }
}

/// <summary>
/// Event args for transfer completion.
/// </summary>
public sealed class TransferCompletedEventArgs : EventArgs
{
    public string Status { get; }

    public TransferCompletedEventArgs(string status)
    {
        Status = status;
    }
}
