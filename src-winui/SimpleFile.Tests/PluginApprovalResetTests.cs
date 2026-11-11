using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Final fix wave, item 1: an approval is keyed on (id, plugin.json hash, signature status, folder), which a package with
/// the same plugin.json but other code matches. Install from file (also over an existing copy) and Remove therefore
/// clear the stored approval, so the next scan asks for consent.
/// </summary>
public sealed class PluginApprovalResetTests : IAsyncLifetime
{
    private const string Id = "com.example.reset";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
    private readonly List<PluginManager> _managers = [];

    private string Root => Path.Combine(_temp, "plugins");
    private string StorePath => Path.Combine(_temp, "approvals.json");

    // The same bytes every time: a byte-identical plugin.json is the case the hash cannot tell apart.
    private const string ManifestJson =
        "{ \"id\": \"" + Id + "\", \"name\": \"Reset\", \"version\": \"1.0.0\", \"sdkVersion\": \"1.0.0\", " +
        "\"publisher\": { \"name\": \"P\" }, \"ui\": { \"assembly\": \"Reset.dll\", \"entryType\": \"Reset.Plugin\" }, " +
        "\"contributions\": { \"commands\": [ { \"id\": \"go\", \"label\": \"Go\" } ] } }";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }

        try { Directory.Delete(_temp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private PluginManager Manager()
    {
        var manager = new PluginManager(new PluginManagerOptions
        {
            Roots = [Root],
            ApprovalStorePath = StorePath,
            DataRoot = Path.Combine(_temp, "data"),
            Launcher = new NoLaunches(),
            PipeNameFor = _ => "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N"),
            Settings = new FakeSettings(),
            Progress = new RecordingSink(),
            Notifier = new RecordingNotifier(),
            DialogsFor = _ => null!,
            Workspace = null!,
            Log = (_, _) => { },
        });
        _managers.Add(manager);
        return manager;
    }

    private string Package(string dllContent)
    {
        var path = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".sfplugin");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in new[] { ("plugin.json", ManifestJson), ("Reset.dll", dllContent) })
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    private PluginRecord Record(PluginManager manager) => manager.Plugins.Single(record => record.Id == Id);

    /// <summary>Installs the first package, lets the user approve it, and returns the folder.</summary>
    private async Task<string> InstallAndApproveAsync(PluginManager manager)
    {
        var folder = await manager.InstallPackageAsync(Package("approved code"), Root, CancellationToken.None);
        Assert.Equal(PluginState.NeedsConsent, Record(manager).State);
        await manager.EnableAsync(Id, CancellationToken.None);
        Assert.Equal(PluginState.Enabled, Record(manager).State);
        return folder;
    }

    [Fact]
    public async Task InstallOverAnApprovedCopy_WithTheSamePluginJson_AsksForConsentAgain()
    {
        var manager = Manager();
        var folder = await InstallAndApproveAsync(manager);
        var approvedHash = Record(manager).ManifestHash;

        await manager.InstallPackageAsync(Package("other code"), Root, CancellationToken.None);

        var record = Record(manager);
        Assert.Equal("other code", File.ReadAllText(Path.Combine(folder, "Reset.dll")));
        Assert.Equal(approvedHash, record.ManifestHash);
        Assert.Equal(PluginState.NeedsConsent, record.State);
        Assert.Null(new PluginApprovalStore(StorePath).StoredState(Id, folder));

        // A new manager (the next start) agrees: nothing approves the new code.
        var next = Manager();
        await next.DiscoverAsync(CancellationToken.None);
        Assert.Equal(PluginState.NeedsConsent, Record(next).State);
    }

    [Fact]
    public async Task RemoveThenReinstall_AsksForConsentAgain()
    {
        var manager = Manager();
        var folder = await InstallAndApproveAsync(manager);
        await manager.DisableAsync(Id, CancellationToken.None);
        Assert.Equal(ApprovalState.Disabled, new PluginApprovalStore(StorePath).StoredState(Id, folder));

        await manager.RemovePackageAsync(folder, Root, CancellationToken.None);

        Assert.False(Directory.Exists(folder));
        Assert.DoesNotContain(manager.Plugins, record => record.Id == Id);
        Assert.Null(new PluginApprovalStore(StorePath).StoredState(Id, folder));

        await manager.InstallPackageAsync(Package("approved code"), Root, CancellationToken.None);
        Assert.Equal(PluginState.NeedsConsent, Record(manager).State);
    }

    [Fact]
    public async Task RemoveOfAnEnabledPlugin_ThenReinstall_AsksForConsentAgain()
    {
        // The approval Enable stored would otherwise survive: the reinstalled folder has the same id, hash and path.
        var manager = Manager();
        var folder = await InstallAndApproveAsync(manager);

        await manager.RemovePackageAsync(folder, Root, CancellationToken.None);
        await manager.InstallPackageAsync(Package("approved code"), Root, CancellationToken.None);

        Assert.Equal(PluginState.NeedsConsent, Record(manager).State);
    }

    [Fact]
    public async Task Install_KeepsTheOtherPluginsApprovals()
    {
        var store = new PluginApprovalStore(StorePath);
        var other = Path.Combine(Root, "com.example.other");
        store.Set("com.example.other", new string('a', 64), SignatureStatus.Unsigned, ApprovalState.Enabled, other);
        var manager = Manager();

        await InstallAndApproveAsync(manager);
        await manager.InstallPackageAsync(Package("other code"), Root, CancellationToken.None);

        Assert.Equal(ApprovalState.Enabled, store.StoredState("com.example.other", other));
    }

    [Fact]
    public async Task Install_WhenTheApprovalsFileCannotBeRead_FailsAndKeepsThePreviousVersion()
    {
        var manager = Manager();
        var folder = await InstallAndApproveAsync(manager);
        // Held without sharing: every read fails (after the store's retries), so the approval cannot be cleared.
        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var thrown = await Assert.ThrowsAsync<SimpleFile.PluginHost.Install.PluginInstallException>(
                () => manager.InstallPackageAsync(Package("other code"), Root, CancellationToken.None));
            Assert.StartsWith("The plugin could not be installed: Could not read the plugin approvals file", thrown.Message, StringComparison.Ordinal);
        }

        Assert.Equal("approved code", File.ReadAllText(Path.Combine(folder, "Reset.dll")));
    }

    private sealed class NoLaunches : IPluginProcessLauncher
    {
        public IPluginProcess Launch(
            string executable,
            IReadOnlyList<string> args,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            Action<string> onStderrLine) =>
            throw new InvalidOperationException("these plugins declare no backend");
    }
}
