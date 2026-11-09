using System;
using System.IO;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Every test that sets or reads a plugin environment variable (<c>SUMAFILE_PLUGIN_ROOTS</c>,
/// <c>SUMAFILE_PLUGIN_APPROVALS_PATH</c>). The variables are process-wide, so the collection runs with
/// parallelization disabled: alone, after the parallel collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginEnvironmentCollection
{
    public const string Name = "Plugin environment";
}

[Collection(PluginEnvironmentCollection.Name)]
public class PluginEnvironmentTests
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void DefaultRoots_InstallThenLocalAppData()
    {
        var previous = Environment.GetEnvironmentVariable(PluginDiscovery.RootsVariable);
        try
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, null);
            var roots = PluginDiscovery.DefaultRoots();
            Assert.Equal(2, roots.Count);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "plugins"), roots[0]);
            Assert.Equal(PluginDiscovery.InstallRoot(), roots[0]);
            Assert.Equal(Path.Combine(LocalAppData, "SumaFile", "plugins"), roots[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, previous);
        }
    }

    [Fact]
    public void DefaultRoots_EnvOverride_ReplacesDefaultsAndSkipsEmpties()
    {
        var previous = Environment.GetEnvironmentVariable(PluginDiscovery.RootsVariable);
        try
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, @" C:\one ;;  ; C:\two");
            Assert.Equal([@"C:\one", @"C:\two"], PluginDiscovery.DefaultRoots());

            // Only separators and blanks: no usable entry, so the defaults stay.
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, " ; ");
            Assert.Equal(2, PluginDiscovery.DefaultRoots().Count);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "plugins"), PluginDiscovery.DefaultRoots()[0]);

            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, null);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "plugins"), PluginDiscovery.DefaultRoots()[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, previous);
        }
    }

    [Fact]
    public void UserRoot_IsLocalAppData_EvenWithEnvOverride()
    {
        var expected = Path.Combine(LocalAppData, "SumaFile", "plugins");
        var previous = Environment.GetEnvironmentVariable(PluginDiscovery.RootsVariable);
        try
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, @"C:\only");
            Assert.Equal(expected, PluginDiscovery.UserRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, previous);
        }
    }

    [Fact]
    public void ApprovalStore_DefaultPath_HonorsEnvOverrideOnlyWhenSet()
    {
        var localDefault = Path.Combine(LocalAppData, "SumaFile", "plugin-approvals.json");
        var previous = Environment.GetEnvironmentVariable(PluginApprovalStore.PathVariable);
        try
        {
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, null);
            Assert.Equal(localDefault, PluginApprovalStore.DefaultPath());
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, "  ");
            Assert.Equal(localDefault, PluginApprovalStore.DefaultPath());
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, @" C:\smoke\approvals.json ");
            Assert.Equal(@"C:\smoke\approvals.json", PluginApprovalStore.DefaultPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, previous);
        }
    }

    [Fact]
    public void DescribeOverrides_ReportsActiveEnvironmentOverrides()
    {
        var previousRoots = Environment.GetEnvironmentVariable(PluginDiscovery.RootsVariable);
        var previousApprovals = Environment.GetEnvironmentVariable(PluginApprovalStore.PathVariable);
        var previousDataRoot = Environment.GetEnvironmentVariable(PluginDiscovery.DataRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, null);
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, null);
            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, null);
            Assert.Empty(PluginDiscovery.DescribeOverrides());

            // Blank values are not overrides: DefaultRoots and DefaultPath ignore them.
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, " ; ");
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, "  ");
            Assert.Empty(PluginDiscovery.DescribeOverrides());

            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, @"C:\one;C:\two");
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, @" C:\smoke\approvals.json ");
            Assert.Equal(
                [
                    @"[warn] SUMAFILE_PLUGIN_ROOTS replaces the plugin roots: C:\one;C:\two",
                    @"[warn] SUMAFILE_PLUGIN_APPROVALS_PATH replaces the plugin approvals file: C:\smoke\approvals.json",
                ],
                PluginDiscovery.DescribeOverrides());

            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, null);
            Assert.Equal(
                [@"[warn] SUMAFILE_PLUGIN_APPROVALS_PATH replaces the plugin approvals file: C:\smoke\approvals.json"],
                PluginDiscovery.DescribeOverrides());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, previousRoots);
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, previousApprovals);
            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, previousDataRoot);
        }
    }

    [Fact]
    public void DataRoot_IsLocalAppDataUnlessOverridden()
    {
        var localDefault = Path.Combine(LocalAppData, "SumaFile", "plugins-data");
        var previous = Environment.GetEnvironmentVariable(PluginDiscovery.DataRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, null);
            Assert.Equal(localDefault, PluginDiscovery.DataRoot());
            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, "  ");
            Assert.Equal(localDefault, PluginDiscovery.DataRoot());
            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, @" C:\smoke\plugins-data ");
            Assert.Equal(@"C:\smoke\plugins-data", PluginDiscovery.DataRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, previous);
        }
    }

    [Fact]
    public void DescribeOverrides_ReportsTheDataRootOverride()
    {
        var previousRoots = Environment.GetEnvironmentVariable(PluginDiscovery.RootsVariable);
        var previousApprovals = Environment.GetEnvironmentVariable(PluginApprovalStore.PathVariable);
        var previousDataRoot = Environment.GetEnvironmentVariable(PluginDiscovery.DataRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, null);
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, null);
            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, "  ");
            Assert.Empty(PluginDiscovery.DescribeOverrides());

            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, @" C:\smoke\plugins-data ");
            Assert.Equal(
                [@"[warn] SUMAFILE_PLUGIN_DATA_ROOT replaces the plugin data folder: C:\smoke\plugins-data"],
                PluginDiscovery.DescribeOverrides());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginDiscovery.RootsVariable, previousRoots);
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, previousApprovals);
            Environment.SetEnvironmentVariable(PluginDiscovery.DataRootVariable, previousDataRoot);
        }
    }
}
