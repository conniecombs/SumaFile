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

/// <summary>A budgeted span of startup and the stages that split it, in order; their deltas add up to the span.</summary>
public sealed record StartupStageSpan(double TotalMilliseconds, IReadOnlyList<StartupTimingEvent> Stages);

public static partial class StartupTimingBudget
{
    private const string PluginDiscoveredStage = "MainWindow.Connect.plugins-discovered";

    /// <summary>
    /// Most plugin discovery may add to startup (spec §5.2), over the whole <see cref="PluginDiscoverySpan"/>; the
    /// stage is optional.
    /// </summary>
    public const double PluginDiscoveryMaxSpanMs = 50;

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
        CheckPluginDiscovery(sessionEvents, violations);
        CheckDeferredDriveRefresh(sessionEvents, budget.MinStartupDriveRefreshMilliseconds, violations);

        return new StartupPerformanceReport(violations.Count == 0, violations, sessionEvents);
    }

    /// <summary>
    /// The plugin discovery span of a session: <c>MainWindow.Connect.plugins-discovered</c> and the
    /// <c>MainWindow.Connect.plugins-*</c> sub-stages written just before it. Each delta runs from the previous
    /// <c>MainWindow.Connect</c> stage, so the span runs from the stage before the first of them (backend-started) to
    /// plugins-discovered however the App splits it, and a log from before the split budgets plugins-discovered alone.
    /// Lines of other scopes are skipped. Null when the session has no plugins-discovered stage.
    /// </summary>
    public static StartupStageSpan? PluginDiscoverySpan(IReadOnlyList<StartupTimingEvent> sessionEvents)
    {
        ArgumentNullException.ThrowIfNull(sessionEvents);
        var end = -1;
        for (var index = sessionEvents.Count - 1; index >= 0; index--)
        {
            if (string.Equals(sessionEvents[index].Stage, PluginDiscoveredStage, StringComparison.Ordinal))
            {
                end = index;
                break;
            }
        }

        if (end < 0)
        {
            return null;
        }

        var stages = new List<StartupTimingEvent>();
        for (var index = end; index >= 0; index--)
        {
            var entry = sessionEvents[index];
            if (!string.Equals(entry.Scope, "MainWindow.Connect", StringComparison.Ordinal))
            {
                continue;
            }

            if (!entry.Marker.StartsWith("plugins-", StringComparison.Ordinal))
            {
                break;
            }

            stages.Insert(0, entry);
        }

        return new StartupStageSpan(stages.Sum(stage => stage.DeltaMilliseconds), stages);
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

    /// <summary>Flags a plugin discovery span over its budget; a session without one is not a violation.</summary>
    private static void CheckPluginDiscovery(IReadOnlyList<StartupTimingEvent> events, List<string> violations)
    {
        if (PluginDiscoverySpan(events) is { } span && span.TotalMilliseconds > PluginDiscoveryMaxSpanMs)
        {
            var stages = span.Stages.Count == 1 ? "1 stage" : $"{span.Stages.Count} stages";
            violations.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{PluginDiscoveredStage} took {span.TotalMilliseconds:0.0} ms over {stages}; budget is {PluginDiscoveryMaxSpanMs:0} ms"));
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
