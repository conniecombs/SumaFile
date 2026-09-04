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
    private Task? _backendCancelTask;
    private readonly object _cancelSync = new();

    /// <summary>
    /// Max time to wait for backend <c>cancel_operation</c> before allowing a new transfer to start.
    /// On timeout we proceed; stale progress is still ignored via CurrentOperationId filtering.
    /// </summary>
    public static readonly TimeSpan BackendCancelTimeout = TimeSpan.FromSeconds(15);

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
    /// Cancels any active transfer (awaiting backend cancel completion or
    /// <see cref="BackendCancelTimeout"/>), then creates a fresh CTS for the next transfer.
    /// </summary>
    public async Task<CancellationTokenSource> BeginTransferAsync()
    {
        await CancelActiveTransferAsync().ConfigureAwait(false);
        return BeginTransfer();
    }

    /// <summary>
    /// Creates a new CancellationTokenSource for the current transfer.
    /// Returns the token for the caller to use.
    /// Prefer <see cref="BeginTransferAsync"/> when replacing an in-flight transfer so backend
    /// cancel is awaited before the next start.
    /// </summary>
    public CancellationTokenSource BeginTransfer()
    {
        _transferCts?.Cancel();
        // Drop the prior operation id immediately so a late completed/cancelled/error
        // event for the old transfer cannot CompleteTransfer while a new one prepares.
        CurrentOperationId = null;
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
        // Ignore all events (including terminal) for non-active operation ids so a
        // stale completed/cancelled/error cannot finish a newer transfer.
        if (!IsCurrentOperation(update.OperationId))
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
            CompleteTransfer(update.Status);
        }
    }

    public void CompleteCurrentOperation(string status)
    {
        if (CurrentOperationId is null)
        {
            return;
        }

        if (status == "completed")
        {
            ProgressPercent = 100;
        }

        CompleteTransfer(status);
    }

    /// <summary>
    /// If a transfer is active (or a backend cancel is already in flight), cancel and
    /// <b>await</b> backend cancel completion (or <see cref="BackendCancelTimeout"/>).
    /// Call this before starting a new transfer.
    /// </summary>
    public async Task CancelActiveTransferAsync()
    {
        Task? pending;
        lock (_cancelSync)
        {
            pending = _backendCancelTask;
        }

        if (pending is not null)
        {
            await AwaitCancelTaskAsync(pending, BackendCancelTimeout).ConfigureAwait(false);
        }

        if (!HasActiveTransfer)
        {
            return;
        }

        await CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the active transfer via both the local CTS and backend cancel.
    /// Awaits backend <c>cancel_operation</c> completion (or <see cref="BackendCancelTimeout"/>).
    /// </summary>
    public async Task CancelAsync()
    {
        var operationId = CurrentOperationId;
        _transferCts?.Cancel();
        IsCancelling = true;

        if (string.IsNullOrEmpty(operationId) || _workspace.FileOps is null)
        {
            Task? pendingOnly;
            lock (_cancelSync)
            {
                pendingOnly = _backendCancelTask;
            }

            if (pendingOnly is not null)
            {
                await AwaitCancelTaskAsync(pendingOnly, BackendCancelTimeout).ConfigureAwait(false);
            }

            return;
        }

        Task cancelTask;
        lock (_cancelSync)
        {
            cancelTask = CancelBackendAsync(operationId);
            _backendCancelTask = cancelTask;
        }

        try
        {
            await AwaitCancelTaskAsync(cancelTask, BackendCancelTimeout).ConfigureAwait(false);
        }
        finally
        {
            lock (_cancelSync)
            {
                if (ReferenceEquals(_backendCancelTask, cancelTask))
                {
                    _backendCancelTask = null;
                }
            }
        }
    }

    private async Task CancelBackendAsync(string operationId)
    {
        try
        {
            await _workspace.FileOps!.CancelOperationAsync(operationId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during teardown.
        }
    }

    private static async Task AwaitCancelTaskAsync(Task cancelTask, TimeSpan timeout)
    {
        if (cancelTask.IsCompleted)
        {
            await cancelTask.ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(cancelTask, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == cancelTask)
        {
            await cancelTask.ConfigureAwait(false);
            return;
        }

        // Timed out waiting for backend cancel ack: allow the next transfer to proceed.
        // Stale terminal/progress events for the old operation id remain ignored.
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
        lock (_cancelSync)
        {
            _backendCancelTask = null;
        }
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

    private bool IsCurrentOperation(string operationId)
        => CurrentOperationId is not null
            && string.Equals(operationId, CurrentOperationId, StringComparison.Ordinal);

    private void CompleteTransfer(string status)
    {
        CurrentOperationId = null;
        IsTransferring = false;
        IsCancelling = false;
        Completed?.Invoke(this, new TransferCompletedEventArgs(status));
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
