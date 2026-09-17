using System.Globalization;
using System.Text.RegularExpressions;

namespace SimpleFile.Core;

public sealed record StartupTimingEvent(
    DateTimeOffset Timestamp,
    string Scope,
    string Marker,
    double TotalMilliseconds,
    double DeltaMilliseconds,
    string? Detail)
{
    public string Stage => $"{Scope}.{Marker}";
}

public sealed record StartupPerformanceBudget
{
    public double MaxBackendReadyMilliseconds { get; init; } = 1500;
    public double MaxWorkspaceInitializedMilliseconds { get; init; } = 6000;
    public double MaxFirstSyncMilliseconds { get; init; } = 7000;
    public double MaxReadyMilliseconds { get; init; } = 8000;
    public double MinStartupDriveRefreshMilliseconds { get; init; } = 4500;
}

public sealed record StartupPerformanceReport(
    bool Passed,
    IReadOnlyList<string> Violations,
    IReadOnlyList<StartupTimingEvent> SessionEvents);

public static partial class StartupTimingBudget
{
    private static readonly StartupPerformanceBudget DefaultBudget = new();

    public static StartupPerformanceReport Evaluate(
        IEnumerable<string> lines,
        StartupPerformanceBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        budget ??= DefaultBudget;

        var events = lines
            .Select(ParseLine)
            .Where(entry => entry is not null)
            .Cast<StartupTimingEvent>()
            .ToList();
        var sessionEvents = LatestMainWindowSession(events);
        var violations = new List<string>();

        CheckMaximum(sessionEvents, "BackendSession.Start.ready", budget.MaxBackendReadyMilliseconds, violations);
        CheckMaximum(sessionEvents, "MainWindow.Connect.workspace-initialized", budget.MaxWorkspaceInitializedMilliseconds, violations);
        CheckMaximum(sessionEvents, "MainWindow.Connect.first-sync", budget.MaxFirstSyncMilliseconds, violations);
        CheckMaximum(sessionEvents, "MainWindow.Connect.ready", budget.MaxReadyMilliseconds, violations);
        CheckDeferredDriveRefresh(sessionEvents, budget.MinStartupDriveRefreshMilliseconds, violations);

        return new StartupPerformanceReport(violations.Count == 0, violations, sessionEvents);
    }

    public static StartupTimingEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = TimingLineRegex().Match(line);
        if (!match.Success
            || !DateTimeOffset.TryParse(match.Groups["timestamp"].Value, out var timestamp)
            || !double.TryParse(match.Groups["total"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total)
            || !double.TryParse(match.Groups["delta"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
        {
            return null;
        }

        var stage = match.Groups["stage"].Value;
        var split = stage.LastIndexOf('.');
        if (split <= 0 || split >= stage.Length - 1)
        {
            return null;
        }

        var detail = match.Groups["detail"].Success
            ? match.Groups["detail"].Value.Trim()
            : null;

        return new StartupTimingEvent(
            timestamp,
            stage[..split],
            stage[(split + 1)..],
            total,
            delta,
            string.IsNullOrWhiteSpace(detail) ? null : detail);
    }

    private static IReadOnlyList<StartupTimingEvent> LatestMainWindowSession(IReadOnlyList<StartupTimingEvent> events)
    {
        var startIndex = events
            .Select((entry, index) => (entry, index))
            .Where(pair => string.Equals(pair.entry.Stage, "MainWindow.Connect.begin", StringComparison.Ordinal))
            .Select(pair => pair.index)
            .LastOrDefault(-1);

        return startIndex < 0
            ? events
            : events.Skip(startIndex).ToList();
    }

    private static void CheckMaximum(
        IReadOnlyList<StartupTimingEvent> events,
        string stage,
        double maximumMilliseconds,
        List<string> violations)
    {
        var entry = events.LastOrDefault(item => string.Equals(item.Stage, stage, StringComparison.Ordinal));
        if (entry is null)
        {
            violations.Add($"Missing startup marker: {stage}");
            return;
        }

        if (entry.TotalMilliseconds > maximumMilliseconds)
        {
            violations.Add($"{stage} took {entry.TotalMilliseconds:N1} ms; budget is {maximumMilliseconds:N0} ms");
        }
    }

    private static void CheckDeferredDriveRefresh(
        IReadOnlyList<StartupTimingEvent> events,
        double minimumMilliseconds,
        List<string> violations)
    {
        foreach (var entry in events.Where(item =>
            string.Equals(item.Scope, "MainWindow.StartupDriveRefresh", StringComparison.Ordinal)
            && item.Marker is "refreshed" or "skipped" or "skipped-busy"))
        {
            if (entry.TotalMilliseconds < minimumMilliseconds)
            {
                violations.Add(
                    $"{entry.Stage} ran at {entry.TotalMilliseconds:N1} ms; minimum delay is {minimumMilliseconds:N0} ms");
            }
        }
    }

    [GeneratedRegex(@"^\[(?<timestamp>[^\]]+)\]\s+(?<stage>\S+)\s+total_ms=(?<total>[0-9.]+)\s+delta_ms=(?<delta>[0-9.]+)(?:\s+detail=(?<detail>.*))?$")]
    private static partial Regex TimingLineRegex();
}
