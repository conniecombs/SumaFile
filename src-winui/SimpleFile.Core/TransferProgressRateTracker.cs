using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class TransferProgressRateTracker
{
    /// <summary>Seconds without a transferred byte after which no speed is shown.</summary>
    public const double StallSeconds = 3;

    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _lastSampleAt;
    private DateTimeOffset _startedAt;
    private ulong _lastSampleBytes;
    private double? _bytesPerSecond;
    private bool _hasSample;

    public TransferProgressRateTracker(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Reset();
    }

    public void Reset()
    {
        _startedAt = _now();
        _lastSampleAt = default;
        _lastSampleBytes = 0;
        _bytesPerSecond = null;
        _hasSample = false;
    }

    public TransferProgressDisplay Format(TransferProgressContext context, ProgressUpdate update)
    {
        var speed = TrackSpeed(update);
        return TransferProgressFormatter.Format(context, update, speed, AverageFilesPerSecond(update));
    }

    private double? TrackSpeed(ProgressUpdate update)
    {
        if (update.Status != "running")
        {
            return _bytesPerSecond;
        }

        if (!_hasSample && update.Current == 0 && update.Total == 0)
        {
            return _bytesPerSecond;
        }

        // Files skipped, found already copied or moved by renaming count as done, not as transferred.
        var transferred = update.Current - Math.Min(update.Untransferred, update.Current);
        var now = _now();
        if (!_hasSample)
        {
            _hasSample = true;
            _lastSampleAt = now;
            _lastSampleBytes = transferred;
            return _bytesPerSecond;
        }

        if (transferred < _lastSampleBytes)
        {
            _lastSampleAt = now;
            _lastSampleBytes = transferred;
            _bytesPerSecond = null;
            return _bytesPerSecond;
        }

        var elapsed = (now - _lastSampleAt).TotalSeconds;
        if (transferred == _lastSampleBytes)
        {
            if (elapsed >= StallSeconds)
            {
                // Nothing has moved for a while: the last speed no longer says anything, and the time remaining
                // it gave never changed. The next bytes are measured from here.
                _bytesPerSecond = null;
                _lastSampleAt = now;
            }

            return _bytesPerSecond;
        }

        if (elapsed < 0.25)
        {
            return _bytesPerSecond;
        }

        var sample = (transferred - _lastSampleBytes) / elapsed;
        _bytesPerSecond = _bytesPerSecond is > 0
            ? (_bytesPerSecond.Value * 0.65) + (sample * 0.35)
            : sample;
        _lastSampleAt = now;
        _lastSampleBytes = transferred;
        return _bytesPerSecond;
    }

    private double? AverageFilesPerSecond(ProgressUpdate update)
    {
        if (update.CurrentFiles == 0)
        {
            return null;
        }

        var elapsed = (_now() - _startedAt).TotalSeconds;
        return elapsed > 0.25 ? update.CurrentFiles / elapsed : null;
    }
}
