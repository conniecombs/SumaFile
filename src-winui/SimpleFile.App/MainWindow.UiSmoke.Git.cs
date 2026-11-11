#if DEBUG
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Discovery;
using SimpleFile.PluginHost.Trust;

namespace SimpleFile.App;

// The bundled Git plugin (com.sumafile.git) end to end in the real host, against the repositories that
// scripts/smoke-winui-ui.ps1 creates under SUMAFILE_SMOKE_GIT (repo, a bare origin.git, a peer clone that pushed one
// commit, and not-a-repo). Excluded from release builds.
public sealed partial class MainWindow
{
    private const string SmokeGitId = "com.sumafile.git";
    private const string SmokeGitBackendName = "sumafile-git-backend";
    private const string SmokeGitPanelCommand = "plugin:com.sumafile.git:git-panel";
    private const string SmokeGitRefreshCommand = "plugin:com.sumafile.git:git-refresh";
    private const string SmokeGitCommitCommand = "plugin:com.sumafile.git:git-commit";
    private const string SmokeGitEnabledKey = "plugin.com.sumafile.git.enabled";
    private const string SmokeGitLegacyKey = "enableGitIntegration";
    private const string SmokeGitFlag = SmokeGitId + ":inRepository";
    private const string SmokeGitMenuId = "ctx-plugin-menu:" + SmokeGitId;
    private const string SmokeGitChromeId = "PluginPanel." + SmokeGitId + ".workbench";
    private const string SmokeGitWindowId = "PluginPanelWindow." + SmokeGitId + ".workbench";
    private const string SmokeGitPanelTitle = "Git workbench";
    private const string SmokeGitTitle = "Git";
    private const string SmokeGitOffMessage = "Git integration is turned off. Turn it on in Settings > Plugins > Git.";
    private const string SmokeGitCommitTitle = "Git commit";
    private const string SmokeGitCommitText = "smoke commit";

    // The plugin's automation ids (plugins/git/ui/SumaFile.Git/Views/GitAutomationIds.cs).
    private const string SmokeGitBodyId = "Git.Workbench";
    private const string SmokeGitBranchId = "Git.Branch";
    private const string SmokeGitSummaryId = "Git.Summary";
    private const string SmokeGitFetchId = "Git.Fetch";
    private const string SmokeGitPullId = "Git.Pull";
    private const string SmokeGitPushId = "Git.Push";
    private const string SmokeGitCommitId = "Git.Commit";
    private const string SmokeGitStageId = "Git.Stage";
    private const string SmokeGitUnstageId = "Git.Unstage";
    private const string SmokeGitDiscardId = "Git.Discard";
    private const string SmokeGitDiffId = "Git.Diff";
    private const string SmokeGitChangesId = "Git.Changes";
    private const string SmokeGitDiffTextId = "Git.DiffText";
    private const string SmokeGitCommitMessageId = "Git.CommitMessage";

    private static readonly TimeSpan SmokeGitUiWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SmokeGitNetworkWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SmokeGitNavigationWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SmokeGitCliTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SmokeGitSettle = TimeSpan.FromMilliseconds(750);

    /// <summary>The plugin's ten commands as the host exposes them (plugins/git/plugin.json).</summary>
    private static readonly string[] SmokeGitCommands =
    [
        SmokeGitPanelCommand,
        SmokeGitRefreshCommand,
        "plugin:com.sumafile.git:git-fetch",
        "plugin:com.sumafile.git:git-pull",
        "plugin:com.sumafile.git:git-push",
        SmokeGitCommitCommand,
        "plugin:com.sumafile.git:git-stage-selected",
        "plugin:com.sumafile.git:git-unstage-selected",
        "plugin:com.sumafile.git:git-discard-selected",
        "plugin:com.sumafile.git:git-diff-selected",
    ];

    /// <summary>The repositories scripts/smoke-winui-ui.ps1 created.</summary>
    private sealed record SmokeGitFixture(string Root, string Repo, string Origin, string NotARepo);

    /// <summary>
    /// "with": the Git plugin in the order a user meets it, every wait bounded (10 s for UI, 60 s for git network
    /// steps) and every git command line the smoke runs itself bounded at 30 s:
    /// <list type="number">
    /// <item>Trust and surface: bundled trust, the ten commands, the offered toolbar item, the backend under the install
    /// root, and no decoration request before ready.</item>
    /// <item>The legacy migration turns Git off; commands still run and say so; the Settings switch turns it on.</item>
    /// <item>Decorations in <c>repo</c>, none in <c>not-a-repo</c>.</item>
    /// <item>The workbench panel from its toolbar toggle, which stays visible outside a repository while shown.</item>
    /// <item>Stage, unstage, diff, discard, fetch, pull, commit and push, each checked with the git command line.</item>
    /// <item>Detach, dock, resize, then Disable with the commit dialog open on the detached window; a save while
    /// disabled keeps the toolbar item, and Enable brings it back in place.</item>
    /// <item>A toolbar toggle the user removed stays removed across a backend restart and a disable and enable.</item>
    /// </list>
    /// "none": nothing of Git anywhere, and nothing that can start git.
    /// </summary>
    private async Task SmokeGitAsync(List<object> results, string mode, Func<string, Task> checkpoint, DateTimeOffset? readyAt)
    {
        var fixture = SmokeGitFixtureInfo();
        SmokeGitAssertIsolation();
        if (mode == "none")
        {
            await SmokeGitNoneAsync(results, fixture);
            return;
        }

        if (mode != "with")
        {
            return;
        }

        var manager = _plugins ?? throw new InvalidOperationException("Plugin manager was not created");
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");
        var fileOps = workspace.FileOps ?? throw new InvalidOperationException("File operations are not available");
        var transfers = _transfer ?? throw new InvalidOperationException("The transfer manager is not available");

        await checkpoint("git-trust");
        var backendPid = await SmokeGitTrustAsync(manager, workspace, readyAt);
        await checkpoint("git-migration");
        await SmokeGitMigrationAsync(manager, workspace, fileOps, fixture);
        await checkpoint("git-decorations");
        await SmokeGitDecorationsAsync(fixture);
        await checkpoint("git-workbench");
        var (branch, changes) = await SmokeGitPanelAsync(fixture);
        await checkpoint("git-mutations");
        var mutations = await SmokeGitMutationsAsync(transfers, fixture);
        await checkpoint("git-detach-disable");
        var bottomHeight = await SmokeGitDetachAndDisableAsync(manager, workspace, fileOps, fixture);
        await checkpoint("git-removed-toggle");
        await SmokeGitRemovedToggleAsync(manager, workspace, fileOps);

        HidePluginPanel();
        transfers.ClearCompleted();
        CloseTransferProgressWindow();
        results.Add(new
        {
            scenario = "git",
            mode,
            repository = fixture.Repo,
            backendPid,
            branch,
            changes,
            fetchMessage = mutations.FetchMessage,
            commit = mutations.CommitSha,
            commitMessage = mutations.CommitMessage,
            bottomHeight,
            passed = true,
        });
    }

    private sealed record SmokeGitMutations(string FetchMessage, string CommitSha, string CommitMessage);

    /// <summary>Step 1. Returns the backend's process id.</summary>
    private async Task<int> SmokeGitTrustAsync(PluginManager manager, ExplorerWorkspace workspace, DateTimeOffset? readyAt)
    {
        // Discovered from <exe dir>\plugins and Enabled through bundled.json; activateOn startup runs its backend.
        var installRoot = PluginDiscovery.InstallRoot();
        PluginRecord? record = null;
        int[] backends = [];
        await SmokeWaitWithinAsync(
            () => Task.FromResult((record = manager.Plugins.FirstOrDefault(item => item.Id == SmokeGitId)) is { BackendRunning: true }
                && (backends = SmokeProcessIdsUnder(SmokeGitBackendName, installRoot)).Length == 1),
            TimeSpan.FromSeconds(15),
            () => record is null
                ? $"{SmokeGitId} was not discovered under {installRoot}"
                : $"{SmokeGitId} is {record.State} with backend running {record.BackendRunning} and "
                    + $"{SmokeProcessIdsUnder(SmokeGitBackendName, installRoot).Length} {SmokeGitBackendName} under {installRoot}: {record.Error}");
        SmokeAssert(record!.State == PluginState.Enabled, $"{SmokeGitId} is {record.State}, expected Enabled: {record.Error}");
        SmokeAssert(record.IsBundledId, $"{SmokeGitId} is not listed in bundled.json");
        var installFolder = Path.Combine(installRoot, SmokeGitId);
        SmokeAssert(SmokeSameFolder(record.Discovered.Folder, installFolder),
            $"{SmokeGitId} was discovered in {record.Discovered.Folder}, expected {installFolder}");
        var approvalsPath = PluginApprovalStore.DefaultPath();
        var approvals = File.Exists(approvalsPath) ? await File.ReadAllTextAsync(approvalsPath) : "";
        SmokeAssert(!approvals.Contains(SmokeGitId, StringComparison.OrdinalIgnoreCase),
            $"{approvalsPath} holds a record for {SmokeGitId}; bundled trust must not write one");

        // The ten commands are on the palette; no core Git command is left.
        SmokeGitAssertPaletteCommands("With Git on");
        var bare = AppCommandCatalog.All.Where(command => command.Id.StartsWith("git-", StringComparison.Ordinal)).Select(command => command.Id).ToArray();
        SmokeAssert(bare.Length == 0, "Core Git commands are still in the catalog: " + string.Join(", ", bare));
        SmokeAssert(workspace.Settings.CommandSurface.OfferedPrimaryItems.Contains(SmokeGitPanelCommand, StringComparer.Ordinal),
            $"OfferedPrimaryItems lacks {SmokeGitPanelCommand}: " + string.Join(", ", workspace.Settings.CommandSurface.OfferedPrimaryItems));

        // Carry-over (h): no decoration request reached a provider before ready.
        var firstRequest = workspace.RowDecorations.FirstRequestAt;
        SmokeAssert(readyAt is { } ready && firstRequest is { } first && first > ready,
            $"The first decoration request was at {firstRequest:O}, ready at {readyAt:O}");
        return backends[0];
    }

    /// <summary>
    /// Step 2 (Review Focus 1): a 1.0.4 user who turned Git integration off. The migration copies the off value once;
    /// with Git off nothing Git shows, yet the commands run and say Git is off; the Settings switch reads off and,
    /// turned on, decorates the open folder without a navigation.
    /// </summary>
    private async Task SmokeGitMigrationAsync(PluginManager manager, ExplorerWorkspace workspace, FileOperationService fileOps, SmokeGitFixture fixture)
    {
        await fileOps.SetSettingAsync(SmokeGitEnabledKey, "").WaitAsync(SmokeGitUiWait);
        await fileOps.SetSettingAsync(SmokeGitLegacyKey, "false").WaitAsync(SmokeGitUiWait);
        var copied = await manager.MigrateLegacySettingsAsync(CancellationToken.None).WaitAsync(SmokeGitUiWait);
        SmokeAssert(copied == 1, $"MigrateLegacySettingsAsync copied {copied} settings, expected 1");
        var stored = await fileOps.GetSettingAsync(SmokeGitEnabledKey).WaitAsync(SmokeGitUiWait);
        SmokeAssert(stored == "false", $"{SmokeGitEnabledKey} reads '{stored}' after the migration, expected 'false'");

        await workspace.InvalidateDecorationsAsync().WaitAsync(SmokeGitUiWait);
        await SmokeGitNavigateAsync(fixture.Repo, "tracked.txt");
        await SmokeGitStaysAsync(
            () => SmokeGitUndecorated() && !SmokeGitHasFlag() && SmokeGitMenuHidden() && SmokeGitToolbarHidden(),
            () => "With Git off, repo shows Git: " + SmokeGitDescribe());

        // The commands stay on the palette and say that Git is off; the message also proves Notify reaches the bar.
        SmokeGitAssertPaletteCommands("With Git off");
        SmokeGitClearMessageBar();
        await RunAppCommandAsync(SmokeGitRefreshCommand).WaitAsync(SmokeGitUiWait);
        await SmokeGitMessageAsync(message => message == SmokeGitOffMessage, "the turned-off message after " + SmokeGitRefreshCommand);

        // Settings > Plugins > Git: the switch reads the migrated value, and turning it on writes through the card.
        var navigationToken = workspace.Primary.NavigationToken;
        await SmokeGitSettingsSwitchAsync(fileOps);
        await SmokeGitWaitAsync(
            () => SmokeGitDecorated("tracked.txt", "modified") && SmokeGitHasFlag() && SmokeGitToolbarShown(),
            () => "repo's decorations and the Git toolbar toggle after the Settings switch: " + SmokeGitDescribe());
        SmokeAssert(workspace.Primary.NavigationToken == navigationToken,
            $"The primary pane navigated during the Settings switch: token {workspace.Primary.NavigationToken}, expected {navigationToken}");
        SmokeAssert(PathRules.PathsEqual(workspace.Primary.Path, fixture.Repo), $"The primary pane shows {workspace.Primary.Path}, expected {fixture.Repo}");
    }

    /// <summary>Opens Settings on Plugins, checks the Git card's switch reads off, turns it on through the control, closes Settings.</summary>
    private async Task SmokeGitSettingsSwitchAsync(FileOperationService fileOps)
    {
        var toggleId = PluginSettingsPanelBuilder.SettingAutomationId(SmokeGitId, "enabled");
        var showing = _fileOperationDialogs.ShowSettingsAsync("Plugins");
        try
        {
            SettingsWindow? settings = null;
            await SmokeGitWaitAsync(
                () => (settings = _fileOperationDialogs.OpenSettingsWindow) is { IsShown: true }
                    && settings.Content is FrameworkElement { IsLoaded: true }
                    && settings.SmokePluginsList.Children.Count > 0,
                () => "the Settings window with plugin cards");
            // The Git plugin's own page, under Plugins.
            settings!.SmokeSelectPlugin(SmokeGitId);
            await SmokeGitWaitAsync(() => settings.SmokeSelectedPlugin == SmokeGitId, () => "the Git plugin's Settings page");

            // The panel re-renders on plugin changes, so the card and its switch are looked up again each time. The
            // switch starts at the manifest default (on) and reads the stored value once it has loaded it.
            ToggleSwitch? toggle = null;
            FrameworkElement? card = null;
            await SmokeGitWaitAsync(
                () =>
                {
                    card = settings!.SmokePluginsList.Children.OfType<FrameworkElement>()
                        .FirstOrDefault(item => SmokeFindById<ToggleSwitch>(item, toggleId) is not null);
                    toggle = card is null ? null : SmokeFindById<ToggleSwitch>(card, toggleId);
                    return toggle is { IsOn: false };
                },
                () => $"{toggleId} to read off (the migrated value): found {toggle is not null}, on {toggle?.IsOn}");
            SmokeAssert(SmokeLogicalDescendants<TextBlock>(card!).Any(text => text.Text == SmokeGitTitle),
                $"{toggleId} is not in the Git card");

            toggle!.IsOn = true;
            string? stored = null;
            await SmokeGitPollAsync(
                async () => (stored = await fileOps.GetSettingAsync(SmokeGitEnabledKey)) == "true",
                SmokeGitUiWait,
                () => $"{SmokeGitEnabledKey} to read 'true' after the switch, reads '{stored}'");
        }
        finally
        {
            _fileOperationDialogs.CloseSettingsWindow();
            await showing.WaitAsync(SmokeGitUiWait);
        }
    }

    /// <summary>Step 3: the badges, brush keys and folder flag in repo; nothing Git in not-a-repo.</summary>
    private async Task SmokeGitDecorationsAsync(SmokeGitFixture fixture)
    {
        (string Name, string Text, string Brush)[] expected =
        [
            ("tracked.txt", "modified", "SfDecorationModifiedBrush"),
            ("new.txt", "untracked", "SfDecorationUntrackedBrush"),
            ("staged.txt", "added", "SfDecorationAddedBrush"),
            ("discard-me.txt", "modified", "SfDecorationModifiedBrush"),
        ];
        await SmokeGitWaitAsync(
            () => expected.All(item => PrimaryFiles.Any(row => row.Name == item.Name && row.DecorationText == item.Text
                    && row.DecorationBrushKey == item.Brush && row.DecorationPlugins.Contains(SmokeGitId)))
                && SmokeGitHasFlag(),
            () => "repo's rows to be decorated: " + SmokeGitDescribe());
        foreach (var unchanged in new[] { SevenZipUnicodeFolder, SevenZipLiteralFile })
        {
            var row = PrimaryFiles.FirstOrDefault(item => item.Name == unchanged);
            SmokeAssert(row is { DecorationText.Length: 0 }, $"repo's unchanged '{unchanged}' is listed {row is not null}, decorated '{row?.DecorationText}'");
        }

        SmokeAssert(SmokeGitMenuGroup() is { Hidden: false }, "The Git menu group is hidden in repo");

        await SmokeGitNavigateAsync(fixture.NotARepo, "plain.txt");
        await SmokeGitStaysAsync(
            () => SmokeGitUndecorated() && !SmokeGitHasFlag() && SmokeGitMenuHidden() && SmokeGitToolbarHidden(),
            () => "not-a-repo shows Git: " + SmokeGitDescribe());
    }

    /// <summary>Step 4. Returns the branch text and the number of changes the panel lists.</summary>
    private async Task<(string Branch, int Changes)> SmokeGitPanelAsync(SmokeGitFixture fixture)
    {
        await SmokeGitNavigateAsync(fixture.Repo, "tracked.txt");
        await SmokeGitWaitAsync(() => SmokeGitToolbarShown() && SmokeGitDecorated("tracked.txt", "modified"),
            () => "the Git toolbar toggle in repo: " + SmokeGitDescribe());
        SmokeAssert(_shownPluginPanel is null && _pluginPanelWindow is null, "A plugin panel is shown before the Git panel check");
        SmokeGitAssertToggle(false, "Before the Git panel is shown");

        // The generated toolbar button toggles the panel; it shows docked in the bottom region.
        OnGeneratedToolbarCommandClick(SmokeGitToggleButton()!, new RoutedEventArgs());
        await SmokeGitWaitAsync(
            () => PluginPanelBottomRegion.Visibility == Visibility.Visible
                && SmokeGitIdOf(PluginPanelBottomHost.Child) == SmokeGitChromeId
                && SmokeGitBody() is { IsLoaded: true },
            () => "the Git panel docked at the bottom: " + SmokeGitDescribePanel());
        await SmokeGitWaitAsync(() => SmokeGitToggleIs(true), () => "the Git toggle to read shown: " + SmokeGitDescribeToggle());

        TextBlock? branch = null;
        await SmokeGitWaitAsync(
            () => (branch = SmokeGitBody() is { } body ? SmokeFindById<TextBlock>(body, SmokeGitBranchId) : null) is not null
                && (branch.Text ?? "").StartsWith("main", StringComparison.Ordinal),
            () => $"{SmokeGitBranchId} to name main, reads '{branch?.Text}'");

        var porcelain = SmokeGitLines(await SmokeGitCliAsync(fixture.Repo, "status", "--porcelain=v1"));
        SmokeAssert(porcelain.Length == 4, "git status --porcelain=v1 lists: " + string.Join(" | ", porcelain));
        await SmokeGitWaitAsync(
            () => SmokeGitChanges() is { } list && list.Items.Count == porcelain.Length,
            () => $"{SmokeGitChangesId} to hold {porcelain.Length} rows, holds {SmokeGitChanges()?.Items.Count}");

        // Outside a repository the decorations and the flag go, but the toggle of the shown panel stays, toggled on.
        await SmokeGitNavigateAsync(fixture.NotARepo, "plain.txt");
        await SmokeGitWaitAsync(
            () => SmokeGitUndecorated() && !SmokeGitHasFlag() && SmokeGitToolbarShown() && SmokeGitToggleIs(true),
            () => "not-a-repo with the Git panel shown: " + SmokeGitDescribe() + "; " + SmokeGitDescribeToggle());
        await SmokeGitNavigateAsync(fixture.Repo, "tracked.txt");
        await SmokeGitWaitAsync(() => SmokeGitDecorated("tracked.txt", "modified") && SmokeGitHasFlag(),
            () => "repo decorated again: " + SmokeGitDescribe());
        return (branch!.Text, porcelain.Length);
    }

    /// <summary>
    /// Step 5 (Review Focus 2 and 3): each mutation through the panel, checked with the git command line and in the
    /// pane. Fetch and pull run before the commit, so the pull is a fast-forward.
    /// </summary>
    private async Task<SmokeGitMutations> SmokeGitMutationsAsync(TransferManagerViewModel transfers, SmokeGitFixture fixture)
    {
        var repo = fixture.Repo;

        // Stage new.txt, then unstage it.
        SmokeInvoke(await SmokeGitSelectChangeAsync("new.txt", SmokeGitStageId));
        await SmokeGitPollAsync(
            async () => SmokeGitLines(await SmokeGitCliAsync(repo, "diff", "--cached", "--name-only")).Contains("new.txt"),
            SmokeGitUiWait,
            () => "git diff --cached --name-only to list new.txt after Stage");
        SmokeInvoke(await SmokeGitSelectChangeAsync("new.txt", SmokeGitUnstageId));
        await SmokeGitPollAsync(
            async () => !SmokeGitLines(await SmokeGitCliAsync(repo, "diff", "--cached", "--name-only")).Contains("new.txt"),
            SmokeGitUiWait,
            () => "git diff --cached --name-only to drop new.txt after Unstage");

        // Diff tracked.txt, once the refresh that Unstage started has landed: a status refresh replaces the rows and
        // clears the preview, as 1.0.4 did (CI run 42740804951).
        await SmokeGitSettleAsync("after Unstage");
        await SmokeGitDiffAsync("tracked.txt", "Working tree changes");

        // Discard discard-me.txt through the confirmation's primary button.
        var discardPath = Path.Combine(repo, "discard-me.txt");
        var committed = await SmokeGitCliAsync(repo, "show", "HEAD:discard-me.txt");
        SmokeAssert(await File.ReadAllTextAsync(discardPath) != committed, "discard-me.txt is not modified before Discard");
        SmokeInvoke(await SmokeGitSelectChangeAsync("discard-me.txt", SmokeGitDiscardId));
        var confirm = await SmokeOpenDialogAsync(SmokeGitBody()!.XamlRoot!, "Discard this change?");
        SmokeInvoke(SmokeDialogButton(confirm, "PrimaryButton"));
        await SmokeGitWaitAsync(
            () => File.ReadAllText(discardPath) == committed
                && PrimaryFiles.Any(row => row.Name == "discard-me.txt" && row.DecorationText.Length == 0),
            () => $"discard-me.txt to read its committed text and lose its badge: {SmokeGitDescribe()}");
        await SmokeGitSettleAsync("after Discard");

        // Fetch: a Transfers row, behind 1 and ahead 0, and git's result in the message bar.
        var folderName = Path.GetFileName(repo);
        SmokeGitClearMessageBar();
        var fetch = await SmokeGitTransferAsync(transfers, SmokeGitFetchId, "Fetching " + folderName);
        string? fetchMessage = null;
        await SmokeGitMessageAsync(message => (fetchMessage = message).Length > 0, "the fetch result");
        var counts = (await SmokeGitCliAsync(repo, "rev-list", "--left-right", "--count", "HEAD...@{u}"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        SmokeAssert(counts is ["0", "1"], $"After Fetch, ahead/behind is [{string.Join(", ", counts)}], expected ahead 0 and behind 1");
        TextBlock? summary = null;
        await SmokeGitWaitAsync(
            () => (summary = SmokeGitBody() is { } body ? SmokeFindById<TextBlock>(body, SmokeGitSummaryId) : null) is not null
                && (summary.Text ?? "").Contains("1 behind", StringComparison.Ordinal)
                && !(summary.Text ?? "").Contains("ahead", StringComparison.Ordinal),
            () => $"{SmokeGitSummaryId} to read 1 behind and nothing ahead, reads '{summary?.Text}'");

        // Pull, a fast-forward: the peer's file arrives on disk and in the pane.
        await SmokeGitTransferAsync(transfers, SmokeGitPullId, "Pulling " + folderName);
        var fromPeer = Path.Combine(repo, "from-peer.txt");
        await SmokeGitWaitAsync(() => File.Exists(fromPeer) && PrimaryFiles.Any(row => row.Name == "from-peer.txt"),
            () => $"from-peer.txt on disk ({File.Exists(fromPeer)}) and in the pane: {SmokeGitDescribe()}");
        await SmokeGitSettleAsync("after Pull");

        // Commit the staged staged.txt: git's own output leads the message.
        SmokeGitClearMessageBar();
        SmokeInvoke(await SmokeGitButtonAsync(SmokeGitCommitId));
        var commitDialog = await SmokeOpenDialogAsync(SmokeGitBody()!.XamlRoot!, SmokeGitCommitTitle);
        SmokeRemoteControl<TextBox>(commitDialog, SmokeGitCommitMessageId).Text = SmokeGitCommitText;
        SmokeInvoke(SmokeDialogButton(commitDialog, "PrimaryButton"));
        string? subject = null;
        await SmokeGitPollAsync(
            async () => (subject = (await SmokeGitCliAsync(repo, "log", "-1", "--format=%s")).Trim()) == SmokeGitCommitText,
            SmokeGitNetworkWait,
            () => $"git log -1 to name '{SmokeGitCommitText}', names '{subject}'");
        string? commitMessage = null;
        await SmokeGitMessageAsync(
            message => (commitMessage = message).Contains(SmokeGitCommitText, StringComparison.Ordinal)
                && message.StartsWith("[main ", StringComparison.Ordinal),
            "git's commit output");
        var head = (await SmokeGitCliAsync(repo, "rev-parse", "HEAD")).Trim();
        await SmokeGitSettleAsync("after Commit");

        // Push: the bare origin's main is repo's HEAD.
        await SmokeGitTransferAsync(transfers, SmokeGitPushId, "Pushing " + folderName);
        string? origin = null;
        await SmokeGitPollAsync(
            async () => (origin = (await SmokeGitCliAsync(fixture.Origin, "rev-parse", "main")).Trim()) == head,
            SmokeGitNetworkWait,
            () => $"origin.git's main to be {head}, is {origin}");
        SmokeAssert(fetch.Status == TransferOperationStatus.Completed, $"The fetch row is {fetch.Status}");
        return new SmokeGitMutations(fetchMessage!, head, commitMessage!);
    }

    /// <summary>
    /// Step 6 (critique 4): detach and dock, resize, then Disable while the commit dialog is open on the detached
    /// window; a save while Git is disabled keeps its toolbar item, and Enable draws it again in place. Returns the
    /// bottom height after the resize.
    /// </summary>
    private async Task<double> SmokeGitDetachAndDisableAsync(
        PluginManager manager,
        ExplorerWorkspace workspace,
        FileOperationService fileOps,
        SmokeGitFixture fixture)
    {
        var installRoot = PluginDiscovery.InstallRoot();

        // Detach opens one PluginPanelWindow, tracked by the plugin's window host; its Dock docks it again.
        var window = await SmokeGitDetachAsync();
        SmokeAssert(window.Title == SmokeGitPanelTitle, $"The Git panel window is titled '{window.Title}'");
        SmokeAssert(SmokeGitIdOf(window.Content) == SmokeGitWindowId, $"The Git panel window's content is {SmokeGitIdOf(window.Content)}");
        SmokeAssert(PluginWindowHostFor(SmokeGitId) is { } host && host.OpenWindows.Count == 1 && ReferenceEquals(host.OpenWindows[0], window),
            $"The Git window host tracks {PluginWindowHostFor(SmokeGitId)?.OpenWindows.Count ?? -1} windows, expected the panel window alone");
        SmokeAssert(PluginPanelBottomHost.Child is null && PluginPanelBottomRegion.Visibility == Visibility.Collapsed,
            "The bottom region still holds the detached Git panel");
        var dock = SmokeFindById<Button>(window.Chrome.Root, SmokeGitWindowId + ".Detach");
        SmokeAssert(dock is not null, $"{SmokeGitWindowId}.Detach is missing");
        await SmokeGitWaitAsync(() => dock!.IsLoaded, () => "the window's Dock button loaded");
        SmokeInvoke(dock!);
        await SmokeGitWaitAsync(
            () => window.IsClosed && _pluginPanelWindow is null && SmokeGitIdOf(PluginPanelBottomHost.Child) == SmokeGitChromeId
                && SmokeGitBody() is { IsLoaded: true },
            () => "the Git panel docked after Dock: " + SmokeGitDescribePanel());

        // The grips' resize path: the saved bottom height shrinks by 40, clamped, and the region follows.
        var heightBefore = CurrentPluginPanelBottomHeight();
        var expectedHeight = UiSettings.NormalizePluginPanelBottomHeight(
            PluginPanelLayout.ClampBottomHeight(heightBefore - 40, FilePaneShell.ActualHeight));
        ResizePluginPanelBy(-40);
        SmokeAssert(Math.Abs(workspace.Settings.PluginPanelBottomHeight - expectedHeight) < 0.01,
            $"PluginPanelBottomHeight is {workspace.Settings.PluginPanelBottomHeight} after -40 from {heightBefore}, expected {expectedHeight}");
        SmokeAssert(Math.Abs(PluginPanelBottomRegion.Height - CurrentPluginPanelBottomHeight()) < 0.01,
            $"The bottom region is {PluginPanelBottomRegion.Height} high, expected {CurrentPluginPanelBottomHeight()}");

        // Detach again and start a commit: its dialog opens on the detached window and holds the dialog gate.
        var detached = await SmokeGitDetachAsync();
        SmokeAssert(!DialogHost.IsDialogOpen, "The dialog gate is held before the commit dialog opens");
        var commit = RunAppCommandAsync(SmokeGitCommitCommand);
        await SmokeOpenDialogAsync(detached.Chrome.Root.XamlRoot!, SmokeGitCommitTitle);
        SmokeAssert(DialogHost.IsDialogOpen, "The Git commit dialog does not hold the dialog gate");

        var layout = workspace.Settings.CommandSurface;
        var position = SmokeGitToolbarPosition(layout);
        SmokeAssert(position >= 0, $"The toolbar layout lacks {SmokeGitPanelCommand}");
        await manager.DisableAsync(SmokeGitId, CancellationToken.None).WaitAsync(SmokeGitUiWait);
        await SmokeGitWaitAsync(
            () => detached.IsClosed && _pluginPanelWindow is null && !DialogHost.IsDialogOpen
                && SmokeGitUndecorated() && !SmokeGitHasFlag() && SmokeGitMenuGroup() is null
                && !_primaryToolbarActionElements.ContainsKey(SmokeGitPanelCommand)
                && SmokeProcessIdsUnder(SmokeGitBackendName, installRoot).Length == 0,
            () => $"10 s after the disable: window closed {detached.IsClosed}, dialog gate held {DialogHost.IsDialogOpen}, "
                + $"menu group {SmokeGitMenuGroup() is not null}, toolbar button {_primaryToolbarActionElements.ContainsKey(SmokeGitPanelCommand)}, "
                + $"backends {SmokeProcessIdsUnder(SmokeGitBackendName, installRoot).Length}, {SmokeGitDescribe()}");
        await commit.WaitAsync(SmokeGitUiWait);

        // A save while Git is disabled keeps its item, in place; the toolbar does not draw it.
        await workspace.SaveUiSettingsAsync().WaitAsync(SmokeGitUiWait);
        var saved = CommandSurfaceLayout.FromJson(await fileOps.GetSettingAsync(CommandSurfaceLayout.SettingsKey).WaitAsync(SmokeGitUiWait));
        SmokeAssert(SmokeGitToolbarPosition(saved) >= 0, $"The layout saved while Git is disabled lost {SmokeGitPanelCommand}");
        SmokeAssert(SmokeGitToolbarPosition(layout) == position,
            $"{SmokeGitPanelCommand} moved from {position} to {SmokeGitToolbarPosition(layout)} while Git was disabled");
        SmokeAssert(!_primaryToolbarActionElements.ContainsKey(SmokeGitPanelCommand), "The toolbar draws the Git toggle while Git is disabled");

        // Enable brings the decorations, the menu group and the toolbar toggle back, at its old position.
        await manager.EnableAsync(SmokeGitId, CancellationToken.None).WaitAsync(SmokeGitUiWait);
        await SmokeGitWaitAsync(
            () => manager.Plugins.FirstOrDefault(item => item.Id == SmokeGitId) is { State: PluginState.Enabled }
                && SmokeGitDecorated("tracked.txt", "modified") && SmokeGitHasFlag()
                && SmokeGitMenuGroup() is { Hidden: false } && SmokeGitToolbarShown(),
            () => $"Git after the re-enable: {manager.Plugins.FirstOrDefault(item => item.Id == SmokeGitId)?.State}, "
                + $"menu group {SmokeGitMenuGroup() is { Hidden: false }}, toolbar shown {SmokeGitToolbarShown()}, {SmokeGitDescribe()}");
        SmokeAssert(SmokeGitToolbarPosition(layout) == position,
            $"After the re-enable {SmokeGitPanelCommand} is at {SmokeGitToolbarPosition(layout)}, expected {position}");
        SmokeAssert(PathRules.PathsEqual(workspace.Primary.Path, fixture.Repo), $"The primary pane left repo: {workspace.Primary.Path}");
        return expectedHeight;
    }

    /// <summary>Invokes the docked panel's Detach and returns its loaded window.</summary>
    private async Task<PluginPanelWindow> SmokeGitDetachAsync()
    {
        var chrome = PluginPanelBottomHost.Child as FrameworkElement;
        SmokeAssert(SmokeGitIdOf(chrome) == SmokeGitChromeId, "The Git panel is not docked before Detach: " + SmokeGitDescribePanel());
        var detach = SmokeFindById<Button>(chrome!, SmokeGitChromeId + ".Detach");
        SmokeAssert(detach is not null && AutomationProperties.GetName(detach) == "Detach " + SmokeGitPanelTitle,
            $"{SmokeGitChromeId}.Detach is missing or named '{(detach is null ? "" : AutomationProperties.GetName(detach))}'");
        await SmokeGitWaitAsync(() => detach!.IsLoaded, () => "the Detach button loaded");
        SmokeInvoke(detach!);
        PluginPanelWindow? window = null;
        await SmokeGitWaitAsync(
            () => (window = _pluginPanelWindow) is { IsClosed: false }
                && SmokeGitBody() is { IsLoaded: true, XamlRoot: not null } body
                && ReferenceEquals(body.XamlRoot, window.Chrome.Root.XamlRoot),
            () => "the Git panel window with its loaded body: " + SmokeGitDescribePanel());
        return window!;
    }

    /// <summary>
    /// Step 7 (critique 4): a toolbar toggle the user removed stays removed across a backend restart, which raises
    /// Changed, and across a disable, a save and an enable.
    /// </summary>
    private async Task SmokeGitRemovedToggleAsync(PluginManager manager, ExplorerWorkspace workspace, FileOperationService fileOps)
    {
        var layout = workspace.Settings.CommandSurface;
        var removed = layout.PrimaryToolbar.RemoveAll(item => item.IsCommand && item.Id == SmokeGitPanelCommand);
        SmokeAssert(removed == 1, $"Removed {removed} Git toolbar items, expected 1");
        ApplyCommandSurfaceLayout();
        await workspace.SaveUiSettingsAsync().WaitAsync(SmokeGitUiWait);
        SmokeAssert(!_primaryToolbarActionElements.ContainsKey(SmokeGitPanelCommand), "The toolbar still draws the removed Git toggle");

        var changed = 0;
        void OnChanged(object? sender, EventArgs args) => Interlocked.Increment(ref changed);
        manager.Changed += OnChanged;
        try
        {
            await manager.RestartBackendAsync(SmokeGitId, _pluginCts?.Token ?? CancellationToken.None).WaitAsync(SmokeGitUiWait);
            await SmokeGitWaitAsync(
                () => Volatile.Read(ref changed) > 0 && manager.Plugins.FirstOrDefault(item => item.Id == SmokeGitId) is { BackendRunning: true },
                () => $"Changed after the backend restart ({Volatile.Read(ref changed)}) and the backend running "
                    + $"({manager.Plugins.FirstOrDefault(item => item.Id == SmokeGitId)?.BackendRunning})");
            await SmokeGitDrainAsync();
            SmokeGitAssertToggleRemoved(layout, "After the backend restart");

            await manager.DisableAsync(SmokeGitId, CancellationToken.None).WaitAsync(SmokeGitUiWait);
            await SmokeGitWaitAsync(() => !ToolbarActionCatalog.All.Any(action => action.Id == SmokeGitPanelCommand),
                () => "the Git toolbar action to leave the catalog after the disable");
            await workspace.SaveUiSettingsAsync().WaitAsync(SmokeGitUiWait);
            await manager.EnableAsync(SmokeGitId, CancellationToken.None).WaitAsync(SmokeGitUiWait);
            await SmokeGitWaitAsync(
                () => manager.Plugins.FirstOrDefault(item => item.Id == SmokeGitId) is { State: PluginState.Enabled }
                    && ToolbarActionCatalog.All.Any(action => action.Id == SmokeGitPanelCommand)
                    && _pluginPanels.Any(panel => panel.PluginId == SmokeGitId),
                () => $"Git's contributions after the re-enable: {manager.Plugins.FirstOrDefault(item => item.Id == SmokeGitId)?.State}");
            await SmokeGitDrainAsync();
            SmokeGitAssertToggleRemoved(layout, "After a disable, a save and an enable");
            var saved = CommandSurfaceLayout.FromJson(await fileOps.GetSettingAsync(CommandSurfaceLayout.SettingsKey).WaitAsync(SmokeGitUiWait));
            SmokeAssert(SmokeGitToolbarPosition(saved) < 0, $"The saved layout holds the removed {SmokeGitPanelCommand} again");
        }
        finally
        {
            manager.Changed -= OnChanged;
        }
    }

    private void SmokeGitAssertToggleRemoved(CommandSurfaceLayout layout, string when)
    {
        SmokeAssert(SmokeGitToolbarPosition(layout) < 0, $"{when}: the toolbar layout holds the removed {SmokeGitPanelCommand} again");
        SmokeAssert(layout.OfferedPrimaryItems.Contains(SmokeGitPanelCommand, StringComparer.Ordinal),
            $"{when}: OfferedPrimaryItems lost {SmokeGitPanelCommand}");
        SmokeAssert(!_primaryToolbarActionElements.ContainsKey(SmokeGitPanelCommand), $"{when}: the toolbar draws the removed Git toggle");
    }

    /// <summary>
    /// "none": no Git command, toolbar action, element, menu entry, Settings text, decoration or backend, and no
    /// decoration provider, so nothing in the App can start git.
    /// </summary>
    private async Task SmokeGitNoneAsync(List<object> results, SmokeGitFixture fixture)
    {
        static bool IsGit(string? id) => id is not null
            && (id.StartsWith("git-", StringComparison.Ordinal) || id.StartsWith("plugin:" + SmokeGitId + ":", StringComparison.Ordinal));
        var commands = AppCommandCatalog.All.Where(command => IsGit(command.Id)).Select(command => command.Id).ToArray();
        SmokeAssert(commands.Length == 0, "Git commands without plugins: " + string.Join(", ", commands));
        var actions = ToolbarActionCatalog.All.Where(action => IsGit(action.Id) || IsGit(action.CommandId)).Select(action => action.Id).ToArray();
        SmokeAssert(actions.Length == 0, "Git toolbar actions without plugins: " + string.Join(", ", actions));

        // A panel's chrome and its parts are PluginPanel.<pluginId>.<panelId>[.part]; the host's own resize grips
        // (PluginPanel.RightGrip and PluginPanel.BottomGrip, in MainWindow.xaml) are always there.
        static bool IsPanelChrome(string automationId) =>
            automationId.StartsWith("PluginPanel.", StringComparison.Ordinal)
            && automationId is not ("PluginPanel.RightGrip" or "PluginPanel.BottomGrip");
        var content = Content as FrameworkElement ?? throw new InvalidOperationException("The main window has no content");
        var elements = SmokeLogicalDescendants<FrameworkElement>(content).Concat(SmokeDescendants<FrameworkElement>(content)).ToArray();
        var gitElements = elements
            .Where(element => element.Name is "GitPanel" or "GitToggleButton"
                || IsPanelChrome(AutomationProperties.GetAutomationId(element)))
            .Select(element => element.Name.Length > 0 ? element.Name : AutomationProperties.GetAutomationId(element))
            .ToArray();
        SmokeAssert(content.FindName("GitPanel") is null && content.FindName("GitToggleButton") is null && gitElements.Length == 0,
            "The main window has Git or plugin panel elements: " + string.Join(", ", gitElements));
        SmokeAssert(_pluginPanels.Count == 0 && PluginPanelBottomHost.Child is null && PluginPanelRightRegion.Child is null,
            $"Plugin panels without plugins: {_pluginPanels.Count} declared");

        // The menus of a file in repo: no core Git entry and no Git plugin group.
        await SmokeGitNavigateAsync(fixture.Repo, "tracked.txt");
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");
        var tracked = Path.Combine(fixture.Repo, "tracked.txt");
        workspace.SelectPath(tracked, PaneId.Primary);
        SyncFromWorkspace();
        IReadOnlyList<FileRow> selected = [];
        await SmokeGitWaitAsync(() => (selected = SelectedRowsForPane(PaneId.Primary)).Count == 1 && selected[0].Path == tracked,
            () => "tracked.txt to be selected: " + string.Join(", ", selected.Select(row => row.Name)));
        var request = BuildContextMenuRequest(selected);
        var menuIds = SmokeFlatten(ContextMenuBuilder.Build(request)).Concat(SmokeFlatten(ContextMenuBuilder.BuildPaneMoreMenu(request)))
            .Select(entry => entry.Id)
            .Where(id => id.Contains("git", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        SmokeAssert(menuIds.Length == 0, "The context menus have Git entries without plugins: " + string.Join(", ", menuIds));

        // No decoration ever arrives in repo.
        await SmokeGitStaysAsync(() => SmokeGitUndecorated() && !SmokeGitHasFlag(), () => "repo is decorated without plugins: " + SmokeGitDescribe());

        // Settings: no "Git integration" text anywhere, including the collapsed category panels.
        var showing = _fileOperationDialogs.ShowSettingsAsync();
        Exception? scanFailure = null;
        try
        {
            SettingsWindow? settings = null;
            // The first Settings window of a process builds all of its XAML, which a loaded runner can take well over
            // the UI wait to do (CI run 42760411425).
            await SmokeWaitWithinAsync(
                () => Task.FromResult((settings = _fileOperationDialogs.OpenSettingsWindow) is { IsShown: true }
                    && settings.Content is FrameworkElement { IsLoaded: true }),
                TimeSpan.FromSeconds(30),
                () => "the Settings window");
            var settingsContent = (FrameworkElement)settings!.Content;
            var texts = SmokeLogicalDescendants<TextBlock>(settingsContent).Concat(SmokeDescendants<TextBlock>(settingsContent)).ToArray();
            SmokeAssert(settingsContent.FindName("PluginsPanel") is Panel plugins && texts.Any(text => text.Text == "Plugins" && SmokeIsInside(text, plugins)),
                "The Settings scan did not reach the Plugins panel's heading");
            var gitTexts = texts.Where(text => (text.Text ?? "").Contains("Git integration", StringComparison.OrdinalIgnoreCase)).ToArray();
            SmokeAssert(gitTexts.Length == 0, "The Settings window shows 'Git integration' without plugins");
        }
        catch (Exception exception)
        {
            scanFailure = exception;
            throw;
        }
        finally
        {
            _fileOperationDialogs.CloseSettingsWindow();
            try
            {
                await showing.WaitAsync(SmokeGitUiWait);
            }
            catch when (scanFailure is not null)
            {
                // The scan's own failure is the one to report, not what closing the window then did to the show.
            }
        }

        // Nothing can start git: no backend runs and no decoration provider is set.
        var backends = SmokeProcessIdsUnder(SmokeGitBackendName, AppContext.BaseDirectory);
        SmokeAssert(backends.Length == 0, $"{SmokeGitBackendName} runs without plugins: {string.Join(", ", backends)}");
        SmokeAssert(workspace.RowDecorations.HasProviders == false, "A row decoration provider is set without plugins");
        results.Add(new { scenario = "git", mode = "none", repository = fixture.Repo, passed = true });
    }

    /// <summary>
    /// For <see cref="SmokeServiceReconnectAsync"/>: the Git backend before the service is killed (it is the App's
    /// client, not the service's).
    /// </summary>
    private async Task<int[]> SmokeGitBackendsBeforeServiceKillAsync(string installRoot)
    {
        int[] backends = [];
        await SmokeWaitWithinAsync(
            () => Task.FromResult((backends = SmokeProcessIdsUnder(SmokeGitBackendName, installRoot)).Length == 1),
            TimeSpan.FromSeconds(15),
            () => $"one {SmokeGitBackendName} under {installRoot} before the service kill, found {backends.Length}");
        return backends;
    }

    /// <summary>
    /// For <see cref="SmokeServiceReconnectAsync"/>: after the reconnect the Git backend is the same process and repo,
    /// listed again through the new service, is decorated again.
    /// </summary>
    private async Task SmokeGitAfterServiceReconnectAsync(string installRoot, int[] backendsBefore)
    {
        var after = SmokeProcessIdsUnder(SmokeGitBackendName, installRoot);
        SmokeAssert(after.SequenceEqual(backendsBefore),
            $"The service restart changed {SmokeGitBackendName} from [{string.Join(", ", backendsBefore)}] to [{string.Join(", ", after)}]");
        var git = _plugins?.Plugins.FirstOrDefault(item => item.Id == SmokeGitId);
        SmokeAssert(git?.State == PluginState.Enabled, $"{SmokeGitId} is {git?.State} after the service restart: {git?.Error}");
        var fixture = SmokeGitFixtureInfo();
        await SmokeGitNavigateAsync(fixture.Repo, "tracked.txt");
        await (_workspace?.RefreshAsync(PaneId.Primary) ?? Task.CompletedTask).WaitAsync(SmokeGitNavigationWait);
        await SmokeGitWaitAsync(() => SmokeGitDecorated("tracked.txt", "modified") && SmokeGitHasFlag(),
            () => "repo decorated again after the service reconnect: " + SmokeGitDescribe());
    }

    private static SmokeGitFixture SmokeGitFixtureInfo()
    {
        var root = Environment.GetEnvironmentVariable("SUMAFILE_SMOKE_GIT");
        SmokeAssert(!string.IsNullOrWhiteSpace(root)
                && Directory.Exists(Path.Combine(root, "repo", ".git"))
                && Directory.Exists(Path.Combine(root, "origin.git"))
                && File.Exists(Path.Combine(root, "not-a-repo", "plain.txt")),
            $"SUMAFILE_SMOKE_GIT does not hold the Git fixture (repo, origin.git, not-a-repo): '{root}'");
        var full = Path.GetFullPath(root!);
        SmokeAssert(SmokeIsUnder(full, UiSmokeRoot()!), $"The Git fixture {full} is outside the smoke root");
        return new SmokeGitFixture(full, Path.Combine(full, "repo"), Path.Combine(full, "origin.git"), Path.Combine(full, "not-a-repo"));
    }

    /// <summary>Git reads none of the developer's config and never walks out of the smoke root.</summary>
    private static void SmokeGitAssertIsolation()
    {
        var smokeRoot = UiSmokeRoot()!;
        SmokeAssert(Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM") == "1", "GIT_CONFIG_NOSYSTEM is not 1");
        var global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        SmokeAssert(!string.IsNullOrWhiteSpace(global) && File.Exists(global) && SmokeIsUnder(global, smokeRoot),
            $"GIT_CONFIG_GLOBAL is '{global}', expected a file under {smokeRoot}");
        var ceiling = Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES");
        SmokeAssert(!string.IsNullOrWhiteSpace(ceiling) && SmokeSameFolder(ceiling, smokeRoot),
            $"GIT_CEILING_DIRECTORIES is '{ceiling}', expected {smokeRoot}");
    }

    /// <summary>
    /// Runs one read-only git command in <paramref name="workingDirectory"/>, bounded at 30 s, with the App's isolated
    /// git environment, no fsmonitor and no optional locks (so it never races the backend for the index lock).
    /// </summary>
    private static async Task<string> SmokeGitCliAsync(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-c", "core.fsmonitor=false", "--no-optional-locks" }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }

        var line = "git " + string.Join(' ', args);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{line} did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(SmokeGitCliTimeout))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It exited meanwhile.
                }

                throw new TimeoutException($"{line} in {workingDirectory} did not finish within {SmokeGitCliTimeout.TotalSeconds:0} s");
            }
        }

        var output = await stdout;
        var error = await stderr;
        SmokeAssert(process.ExitCode == 0, $"{line} in {workingDirectory} exited {process.ExitCode}: {error.Trim()}");
        return output;
    }

    private static string[] SmokeGitLines(string output) =>
        [.. output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];

    private static Task SmokeGitWaitAsync(Func<bool> condition, Func<string> describe) =>
        SmokeWaitWithinAsync(() => Task.FromResult(condition()), SmokeGitUiWait, describe);

    /// <summary>Polls a condition that runs git; a git failure fails the smoke at once rather than being retried.</summary>
    private static async Task SmokeGitPollAsync(Func<Task<bool>> condition, TimeSpan timeout, Func<string> describe)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timer.Elapsed > timeout)
            {
                throw new TimeoutException($"UI smoke timed out after {timeout.TotalSeconds:0} s: {describe()}");
            }

            await Task.Delay(200);
        }
    }

    /// <summary>The condition holds throughout <see cref="SmokeGitSettle"/>: the check for something that must not arrive.</summary>
    private static async Task SmokeGitStaysAsync(Func<bool> condition, Func<string> describe)
    {
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < SmokeGitSettle)
        {
            SmokeAssert(condition(), describe());
            await Task.Delay(50);
        }
    }

    /// <summary>Lets the UI thread run what it queued (a Changed handler's contribution render) before an assertion.</summary>
    private async Task SmokeGitDrainAsync()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var drained = new TaskCompletionSource();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => drained.TrySetResult());
            await drained.Task.WaitAsync(SmokeGitUiWait);
        }
    }

    private async Task SmokeGitNavigateAsync(string folder, string expectedName)
    {
        var workspace = _workspace ?? throw new InvalidOperationException("Workspace is not available");
        await workspace.NavigatePaneAsync(PaneId.Primary, folder).WaitAsync(SmokeGitNavigationWait);
        await SmokeWaitWithinAsync(
            () => Task.FromResult(PathRules.PathsEqual(workspace.Primary.Path, folder) && !workspace.Primary.ListingInProgress
                && PrimaryFiles.Any(row => row.Name == expectedName)),
            SmokeGitNavigationWait,
            () => $"{folder} listed with {expectedName}: the pane is at {workspace.Primary.Path}, error '{workspace.ErrorMessage}', rows {SmokeGitDescribe()}");
    }

    private void SmokeGitAssertPaletteCommands(string when)
    {
        RefreshCommandPalette(SmokeGitTitle);
        var palette = _paletteCommands.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
        var missing = SmokeGitCommands.Where(id => !palette.Contains(id)).ToArray();
        SmokeAssert(missing.Length == 0, $"{when}, the palette filtered by 'Git' lacks: " + string.Join(", ", missing));
    }

    private bool SmokeGitDecorated(string name, string text) =>
        PrimaryFiles.Any(row => row.Name == name && row.DecorationText == text);

    private bool SmokeGitUndecorated() => PrimaryFiles.All(row => row.DecorationText.Length == 0);

    private bool SmokeGitHasFlag() => _workspace?.Primary.Decorations.FolderFlags.Contains(SmokeGitFlag) == true;

    /// <summary>The Git group of the file list's context menu for the current selection, or null when it has none.</summary>
    private ContextMenuEntry? SmokeGitMenuGroup() =>
        SmokeFlatten(ContextMenuBuilder.Build(BuildContextMenuRequest(SelectedRowsForPane(PaneId.Primary))))
            .FirstOrDefault(entry => entry.Id == SmokeGitMenuId);

    private bool SmokeGitMenuHidden() => SmokeGitMenuGroup() is null or { Hidden: true };

    private Button? SmokeGitToggleButton() =>
        _primaryToolbarActionElements.TryGetValue(SmokeGitPanelCommand, out var element) ? element as Button : null;

    /// <summary>The toggle is drawn and its <c>when</c> allows it: visible, or in the overflow when the toolbar is narrow.</summary>
    private bool SmokeGitToolbarShown()
    {
        if (SmokeGitToggleButton() is not { } button)
        {
            return false;
        }

        ContextMenuRequest? request = null;
        return PluginToolbarWhenAllows(SmokeGitPanelCommand, ref request)
            && (button.Visibility == Visibility.Visible || _primaryToolbarOverflow.Contains(SmokeGitPanelCommand));
    }

    /// <summary>The toggle is not drawn, or is hidden because its <c>when</c> is false.</summary>
    private bool SmokeGitToolbarHidden()
    {
        if (SmokeGitToggleButton() is not { } button)
        {
            return true;
        }

        ContextMenuRequest? request = null;
        return button.Visibility != Visibility.Visible && !PluginToolbarWhenAllows(SmokeGitPanelCommand, ref request);
    }

    private bool SmokeGitToggleIs(bool shown) =>
        SmokeGitToggleButton() is { } button
        && AutomationProperties.GetName(button) == (shown ? "Hide " : "Show ") + SmokeGitPanelTitle
        && ReferenceEquals(button.Background, Brush("SfBgSelectedBrush")) == shown;

    private void SmokeGitAssertToggle(bool shown, string when) =>
        SmokeAssert(SmokeGitToggleIs(shown), $"{when}: {SmokeGitDescribeToggle()}, expected {(shown ? "Hide" : "Show")} {SmokeGitPanelTitle}");

    private static int SmokeGitToolbarPosition(CommandSurfaceLayout layout) =>
        layout.PrimaryToolbar.FindIndex(item => item.IsCommand && item.Id == SmokeGitPanelCommand);

    private static string SmokeGitIdOf(object? element) =>
        element is DependencyObject item ? AutomationProperties.GetAutomationId(item) : "";

    /// <summary>The Git panel's body, docked or in its window, or null.</summary>
    private FrameworkElement? SmokeGitBody()
    {
        var chrome = _pluginPanelWindow is { } window ? window.Chrome.Root : PluginPanelBottomHost.Child as FrameworkElement;
        if (chrome is null)
        {
            return null;
        }

        var bodyId = SmokeGitIdOf(chrome) + ".Body";
        var body = SmokeLogicalDescendants<ContentPresenter>(chrome).FirstOrDefault(item => SmokeGitIdOf(item) == bodyId)?.Content as FrameworkElement;
        return SmokeGitIdOf(body) == SmokeGitBodyId ? body : null;
    }

    private ListView? SmokeGitChanges() => SmokeGitBody() is { } body ? SmokeFindById<ListView>(body, SmokeGitChangesId) : null;

    /// <summary>Waits for the panel's button to be enabled and loaded, and returns it.</summary>
    private async Task<Button> SmokeGitButtonAsync(string automationId)
    {
        Button? button = null;
        await SmokeGitWaitAsync(
            () => (button = SmokeGitBody() is { } body ? SmokeFindById<Button>(body, automationId) : null) is { IsEnabled: true, IsLoaded: true },
            () => $"{automationId} to be enabled (found {button is not null}, enabled {button?.IsEnabled})");
        return button!;
    }

    /// <summary>
    /// Makes the change row of <paramref name="path"/> the panel's only selection and waits for
    /// <paramref name="buttonId"/> to be enabled. A refresh replaces the rows and clears the selection, so the row is
    /// selected again until the button follows it.
    /// </summary>
    /// <summary>
    /// Waits until the workbench's change list holds the same rows for 600 ms, so a refresh a mutation started has
    /// landed before the next step selects a row or reads the preview. Bounded by the UI wait.
    /// </summary>
    private async Task SmokeGitSettleAsync(string what)
    {
        var timer = Stopwatch.StartNew();
        var last = "";
        var stableSince = TimeSpan.Zero;
        while (true)
        {
            var current = SmokeGitChanges() is { } list ? string.Join(" | ", SmokeRowNames(list)) : "<no list>";
            if (!string.Equals(current, last, StringComparison.Ordinal))
            {
                last = current;
                stableSince = timer.Elapsed;
            }
            else if (timer.Elapsed - stableSince >= TimeSpan.FromMilliseconds(600))
            {
                return;
            }

            if (timer.Elapsed > SmokeGitUiWait)
            {
                throw new TimeoutException($"UI smoke timed out after {SmokeGitUiWait.TotalSeconds:0} s: {SmokeGitChangesId} to settle {what}, rows [{last}]");
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Selects <paramref name="path"/>, presses Diff and waits for the preview to contain <paramref name="expected"/>.
    /// A refresh that lands meanwhile clears the selection and the preview, so it selects and presses again, within the
    /// UI wait overall.
    /// </summary>
    private async Task SmokeGitDiffAsync(string path, string expected)
    {
        var timer = Stopwatch.StartNew();
        TextBox? diff = null;
        while (true)
        {
            SmokeInvoke(await SmokeGitSelectChangeAsync(path, SmokeGitDiffId));
            var attempt = Stopwatch.StartNew();
            while (attempt.Elapsed < TimeSpan.FromSeconds(3))
            {
                diff = SmokeGitBody() is { } body ? SmokeFindById<TextBox>(body, SmokeGitDiffTextId) : null;
                if ((diff?.Text ?? "").Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(100);
            }

            if (timer.Elapsed > SmokeGitUiWait)
            {
                throw new TimeoutException(
                    $"UI smoke timed out after {SmokeGitUiWait.TotalSeconds:0} s: {SmokeGitDiffTextId} to show the diff of {path} with '{expected}', reads '{diff?.Text}'");
            }
        }
    }

    private async Task<Button> SmokeGitSelectChangeAsync(string path, string buttonId)
    {
        Button? button = null;
        await SmokeGitWaitAsync(
            () =>
            {
                if (SmokeGitBody() is not { } body || SmokeFindById<ListView>(body, SmokeGitChangesId) is not { } list
                    || SmokeRow(list, path) is not { } row)
                {
                    return false;
                }

                if (!row.IsSelected || list.SelectedItems.Count != 1)
                {
                    list.SelectedIndex = list.IndexFromContainer(row);
                    return false;
                }

                return (button = SmokeFindById<Button>(body, buttonId)) is { IsEnabled: true, IsLoaded: true };
            },
            () => $"'{path}' selected in {SmokeGitChangesId} with {buttonId} enabled: rows [{string.Join(" | ", SmokeRowNames(SmokeGitChanges()))}], "
                + $"button found {button is not null}, enabled {button?.IsEnabled}");
        return button!;
    }

    /// <summary>Invokes a network button and waits for its new Transfers row to complete.</summary>
    private async Task<TransferOperationViewModel> SmokeGitTransferAsync(TransferManagerViewModel transfers, string buttonId, string title)
    {
        var before = transfers.Operations.ToHashSet();
        SmokeInvoke(await SmokeGitButtonAsync(buttonId));
        TransferOperationViewModel? row = null;
        await SmokeGitWaitAsync(
            () => (row = transfers.Operations.FirstOrDefault(operation => !before.Contains(operation) && operation.Title == title)) is not null,
            () => $"a Transfers row titled '{title}' after {buttonId}, rows [{string.Join(" | ", transfers.Operations.Select(operation => operation.Title))}]");
        await SmokeAwaitRowAsync(row!, SmokeGitNetworkWait, title);
        SmokeAssert(row!.Status == TransferOperationStatus.Completed, $"{title} ended {row.Status}: {row.ErrorMessage}");
        return row;
    }

    private void SmokeGitClearMessageBar()
    {
        MessageBar.IsOpen = false;
        MessageBar.Title = "";
        MessageBar.Message = "";
    }

    /// <summary>Waits for the message bar to show a message titled "Git" that <paramref name="accept"/> accepts.</summary>
    private Task SmokeGitMessageAsync(Func<string, bool> accept, string what) =>
        SmokeGitWaitAsync(
            () => MessageBar.IsOpen && MessageBar.Title == SmokeGitTitle && accept(MessageBar.Message ?? ""),
            () => $"the message bar to show {what}: open {MessageBar.IsOpen}, '{MessageBar.Title}': '{MessageBar.Message}'");

    private string SmokeGitDescribe() =>
        string.Join(", ", PrimaryFiles.Select(row => $"{row.Name}='{row.DecorationText}'"))
        + $"; flags [{string.Join(", ", _workspace?.Primary.Decorations.FolderFlags ?? new HashSet<string>())}]"
        + $"; menu group {(SmokeGitMenuGroup() is { } group ? (group.Hidden ? "hidden" : "shown") : "absent")}"
        + $"; toolbar {(SmokeGitToggleButton() is { } button ? button.Visibility.ToString() : "absent")}";

    private string SmokeGitDescribeToggle() => SmokeGitToggleButton() is { } button
        ? $"toggle '{AutomationProperties.GetName(button)}', selected {ReferenceEquals(button.Background, Brush("SfBgSelectedBrush"))}, {button.Visibility}"
        : "no toggle button";

    private string SmokeGitDescribePanel() =>
        $"shown {(_shownPluginPanel is { } shown ? PluginPanelLayout.Key(shown) : "none")}, window {(_pluginPanelWindow is null ? "none" : "open")}, "
        + $"bottom {PluginPanelBottomRegion.Visibility}/{SmokeGitIdOf(PluginPanelBottomHost.Child)}, body {SmokeGitBody() is not null}";
}
#endif
