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
}
