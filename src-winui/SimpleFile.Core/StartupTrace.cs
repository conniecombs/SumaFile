using System.Diagnostics;

namespace SimpleFile.Core;

public sealed class StartupTimer
{
    private readonly string _scope;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private TimeSpan _last;

    public StartupTimer(string scope)
    {
        _scope = scope;
    }

    public void Mark(string stage, string? detail = null)
    {
        var elapsed = _watch.Elapsed;
        var delta = elapsed - _last;
        _last = elapsed;
        StartupTrace.Write(_scope, stage, elapsed, delta, detail);
    }
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
