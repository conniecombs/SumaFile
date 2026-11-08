using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SimpleFile.PluginHost.Install;
using Xunit;

namespace SimpleFile.Tests;

public sealed class PluginPackageInstallerTests : IDisposable
{
    private const string Id = "com.example.pkg";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));

    public PluginPackageInstallerTests()
    {
        Directory.CreateDirectory(_temp);
    }

    private string UserRoot => Path.Combine(_temp, "user");

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

    [Fact]
    public void Install_ValidPackage_ExtractsIntoIdFolder()
    {
        var package = Package(("plugin.json", Manifest()), ("lib/Plugin.dll", "dll"), ("assets/", ""));

        var folder = PluginPackageInstaller.Install(package, UserRoot);

        Assert.Equal(Path.Combine(Path.GetFullPath(UserRoot), Id), folder);
        Assert.Equal(Manifest(), File.ReadAllText(Path.Combine(folder, "plugin.json")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(folder, "lib", "Plugin.dll")));
        Assert.True(Directory.Exists(Path.Combine(folder, "assets")));
        Assert.Equal(new[] { Id }, Directory.GetDirectories(UserRoot).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Install_ReplacesExistingFolder()
    {
        var existing = Path.Combine(UserRoot, Id);
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "plugin.json"), Manifest(version: "0.9.0"));
        File.WriteAllText(Path.Combine(existing, "stale.txt"), "old");

        var folder = PluginPackageInstaller.Install(Package(("plugin.json", Manifest(version: "2.0.0"))), UserRoot);

        Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(folder, "plugin.json")));
        Assert.False(File.Exists(Path.Combine(folder, "stale.txt")));
        Assert.Equal(new[] { Id }, Directory.GetDirectories(UserRoot).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Install_MissingManifest_Throws()
    {
        var package = Package(("sub/plugin.json", Manifest()), ("Plugin.dll", "dll"));

        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, UserRoot));
        Assert.False(Directory.Exists(UserRoot));
    }

    [Fact]
    public void Install_InvalidManifest_Throws()
    {
        var package = Package(("plugin.json", "{ \"id\": \"com.example.pkg\" }"));

        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, UserRoot));
        Assert.False(Directory.Exists(UserRoot));
    }

    [Fact]
    public void Install_ZipSlipEntry_ThrowsAndWritesNothing()
    {
        var existing = Path.Combine(UserRoot, Id);
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "plugin.json"), Manifest(version: "0.9.0"));
        var evilDrive = Path.Combine(_temp, "evil-rooted.txt");

        foreach (var evil in new[] { "..\evil.txt", "../../evil.txt", "a/../../evil.txt", evilDrive, "/evil.txt" })
        {
            var package = Package(("plugin.json", Manifest(version: "2.0.0")), ("ok.txt", "ok"), (evil, "pwned"));

            Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(package, UserRoot));
        }

        Assert.False(File.Exists(Path.Combine(UserRoot, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_temp, "evil.txt")));
        Assert.False(File.Exists(evilDrive));
        Assert.False(File.Exists(Path.Combine(existing, "ok.txt")));
        Assert.Contains("0.9.0", File.ReadAllText(Path.Combine(existing, "plugin.json")));
        Assert.Equal(new[] { Id }, Directory.GetDirectories(UserRoot).Select(Path.GetFileName).ToArray());
        Assert.Empty(Directory.GetFiles(UserRoot));
    }

    [Fact]
    public void Install_NotAZip_Throws()
    {
        var path = Path.Combine(_temp, "broken.sfplugin");
        File.WriteAllText(path, "not a zip");

        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Install(path, UserRoot));
    }

    [Fact]
    public void Remove_DeletesPluginFolderUnderUserRoot()
    {
        var folder = PluginPackageInstaller.Install(Package(("plugin.json", Manifest()), ("lib/a.dll", "a")), UserRoot);

        PluginPackageInstaller.Remove(folder, UserRoot);

        Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.GetFileSystemEntries(UserRoot));
    }

    [Fact]
    public void Remove_RefusesFolderOutsideUserRoot()
    {
        var installRoot = Path.Combine(_temp, "install");
        var folder = Path.Combine(installRoot, Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), Manifest());

        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Remove(folder, UserRoot));
        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Remove(UserRoot, UserRoot));
        Assert.Throws<PluginInstallException>(() => PluginPackageInstaller.Remove(Path.Combine(UserRoot, Id, "..", "..", "install", Id), UserRoot));
        Assert.True(File.Exists(Path.Combine(folder, "plugin.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch (IOException) { }
    }
}
