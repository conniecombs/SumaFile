using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Contributions;
using SimpleFile.PluginHost.Loading;
using SimpleFile.PluginHost.Trust;
using SimpleFile.PluginSdk;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// The App's bridge from the decoration coordinator to the plugins' row decoration providers: it maps the SDK's
/// answer to Core batches, prefixes folder flags with the plugin id and keeps only the keys that were asked for.
/// </summary>
public class PluginRowDecorationSourceTests : IAsyncLifetime
{
    private const string Id = "com.example.deco";
    private const string Provider = "badges";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
    private PluginManager? _manager;

    private string Root => Path.Combine(_temp, "plugins");
    private string StorePath => Path.Combine(_temp, "approvals.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager is not null)
        {
            await _manager.DisposeAsync();
        }

        try { Directory.Delete(_temp, true); } catch { }
    }

    [Fact]
    public async Task Decorate_MapsRowsAndPrefixesFlags()
    {
        var plugin = new DecoratingPlugin(_ => new PluginFolderDecorations(
            new Dictionary<string, RowDecoration>
            {
                [@"R:\repo\a.txt"] = new("M", "SfDecorationModifiedBrush", "Modified"),
                [@"R:\repo\b.txt"] = new("A"),
            },
            ["inRepository", "dirty"]));
        var source = await SourceAsync(plugin);

        var batch = await source.DecorateAsync(Id, @"R:\repo", [@"R:\repo\a.txt", @"R:\repo\b.txt"], CancellationToken.None);

        Assert.Equal([Id], source.PluginIds);
        Assert.NotNull(batch);
        Assert.Equal(Id, batch!.PluginId);
        Assert.Equal(new RowDecorationEntry(Id, "M", "SfDecorationModifiedBrush", "Modified"), batch.Rows[@"R:\repo\a.txt"]);
        Assert.Equal(new RowDecorationEntry(Id, "A", null, null), batch.Rows[@"R:\repo\b.txt"]);
        Assert.Equal([Id + ":inRepository", Id + ":dirty"], batch.FolderFlags);
        var request = Assert.Single(plugin.Requests);
        Assert.Equal(Provider, request.Provider);
        Assert.Equal(@"R:\repo", request.FolderPath);
        Assert.Equal([@"R:\repo\a.txt", @"R:\repo\b.txt"], request.Paths);
    }

    [Fact]
    public async Task Decorate_DropsKeysThatWereNotRequested()
    {
        var plugin = new DecoratingPlugin(_ => new PluginFolderDecorations(
            new Dictionary<string, RowDecoration>
            {
                [@"R:\repo\a.txt"] = new("M"),
                [@"R:\repo\elsewhere.txt"] = new("M"),
                [@"R:\other\a.txt"] = new("M"),
            },
            []));
        var source = await SourceAsync(plugin);

        var batch = await source.DecorateAsync(Id, @"R:\repo", [@"R:\repo\a.txt", @"R:\repo\b.txt"], CancellationToken.None);

        Assert.NotNull(batch);
        Assert.Equal([@"R:\repo\a.txt"], batch!.Rows.Keys);
        Assert.Empty(batch.FolderFlags);
    }

    [Fact]
    public async Task Decorate_KeysDifferingInCaseOrSeparator_AreKept()
    {
        // Git reports R:/repo/Dir/A.TXT and untracked folders with a trailing separator. The host's key filter runs
        // before the merge, so it must use the same rule as the row lookup (RowDecorationKeys), not ordinal equality.
        var plugin = new DecoratingPlugin(_ => new PluginFolderDecorations(
            new Dictionary<string, RowDecoration>
            {
                ["R:/repo/Dir/A.TXT"] = new("M", "SfDecorationModifiedBrush"),
                [@"R:\repo\untracked\"] = new("?", "SfDecorationUntrackedBrush"),
                // A second spelling of a row already decorated: the first one wins.
                [@"r:\REPO\dir\a.txt"] = new("D", "SfDecorationDeletedBrush"),
            },
            []));
        var source = await SourceAsync(plugin);

        var batch = await source.DecorateAsync(
            Id, @"R:\repo", [@"R:\repo\dir\a.txt", @"R:\repo\untracked"], CancellationToken.None);

        Assert.NotNull(batch);
        Assert.Equal(2, batch!.Rows.Count);
        Assert.Contains(@"R:\repo\dir\a.txt", batch.Rows.Keys, StringComparer.Ordinal);
        Assert.Contains(@"R:\repo\untracked", batch.Rows.Keys, StringComparer.Ordinal);
        Assert.Equal("M", batch.Rows[@"R:\repo\dir\a.txt"].Text);
        Assert.Equal("?", batch.Rows[@"R:\repo\untracked"].Text);

        // And the merged pane finds them for the listed rows.
        var pane = PaneDecorations.Merge(@"R:\repo", [Id], [batch]);
        Assert.Equal("M", pane.For(@"R:\repo\dir\a.txt")?.Text);
        Assert.Equal("?", pane.For(@"R:\repo\untracked")?.Text);
    }

    [Fact]
    public async Task Decorate_ProviderFailure_ReturnsNull()
    {
        var calls = 0;
        var plugin = new DecoratingPlugin(_ => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("bad repo")
            : null!);
        var source = await SourceAsync(plugin);

        Assert.Null(await source.DecorateAsync(Id, @"R:\repo", [@"R:\repo\a.txt"], CancellationToken.None));
        Assert.Null(await source.DecorateAsync(Id, @"R:\repo", [@"R:\repo\a.txt"], CancellationToken.None));
        // A plugin the source does not know is not asked.
        Assert.Null(await source.DecorateAsync("com.example.other", @"R:\repo", [@"R:\repo\a.txt"], CancellationToken.None));
        Assert.Equal(2, plugin.Requests.Count);
        Assert.Equal(PluginState.Enabled, _manager!.Plugins.Single(record => record.Id == Id).State);
    }

    private async Task<PluginRowDecorationSource> SourceAsync(DecoratingPlugin plugin)
    {
        var folder = Path.Combine(Root, Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            {
              "id": "{{Id}}",
              "name": "Deco",
              "version": "1.0.0",
              "publisher": { "name": "P" },
              "sdkVersion": "1.0.0",
              "ui": { "assembly": "Deco.dll", "entryType": "Deco.Plugin" },
              "contributions": { "rowDecorations": { "provider": "{{Provider}}" } }
            }
            """);
        new PluginApprovalStore(StorePath).Set(
            Id, PluginPackageHash.ComputeManifestHash(folder), SignatureStatus.Unsigned, ApprovalState.Enabled, folder);

        var manager = new PluginManager(new PluginManagerOptions
        {
            Roots = [Root],
            ApprovalStorePath = StorePath,
            DataRoot = Path.Combine(_temp, "data"),
            Launcher = new NoLauncher(),
            PipeNameFor = _ => "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N"),
            Settings = new FakeSettings(),
            Progress = new RecordingSink(),
            Notifier = new RecordingNotifier(),
            DialogsFor = _ => null!,
            Workspace = null!,
            Log = (_, _) => { },
        });
        _manager = manager;
        await manager.DiscoverAsync(CancellationToken.None);
        manager.InstanceFactory = _ => plugin;
        return new PluginRowDecorationSource(manager, PluginContributionAdapter.RowDecorationProviders(manager.EnabledPlugins));
    }

    private sealed class DecoratingPlugin(Func<PluginDecorationRequest, PluginFolderDecorations> decorate)
        : IPlugin, IRowDecorationProvider
    {
        private readonly object _lock = new();

        public List<PluginDecorationRequest> Requests { get; } = [];

        public Task ActivateAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct)
        {
            lock (_lock) Requests.Add(request);
            return Task.FromResult(decorate(request));
        }
    }

    private sealed class NoLauncher : IPluginProcessLauncher
    {
        public IPluginProcess Launch(
            string executable,
            IReadOnlyList<string> args,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            Action<string> onStderrLine) =>
            throw new IOException("This test plugin has no backend.");
    }
}
