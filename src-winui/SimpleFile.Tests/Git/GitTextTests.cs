using SumaFile.Git.Model;
using Xunit;

namespace SimpleFile.Tests.Git;

public class GitTextTests
{
    [Fact]
    public void StatusSummary_ListsEveryNonZeroCount()
    {
        var status = new GitRepositoryStatus
        {
            IsRepo = true,
            Staged = 1,
            Unstaged = 2,
            Untracked = 3,
            Conflicted = 4,
            Ahead = 5,
            Behind = 6,
        };

        Assert.Equal("1 staged, 2 unstaged, 3 untracked, 4 conflicted, 5 ahead, 6 behind", GitText.StatusSummary(status));
        Assert.Equal("2 unstaged, 6 behind", GitText.StatusSummary(new GitRepositoryStatus { IsRepo = true, Unstaged = 2, Behind = 6 }));
    }

    [Fact]
    public void StatusSummary_CleanTreeAndNotARepository()
    {
        Assert.Equal("Working tree clean.", GitText.StatusSummary(new GitRepositoryStatus { IsRepo = true }));
        Assert.Equal("Current folder is not inside a Git repository.", GitText.StatusSummary(new GitRepositoryStatus { Staged = 3 }));
        Assert.Equal(GitText.NotARepositoryMessage, GitText.StatusSummary(new GitRepositoryStatus()));
    }

    [Fact]
    public void ResultMessage_FallsBackToTheSummary()
    {
        Assert.Equal("Already up to date.", GitText.ResultMessage("pull", new GitCommandResult { Stdout = "  Already up to date.\n", Summary = "pulled" }));
        Assert.Equal("Fetched origin", GitText.ResultMessage("fetch", new GitCommandResult { Stdout = "  \n", Summary = "Fetched origin" }));
        Assert.Equal("Git push completed.", GitText.ResultMessage("push", new GitCommandResult()));

        var longOutput = new string('x', 300);
        Assert.Equal(new string('x', 220) + "...", GitText.ResultMessage("fetch", new GitCommandResult { Stdout = longOutput }));
    }

    /// <summary>Moved from UxRecommendationsSettingsGitRemoteTests.GitDiscard_ListsPathsAndWarnsAboutUntrackedFiles.</summary>
    [Fact]
    public void DiscardConfirmationText_ListsPathsAndWarnsAboutUntrackedFiles()
    {
        var one = new[] { new GitChangeRow { Path = "a.txt", AbsolutePath = @"R:\repo\a.txt", StageText = "unstaged" } };
        Assert.Equal("a.txt\n\nThis cannot be undone.", GitText.DiscardConfirmationText(one));

        var untracked = new[]
        {
            new GitChangeRow { Path = "", AbsolutePath = @"R:\repo\new.txt", StageText = "untracked" },
            new GitChangeRow { Path = "b.txt", StageText = "staged" },
        };
        Assert.Equal(
            "R:\\repo\\new.txt\nb.txt\n\nThis cannot be undone.\n1 untracked file will be deleted from disk, not moved to the Recycle Bin.",
            GitText.DiscardConfirmationText(untracked));

        var seven = Enumerable.Range(1, 7)
            .Select(i => new GitChangeRow { Path = $"f{i}.txt", StageText = i <= 3 ? "untracked" : "unstaged" })
            .ToArray();
        Assert.Equal(
            string.Join("\n",
                "f1.txt", "f2.txt", "f3.txt", "f4.txt", "f5.txt", "and 2 more", "", "This cannot be undone.",
                "3 untracked files will be deleted from disk, not moved to the Recycle Bin."),
            GitText.DiscardConfirmationText(seven));
    }

    [Fact]
    public void DiscardConfirmationText_CountsStagedNewFilesAsDeletedFromDisk()
    {
        // Review finding: discard runs restore --staged and then git clean -fd, so a file staged as added (index
        // status A) is deleted from disk too.
        var rows = new[]
        {
            GitChangeRow.From(new GitFileStatus { Path = "added.txt", AbsolutePath = @"R:\repo\added.txt", Status = "added", IndexStatus = "A", WorktreeStatus = " ", Staged = true }),
            GitChangeRow.From(new GitFileStatus { Path = "mod.txt", AbsolutePath = @"R:\repo\mod.txt", Status = "staged", IndexStatus = "M", WorktreeStatus = " ", Staged = true }),
        };
        Assert.Equal("A", rows[0].IndexStatus);
        Assert.Equal(
            "added.txt\nmod.txt\n\nThis cannot be undone.\n1 untracked or added file will be deleted from disk, not moved to the Recycle Bin.",
            GitText.DiscardConfirmationText(rows));

        var mixed = new[]
        {
            new GitChangeRow { Path = "new.txt", StageText = "untracked", IndexStatus = "?" },
            new GitChangeRow { Path = "both.txt", StageText = "staged + unstaged", IndexStatus = "A" },
            new GitChangeRow { Path = "m.txt", StageText = "unstaged", IndexStatus = " " },
        };
        Assert.Equal(
            "new.txt\nboth.txt\nm.txt\n\nThis cannot be undone.\n2 untracked or added files will be deleted from disk, not moved to the Recycle Bin.",
            GitText.DiscardConfirmationText(mixed));
    }

    [Theory]
    [InlineData("added", "SfDecorationAddedBrush")]
    [InlineData("staged", "SfDecorationAddedBrush")]
    [InlineData("modified", "SfDecorationModifiedBrush")]
    [InlineData("renamed", "SfDecorationModifiedBrush")]
    [InlineData("staged + modified", "SfDecorationModifiedBrush")]
    [InlineData("untracked", "SfDecorationUntrackedBrush")]
    [InlineData("deleted", "SfDecorationDeletedBrush")]
    [InlineData("conflicted", "SfDecorationDeletedBrush")]
    [InlineData("typechange", "SfTextMutedBrush")]
    [InlineData("", "SfTextMutedBrush")]
    public void DecorationBrushKey_MapsEveryBackendLabel(string status, string brushKey)
    {
        Assert.Equal(brushKey, GitText.DecorationBrushKey(status));
    }

    [Fact]
    public void Messages_AreTheOnesThePlanFixes()
    {
        Assert.Equal("Git integration is turned off. Turn it on in Settings > Plugins > Git.", GitText.DisabledMessage);
        Assert.Equal("Current folder is not inside a Git repository.", GitText.NotARepositoryMessage);
        Assert.Equal("Git was not found. Install Git for Windows or add git.exe to PATH.", GitErrors.NotFound);
        Assert.Equal("Git operation cancelled", GitErrors.Cancelled);
    }

    [Theory]
    [InlineData(@"R:\repo\a.txt", "r:/REPO/A.txt", true)]
    [InlineData(@"R:\repo\dir\", @"R:\repo\dir", true)]
    [InlineData(@"R:\repo\a.txt", @"R:\repo\b.txt", false)]
    public void GitPaths_PathsEqual_IgnoresCaseSeparatorsAndTrailingSeparators(string left, string right, bool equal)
    {
        Assert.Equal(equal, GitPaths.PathsEqual(left, right));
    }

    [Fact]
    public void GitPaths_ContainsAndRecycleBin()
    {
        Assert.True(GitPaths.PathContains(@"R:\repo", "r:/repo/sub/a.txt"));
        Assert.True(GitPaths.PathContains(@"R:\repo\", @"R:\repo"));
        Assert.False(GitPaths.PathContains(@"R:\repo", @"R:\repository\a.txt"));
        Assert.False(GitPaths.PathContains("", @"R:\repo"));
        Assert.True(GitPaths.IsRecycleBinPath(" Recycle-Bin: "));
        Assert.False(GitPaths.IsRecycleBinPath(@"R:\$Recycle.Bin"));
        Assert.False(GitPaths.IsRecycleBinPath(null));
        Assert.Equal("a.txt", GitPaths.Basename(@"R:\repo\a.txt"));
        Assert.Equal("dir", GitPaths.Basename("R:/repo/dir/"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData(" false ", false)]
    [InlineData("nonsense", true)]
    public async Task GitSettings_MissingOrUnparsableReadsAsEnabled(string? value, bool enabled)
    {
        var host = new FakeGitHost();
        if (value is not null)
        {
            host.SettingValues[GitSettings.EnabledKey] = value;
        }

        Assert.Equal(enabled, await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));
    }

    [Fact]
    public async Task GitSettings_AFailingStoreReadsAsTheLastValueReadSuccessfully()
    {
        // Review finding: a store that failed to answer (the service restarting) read as on, so git ran for a user
        // who had turned Git off.
        var host = new FakeGitHost();
        host.SettingsFailure = new InvalidOperationException("The settings service is restarting.");
        Assert.True(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));

        host.SettingsFailure = null;
        host.SettingValues[GitSettings.EnabledKey] = "false";
        Assert.False(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));
        host.SettingsFailure = new InvalidOperationException("The settings service is restarting.");
        host.SettingValues[GitSettings.EnabledKey] = "true";
        Assert.False(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));

        host.SettingsFailure = null;
        Assert.True(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));
        host.SettingsFailure = new InvalidOperationException("again");
        Assert.True(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));

        // A missing value read successfully is the default, on.
        host.SettingsFailure = null;
        host.SettingValues[GitSettings.EnabledKey] = "false";
        Assert.False(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));
        host.SettingValues.Remove(GitSettings.EnabledKey);
        Assert.True(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));
        host.SettingsFailure = new InvalidOperationException("again");
        Assert.True(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));

        // What one plugin's store read says nothing about another's.
        host.SettingsFailure = null;
        host.SettingValues[GitSettings.EnabledKey] = "false";
        Assert.False(await GitSettings.IsEnabledAsync(host.Settings, CancellationToken.None));
        var other = new FakeGitHost { SettingsFailure = new InvalidOperationException("down") };
        Assert.True(await GitSettings.IsEnabledAsync(other.Settings, CancellationToken.None));

        // Cancellation still propagates.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        host.SettingsFailure = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitSettings.IsEnabledAsync(host.Settings, cancelled.Token));
    }
}
