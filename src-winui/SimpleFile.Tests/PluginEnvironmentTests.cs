using System;
using System.Collections.Generic;
using System.IO;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Every test that sets or reads a plugin environment variable (<c>SUMAFILE_PLUGIN_ROOTS</c>,
/// <c>SUMAFILE_PLUGIN_APPROVALS_PATH</c>, <c>SUMAFILE_PLUGIN_DATA_ROOT</c>, <c>SUMAFILE_PLUGIN_LOG_PATH</c>). The
/// variables are process-wide, so the collection runs with parallelization disabled: alone, after the parallel
/// collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginEnvironmentCollection
{
    public const string Name = "Plugin environment";
}

/// <summary>
/// The plugin locations (user requirement 2026-11-09): every plugin file lives in the installation folder, and
/// only the four overrides move it.
/// </summary>
[Collection(PluginEnvironmentCollection.Name)]
public class PluginEnvironmentTests
{
    private static readonly string[] PluginVariables =
    [
        PluginDiscovery.RootsVariable,
        PluginApprovalStore.PathVariable,
        PluginDiscovery.DataRootVariable,
        PluginDiscovery.LogPathVariable,
    ];

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Clears every plugin variable for the test and puts the previous values back afterwards.</summary>
    private static void WithCleanPluginEnvironment(Action test)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in PluginVariables)
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        try
        {
            test();
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void DefaultRoots_AreTheInstallRootOnly() => WithCleanPluginEnvironment(() =>
    {
        Assert.Equal([Path.Combine(AppContext.BaseDirectory, "plugins")], PluginDiscovery.DefaultRoots());
        Assert.Equal(PluginDiscovery.InstallRoot(), PluginDiscovery.DefaultRoots()[0]);
    });

    [Fact]
    public void DefaultRoots_EnvOverride_ReplacesDefaultsAndSkipsEmpties() => WithCleanPluginEnvironment(() =>
    {
        Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, @" C:\one ;;  ; C:\two");
        Assert.Equal([@"C:\one", @"C:\two"], PluginDiscovery.DefaultRoots());

        // Only separators and blanks: no usable entry, so the default stays.
        Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, " ; ");
        Assert.Equal([PluginDiscovery.InstallRoot()], PluginDiscovery.DefaultRoots());

        Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, null);
        Assert.Equal([PluginDiscovery.InstallRoot()], PluginDiscovery.DefaultRoots());
    });

    [Fact]
    public void ApprovalStore_DefaultPath_IsInTheInstallFolderUnlessOverridden() => WithCleanPluginEnvironment(() =>
    {
        var installDefault = Path.Combine(AppContext.BaseDirectory, "plugin-approvals.json");
        Assert.Equal(installDefault, PluginApprovalStore.DefaultPath());
        Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, "  ");
        Assert.Equal(installDefault, PluginApprovalStore.DefaultPath());
        Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, @" C:\smoke\approvals.json ");
        Assert.Equal(@"C:\smoke\approvals.json", PluginApprovalStore.DefaultPath());
    });

    [Fact]
    public void DataRoot_IsInTheInstallFolderUnlessOverridden() => WithCleanPluginEnvironment(() =>
    {
        var installDefault = Path.Combine(AppContext.BaseDirectory, "plugins-data");
        Assert.Equal(installDefault, PluginDiscovery.DataRoot());
        Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, "  ");
        Assert.Equal(installDefault, PluginDiscovery.DataRoot());
        Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, @" C:\smoke\plugins-data ");
        Assert.Equal(@"C:\smoke\plugins-data", PluginDiscovery.DataRoot());
    });

    [Fact]
    public void LogPath_IsInTheInstallFolderUnlessOverridden() => WithCleanPluginEnvironment(() =>
    {
        var installDefault = Path.Combine(AppContext.BaseDirectory, "plugins.log");
        Assert.Equal(installDefault, PluginDiscovery.LogPath());
        Environment.SetEnvironmentVariable(PluginDiscovery.LogPathVariable, "  ");
        Assert.Equal(installDefault, PluginDiscovery.LogPath());
        Environment.SetEnvironmentVariable(PluginDiscovery.LogPathVariable, @" C:\smoke\plugins.log ");
        Assert.Equal(@"C:\smoke\plugins.log", PluginDiscovery.LogPath());
    });

    [Fact]
    public void DescribeOverrides_ReportsActiveEnvironmentOverrides() => WithCleanPluginEnvironment(() =>
    {
        Assert.Empty(PluginDiscovery.DescribeOverrides());

        // Blank values are not overrides: the resolvers ignore them.
        Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, " ; ");
        Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, "  ");
        Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, "  ");
        Environment.SetEnvironmentVariable(PluginDiscovery.LogPathVariable, "  ");
        Assert.Empty(PluginDiscovery.DescribeOverrides());

        Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, @"C:\one;C:\two");
        Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, @" C:\smoke\approvals.json ");
        Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, @" C:\smoke\plugins-data ");
        Environment.SetEnvironmentVariable(PluginDiscovery.LogPathVariable, @" C:\smoke\plugins.log ");
        Assert.Equal(
            [
                @"[warn] SUMAFILE_PLUGIN_ROOTS replaces the plugin roots: C:\one;C:\two",
                @"[warn] SUMAFILE_PLUGIN_APPROVALS_PATH replaces the plugin approvals file: C:\smoke\approvals.json",
                @"[warn] SUMAFILE_PLUGIN_DATA_ROOT replaces the plugin data folder: C:\smoke\plugins-data",
                @"[warn] SUMAFILE_PLUGIN_LOG_PATH replaces the plugin log: C:\smoke\plugins.log",
            ],
            PluginDiscovery.DescribeOverrides());

        Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, null);
        Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, null);
        Environment.SetEnvironmentVariable(PluginDiscovery.LogPathVariable, null);
        Assert.Equal(
            [@"[warn] SUMAFILE_PLUGIN_APPROVALS_PATH replaces the plugin approvals file: C:\smoke\approvals.json"],
            PluginDiscovery.DescribeOverrides());
    });

    [Fact]
    public void DescribeOverrides_ReportsTheDataRootOverride() => WithCleanPluginEnvironment(() =>
    {
        Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, @" C:\smoke\plugins-data ");
        Assert.Equal(
            [@"[warn] SUMAFILE_PLUGIN_DATA_ROOT replaces the plugin data folder: C:\smoke\plugins-data"],
            PluginDiscovery.DescribeOverrides());
    });

    [Fact]
    public void DescribeOverrides_ReportsTheLogPathOverride() => WithCleanPluginEnvironment(() =>
    {
        Environment.SetEnvironmentVariable(PluginDiscovery.LogPathVariable, @" C:\smoke\plugins.log ");
        Assert.Equal(
            [@"[warn] SUMAFILE_PLUGIN_LOG_PATH replaces the plugin log: C:\smoke\plugins.log"],
            PluginDiscovery.DescribeOverrides());
    });

    [Fact]
    public void DefaultLocations_NeverPointIntoLocalAppDataSumaFile() => WithCleanPluginEnvironment(() =>
    {
        var legacy = Path.Combine(LocalAppData, "SumaFile");
        var locations = new List<string>(PluginDiscovery.DefaultRoots())
        {
            PluginDiscovery.DataRoot(),
            PluginApprovalStore.DefaultPath(),
            PluginDiscovery.LogPath(),
        };

        Assert.Equal(4, locations.Count);
        foreach (var location in locations)
        {
            Assert.False(
                location.StartsWith(legacy, StringComparison.OrdinalIgnoreCase),
                $"{location} points into {legacy}");
            Assert.StartsWith(AppContext.BaseDirectory, location, StringComparison.OrdinalIgnoreCase);
        }
    });
}
