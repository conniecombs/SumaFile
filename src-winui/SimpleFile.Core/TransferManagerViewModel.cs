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
    private readonly TransferProgressRateTracker _rateTracker;
    // Set for an operation that is not a file transfer (a plugin's long-running work).
    private readonly string? _externalTitle;

    private TransferOperationStatus _status = TransferOperationStatus.Queued;
    private bool _isWaitingForDestination;
    private bool _isIndeterminate = true;
    private string? _operationId;
    private double _progressPercent;
    private string _percentText = "";
    private string _summaryText = "Waiting to start";
    private string _currentItemText = "Preparing transfer";
    private string _speedText = "";
    private string _etaText = "";
    private string _errorMessage = "";
    private string _tuningText = "Automatic tuning starts when files are copied";
    public string TuningText { get => _tuningText; private set => SetProperty(ref _tuningText, value); }
    public ObservableCollection<ActiveTransferFile> ActiveFiles { get; } = [];
    public TransferPolicy? PolicyOverride { get; set; }

    public void ApplyTuning(TuningUpdate update)
    {
        if (IsTerminal || update.OperationId != OperationId) return;
        var mode = update.Mode switch { "manual" => "Custom", "conservative" => "Conservative", "maximum" => "Maximum throughput", _ => "Automatic" };
        TuningText = $"{mode} · up to {update.Concurrency} {(update.Concurrency == 1 ? "file" : "files")} · {update.Explanation}";
        var paths = update.ActiveFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        for (var i = ActiveFiles.Count - 1; i >= 0; i--)
            if (!paths.Contains(ActiveFiles[i].Path)) ActiveFiles.RemoveAt(i);
        foreach (var file in update.ActiveFiles.Take(32))
        {
            var row = ActiveFiles.FirstOrDefault(item => item.Path == file.Path);
            if (row is null) { row = new ActiveTransferFile(file.Path); ActiveFiles.Add(row); }
            row.Update(file);
        }
    }

    internal TransferOperationViewModel(
        bool move,
        string[] sources,
        string destination,
        TransferOperationRunner runner,
        Func<DateTimeOffset>? now = null)
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
        _rateTracker = new TransferProgressRateTracker(now);
    }

    internal TransferOperationViewModel(
        string title,
        string detail,
        TransferOperationRunner runner,
        Func<DateTimeOffset>? now = null)
    {
        _externalTitle = title;
        Sources = [];
        Destination = detail;
        _runner = runner;
        Context = new TransferProgressContext(false, 0, "", detail);
        _potentialDestinationPaths = [];
        _rateTracker = new TransferProgressRateTracker(now);
    }

    /// <summary>True for an operation added with <see cref="TransferManagerViewModel.EnqueueExternal"/>.</summary>
    public bool IsExternal => _externalTitle is not null;

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

    public string SpeedText
    {
        get => _speedText;
        private set
        {
            if (SetProperty(ref _speedText, value))
            {
                OnPropertyChanged(nameof(StatusDetailText));
            }
        }
    }

    public string EtaText
    {
        get => _etaText;
        private set
        {
            if (SetProperty(ref _etaText, value))
            {
                OnPropertyChanged(nameof(StatusDetailText));
            }
        }
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
            if (_externalTitle is not null)
            {
                return _externalTitle;
            }

            var verb = Move ? "Move" : "Copy";
            var noun = Sources.Length == 1 ? "item" : "items";
            return $"{verb} {Math.Max(Sources.Length, 1)} {noun}";
        }
    }

    public string RouteText => IsExternal ? Destination : $"{Context.Source} -> {Destination}";

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
            : Status == TransferOperationStatus.Running
                ? RunningStatusDetailText()
                : SummaryText;

    public bool CanCancel => Status is TransferOperationStatus.Queued or TransferOperationStatus.Running;

    /// <summary>
    /// The per-task Options button. Only the service's local engine reads <c>transfer.overrides.v1</c>, so an external
    /// row (a plugin or archive-engine row) never offers it; a remote transfer's settings live in its plugin.
    /// </summary>
    public bool CanChangeOptions => CanCancel && !IsExternal;

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

        var display = _rateTracker.Format(Context, update);
        IsIndeterminate = display.IsIndeterminate;
        ProgressPercent = display.ProgressPercent;
        PercentText = display.Percent;
        SummaryText = display.Summary;
        CurrentItemText = display.CurrentItemName;
        SpeedText = update.Status is "running" or "finalizing" ? display.Speed : "";
        EtaText = update.Status is "running" or "finalizing" ? display.Eta : "";
    }

    /// <summary>Progress of an external operation, in its own units rather than bytes.</summary>
    public void ApplyExternalProgress(long current, long total, string? detail)
    {
        if (IsTerminal)
        {
            return;
        }

        var bounded = total > 0 ? Math.Clamp(current, 0, total) : Math.Max(current, 0);
        IsIndeterminate = total <= 0;
        ProgressPercent = total > 0 ? TransferProgressFormatter.Percent((ulong)bounded, (ulong)total) : 0;
        PercentText = total > 0 ? $"{ProgressPercent:0}%" : "";
        SummaryText = total > 0 ? $"{bounded:N0} of {total:N0}" : bounded > 0 ? $"{bounded:N0} done" : "Working";
        CurrentItemText = string.IsNullOrWhiteSpace(detail) ? Title : detail;
    }

    internal void MarkQueued(bool waitingForDestination)
    {
        Status = TransferOperationStatus.Queued;
        IsWaitingForDestination = waitingForDestination;
        IsIndeterminate = true;
        PercentText = "";
        SpeedText = "";
        EtaText = "";
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
        SpeedText = "";
        EtaText = "";
        _rateTracker.Reset();
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
        SpeedText = "";
        EtaText = "";
        SummaryText = "Stopping transfer safely";
        CurrentItemText = "Cancelling transfer";
    }

    internal void Complete(TransferOperationStatus status)
    {
        ActiveFiles.Clear();
        TuningText = "";
        IsWaitingForDestination = false;
        Status = status;
        SpeedText = "";
        EtaText = "";
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
        if (IsExternal || other.IsExternal)
        {
            return false;
        }

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
        OnPropertyChanged(nameof(CanChangeOptions));
        OnPropertyChanged(nameof(IsTerminal));
    }

    private string RunningStatusDetailText()
    {
        var parts = new List<string> { SummaryText };
        if (!string.IsNullOrWhiteSpace(SpeedText))
        {
            parts.Add(SpeedText);
        }

        if (!string.IsNullOrWhiteSpace(EtaText))
        {
            parts.Add(EtaText);
        }

        return string.Join(" · ", parts);
    }
}

public sealed class ActiveTransferFile(string path) : ObservableObject
{
    private string _progress = "Starting";
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public void Update(ActiveFile file) => Progress = file.Total == 0 ? "Finishing" :
        $"{TransferProgressFormatter.FormatBytes(file.Current)} / {TransferProgressFormatter.FormatBytes(file.Total)}";
}

public sealed class TransferManagerViewModel : ObservableObject
{
    public const int DefaultMaxConcurrentTransfers = 2;

    private readonly List<TransferOperationViewModel> _queued = [];
    private readonly List<TransferOperationViewModel> _running = [];
    private readonly Func<DateTimeOffset> _now;
    private int _maxConcurrentTransfers = DefaultMaxConcurrentTransfers;
    private int _activeCount;
    private int _queuedCount;
    private bool _hasTerminalOperations;

    public TransferManagerViewModel()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    internal TransferManagerViewModel(Func<DateTimeOffset> now)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

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

        var operation = new TransferOperationViewModel(move, sources, destination, runner, _now);
        Operations.Insert(0, operation);
        _queued.Add(operation);
        PumpQueue();
        return operation;
    }

    /// <summary>
    /// Adds an operation that is not a file transfer (a plugin's work) as a row with its own title.
    /// It starts at once and does not take one of the <see cref="MaxConcurrentTransfers"/> slots.
    /// </summary>
    public TransferOperationViewModel EnqueueExternal(string title, string detail, TransferOperationRunner runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(runner);

        var operation = new TransferOperationViewModel(title, detail, runner, _now);
        Operations.Insert(0, operation);
        _running.Add(operation);
        operation.MarkRunning();
        _ = RunOperationAsync(operation);
        RefreshCounts();
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
        while (_running.Count(operation => !operation.IsExternal) < MaxConcurrentTransfers)
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
