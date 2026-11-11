using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class StartupTimingBudgetTests
{
    [Fact]
    public void ParseLine_ReadsStageTotalsAndDetails()
    {
        var entry = StartupTimingBudget.ParseLine(
            "[2026-09-17T16:21:41.8424866-05:00] BackendSession.Start.handshake total_ms=56.0 delta_ms=27.8 detail=methods=84");

        Assert.NotNull(entry);
        Assert.Equal("BackendSession.Start", entry.Scope);
        Assert.Equal("handshake", entry.Marker);
        Assert.Equal("BackendSession.Start.handshake", entry.Stage);
        Assert.Equal(56.0, entry.TotalMilliseconds);
        Assert.Equal("methods=84", entry.Detail);
    }

    [Fact]
    public void Evaluate_PassesWhenStartupMarkersMeetBudgets()
    {
        var report = StartupTimingBudget.Evaluate(
            [
                Line("MainWindow.Connect.begin", 0.0, 0.0),
                Line("BackendSession.Start.ready", 230.0, 4.0),
                Line("MainWindow.Connect.workspace-initialized", 900.0, 670.0),
                Line("MainWindow.Connect.first-sync", 1100.0, 200.0),
                Line("MainWindow.Connect.ready", 1150.0, 50.0),
                Line("MainWindow.StartupDriveRefresh.queued", 0.0, 0.0),
                Line("MainWindow.StartupDriveRefresh.refreshed", 5050.0, 5050.0),
            ]);

        Assert.True(report.Passed);
        Assert.Empty(report.Violations);
    }

    [Fact]
    public void Evaluate_FlagsSlowFirstSyncAndEarlyDriveRefresh()
    {
        var report = StartupTimingBudget.Evaluate(
            [
                Line("MainWindow.Connect.begin", 0.0, 0.0),
                Line("BackendSession.Start.ready", 220.0, 4.0),
                Line("MainWindow.Connect.workspace-initialized", 6200.0, 5980.0),
                Line("MainWindow.Connect.first-sync", 7300.0, 1100.0),
                Line("MainWindow.Connect.ready", 7600.0, 300.0),
                Line("MainWindow.StartupDriveRefresh.queued", 0.0, 0.0),
                Line("MainWindow.StartupDriveRefresh.refreshed", 150.0, 150.0),
            ]);

        Assert.False(report.Passed);
        Assert.Contains(report.Violations, violation => violation.Contains("workspace-initialized", StringComparison.Ordinal));
        Assert.Contains(report.Violations, violation => violation.Contains("first-sync", StringComparison.Ordinal));
        Assert.Contains(report.Violations, violation => violation.Contains("StartupDriveRefresh.refreshed", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_UsesLatestMainWindowSession()
    {
        var report = StartupTimingBudget.Evaluate(
            [
                Line("MainWindow.Connect.begin", 0.0, 0.0, minute: 1),
                Line("MainWindow.Connect.ready", 20000.0, 20000.0, minute: 1),
                Line("MainWindow.Connect.begin", 0.0, 0.0, minute: 2),
                Line("BackendSession.Start.ready", 210.0, 4.0, minute: 2),
                Line("MainWindow.Connect.workspace-initialized", 850.0, 640.0, minute: 2),
                Line("MainWindow.Connect.first-sync", 960.0, 110.0, minute: 2),
                Line("MainWindow.Connect.ready", 990.0, 30.0, minute: 2),
            ]);

        Assert.True(report.Passed);
        Assert.Equal(5, report.SessionEvents.Count);
    }

    [Fact]
    public void Evaluate_FlagsSlowPluginDiscovery()
    {
        var report = StartupTimingBudget.Evaluate(
            [
                Line("MainWindow.Connect.begin", 0.0, 0.0),
                Line("BackendSession.Start.ready", 230.0, 4.0),
                Line("MainWindow.Connect.plugins-discovered", 320.0, 80.0),
                Line("MainWindow.Connect.workspace-initialized", 900.0, 580.0),
                Line("MainWindow.Connect.first-sync", 1100.0, 200.0),
                Line("MainWindow.Connect.ready", 1150.0, 50.0),
            ]);

        Assert.False(report.Passed);
        var violation = Assert.Single(report.Violations);
        Assert.Contains("plugins-discovered", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BudgetsThePluginSpanAcrossItsSubStages()
    {
        // No stage alone is over 50 ms, but the span from backend-started to plugins-discovered is.
        var report = StartupTimingBudget.Evaluate(
            [
                Line("MainWindow.Connect.begin", 0.0, 0.0),
                Line("BackendSession.Start.ready", 230.0, 4.0),
                Line("MainWindow.Connect.backend-started", 240.0, 240.0),
                Line("MainWindow.Connect.plugins-begin", 241.0, 1.0),
                Line("MainWindow.Connect.plugins-manager-built", 251.0, 10.0),
                Line("MainWindow.Connect.plugins-scan-awaited", 261.0, 10.0),
                Line("MainWindow.Connect.plugins-evaluated", 271.0, 10.0),
                Line("MainWindow.Connect.plugins-discovered", 301.0, 30.0),
                Line("MainWindow.Connect.workspace-initialized", 900.0, 599.0),
                Line("MainWindow.Connect.first-sync", 1100.0, 200.0),
                Line("MainWindow.Connect.ready", 1150.0, 50.0),
            ]);

        Assert.False(report.Passed);
        var violation = Assert.Single(report.Violations);
        Assert.StartsWith("MainWindow.Connect.plugins-discovered took 61.0 ms", violation, StringComparison.Ordinal);
        Assert.Contains("budget is 50 ms", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_PassesAPluginSpanUnderBudget()
    {
        var report = StartupTimingBudget.Evaluate(
            [
                Line("MainWindow.Connect.begin", 0.0, 0.0),
                Line("BackendSession.Start.ready", 230.0, 4.0),
                Line("MainWindow.Connect.backend-started", 240.0, 240.0),
                Line("MainWindow.Connect.plugins-begin", 241.0, 1.0),
                Line("MainWindow.Connect.plugins-manager-built", 245.0, 4.0),
                Line("MainWindow.Connect.plugins-scan-awaited", 245.0, 0.0),
                Line("MainWindow.Connect.plugins-evaluated", 247.0, 2.0),
                Line("MainWindow.Connect.plugins-discovered", 260.0, 13.0),
                Line("MainWindow.Connect.workspace-initialized", 900.0, 640.0),
                Line("MainWindow.Connect.first-sync", 1100.0, 200.0),
                Line("MainWindow.Connect.ready", 1150.0, 50.0),
            ]);

        Assert.True(report.Passed, string.Join("; ", report.Violations));
    }

    [Fact]
    public void PluginDiscoverySpan_RunsFromTheMarkBeforeTheSubStagesAndSkipsOtherScopes()
    {
        var session = new[]
            {
                Line("MainWindow.Connect.begin", 0.0, 0.0),
                Line("MainWindow.Connect.backend-started", 240.0, 240.0),
                Line("MainWindow.Connect.plugins-begin", 241.0, 1.0),
                // Another scope's line between two of ours: written by another timer, so not part of the span.
                Line("ExplorerWorkspace.Initialize.begin", 0.0, 0.0),
                Line("MainWindow.Connect.plugins-manager-built", 244.0, 3.0),
                Line("MainWindow.Connect.plugins-scan-awaited", 244.5, 0.5),
                Line("MainWindow.Connect.plugins-evaluated", 246.0, 1.5),
                Line("MainWindow.Connect.plugins-discovered", 256.0, 10.0),
                Line("MainWindow.Connect.workspace-initialized", 900.0, 644.0),
            }
            .Select(StartupTimingBudget.ParseLine)
            .Cast<StartupTimingEvent>()
            .ToList();

        var span = StartupTimingBudget.PluginDiscoverySpan(session);

        Assert.NotNull(span);
        Assert.Equal(16.0, span.TotalMilliseconds, 3);
        Assert.Equal(
            ["plugins-begin", "plugins-manager-built", "plugins-scan-awaited", "plugins-evaluated", "plugins-discovered"],
            span.Stages.Select(stage => stage.Marker));
        Assert.Null(StartupTimingBudget.PluginDiscoverySpan(session.Where(stage => stage.Marker != "plugins-discovered").ToList()));
    }

    [Fact]
    public void Evaluate_PassesWithoutPluginStage()
    {
        var report = StartupTimingBudget.Evaluate(
            [
                Line("MainWindow.Connect.begin", 0.0, 0.0),
                Line("BackendSession.Start.ready", 230.0, 4.0),
                Line("MainWindow.Connect.workspace-initialized", 900.0, 670.0),
                Line("MainWindow.Connect.first-sync", 1100.0, 200.0),
                Line("MainWindow.Connect.ready", 1150.0, 50.0),
            ]);

        Assert.True(report.Passed);
        Assert.Empty(report.Violations);
    }

    private static string Line(string stage, double total, double delta, int minute = 0) =>
        $"[2026-09-17T16:{minute:D2}:00.0000000-05:00] {stage} total_ms={total:0.0} delta_ms={delta:0.0}";
}
