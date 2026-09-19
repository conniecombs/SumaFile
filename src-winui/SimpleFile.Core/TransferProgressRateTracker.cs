using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class TransferProgressRateTracker
{
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

        var now = _now();
        if (!_hasSample)
        {
            _hasSample = true;
            _lastSampleAt = now;
            _lastSampleBytes = update.Current;
            return _bytesPerSecond;
        }

        if (update.Current < _lastSampleBytes)
        {
            _lastSampleAt = now;
            _lastSampleBytes = update.Current;
            _bytesPerSecond = null;
            return _bytesPerSecond;
        }

        var elapsed = (now - _lastSampleAt).TotalSeconds;
        if (elapsed < 0.25 || update.Current == _lastSampleBytes)
        {
            return _bytesPerSecond;
        }

        var sample = (update.Current - _lastSampleBytes) / elapsed;
        _bytesPerSecond = _bytesPerSecond is > 0
            ? (_bytesPerSecond.Value * 0.65) + (sample * 0.35)
            : sample;
        _lastSampleAt = now;
        _lastSampleBytes = update.Current;
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
