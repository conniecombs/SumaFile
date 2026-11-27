using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class TransferProgressRateTracker
{
    /// <summary>
    /// Seconds without a transferred byte after which no speed is shown, or more when bytes usually come further
    /// apart (see <see cref="MovingRate"/>).
    /// </summary>
    public const double StallSeconds = 3;

    private readonly Func<DateTimeOffset> _now;
    private readonly MovingRate _transferred = new();
    private readonly MovingRate _done = new();
    private DateTimeOffset _startedAt;

    public TransferProgressRateTracker(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Reset();
    }

    public void Reset()
    {
        _startedAt = _now();
        _transferred.Reset();
        _done.Reset();
    }

    public TransferProgressDisplay Format(TransferProgressContext context, ProgressUpdate update)
    {
        var (speed, progress) = Track(update);
        return TransferProgressFormatter.Format(
            context,
            update,
            speed,
            AverageFilesPerSecond(update),
            progress);
    }

    /// <summary>
    /// The speed, measured on the bytes written (a skipped file or one renamed in place moves the bytes done but
    /// writes nothing), and the rate the bytes done grow at, which the time remaining is taken from: the bytes left
    /// to do include those that will be skipped or renamed, which take no time.
    /// </summary>
    private (double? Speed, double? Progress) Track(ProgressUpdate update)
    {
        if (update.Status != "running")
        {
            return (_transferred.Rate, _done.Rate);
        }

        if (!_transferred.HasSample && update.Current == 0 && update.Total == 0)
        {
            return (_transferred.Rate, _done.Rate);
        }

        var now = _now();
        return (_transferred.Track(update.Transferred, now), _done.Track(update.Current, now));
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

    /// <summary>
    /// A moving average of how fast a count of bytes grows.
    /// </summary>
    /// <remarks>
    /// The count can grow in steps: a slow file's bytes reach the window when a buffer is written, seconds apart. A
    /// count that has not changed is taken for stalled, and the rate hidden, only after <see cref="StallSeconds"/>
    /// and after two and a half of the usual gaps between its changes. Until then the time of the last sample is
    /// kept, so that the next change is measured over the whole gap. After a stall the first change only starts a
    /// new measurement: measured over the stall, it would read far too low.
    /// </remarks>
    private sealed class MovingRate
    {
        private DateTimeOffset _sampledAt;
        private ulong _sampledBytes;
        private DateTimeOffset _changedAt;
        private double? _gap;
        private bool _stalled;

        public double? Rate { get; private set; }

        public bool HasSample { get; private set; }

        public void Reset()
        {
            _sampledAt = default;
            _sampledBytes = 0;
            _changedAt = default;
            _gap = null;
            _stalled = false;
            Rate = null;
            HasSample = false;
        }

        public double? Track(ulong bytes, DateTimeOffset now)
        {
            if (!HasSample || bytes < _sampledBytes)
            {
                // The first sample, or a count that went back (a new pass): measured from here.
                HasSample = true;
                _sampledAt = now;
                _sampledBytes = bytes;
                _changedAt = now;
                _stalled = false;
                Rate = null;
                return Rate;
            }

            if (bytes == _sampledBytes)
            {
                var still = (now - _changedAt).TotalSeconds;
                if (still >= Math.Max(StallSeconds, 2.5 * (_gap ?? 0)))
                {
                    Rate = null;
                    _stalled = true;
                }

                return Rate;
            }

            var gap = (now - _changedAt).TotalSeconds;
            _gap = _gap is { } usual ? (usual * 0.7) + (gap * 0.3) : gap;
            _changedAt = now;
            if (_stalled)
            {
                _stalled = false;
                _sampledAt = now;
                _sampledBytes = bytes;
                return Rate;
            }

            var elapsed = (now - _sampledAt).TotalSeconds;
            if (elapsed < 0.25)
            {
                return Rate;
            }

            var sample = (bytes - _sampledBytes) / elapsed;
            Rate = Rate is > 0 ? (Rate.Value * 0.65) + (sample * 0.35) : sample;
            _sampledAt = now;
            _sampledBytes = bytes;
            return Rate;
        }
    }
}
