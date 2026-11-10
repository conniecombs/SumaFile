using SimpleFile.PluginSdk;
using SumaFile.Git.Model;
using Xunit;

namespace SimpleFile.Tests.Git;

public class GitDecorationProviderTests
{
    private const string Folder = @"R:\repo";

    [Fact]
    public async Task GetAsync_MapsRowsAndTheInRepositoryFlag()
    {
        var (host, backend, provider) = Create();
        backend.Decorations = (_, _) => Task.FromResult(new GitFolderDecorations
        {
            InRepository = true,
            Root = Folder,
            Rows =
            [
                new GitDecorationRow { Path = @"R:\repo\a.txt", Status = "modified" },
                new GitDecorationRow { Path = @"R:\repo\newdir", Status = "untracked" },
                new GitDecorationRow { Path = @"R:\repo\café 資料", Status = "staged + modified" },
                new GitDecorationRow { Path = "", Status = "modified" },
            ],
        });

        var result = await provider.GetAsync(Request(Folder, @"R:\repo\a.txt", @"R:\repo\newdir"), CancellationToken.None);

        Assert.Equal(["decorations " + Folder], backend.Calls);
        Assert.Equal(["inRepository"], result.FolderFlags);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(new RowDecoration("modified", "SfDecorationModifiedBrush", "Git: modified"), result.Rows[@"R:\repo\a.txt"]);
        Assert.Equal(new RowDecoration("untracked", "SfDecorationUntrackedBrush", "Git: untracked"), result.Rows[@"r:\REPO\NEWDIR"]);
        Assert.Equal(new RowDecoration("staged + modified", "SfDecorationModifiedBrush", "Git: staged + modified"), result.Rows[@"R:\repo\café 資料"]);

        // A clean folder inside a repository keeps its flag; a folder outside one gets nothing.
        backend.Decorations = (_, _) => Task.FromResult(new GitFolderDecorations { InRepository = true, Root = Folder });
        var clean = await provider.GetAsync(Request(Folder), CancellationToken.None);
        Assert.Empty(clean.Rows);
        Assert.Equal(["inRepository"], clean.FolderFlags);

        backend.Decorations = (_, _) => Task.FromResult(new GitFolderDecorations());
        var outside = await provider.GetAsync(Request(@"R:\elsewhere"), CancellationToken.None);
        Assert.Same(PluginFolderDecorations.Empty, outside);
        Assert.Empty(host.Log.Lines);
    }

    [Fact]
    public async Task GetAsync_EnabledFalse_ReturnsNothingAndCallsNoBackend()
    {
        var (host, backend, provider) = Create();
        host.SettingValues[GitSettings.EnabledKey] = "false";
        backend.Decorations = (_, _) => throw new InvalidOperationException("The backend must not be asked.");

        var result = await provider.GetAsync(Request(Folder, @"R:\repo\a.txt"), CancellationToken.None);

        Assert.Same(PluginFolderDecorations.Empty, result);
        Assert.Empty(backend.Calls);
        Assert.Empty(host.PanelCalls);

        // Turning it back on asks the backend again.
        host.SettingValues[GitSettings.EnabledKey] = "true";
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        Assert.Single(backend.Calls);
    }

    [Fact]
    public async Task GetAsync_TurnedOff_HidesAShownWorkbenchOnce()
    {
        // Shown workbench: on, then off, hides it once; a further off request hides nothing more.
        var (host, _, provider) = Create();
        host.PanelVisible = true;
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        Assert.Empty(host.PanelCalls);

        host.SettingValues[GitSettings.EnabledKey] = "false";
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        Assert.Equal(["hide:workbench"], host.PanelCalls);

        // A workbench the user opens on purpose while Git is off stays open.
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        Assert.Equal(["hide:workbench"], host.PanelCalls);

        // Hidden workbench: turning Git off hides nothing.
        (host, _, provider) = Create();
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        host.SettingValues[GitSettings.EnabledKey] = "false";
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        Assert.Empty(host.PanelCalls);

        // Off from the first request: nothing was turned off, so nothing is hidden.
        (host, _, provider) = Create();
        host.PanelVisible = true;
        host.SettingValues[GitSettings.EnabledKey] = "false";
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        Assert.Empty(host.PanelCalls);
    }

    [Fact]
    public async Task GetAsync_RecycleBin_ReturnsNothing()
    {
        var (_, backend, provider) = Create();

        var result = await provider.GetAsync(Request("recycle-bin:", @"R:\$Recycle.Bin\a.txt"), CancellationToken.None);

        Assert.Same(PluginFolderDecorations.Empty, result);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task GetAsync_GitNotFound_ReturnsNothingAndLogsOnce()
    {
        var (host, backend, provider) = Create();
        backend.Decorations = (_, _) => Task.FromException<GitFolderDecorations>(new InvalidOperationException(GitErrors.NotFound));

        var first = await provider.GetAsync(Request(Folder), CancellationToken.None);
        var second = await provider.GetAsync(Request(@"R:\repo\sub"), CancellationToken.None);

        Assert.Same(PluginFolderDecorations.Empty, first);
        Assert.Same(PluginFolderDecorations.Empty, second);
        Assert.Equal(2, backend.Calls.Count);
        var line = Assert.Single(host.Log.Lines);
        Assert.Contains(GitErrors.NotFound, line);

        // A different failure is logged once too.
        backend.Decorations = (_, _) => Task.FromException<GitFolderDecorations>(new InvalidOperationException("The plugin backend stopped."));
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        await provider.GetAsync(Request(Folder), CancellationToken.None);
        Assert.Equal(2, host.Log.Lines.Count);
    }

    [Fact]
    public async Task GetAsync_Cancelled_ReturnsNothingAndLogsNothing()
    {
        var (host, backend, provider) = Create();
        using var cancellation = new CancellationTokenSource();
        backend.Decorations = async (_, ct) =>
        {
            await cancellation.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new GitFolderDecorations { InRepository = true };
        };

        var result = await provider.GetAsync(Request(Folder), cancellation.Token);

        Assert.Same(PluginFolderDecorations.Empty, result);
        Assert.Empty(host.Log.Lines);
    }

    private static (FakeGitHost Host, FakeGitBackend Backend, GitDecorationProvider Provider) Create()
    {
        var host = new FakeGitHost();
        var backend = new FakeGitBackend();
        return (host, backend, new GitDecorationProvider(backend, host.Settings, host.Panels, host.Logger));
    }

    private static PluginDecorationRequest Request(string folder, params string[] paths) => new("status", folder, paths);
}
