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

    private static string Line(string stage, double total, double delta, int minute = 0) =>
        $"[2026-09-17T16:{minute:D2}:00.0000000-05:00] {stage} total_ms={total:0.0} delta_ms={delta:0.0}";
}
