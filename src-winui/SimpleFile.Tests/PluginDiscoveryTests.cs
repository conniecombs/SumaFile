using System;
using System.IO;
using System.Linq;
using SimpleFile.PluginHost.Discovery;
using Xunit;

namespace SimpleFile.Tests;

public class PluginDiscoveryTests
{
    private static string Manifest(string id, string sdk = "1.0.0") =>
        "{ \"id\": \"" + id + "\", \"name\": \"N\", \"version\": \"1.0.0\", \"sdkVersion\": \"" + sdk + "\", " +
        "\"publisher\": { \"name\": \"P\" }, \"contributions\": {} }";

    private static string NewTemp() =>
        Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));

    private static string Plugin(string root, string id, string? json = null)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), json ?? Manifest(id));
        return dir;
    }

    [Fact]
    public void Scan_FindsValidPluginsAcrossRoots()
    {
        var tmp = NewTemp();
        try
        {
            var r1 = Path.Combine(tmp, "install");
            var r2 = Path.Combine(tmp, "user");
            Plugin(r2, "com.example.b");
            Plugin(r1, "com.example.a");
            var result = PluginDiscovery.Scan([r1, r2]);
            Assert.Equal(new[] { "com.example.a", "com.example.b" }, result.Select(p => p.Id).ToArray());
            Assert.All(result, p => Assert.Equal(DiscoveryState.Valid, p.State));
            Assert.All(result, p => Assert.NotNull(p.Manifest));
            Assert.Equal(r1, result[0].Root);
            Assert.Equal(Path.Combine(r1, "com.example.a"), result[0].Folder);
            Assert.Null(result[0].ShadowedByFolder);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_SkipsFoldersWithoutManifest()
    {
        var tmp = NewTemp();
        try
        {
            var r = Path.Combine(tmp, "root");
            Directory.CreateDirectory(Path.Combine(r, "empty"));
            Plugin(r, "com.example.a");
            var result = PluginDiscovery.Scan([r]);
            Assert.Equal("com.example.a", Assert.Single(result).Id);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_MarksInvalidManifestWithErrors()
    {
        var tmp = NewTemp();
        try
        {
            var r = Path.Combine(tmp, "root");
            Plugin(r, "com.example.a", "{ not json");
            var p = Assert.Single(PluginDiscovery.Scan([r]));
            Assert.Equal(DiscoveryState.InvalidManifest, p.State);
            Assert.Null(p.Manifest);
            Assert.NotEmpty(p.Errors);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_MarksIncompatibleSdk()
    {
        var tmp = NewTemp();
        try
        {
            var r = Path.Combine(tmp, "root");
            Plugin(r, "com.example.a", Manifest("com.example.a", "99.0.0"));
            var p = Assert.Single(PluginDiscovery.Scan([r]));
            Assert.Equal(DiscoveryState.IncompatibleSdk, p.State);
            Assert.Contains(p.Errors, e => e.Contains("sdkVersion"));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_LaterRootShadowsEarlierRoot()
    {
        var tmp = NewTemp();
        try
        {
            var install = Path.Combine(tmp, "install");
            var user = Path.Combine(tmp, "user");
            Plugin(install, "com.example.a");
            var userFolder = Plugin(user, "com.example.a");
            var result = PluginDiscovery.Scan([install, user]);
            Assert.Equal(2, result.Count);
            var shadowed = result.Single(p => p.Root == install);
            var live = result.Single(p => p.Root == user);
            Assert.Equal(DiscoveryState.Shadowed, shadowed.State);
            Assert.Equal(userFolder, shadowed.ShadowedByFolder);
            Assert.NotNull(shadowed.Manifest);
            Assert.Equal(DiscoveryState.Valid, live.State);
            Assert.Null(live.ShadowedByFolder);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Scan_MissingRootIsNotAnError()
    {
        var tmp = NewTemp();
        Assert.Empty(PluginDiscovery.Scan([Path.Combine(tmp, "does-not-exist")]));
    }

    [Fact]
    public void Scan_SkipsDotPrefixedFolders()
    {
        var tmp = NewTemp();
        try
        {
            Plugin(tmp, "com.example.a");
            // Installer staging, replaced and removing folders carry a real plugin.json.
            Plugin(tmp, ".com.example.a.installing-0123", Manifest("com.example.a"));
            Plugin(tmp, ".com.example.a.replaced-4567", Manifest("com.example.a"));
            Plugin(tmp, ".com.example.b.removing-89ab", Manifest("com.example.b"));

            var result = PluginDiscovery.Scan([tmp]);

            var only = Assert.Single(result);
            Assert.Equal("com.example.a", only.Id);
            Assert.Equal(DiscoveryState.Valid, only.State);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DefaultRoots_InstallThenLocalAppData()
    {
        var roots = PluginDiscovery.DefaultRoots();
        Assert.Equal(2, roots.Count);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "plugins"), roots[0]);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SumaFile", "plugins"), roots[1]);
    }

    // DefaultRoots reads a process-wide variable; every test touching it lives in this class so xUnit runs them one at a time.
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
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SumaFile", "plugins");
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
}
