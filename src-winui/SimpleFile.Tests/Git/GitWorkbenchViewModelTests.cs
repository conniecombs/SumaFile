using SumaFile.Git.Model;
using Xunit;

namespace SimpleFile.Tests.Git;

/// <summary>The 1.0.4 workbench view model's behaviour, ported with the plugin's <c>IsBusy</c> and <c>ChangesEmpty</c>.</summary>
public class GitWorkbenchViewModelTests
{
    [Fact]
    public void NewModel_CanOnlyRefresh()
    {
        var model = new GitWorkbenchViewModel();

        Assert.Equal("Git", model.BranchText);
        Assert.Equal("Diff preview", model.DiffTitle);
        Assert.Equal("Select a changed file to preview its diff.", model.DiffText);
        Assert.True(model.ChangesEmpty);
        Assert.False(model.IsBusy);
        Assert.True(model.CanRefresh);
        Assert.False(model.CanFetch || model.CanPull || model.CanPush || model.CanCommit);
        Assert.False(model.CanStage || model.CanUnstage || model.CanDiscard || model.CanDiff);
    }

    [Fact]
    public void SetStatus_FillsBranchSummaryAndChangesAndEnablesTheRepositoryActions()
    {
        var model = new GitWorkbenchViewModel();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.SetStatus(Status(staged: 0), "1 unstaged");

        Assert.Equal("main -> origin/main", model.BranchText);
        Assert.Equal("1 unstaged", model.SummaryText);
        var row = Assert.Single(model.Changes);
        Assert.Equal(("a.txt", @"R:\repo\a.txt", "modified", "unstaged", @"R:\repo\a.txt"), (row.Path, row.AbsolutePath, row.StatusText, row.StageText, row.DetailText));
        Assert.False(model.ChangesEmpty);
        Assert.True(model.IsRepository);
        Assert.True(model.CanRefresh && model.CanFetch && model.CanPull && model.CanPush);
        Assert.False(model.CanCommit);
        Assert.Contains(nameof(GitWorkbenchViewModel.Changes), changed);
        Assert.Contains(nameof(GitWorkbenchViewModel.ChangesEmpty), changed);

        model.SetStatus(Status(staged: 2), "2 staged");
        Assert.True(model.CanCommit);
    }

    [Fact]
    public void SetStatus_BranchLabelFallsBackToTheDetachedHeadAndUnknown()
    {
        var model = new GitWorkbenchViewModel();

        model.SetStatus(new GitRepositoryStatus { IsRepo = true, Head = "abc1234" }, "");
        Assert.Equal("detached at abc1234", model.BranchText);
        model.SetStatus(new GitRepositoryStatus { IsRepo = true }, "");
        Assert.Equal("unknown branch", model.BranchText);
        model.SetStatus(new GitRepositoryStatus { IsRepo = true, Branch = "dev" }, "");
        Assert.Equal("dev", model.BranchText);
        model.SetStatus(new GitRepositoryStatus { IsRepo = false, Branch = "dev" }, GitText.NotARepositoryMessage);
        Assert.Equal("Git", model.BranchText);
        Assert.False(model.CanFetch);
        Assert.True(model.CanRefresh);
    }

    [Fact]
    public void UpdateSelection_PanelRowsGateEachPathAction()
    {
        var model = new GitWorkbenchViewModel();
        model.SetStatus(Status(staged: 0), "");
        Assert.False(model.CanStage || model.CanUnstage || model.CanDiscard || model.CanDiff);

        var untracked = GitChangeRow.From(new GitFileStatus { Path = "new.txt", AbsolutePath = @"R:\repo\new.txt", Status = "untracked", Untracked = true });
        model.UpdateSelection([untracked], [untracked.AbsolutePath]);
        Assert.True(model.CanStage);
        Assert.False(model.CanUnstage);
        Assert.True(model.CanDiscard);
        Assert.False(model.CanDiff);
        Assert.Same(untracked, Assert.Single(model.SelectedChanges));

        var staged = GitChangeRow.From(new GitFileStatus { Path = "s.txt", AbsolutePath = @"R:\repo\s.txt", Status = "staged", Staged = true });
        model.UpdateSelection([staged], [staged.AbsolutePath]);
        Assert.False(model.CanStage);
        Assert.True(model.CanUnstage && model.CanDiscard && model.CanDiff);

        // Two rows: diff needs exactly one path.
        model.UpdateSelection([untracked, staged], [untracked.AbsolutePath, staged.AbsolutePath]);
        Assert.True(model.CanStage && model.CanUnstage && model.CanDiscard);
        Assert.False(model.CanDiff);
    }

    [Fact]
    public void UpdateSelection_FileListPathsWithoutPanelRows_EnableEveryPathAction()
    {
        var model = new GitWorkbenchViewModel();
        model.SetStatus(Status(staged: 0), "");

        model.UpdateSelection([], [@"R:\repo\a.txt"]);

        Assert.True(model.CanStage && model.CanUnstage && model.CanDiscard && model.CanDiff);
        model.UpdateSelection([], []);
        Assert.False(model.CanStage || model.CanUnstage || model.CanDiscard || model.CanDiff);
    }

    [Fact]
    public void SetStatus_ClearsTheSelectionAndTheDiff()
    {
        var model = new GitWorkbenchViewModel();
        model.SetStatus(Status(staged: 0), "");
        model.UpdateSelection(model.Changes, [@"R:\repo\a.txt"]);
        model.SetDiff("a.txt", "diff text");

        model.SetStatus(Status(staged: 0), "");

        Assert.Empty(model.SelectedChanges);
        Assert.False(model.CanStage);
        Assert.Equal("Diff preview", model.DiffTitle);
        Assert.Equal("Select a changed file to preview its diff.", model.DiffText);
    }

    [Fact]
    public void BusyStates_DisableEveryActionUntilTheyEnd()
    {
        var model = new GitWorkbenchViewModel();
        model.SetStatus(Status(staged: 1), "");
        model.UpdateSelection([], [@"R:\repo\a.txt"]);

        model.SetCommandRunning(true);
        Assert.True(model.IsBusy);
        Assert.False(model.CanRefresh || model.CanFetch || model.CanCommit || model.CanStage || model.CanDiff);
        model.SetCommandRunning(false);
        Assert.False(model.IsBusy);
        Assert.True(model.CanRefresh && model.CanFetch && model.CanCommit && model.CanStage);

        model.SetLoading();
        Assert.True(model.IsBusy);
        Assert.Equal("Checking Git status...", model.SummaryText);
        Assert.False(model.CanRefresh);
        model.SetUnavailable("boom");
        Assert.False(model.IsBusy);
        Assert.True(model.CanRefresh);
        Assert.False(model.CanFetch);
        Assert.Equal("boom", model.SummaryText);
        Assert.True(model.ChangesEmpty);

        model.SetLoading();
        model.SetLoadingCancelled();
        Assert.False(model.IsBusy);
        Assert.Equal("Refresh was cancelled.", model.SummaryText);
    }

    [Fact]
    public void SetDisabled_ShowsTheDisabledMessageAndDisablesEverything()
    {
        var model = new GitWorkbenchViewModel();
        model.SetStatus(Status(staged: 1), "");

        model.SetDisabled();

        Assert.Equal(GitText.DisabledMessage, model.SummaryText);
        Assert.Equal("Git", model.BranchText);
        Assert.True(model.ChangesEmpty);
        Assert.False(model.CanRefresh || model.CanFetch || model.CanCommit || model.IsRepository);
    }

    [Fact]
    public void Diff_LoadingAndEmptyTexts()
    {
        var model = new GitWorkbenchViewModel();

        model.SetDiffLoading("a.txt");
        Assert.Equal(("a.txt", "Loading diff..."), (model.DiffTitle, model.DiffText));
        model.SetDiff("", "  ");
        Assert.Equal(("Diff preview", "No Git diff for the selected path."), (model.DiffTitle, model.DiffText));
        model.SetDiff("a.txt", "+line");
        Assert.Equal(("a.txt", "+line"), (model.DiffTitle, model.DiffText));
        model.ClearDiff();
        Assert.Equal("Diff preview", model.DiffTitle);
    }

    [Fact]
    public void GitChangeRow_From_PortsTheStageTextsAndActionFlags()
    {
        var conflicted = GitChangeRow.From(new GitFileStatus { Path = "c", Conflicted = true, Staged = true });
        Assert.Equal(("conflict", true, true, true, true), (conflicted.StageText, conflicted.CanStage, conflicted.CanUnstage, conflicted.CanDiscard, conflicted.CanDiff));
        var both = GitChangeRow.From(new GitFileStatus { Path = "b", Staged = true, Unstaged = true });
        Assert.Equal("staged + unstaged", both.StageText);
        var renamed = GitChangeRow.From(new GitFileStatus { Path = "new", OriginalPath = "old", AbsolutePath = @"R:\repo\new", Staged = true });
        Assert.Equal(("staged", "from old", "old"), (renamed.StageText, renamed.DetailText, renamed.OriginalPath));
        var untracked = GitChangeRow.From(new GitFileStatus { Path = "u", Untracked = true });
        Assert.Equal(("untracked", true, false, true, false), (untracked.StageText, untracked.CanStage, untracked.CanUnstage, untracked.CanDiscard, untracked.CanDiff));
    }

    private static GitRepositoryStatus Status(int staged) => new()
    {
        IsRepo = true,
        Root = @"R:\repo",
        Branch = "main",
        Upstream = "origin/main",
        Staged = staged,
        Unstaged = 1,
        Changes =
        [
            new GitFileStatus { Path = "a.txt", AbsolutePath = @"R:\repo\a.txt", Status = "modified", Unstaged = true },
        ],
    };
}
