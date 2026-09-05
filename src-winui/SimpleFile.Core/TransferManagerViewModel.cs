using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

public enum TransferOperationStatus
{
    Queued,
    Running,
    Cancelling,
    Completed,
    Cancelled,
    Failed,
    Skipped,
}

public delegate Task<TransferOperationStatus> TransferOperationRunner(
    TransferOperationViewModel operation,
    CancellationToken cancellationToken);

public sealed class TransferOperationViewModel : ObservableObject
{
    private readonly TransferOperationRunner _runner;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<TransferOperationStatus> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string[] _potentialDestinationPaths;

    private TransferOperationStatus _status = TransferOperationStatus.Queued;
    private bool _isWaitingForDestination;
    private bool _isIndeterminate = true;
    private string? _operationId;
    private double _progressPercent;
    private string _percentText = "";
    private string _summaryText = "Waiting to start";
    private string _currentItemText = "Preparing transfer";
    private string _errorMessage = "";

    internal TransferOperationViewModel(
        bool move,
        string[] sources,
        string destination,
        TransferOperationRunner runner)
    {
        Move = move;
        Sources = [.. sources];
        Destination = destination;
        _runner = runner;
        Context = new TransferProgressContext(
            move,
            Sources.Length,
            TransferViewModel.DescribeSource(Sources),
            destination);
        _potentialDestinationPaths = Sources
            .Select(PathRules.Basename)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => PathRules.JoinPath(destination, name))
            .ToArray();
    }

    public string Id { get; } = Guid.NewGuid().ToString("n");

    public bool Move { get; }

    public string[] Sources { get; }

    public string Destination { get; }

    public TransferProgressContext Context { get; }

    public Task<TransferOperationStatus> CompletionTask => _completion.Task;

    public string? OperationId
    {
        get => _operationId;
        private set => SetProperty(ref _operationId, value);
    }

    public TransferOperationStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                RaiseStatusDerivedProperties();
            }
        }
    }

    public bool IsWaitingForDestination
    {
        get => _isWaitingForDestination;
        private set
        {
            if (SetProperty(ref _isWaitingForDestination, value))
            {
                RaiseStatusDerivedProperties();
            }
        }
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string PercentText
    {
        get => _percentText;
        private set => SetProperty(ref _percentText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set
        {
            if (SetProperty(ref _summaryText, value))
            {
                OnPropertyChanged(nameof(StatusDetailText));
            }
        }
    }

    public string CurrentItemText
    {
        get => _currentItemText;
        private set => SetProperty(ref _currentItemText, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(StatusDetailText));
            }
        }
    }

    public string Title
    {
        get
        {
            var verb = Move ? "Move" : "Copy";
            var noun = Sources.Length == 1 ? "item" : "items";
            return $"{verb} {Math.Max(Sources.Length, 1)} {noun}";
        }
    }

    public string RouteText => $"{Context.Source} -> {Destination}";

    public string StatusLabel => Status switch
    {
        TransferOperationStatus.Queued when IsWaitingForDestination => "Waiting",
        TransferOperationStatus.Queued => "Queued",
        TransferOperationStatus.Running => "Running",
        TransferOperationStatus.Cancelling => "Cancelling",
        TransferOperationStatus.Completed => "Complete",
        TransferOperationStatus.Cancelled => "Cancelled",
        TransferOperationStatus.Failed => "Failed",
        TransferOperationStatus.Skipped => "Skipped",
        _ => "Queued",
    };

    public string StatusDetailText =>
        Status == TransferOperationStatus.Failed && !string.IsNullOrWhiteSpace(ErrorMessage)
            ? ErrorMessage
            : SummaryText;

    public bool CanCancel => Status is TransferOperationStatus.Queued or TransferOperationStatus.Running;

    public bool IsTerminal => Status is
        TransferOperationStatus.Completed or
        TransferOperationStatus.Cancelled or
        TransferOperationStatus.Failed or
        TransferOperationStatus.Skipped;

    internal CancellationToken CancellationToken => _cts.Token;

    internal Task<TransferOperationStatus> RunAsync() => _runner(this, _cts.Token);

    public void SetOperationId(string operationId)
    {
        OperationId = operationId;
    }

    public void SetErrorMessage(string message)
    {
        ErrorMessage = message;
    }

    public void ApplyProgress(ProgressUpdate update)
    {
        if (IsTerminal)
        {
            return;
        }

        if (OperationId is not null
            && !string.Equals(update.OperationId, OperationId, StringComparison.Ordinal))
        {
            return;
        }

        if (OperationId is null && !string.IsNullOrWhiteSpace(update.OperationId))
        {
            OperationId = update.OperationId;
        }

        if (!string.IsNullOrWhiteSpace(update.Error))
        {
            ErrorMessage = update.Error;
        }

        var display = TransferProgressFormatter.Format(Context, update, null, null);
        IsIndeterminate = display.IsIndeterminate;
        ProgressPercent = display.ProgressPercent;
        PercentText = display.Percent;
        SummaryText = display.Summary;
        CurrentItemText = display.CurrentItemName;
    }

    internal void MarkQueued(bool waitingForDestination)
    {
        Status = TransferOperationStatus.Queued;
        IsWaitingForDestination = waitingForDestination;
        IsIndeterminate = true;
        PercentText = "";
        SummaryText = waitingForDestination
            ? "Waiting for another transfer to finish"
            : "Waiting to start";
        CurrentItemText = waitingForDestination ? "Destination busy" : "Preparing transfer";
    }

    internal void MarkRunning()
    {
        IsWaitingForDestination = false;
        Status = TransferOperationStatus.Running;
        IsIndeterminate = true;
        PercentText = "";
        SummaryText = "Starting transfer";
        CurrentItemText = "Preparing transfer";
    }

    internal void MarkCancelling()
    {
        if (IsTerminal)
        {
            return;
        }

        _cts.Cancel();
        IsWaitingForDestination = false;
        Status = TransferOperationStatus.Cancelling;
        IsIndeterminate = true;
        PercentText = "";
        SummaryText = "Stopping transfer safely";
        CurrentItemText = "Cancelling transfer";
    }

    internal void Complete(TransferOperationStatus status)
    {
        IsWaitingForDestination = false;
        Status = status;
        switch (status)
        {
            case TransferOperationStatus.Completed:
                ProgressPercent = 100;
                PercentText = "100%";
                SummaryText = "Transfer complete";
                CurrentItemText = "Transfer complete";
                IsIndeterminate = false;
                break;
            case TransferOperationStatus.Cancelled:
                PercentText = "";
                SummaryText = "Transfer cancelled";
                CurrentItemText = "Cancelled";
                IsIndeterminate = false;
                break;
            case TransferOperationStatus.Failed:
                PercentText = "";
                SummaryText = string.IsNullOrWhiteSpace(ErrorMessage)
                    ? "Transfer failed"
                    : ErrorMessage;
                CurrentItemText = "Transfer failed";
                IsIndeterminate = false;
                break;
            case TransferOperationStatus.Skipped:
                PercentText = "";
                SummaryText = "Transfer skipped";
                CurrentItemText = "Skipped";
                IsIndeterminate = false;
                break;
        }

        _completion.TrySetResult(status);
        _cts.Dispose();
    }

    internal void CancelToken()
    {
        if (!IsTerminal)
        {
            _cts.Cancel();
        }
    }

    internal bool DestinationOverlaps(TransferOperationViewModel other)
    {
        if (PathOverlaps(Destination, other.Destination))
        {
            return true;
        }

        foreach (var left in _potentialDestinationPaths)
        {
            foreach (var right in other._potentialDestinationPaths)
            {
                if (PathRules.PathsEqual(left, right))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool PathOverlaps(string left, string right) =>
        PathRules.PathContains(left, right) || PathRules.PathContains(right, left);

    private void RaiseStatusDerivedProperties()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusDetailText));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsTerminal));
    }
}

public sealed class TransferManagerViewModel : ObservableObject
{
    public const int DefaultMaxConcurrentTransfers = 2;

    private readonly List<TransferOperationViewModel> _queued = [];
    private readonly List<TransferOperationViewModel> _running = [];
    private int _maxConcurrentTransfers = DefaultMaxConcurrentTransfers;
    private int _activeCount;
    private int _queuedCount;
    private bool _hasTerminalOperations;

    public ObservableCollection<TransferOperationViewModel> Operations { get; } = [];

    public int MaxConcurrentTransfers
    {
        get => _maxConcurrentTransfers;
        set
        {
            var next = Math.Max(1, value);
            if (SetProperty(ref _maxConcurrentTransfers, next))
            {
                PumpQueue();
            }
        }
    }

    public int ActiveCount
    {
        get => _activeCount;
        private set
        {
            if (SetProperty(ref _activeCount, value))
            {
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    }

    public int QueuedCount
    {
        get => _queuedCount;
        private set
        {
            if (SetProperty(ref _queuedCount, value))
            {
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    }

    public bool HasTerminalOperations
    {
        get => _hasTerminalOperations;
        private set => SetProperty(ref _hasTerminalOperations, value);
    }

    public string HeaderText
    {
        get
        {
            if (ActiveCount == 0 && QueuedCount == 0)
            {
                return "No active transfers";
            }

            var active = ActiveCount == 1 ? "1 active" : $"{ActiveCount} active";
            var queued = QueuedCount == 1 ? "1 queued" : $"{QueuedCount} queued";
            return $"{active}, {queued}";
        }
    }

    public TransferOperationViewModel Enqueue(
        string[] sources,
        string destination,
        bool move,
        TransferOperationRunner runner)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(runner);

        var operation = new TransferOperationViewModel(move, sources, destination, runner);
        Operations.Insert(0, operation);
        _queued.Add(operation);
        PumpQueue();
        return operation;
    }

    public void Cancel(TransferOperationViewModel operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.IsTerminal)
        {
            return;
        }

        if (_queued.Remove(operation))
        {
            operation.Complete(TransferOperationStatus.Cancelled);
            RefreshCounts();
            PumpQueue();
            return;
        }

        if (_running.Contains(operation))
        {
            operation.MarkCancelling();
            RefreshCounts();
        }
    }

    public void ClearCompleted()
    {
        foreach (var operation in Operations.Where(operation => operation.IsTerminal).ToArray())
        {
            Operations.Remove(operation);
        }

        RefreshCounts();
    }

    public void Reset()
    {
        foreach (var operation in _queued.ToArray())
        {
            _queued.Remove(operation);
            operation.Complete(TransferOperationStatus.Cancelled);
        }

        foreach (var operation in _running.ToArray())
        {
            operation.MarkCancelling();
            operation.CancelToken();
        }

        _running.Clear();
        Operations.Clear();
        RefreshCounts();
    }

    private async Task RunOperationAsync(TransferOperationViewModel operation)
    {
        TransferOperationStatus status;
        try
        {
            status = await operation.RunAsync();
            if (operation.CancellationToken.IsCancellationRequested
                && status == TransferOperationStatus.Completed)
            {
                status = TransferOperationStatus.Cancelled;
            }
        }
        catch (OperationCanceledException)
        {
            status = TransferOperationStatus.Cancelled;
        }
        catch (Exception exception)
        {
            operation.SetErrorMessage(exception.Message);
            status = TransferOperationStatus.Failed;
        }

        _running.Remove(operation);
        operation.Complete(status);
        RefreshCounts();
        PumpQueue();
    }

    private void PumpQueue()
    {
        RefreshQueuedDestinationState();
        while (_running.Count < MaxConcurrentTransfers)
        {
            var next = _queued.FirstOrDefault(operation => !_running.Any(operation.DestinationOverlaps));
            if (next is null)
            {
                break;
            }

            _queued.Remove(next);
            _running.Add(next);
            next.MarkRunning();
            _ = RunOperationAsync(next);
            RefreshQueuedDestinationState();
        }

        RefreshCounts();
    }

    private void RefreshQueuedDestinationState()
    {
        foreach (var operation in _queued)
        {
            operation.MarkQueued(_running.Any(operation.DestinationOverlaps));
        }
    }

    private void RefreshCounts()
    {
        ActiveCount = _running.Count;
        QueuedCount = _queued.Count;
        HasTerminalOperations = Operations.Any(operation => operation.IsTerminal);
    }
}
