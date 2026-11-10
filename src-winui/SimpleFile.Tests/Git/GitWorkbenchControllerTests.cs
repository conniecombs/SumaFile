using SimpleFile.PluginSdk;
using SumaFile.Git.Model;
using Xunit;

namespace SimpleFile.Tests.Git;

public class GitWorkbenchControllerTests
{
    private const string Repo = @"R:\repo";

    [Fact]
    public async Task Refresh_FailedStatus_ClearsBusyAndShowsTheError()
    {
        // Audit P4: a failed status left the workbench busy.
        var (host, backend, model, _, controller) = Create();
        var reply = new TaskCompletionSource<GitRepositoryStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Status = (_, _) => reply.Task;

        var running = controller.RefreshAsync(silent: false, CancellationToken.None);
        Assert.True(model.IsBusy);
        Assert.False(model.CanRefresh);
        reply.SetException(new InvalidOperationException("fatal: not a git repository (or any parent)"));
        await running;

        Assert.False(model.IsBusy);
        Assert.True(model.CanRefresh);
        Assert.Equal("fatal: not a git repository (or any parent)", model.SummaryText);
        Assert.Equal([("fatal: not a git repository (or any parent)", true)], host.Notifications);

        // A silent refresh shows the error in the workbench only.
        backend.Status = (_, _) => Task.FromException<GitRepositoryStatus>(new InvalidOperationException("again"));
        await controller.RefreshAsync(silent: true, CancellationToken.None);
        Assert.Equal("again", model.SummaryText);
        Assert.Single(host.Notifications);
    }

    [Fact]
    public async Task Refresh_StaleResultForAnotherFolder_IsIgnored()
    {
        var (host, backend, model, _, controller) = Create();
        var first = new TaskCompletionSource<GitRepositoryStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Status = (path, _) => GitPaths.PathsEqual(path, Repo)
            ? first.Task
            : Task.FromResult(Status(@"R:\other", "branch-b"));
        var branches = new List<string>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitWorkbenchViewModel.BranchText))
            {
                lock (branches)
                {
                    branches.Add(model.BranchText);
                }
            }
        };

        var running = controller.RefreshAsync(silent: false, CancellationToken.None);
        host.ActivePath = @"R:\other";
        first.SetResult(Status(Repo, "branch-a"));
        await running;

        Assert.DoesNotContain("branch-a", branches);
        Assert.Equal("branch-b", model.BranchText);
        Assert.Equal(["status " + Repo, @"status R:\other"], backend.Calls);
        Assert.False(model.IsBusy);
        Assert.Equal([("1 unstaged", false)], host.Notifications);
    }

    [Fact]
    public async Task Refresh_SupersededByANewerRefresh_KeepsOnlyTheNewerResult()
    {
        var (_, backend, model, _, controller) = Create();
        var first = new TaskCompletionSource<GitRepositoryStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        backend.Status = (_, _) => Interlocked.Increment(ref calls) == 1 ? first.Task : Task.FromResult(Status(Repo, "newer"));

        var older = controller.RefreshAsync(silent: true, CancellationToken.None);
        await controller.RefreshAsync(silent: true, CancellationToken.None);
        first.SetResult(Status(Repo, "older"));
        await older;

        Assert.Equal("newer", model.BranchText);
    }

    [Fact]
    public async Task Refresh_GitNotFound_ShowsTheMessageAndKeepsThePanelUsable()
    {
        var (host, backend, model, _, controller) = Create();
        backend.Status = (_, _) => Task.FromException<GitRepositoryStatus>(new InvalidOperationException(GitErrors.NotFound));

        await controller.RefreshAsync(silent: false, CancellationToken.None);

        Assert.Equal(GitErrors.NotFound, model.SummaryText);
        Assert.False(model.IsBusy);
        Assert.True(model.CanRefresh);
        Assert.True(model.ChangesEmpty);
        Assert.Equal([(GitErrors.NotFound, true)], host.Notifications);

        // Still usable: once Git is there, the next refresh shows the status.
        backend.Status = (_, _) => Task.FromResult(Status(Repo, "main"));
        await controller.RefreshAsync(silent: false, CancellationToken.None);
        Assert.Equal("main", model.BranchText);
        Assert.Equal("1 unstaged", model.SummaryText);

        // A command reports it the same way.
        backend.Fetch = (_, _) => Task.FromException<GitCommandResult>(new InvalidOperationException(GitErrors.NotFound));
        await controller.ExecuteAsync(Context(GitCommandIds.Fetch), CancellationToken.None);
        Assert.Equal((GitErrors.NotFound, true), host.Notifications[^1]);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Fetch_CancelledFromItsRow_ReportsWasCancelledAndClearsBusy()
    {
        // Audit P3: a cancelled command reported nothing and left the workbench busy.
        var (host, backend, model, _, controller) = Create();
        backend.Status = (_, _) => Task.FromResult(Status(Repo, "main"));
        await controller.RefreshAsync(silent: true, CancellationToken.None);
        Assert.True(model.CanFetch);
        var reply = new TaskCompletionSource<GitCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Fetch = (_, _) => reply.Task;

        var running = controller.ExecuteAsync(Context(GitCommandIds.Fetch), CancellationToken.None);
        Assert.True(model.IsBusy);
        Assert.False(model.CanFetch);
        reply.SetException(new SimpleFile.Ipc.IpcException(-32000, GitErrors.Cancelled));
        await running;

        Assert.Equal([("Fetch was cancelled.", false)], host.Notifications);
        Assert.False(model.IsBusy);
        Assert.True(model.CanFetch);
        Assert.Equal(0, host.Invalidations);

        // The caller's own cancellation reads the same.
        using var cancellation = new CancellationTokenSource();
        backend.Push = async (_, ct) =>
        {
            await cancellation.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new GitCommandResult();
        };
        await controller.ExecuteAsync(Context(GitCommandIds.Push), cancellation.Token);
        Assert.Equal(("Push was cancelled.", false), host.Notifications[^1]);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task StageSelected_FallsBackToTheFileListSelectionFilteredByStatus()
    {
        var (host, backend, model, _, controller) = Create();
        backend.Status = (_, _) => Task.FromResult(new GitRepositoryStatus
        {
            IsRepo = true,
            Root = Repo,
            Changes =
            [
                new GitFileStatus { Path = "a.txt", AbsolutePath = @"R:\repo\a.txt", Status = "modified", Unstaged = true },
                new GitFileStatus { Path = "newdir/", AbsolutePath = @"R:\repo\newdir", Status = "untracked", Untracked = true },
                new GitFileStatus { Path = "other.txt", AbsolutePath = @"R:\repo\other.txt", Status = "modified", Unstaged = true },
            ],
        });
        backend.Stage = (_, _, _) => Task.FromResult(new GitCommandResult { Summary = "Staged 2 paths." });

        // Git's own forms: another case, forward slashes, a trailing separator; a clean file is left out.
        await controller.ExecuteAsync(
            Context(GitCommandIds.StageSelected, "r:/REPO/A.TXT", "R:/repo/newdir/", @"R:\repo\clean.txt"),
            CancellationToken.None);

        Assert.Equal(["status " + Repo, @"stage R:\repo [R:\repo\a.txt|R:\repo\newdir]"], backend.Calls);
        Assert.Equal([("Staged 2 paths.", false)], host.Notifications);

        // Nothing selected that the status lists: nothing runs.
        await controller.ExecuteAsync(Context(GitCommandIds.UnstageSelected, @"R:\repo\clean.txt"), CancellationToken.None);
        Assert.Empty(backend.CallsOf("unstage"));
        Assert.Equal(("Select one or more changed paths first.", false), host.Notifications[^1]);

        // A shown workbench's own selection wins over the file list's.
        host.PanelVisible = true;
        await controller.RefreshAsync(silent: true, CancellationToken.None);
        controller.OnPanelSelectionChanged([model.Changes.Single(row => row.Path == "other.txt")]);
        await controller.ExecuteAsync(Context(GitCommandIds.StageSelected, @"R:\repo\a.txt"), CancellationToken.None);
        Assert.Equal(@"stage R:\repo [R:\repo\other.txt]", backend.CallsOf("stage")[^1]);
    }

    [Fact]
    public async Task StageSelected_PanelRowsThatCannotStage_RunNothing()
    {
        var (host, backend, model, _, controller) = Create();
        backend.Status = (_, _) => Task.FromResult(new GitRepositoryStatus
        {
            IsRepo = true,
            Root = Repo,
            Changes = [new GitFileStatus { Path = "s.txt", AbsolutePath = @"R:\repo\s.txt", Status = "staged", Staged = true }],
        });
        host.PanelVisible = true;
        await controller.RefreshAsync(silent: true, CancellationToken.None);
        controller.OnPanelSelectionChanged(model.Changes);

        await controller.ExecuteAsync(Context(GitCommandIds.StageSelected), CancellationToken.None);

        Assert.Empty(backend.CallsOf("stage"));
        Assert.Equal(("The selected Git changes do not support that action.", false), host.Notifications[^1]);
    }

    [Fact]
    public async Task DiscardSelected_Declined_RunsNothing()
    {
        var (host, backend, _, prompts, controller) = Create();
        backend.Status = (_, _) => Task.FromResult(new GitRepositoryStatus
        {
            IsRepo = true,
            Root = Repo,
            Changes =
            [
                new GitFileStatus { Path = "new.txt", AbsolutePath = @"R:\repo\new.txt", Status = "untracked", Untracked = true },
                new GitFileStatus { Path = "a.txt", AbsolutePath = @"R:\repo\a.txt", Status = "modified", Unstaged = true },
            ],
        });
        prompts.ConfirmResult = false;

        await controller.ExecuteAsync(Context(GitCommandIds.DiscardSelected, @"R:\repo\new.txt", @"R:\repo\a.txt"), CancellationToken.None);

        Assert.Empty(backend.CallsOf("discard"));
        var asked = Assert.Single(prompts.DiscardRequests);
        Assert.Equal([("new.txt", "untracked"), ("a.txt", "unstaged")], asked.Select(row => (row.Path, row.StageText)).ToArray());
        Assert.Equal(0, host.Invalidations);
        Assert.Equal(0, host.WorkspaceRefreshes);
        Assert.Empty(host.Notifications);
    }

    [Fact]
    public async Task Commit_EmptyMessage_RunsNothing()
    {
        var (host, backend, _, prompts, controller) = Create();

        prompts.CommitMessage = "   ";
        await controller.ExecuteAsync(Context(GitCommandIds.Commit), CancellationToken.None);
        Assert.Empty(backend.CallsOf("commit"));
        Assert.Equal([("Commit message is required.", false)], host.Notifications);

        prompts.CommitMessage = null;
        await controller.ExecuteAsync(Context(GitCommandIds.Commit), CancellationToken.None);
        Assert.Empty(backend.CallsOf("commit"));
        Assert.Single(host.Notifications);
        Assert.Equal(2, prompts.CommitRequests);

        prompts.CommitMessage = "  Fix it  ";
        await controller.ExecuteAsync(Context(GitCommandIds.Commit), CancellationToken.None);
        Assert.Equal([@"commit R:\repo [Fix it]"], backend.CallsOf("commit"));
        Assert.Equal(("Committed.", false), host.Notifications[^1]);
    }

    [Fact]
    public async Task Commands_WhenDisabled_NotifyTheDisabledMessageAndRunNothing()
    {
        Assert.Equal(9, GitCommandIds.Handled.Count);
        foreach (var commandId in GitCommandIds.Handled)
        {
            var (host, backend, model, prompts, controller) = Create();
            host.SettingValues[GitSettings.EnabledKey] = "false";
            prompts.CommitMessage = "message";

            await controller.ExecuteAsync(Context(commandId, @"R:\repo\a.txt"), CancellationToken.None);

            Assert.True(host.Notifications.SequenceEqual([(GitText.DisabledMessage, false)]), commandId);
            Assert.True(backend.Calls.Count == 0, $"{commandId} called {string.Join(", ", backend.Calls)}.");
            Assert.Equal(0, prompts.CommitRequests);
            Assert.Empty(prompts.DiscardRequests);
            Assert.Empty(host.PanelCalls);
            Assert.Equal(0, host.Invalidations);
            Assert.Equal(GitText.DisabledMessage, model.SummaryText);
        }

        // A refresh while disabled says the same; a silent one (the open workbench) only shows it.
        var (refreshHost, refreshBackend, refreshModel, _, refreshController) = Create();
        refreshHost.SettingValues[GitSettings.EnabledKey] = "false";
        await refreshController.RefreshAsync(silent: true, CancellationToken.None);
        Assert.Empty(refreshHost.Notifications);
        Assert.Equal(GitText.DisabledMessage, refreshModel.SummaryText);
        await refreshController.RefreshAsync(silent: false, CancellationToken.None);
        Assert.Equal([(GitText.DisabledMessage, false)], refreshHost.Notifications);
        Assert.Empty(refreshBackend.Calls);
    }

    [Fact]
    public async Task AfterStageCommitAndDiscard_InvalidatesDecorations()
    {
        var (host, backend, _, prompts, controller) = Create();
        backend.Status = (_, _) => Task.FromResult(Status(Repo, "main"));
        prompts.CommitMessage = "message";

        await controller.ExecuteAsync(Context(GitCommandIds.StageSelected, @"R:\repo\a.txt"), CancellationToken.None);
        Assert.Equal((1, 0), (host.Invalidations, host.WorkspaceRefreshes));
        await controller.ExecuteAsync(Context(GitCommandIds.UnstageSelected, @"R:\repo\a.txt"), CancellationToken.None);
        Assert.Equal((2, 0), (host.Invalidations, host.WorkspaceRefreshes));
        await controller.ExecuteAsync(Context(GitCommandIds.Commit), CancellationToken.None);
        Assert.Equal((3, 0), (host.Invalidations, host.WorkspaceRefreshes));
        await controller.ExecuteAsync(Context(GitCommandIds.DiscardSelected, @"R:\repo\a.txt"), CancellationToken.None);
        Assert.Equal((4, 1), (host.Invalidations, host.WorkspaceRefreshes));
        await controller.ExecuteAsync(Context(GitCommandIds.Pull), CancellationToken.None);
        Assert.Equal((5, 2), (host.Invalidations, host.WorkspaceRefreshes));

        // Fetch and push change neither the files nor their badges.
        await controller.ExecuteAsync(Context(GitCommandIds.Fetch), CancellationToken.None);
        await controller.ExecuteAsync(Context(GitCommandIds.Push), CancellationToken.None);
        Assert.Equal((5, 2), (host.Invalidations, host.WorkspaceRefreshes));

        Assert.Single(backend.CallsOf("stage"));
        Assert.Single(backend.CallsOf("unstage"));
        Assert.Single(backend.CallsOf("discard"));
        Assert.Single(backend.CallsOf("pull"));
        Assert.All(host.Notifications, notification => Assert.False(notification.IsError));

        // A failed command invalidates nothing.
        backend.Stage = (_, _, _) => Task.FromException<GitCommandResult>(new InvalidOperationException("fatal: index.lock exists"));
        await controller.ExecuteAsync(Context(GitCommandIds.StageSelected, @"R:\repo\a.txt"), CancellationToken.None);
        Assert.Equal(5, host.Invalidations);
        Assert.Equal(("fatal: index.lock exists", true), host.Notifications[^1]);
    }

    [Fact]
    public async Task DiffSelected_ShowsTheWorkbenchAndThenTheDiff()
    {
        var (host, backend, model, _, controller) = Create();
        backend.Status = (_, _) => Task.FromResult(new GitRepositoryStatus
        {
            IsRepo = true,
            Root = Repo,
            Changes =
            [
                new GitFileStatus { Path = "a.txt", AbsolutePath = @"R:\repo\a.txt", Status = "modified", Unstaged = true },
                new GitFileStatus { Path = "new.txt", AbsolutePath = @"R:\repo\new.txt", Status = "untracked", Untracked = true },
            ],
        });
        backend.Diff = (_, file, _) => Task.FromResult("+changed " + GitPaths.Basename(file));

        await controller.ExecuteAsync(Context(GitCommandIds.DiffSelected, @"R:\repo\a.txt"), CancellationToken.None);

        Assert.Equal(["show:workbench"], host.PanelCalls);
        Assert.Equal(("a.txt", "+changed a.txt"), (model.DiffTitle, model.DiffText));
        Assert.Contains(@"diff R:\repo [R:\repo\a.txt]", backend.Calls);

        // An untracked file has no diff yet, and the backend is not asked.
        await controller.ExecuteAsync(Context(GitCommandIds.DiffSelected, @"R:\repo\new.txt"), CancellationToken.None);
        Assert.Equal(GitWorkbenchController.UntrackedDiffMessage, model.DiffText);
        Assert.DoesNotContain(backend.Calls, call => call.Contains("[R:\\repo\\new.txt]", StringComparison.Ordinal) && call.StartsWith("diff", StringComparison.Ordinal));

        // Two paths: nothing to show.
        await controller.ExecuteAsync(Context(GitCommandIds.DiffSelected, @"R:\repo\a.txt", @"R:\repo\new.txt"), CancellationToken.None);
        Assert.Equal(("Select exactly one changed path.", false), host.Notifications[^1]);
    }

    [Fact]
    public async Task Commands_InTheRecycleBin_RunNothing()
    {
        var (host, backend, model, _, controller) = Create();
        host.ActivePath = "recycle-bin:";

        await controller.ExecuteAsync(new PluginCommandContext(GitCommandIds.Fetch, "recycle-bin:", []), CancellationToken.None);
        await controller.RefreshAsync(silent: false, CancellationToken.None);

        Assert.Empty(backend.Calls);
        Assert.Equal(
            [("Git status is not available for this location.", false), ("Git status is not available for this location.", false)],
            host.Notifications);
        Assert.Equal("Git status is not available for this location.", model.SummaryText);
    }

    [Fact]
    public async Task Commands_NeverThrow()
    {
        var (host, _, _, prompts, controller) = Create();
        prompts.Failure = new InvalidOperationException("The dialog could not open.");

        await controller.ExecuteAsync(Context(GitCommandIds.Commit), CancellationToken.None);
        await controller.ExecuteAsync(Context("plugin:com.sumafile.git:unknown"), CancellationToken.None);

        Assert.Equal([("The dialog could not open.", true)], host.Notifications);
        Assert.Contains(host.Log.Lines, line => line.Contains("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnWorkspaceChanged_RefreshesAShownWorkbenchAfterTheFolderChanges()
    {
        var (host, backend, model, _, controller) = Create();
        backend.Status = (path, _) => Task.FromResult(Status(path, GitPaths.Basename(path)));
        await controller.RefreshAsync(silent: true, CancellationToken.None);
        Assert.Equal("repo", model.BranchText);

        // Hidden workbench: a folder change runs no git.
        host.ActivePath = @"R:\hidden";
        controller.OnWorkspaceChanged();
        await Task.Delay(GitWorkbenchController.WorkspaceDebounce * 2);
        Assert.Single(backend.Calls);

        // Shown workbench: a burst of changes refreshes once, for the last folder.
        host.PanelVisible = true;
        host.ActivePath = @"R:\first";
        controller.OnWorkspaceChanged();
        host.ActivePath = @"R:\second";
        controller.OnWorkspaceChanged();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (model.BranchText != "second" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal("second", model.BranchText);
        Assert.Equal(["status " + Repo, @"status R:\second"], backend.Calls);

        // A selection change in the same folder refreshes nothing.
        host.SelectedPaths = [@"R:\second\a.txt"];
        controller.OnWorkspaceChanged();
        await Task.Delay(GitWorkbenchController.WorkspaceDebounce * 2);
        Assert.Equal(2, backend.Calls.Count);
        controller.Dispose();
    }

    private static (FakeGitHost Host, FakeGitBackend Backend, GitWorkbenchViewModel Model, FakePrompts Prompts, GitWorkbenchController Controller) Create()
    {
        var host = new FakeGitHost { ActivePath = Repo };
        var backend = new FakeGitBackend();
        var model = new GitWorkbenchViewModel();
        var prompts = new FakePrompts();
        return (host, backend, model, prompts, new GitWorkbenchController(backend, host, model, prompts));
    }

    private static PluginCommandContext Context(string commandId, params string[] selected) => new(commandId, Repo, selected);

    private static GitRepositoryStatus Status(string root, string branch) => new()
    {
        IsRepo = true,
        Root = root,
        Branch = branch,
        Unstaged = 1,
        Changes = [new GitFileStatus { Path = "a.txt", AbsolutePath = root + @"\a.txt", Status = "modified", Unstaged = true }],
    };

    private sealed class FakePrompts : IGitPrompts
    {
        private int _commitRequests;

        public string? CommitMessage { get; set; }

        public bool ConfirmResult { get; set; } = true;

        public Exception? Failure { get; set; }

        public int CommitRequests => Volatile.Read(ref _commitRequests);

        public List<IReadOnlyList<GitChangeRow>> DiscardRequests { get; } = [];

        public Task<string?> PromptCommitMessageAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _commitRequests);
            return Failure is { } failure ? Task.FromException<string?>(failure) : Task.FromResult(CommitMessage);
        }

        public Task<bool> ConfirmDiscardAsync(IReadOnlyList<GitChangeRow> rows, CancellationToken ct)
        {
            lock (DiscardRequests)
            {
                DiscardRequests.Add(rows.ToArray());
            }

            return Failure is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(ConfirmResult);
        }
    }
}
