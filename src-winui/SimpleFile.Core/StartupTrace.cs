using System.Diagnostics;

namespace SimpleFile.Core;

public sealed class StartupTimer
{
    private readonly string _scope;
    private readonly Action<string, string, TimeSpan, TimeSpan, string?> _write;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<PendingLap> _laps = [];
    private TimeSpan _last;

    public StartupTimer(string scope)
        : this(scope, StartupTrace.Write)
    {
    }

    /// <summary>A timer that hands its lines to <paramref name="write"/> instead of the trace file (tests).</summary>
    internal StartupTimer(string scope, Action<string, string, TimeSpan, TimeSpan, string?> write)
    {
        _scope = scope;
        _write = write;
    }

    /// <summary>Records a stage and writes it, after the laps recorded since the previous mark.</summary>
    public void Mark(string stage, string? detail = null)
    {
        var elapsed = _watch.Elapsed;
        var delta = elapsed - _last;
        _last = elapsed;
        // Written only now that this mark's time is taken, so a lap's write never lands in the span it splits.
        foreach (var lap in _laps)
        {
            _write(_scope, lap.Stage, lap.Elapsed, lap.Delta, lap.Detail);
        }

        _laps.Clear();
        _write(_scope, stage, elapsed, delta, detail);
    }

    /// <summary>
    /// Records a sub-stage as <see cref="Mark"/> does (its delta runs from the previous mark or lap, and the next one's
    /// from it) but writes it with the next <see cref="Mark"/>, so splitting a budgeted span adds no file write to the
    /// span. A lap that no mark follows is never written.
    /// </summary>
    public void Lap(string stage, string? detail = null)
    {
        var elapsed = _watch.Elapsed;
        _laps.Add(new PendingLap(stage, elapsed, elapsed - _last, detail));
        _last = elapsed;
    }

    private readonly record struct PendingLap(string Stage, TimeSpan Elapsed, TimeSpan Delta, string? Detail);
}

public static class StartupTrace
{
    private const long MaxLogBytes = 1_048_576;
    private static readonly object Gate = new();

    public static void Write(
        string scope,
        string stage,
        TimeSpan elapsed,
        TimeSpan delta,
        string? detail = null)
    {
        try
        {
            var logPath = TraceLogPath();
            lock (Gate)
            {
                RotateLogIfNeeded(logPath);
                var message =
                    $"[{DateTime.Now:O}] {scope}.{stage} total_ms={elapsed.TotalMilliseconds:F1} delta_ms={delta.TotalMilliseconds:F1}";
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    message += $" detail={Sanitize(detail)}";
                }

                File.AppendAllText(logPath, message + Environment.NewLine);
            }
        }
        catch
        {
            // Startup diagnostics must never block the app from starting.
        }
    }

    public static void WriteInstant(string scope, string stage, string? detail = null) =>
        Write(scope, stage, TimeSpan.Zero, TimeSpan.Zero, detail);

    private static string TraceLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SumaFile");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "startup-timing.log");
    }

    private static string Sanitize(string detail) =>
        detail.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void RotateLogIfNeeded(string logPath)
    {
        var info = new FileInfo(logPath);
        if (!info.Exists || info.Length < MaxLogBytes)
        {
            return;
        }

        var backup = logPath + ".1";
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        File.Move(logPath, backup);
    }
}
