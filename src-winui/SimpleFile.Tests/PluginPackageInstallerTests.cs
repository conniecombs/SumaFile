using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Install;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

public sealed class PluginPackageInstallerTests : IDisposable
{
    private const string Id = "com.example.pkg";
    private const string BundledId = "com.sumafile.sevenzip";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _denials = [];

    public PluginPackageInstallerTests()
    {
        Directory.CreateDirectory(_temp);
    }

    /// <summary>SumaFile's one plugins folder, <c>&lt;install&gt;\plugins</c>, as a test sees it.</summary>
    private string PluginsRoot => Path.Combine(_temp, "plugins");

    private static string Manifest(string id = Id, string version = "1.0.0") =>
        "{ \"id\": \"" + id + "\", \"name\": \"N\", \"version\": \"" + version + "\", \"sdkVersion\": \"1.0.0\", " +
        "\"publisher\": { \"name\": \"P\" } }";

    private string Package(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".sfplugin");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    /// <summary>
    /// The plugins root as a release ships it: a bundled folder <c>com.sumafile.sevenzip</c> and a
    /// <c>bundled.json</c> that lists it. Returns the bundled folder.
    /// </summary>
    private string SeedBundledRoot()
    {
        var bundled = Path.Combine(PluginsRoot, BundledId);
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "plugin.json"), Manifest(BundledId));
        File.WriteAllText(
            Path.Combine(PluginsRoot, BundledPluginList.FileName),
            "{\"plugins\":[{\"id\":\"" + BundledId + "\",\"manifestSha256\":\""
            + PluginPackageHash.ComputeManifestHash(bundled) + "\"}]}");
        return bundled;
    }

    private static PluginRecord Record(string folder, string root, bool isBundledId) =>
        new(
            new DiscoveredPlugin(Path.GetFileName(folder), folder, root, DiscoveryState.Valid, null, [], null),
            PluginState.Enabled,
            new SignatureState(SignatureStatus.Unsigned, null, null),
            null,
            null)
        {
            IsBundledId = isBundledId,
        };

    [Fact]
    public void Install_ValidPackage_ExtractsIntoIdFolder()
    {
        var package = Package(("plugin.json", Manifest()), ("lib/Plugin.dll", "dll"), ("assets/", ""));

        var folder = PluginPackageInstaller.Install(package, PluginsRoot);

        Assert.Equal(Path.Combine(Path.GetFullPath(PluginsRoot), Id), folder);
        Assert.Equal(Manifest(), File.ReadAllText(Path.Combine(folder, "plugin.json")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(folder, "lib", "Plugin.dll")));
        Assert.True(Directory.Exists(Path.Combine(folder, "assets")));
        Assert.Equal(new[] { Id }, Directory.GetDirectories(PluginsRoot).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Install_ReplacesExistingFolder()
    {
        var existing = Path.Combine(PluginsRoot, Id);
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "plugin.json"), Manifest(version: "0.9.0"));
        File.WriteAllText(Path.Combine(existing, "stale.txt"), "old");

        var folder = PluginPackageInstaller.Install(Package(("plugin.json", Manifest(version: "2.0.0"))), PluginsRoot);

        Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(folder, "plugin.json")));
        Assert.False(File.Exists(Path.Combine(folder, "stale.txt")));
        Assert.Equal(new[] { Id }, Directory.GetDirectories(PluginsRoot).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Install_MissingManifest_Throws()
    {
        var package = Package(("sub/plugin.json", Manifest()), ("Plugin.dll", "dll"));

        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot));
        Assert.False(Directory.Exists(PluginsRoot));
    }

    [Fact]
    public void Install_InvalidManifest_Throws()
    {
        var package = Package(("plugin.json", "{ \"id\": \"com.example.pkg\" }"));

        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot));
        Assert.False(Directory.Exists(PluginsRoot));
    }

    /// Parked #28: the installer shares discovery's 1 MiB cap (PluginManifestReader.MaxManifestBytes).
    [Fact]
    public void Install_ManifestLargerThanTheCap_ThrowsAndWritesNothing()
    {
        var manifest = Manifest();
        var atCap = manifest + new string(' ', SimpleFile.PluginHost.Manifest.PluginManifestReader.MaxManifestBytes - manifest.Length);

        var over = Package(("plugin.json", atCap + " "));
        var error = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(over, PluginsRoot));
        Assert.Equal("The package's plugin.json is larger than 1 MiB.", error.Message);
        Assert.False(Directory.Exists(PluginsRoot));

        var folder = PluginPackageInstaller.Install(Package(("plugin.json", atCap)), PluginsRoot);
        Assert.Equal(atCap, File.ReadAllText(Path.Combine(folder, "plugin.json")));
    }

    [Fact]
    public void Install_ZipSlipEntry_ThrowsAndWritesNothing()
    {
        var existing = Path.Combine(PluginsRoot, Id);
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "plugin.json"), Manifest(version: "0.9.0"));
        var evilDrive = Path.Combine(_temp, "evil-rooted.txt");

        foreach (var evil in EvilEntries(evilDrive))
        {
            var package = Package(("plugin.json", Manifest(version: "2.0.0")), ("ok.txt", "ok"), (evil, "pwned"));

            Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot));
        }

        AssertNoStrayFiles(evilDrive);
        Assert.False(File.Exists(Path.Combine(existing, "ok.txt")));
        Assert.Contains("0.9.0", File.ReadAllText(Path.Combine(existing, "plugin.json")));
        Assert.Equal(new[] { Id }, Directory.GetDirectories(PluginsRoot).Select(Path.GetFileName).ToArray());
        Assert.Empty(Directory.GetFiles(PluginsRoot));
    }

    [Fact]
    public void Install_ZipSlipEntry_WithoutExistingInstall_LeavesNoTargetFolder()
    {
        var evilDrive = Path.Combine(_temp, "evil-rooted.txt");

        foreach (var evil in EvilEntries(evilDrive))
        {
            var package = Package(("plugin.json", Manifest()), ("ok.txt", "ok"), (evil, "pwned"));

            var thrown = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot));
            Assert.Contains(evil, thrown.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(PluginsRoot, Id)), evil);
            Assert.False(Directory.Exists(PluginsRoot) && Directory.EnumerateFileSystemEntries(PluginsRoot).Any(), evil);
        }

        AssertNoStrayFiles(evilDrive);
    }

    private static string[] EvilEntries(string evilDrive) =>
    [
        @"..\evil.txt",
        @"a\..\..\x.txt",
        @"sub/..\..\evil.txt",
        "../evil/",
        "../../evil.txt",
        "a/../../evil.txt",
        evilDrive,
        "/evil.txt",
    ];

    private void AssertNoStrayFiles(string evilDrive)
    {
        Assert.False(File.Exists(evilDrive));
        Assert.False(Directory.Exists(Path.Combine(_temp, "evil")));
        Assert.False(File.Exists(Path.Combine(_temp, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_temp, "x.txt")));
        Assert.False(File.Exists(Path.Combine(PluginsRoot, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(PluginsRoot, "x.txt")));
        // Only the generated packages remain beside the plugins root.
        Assert.All(Directory.GetFiles(_temp), file => Assert.EndsWith(".sfplugin", file, StringComparison.Ordinal));
        Assert.All(
            Directory.GetDirectories(_temp).Select(Path.GetFileName),
            name => Assert.Equal("plugins", name));
    }

    [Fact]
    public void Install_NotAZip_Throws()
    {
        var path = Path.Combine(_temp, "broken.sfplugin");
        File.WriteAllText(path, "not a zip");

        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(path, PluginsRoot));
    }

    [Fact]
    public void Install_PackageWithABundledId_IsRefusedAndWritesNothing()
    {
        var bundled = SeedBundledRoot();
        var manifestBefore = File.ReadAllBytes(Path.Combine(bundled, "plugin.json"));
        var listBefore = File.ReadAllBytes(Path.Combine(PluginsRoot, BundledPluginList.FileName));
        var package = Package(("plugin.json", Manifest(BundledId, "9.9.9")), ("evil.dll", "evil"));

        var thrown = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, PluginsRoot));

        Assert.Equal($"{BundledId} ships with SumaFile and cannot be installed from a package.", thrown.Message);
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(bundled, "plugin.json")));
        Assert.Equal(listBefore, File.ReadAllBytes(Path.Combine(PluginsRoot, BundledPluginList.FileName)));
        Assert.False(File.Exists(Path.Combine(bundled, "evil.dll")));
        // No staging, replaced or removing (dot) folder is left behind either.
        Assert.Equal(new[] { BundledId }, Directory.GetDirectories(PluginsRoot).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Install_UserPackage_LandsBesideTheBundledFolders()
    {
        var bundled = SeedBundledRoot();
        var manifestBefore = File.ReadAllBytes(Path.Combine(bundled, "plugin.json"));

        var folder = PluginPackageInstaller.Install(Package(("plugin.json", Manifest()), ("Plugin.dll", "dll")), PluginsRoot);

        Assert.Equal(Path.Combine(Path.GetFullPath(PluginsRoot), Id), folder);
        Assert.Equal("dll", File.ReadAllText(Path.Combine(folder, "Plugin.dll")));
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(bundled, "plugin.json")));
        Assert.Equal(
            new[] { Id, BundledId },
            Directory.GetDirectories(PluginsRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Install_UnwritableRoot_ExplainsTheFolderIsNotWritable()
    {
        SeedBundledRoot();
        _denials.Add(DenyAce.Add(
            PluginsRoot,
            FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.AppendData));

        var thrown = Assert.Throws<PluginInstallException>(
            () => PluginPackageInstaller.Install(Package(("plugin.json", Manifest())), PluginsRoot));

        Assert.StartsWith("SumaFile cannot write to its plugins folder ", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(PluginsRoot), thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(PluginsRoot, Id)));
    }

    [Fact]
    public void Remove_BundledPlugin_IsRefused()
    {
        var bundled = SeedBundledRoot();

        var thrown = Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Remove(bundled, PluginsRoot));

        Assert.Equal($"{BundledId} ships with SumaFile and cannot be removed. Disable it instead.", thrown.Message);
        Assert.True(File.Exists(Path.Combine(bundled, "plugin.json")));
    }

    [Fact]
    public void Remove_DeletesAUserPluginFolderInThePluginsRoot()
    {
        var bundled = SeedBundledRoot();
        var folder = PluginPackageInstaller.Install(Package(("plugin.json", Manifest()), ("lib/a.dll", "a")), PluginsRoot);

        PluginPackageInstaller.Remove(folder, PluginsRoot);

        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(bundled, "plugin.json")));
        Assert.Equal(new[] { BundledId }, Directory.GetDirectories(PluginsRoot).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Remove_RefusesFolderOutsideThePluginsRoot()
    {
        var otherRoot = Path.Combine(_temp, "other");
        var folder = Path.Combine(otherRoot, Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), Manifest());
        const string message = "Only plugins in SumaFile's plugins folder can be removed.";

        Assert.Equal(message, Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Remove(folder, PluginsRoot)).Message);
        Assert.Equal(message, Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Remove(PluginsRoot, PluginsRoot)).Message);
        Assert.Equal(
            message,
            Assert.Throws<PluginInstallException>(
                () => PluginPackageInstaller.Remove(Path.Combine(PluginsRoot, Id, "..", "..", "other", Id), PluginsRoot)).Message);
        Assert.True(File.Exists(Path.Combine(folder, "plugin.json")));
    }

    [Fact]
    public void IsRemovable_UserPluginInThePluginsRoot_IsTrue()
    {
        var folder = Path.Combine(PluginsRoot, Id);
        // Another spelling of the same root (case, trailing separator) still matches.
        var spelled = PluginsRoot.ToUpperInvariant() + Path.DirectorySeparatorChar;

        Assert.True(PluginPackageInstaller.IsRemovable(Record(folder, PluginsRoot, isBundledId: false), spelled));
    }

    [Fact]
    public void IsRemovable_BundledIdWithAModifiedManifest_IsFalse()
    {
        // A bundled id stays SumaFile's whatever its manifest hash: an edited bundled plugin needs consent, but it is
        // never removed.
        var bundled = SeedBundledRoot();
        File.WriteAllText(Path.Combine(bundled, "plugin.json"), Manifest(BundledId, "6.6.6"));

        Assert.False(PluginPackageInstaller.IsRemovable(Record(bundled, PluginsRoot, isBundledId: true), PluginsRoot));
    }

    [Fact]
    public void IsRemovable_PluginInAnotherRoot_IsFalse()
    {
        var otherRoot = Path.Combine(_temp, "other");

        Assert.False(PluginPackageInstaller.IsRemovable(
            Record(Path.Combine(otherRoot, Id), otherRoot, isBundledId: false), PluginsRoot));
    }

    public void Dispose()
    {
        // The deny entries first, or the folder could not be deleted.
        foreach (var denial in _denials)
        {
            denial.Dispose();
        }

        try { Directory.Delete(_temp, true); } catch (IOException) { }
    }
}

/// <summary>
/// Adds a Deny entry for the current user to one folder (not inherited), so a test can make it unwritable without
/// administrator rights; disposing removes exactly that entry again.
/// </summary>
internal static class DenyAce
{
    public static IDisposable Add(string folder, FileSystemRights rights)
    {
        Directory.CreateDirectory(folder);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new Removal(info, rule);
    }

    private sealed class Removal(DirectoryInfo info, FileSystemAccessRule rule) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                var security = info.GetAccessControl();
                security.RemoveAccessRuleSpecific(rule);
                info.SetAccessControl(security);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone already: nothing to remove.
            }
        }
    }
}
