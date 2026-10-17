using System.Diagnostics;

namespace SimpleFile.Core;

public enum ForegroundActivityKind
{
    Navigation,
    Scrolling,
    Typing,
}

/// <summary>
/// Coordinates foreground user interaction (navigation, scrolling, typing) so background
/// enrichment and thumbnail/video poster tasks immediately yield, throttle, or preempt.
/// </summary>
public sealed class ForegroundTaskScheduler
{
    public static ForegroundTaskScheduler Shared { get; } = new();

    public static readonly TimeSpan DefaultNavigationQuietPeriod = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan DefaultScrollingQuietPeriod = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan DefaultTypingQuietPeriod = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly List<PreemptibleEntry> _preemptible = [];
    private readonly TimeSpan _pollSlice;
    private long _quietUntilTimestamp;
    private int _activeScopes;
    private long _activityGeneration;
    private long _yieldCount;
    private long _preemptionCount;
    private ForegroundActivityKind _lastActivityKind;
    private TaskCompletionSource<bool>? _idleSignal;

    private sealed record PreemptibleEntry(
        CancellationTokenSource Cts,
        bool PreemptOnNavigation,
        bool PreemptOnScrolling,
        bool PreemptOnTyping);

    public ForegroundTaskScheduler(TimeSpan? pollSlice = null)
    {
        _pollSlice = pollSlice is { TotalMilliseconds: > 0 } value
            ? value
            : TimeSpan.FromMilliseconds(25);
    }

    public bool IsForegroundActive
    {
        get
        {
            lock (_gate)
            {
                return _activeScopes > 0 || Stopwatch.GetTimestamp() < _quietUntilTimestamp;
            }
        }
    }

    public int ActiveScopeCount
    {
        get
        {
            lock (_gate)
            {
                return _activeScopes;
            }
        }
    }

    public long ActivityGeneration => Interlocked.Read(ref _activityGeneration);

    public long YieldCount => Interlocked.Read(ref _yieldCount);

    public long PreemptionCount => Interlocked.Read(ref _preemptionCount);

    public ForegroundActivityKind LastActivityKind
    {
        get
        {
            lock (_gate)
            {
                return _lastActivityKind;
            }
        }
    }

    public void RecordActivity(ForegroundActivityKind kind, TimeSpan? quietPeriod = null)
    {
        var duration = quietPeriod ?? DefaultQuietPeriodFor(kind);
        List<CancellationTokenSource>? toCancel = null;
        lock (_gate)
        {
            _lastActivityKind = kind;
            Interlocked.Increment(ref _activityGeneration);
            AdvanceQuietWindowLocked(duration);
            toCancel = CollectPreemptibleLocked(kind);
        }

        CancelPreempted(toCancel);
    }

    public IDisposable BeginForegroundScope(ForegroundActivityKind kind, TimeSpan? postScopeQuietPeriod = null)
    {
        var duration = postScopeQuietPeriod ?? DefaultQuietPeriodFor(kind);
        List<CancellationTokenSource>? toCancel = null;
        lock (_gate)
        {
            _lastActivityKind = kind;
            _activeScopes++;
            Interlocked.Increment(ref _activityGeneration);
            AdvanceQuietWindowLocked(duration);
            toCancel = CollectPreemptibleLocked(kind);
        }

        CancelPreempted(toCancel);
        return new Scope(this, duration);
    }

    public IDisposable RegisterPreemptibleBackground(
        CancellationTokenSource cts,
        bool preemptOnNavigation = true,
        bool preemptOnScrolling = false,
        bool preemptOnTyping = false)
    {
        ArgumentNullException.ThrowIfNull(cts);
        var entry = new PreemptibleEntry(cts, preemptOnNavigation, preemptOnScrolling, preemptOnTyping);
        lock (_gate)
        {
            _preemptible.Add(entry);
        }

        return new PreemptibleRegistration(this, entry);
    }

    public async Task YieldOrThrottleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsForegroundActive)
        {
            return;
        }

        Interlocked.Increment(ref _yieldCount);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining;
            Task? scopeSignal = null;
            lock (_gate)
            {
                if (_activeScopes > 0)
                {
                    _idleSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    scopeSignal = _idleSignal.Task;
                    remaining = _pollSlice;
                }
                else
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now >= _quietUntilTimestamp)
                    {
                        return;
                    }

                    remaining = Stopwatch.GetElapsedTime(now, _quietUntilTimestamp);
                    if (remaining > _pollSlice)
                    {
                        remaining = _pollSlice;
                    }
                }
            }

            if (scopeSignal is not null)
            {
                var delayTask = Task.Delay(_pollSlice, cancellationToken);
                var completed = await Task.WhenAny(scopeSignal, delayTask).ConfigureAwait(false);
                if (ReferenceEquals(completed, delayTask))
                {
                    await delayTask.ConfigureAwait(false);
                }
            }
            else if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private void EndForegroundScope(TimeSpan postScopeQuietPeriod)
    {
        TaskCompletionSource<bool>? signalToRelease = null;
        lock (_gate)
        {
            if (_activeScopes > 0)
            {
                _activeScopes--;
            }

            AdvanceQuietWindowLocked(postScopeQuietPeriod);
            if (_activeScopes == 0 && _idleSignal is not null)
            {
                signalToRelease = _idleSignal;
                _idleSignal = null;
            }
        }

        signalToRelease?.TrySetResult(true);
    }

    private void UnregisterPreemptible(PreemptibleEntry entry)
    {
        lock (_gate)
        {
            _preemptible.Remove(entry);
        }
    }

    private void AdvanceQuietWindowLocked(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var ticks = (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
        var candidate = Stopwatch.GetTimestamp() + ticks;
        if (candidate > _quietUntilTimestamp)
        {
            _quietUntilTimestamp = candidate;
        }
    }

    private List<CancellationTokenSource>? CollectPreemptibleLocked(ForegroundActivityKind kind)
    {
        if (_preemptible.Count == 0)
        {
            return null;
        }

        List<CancellationTokenSource>? matched = null;
        for (var i = _preemptible.Count - 1; i >= 0; i--)
        {
            var entry = _preemptible[i];
            if (entry.Cts.IsCancellationRequested)
            {
                _preemptible.RemoveAt(i);
                continue;
            }

            var shouldPreempt = kind switch
            {
                ForegroundActivityKind.Navigation => entry.PreemptOnNavigation,
                ForegroundActivityKind.Scrolling => entry.PreemptOnScrolling,
                ForegroundActivityKind.Typing => entry.PreemptOnTyping,
                _ => false,
            };
            if (shouldPreempt)
            {
                matched ??= [];
                matched.Add(entry.Cts);
                _preemptible.RemoveAt(i);
            }
        }

        return matched;
    }

    private void CancelPreempted(List<CancellationTokenSource>? toCancel)
    {
        if (toCancel is null || toCancel.Count == 0)
        {
            return;
        }

        foreach (var cts in toCancel)
        {
            try
            {
                Interlocked.Increment(ref _preemptionCount);
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public static TimeSpan DefaultQuietPeriodFor(ForegroundActivityKind kind) =>
        kind switch
        {
            ForegroundActivityKind.Navigation => DefaultNavigationQuietPeriod,
            ForegroundActivityKind.Scrolling => DefaultScrollingQuietPeriod,
            ForegroundActivityKind.Typing => DefaultTypingQuietPeriod,
            _ => DefaultScrollingQuietPeriod,
        };

    private sealed class Scope(ForegroundTaskScheduler owner, TimeSpan postScopeQuietPeriod) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndForegroundScope(postScopeQuietPeriod);
            }
        }
    }

    private sealed class PreemptibleRegistration(ForegroundTaskScheduler owner, PreemptibleEntry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.UnregisterPreemptible(entry);
            }
        }
    }
}
