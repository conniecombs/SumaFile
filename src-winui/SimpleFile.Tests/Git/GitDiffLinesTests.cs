using SumaFile.Git.Model;
using Xunit;

namespace SimpleFile.Tests.Git;

/// <summary>What each line of a diff is, which decides its colour in the Git panel's preview (audit U7).</summary>
public class GitDiffLinesTests
{
    private const string Diff =
        "diff --git a/notes.txt b/notes.txt\n"
        + "index 83db48f..bf269f4 100644\n"
        + "--- a/notes.txt\n"
        + "+++ b/notes.txt\n"
        + "@@ -1,3 +1,3 @@ heading\n"
        + " first\n"
        + "-second\n"
        + "+2nd\n"
        + " third\n"
        + "\\ No newline at end of file\n";

    [Fact]
    public void Parse_NamesEachLineOfAUnifiedDiff()
    {
        var lines = GitDiffLines.Parse(Diff);

        Assert.Equal(
            [
                GitDiffLineKind.Header, GitDiffLineKind.Header, GitDiffLineKind.Header, GitDiffLineKind.Header,
                GitDiffLineKind.Hunk,
                GitDiffLineKind.Context, GitDiffLineKind.Removed, GitDiffLineKind.Added, GitDiffLineKind.Context,
                GitDiffLineKind.Header,
            ],
            lines.Select(line => line.Kind));
        // The text is unchanged: the lines joined again are the diff, less its last line break.
        Assert.Equal(Diff.TrimEnd('\n'), string.Join("\n", lines.Select(line => line.Text)));
    }

    [Fact]
    public void Parse_TheFileNamesAboveAHunkAreNotChanges()
    {
        // "--- a/x" and "+++ b/x" start with the signs of a removed and an added line.
        var lines = GitDiffLines.Parse(Diff);

        Assert.Equal(GitDiffLineKind.Header, lines.Single(line => line.Text == "--- a/notes.txt").Kind);
        Assert.Equal(GitDiffLineKind.Header, lines.Single(line => line.Text == "+++ b/notes.txt").Kind);
    }

    [Fact]
    public void Parse_ASecondFileStartsItsOwnHeader()
    {
        var two = Diff + "diff --git a/b.txt b/b.txt\nnew file mode 100644\n--- /dev/null\n+++ b/b.txt\n@@ -0,0 +1 @@\n+added\n";

        var lines = GitDiffLines.Parse(two);

        var second = lines.SkipWhile(line => line.Text != "diff --git a/b.txt b/b.txt").ToArray();
        Assert.Equal(
            [GitDiffLineKind.Header, GitDiffLineKind.Header, GitDiffLineKind.Header, GitDiffLineKind.Header, GitDiffLineKind.Hunk, GitDiffLineKind.Added],
            second.Select(line => line.Kind));
    }

    [Fact]
    public void Parse_TextThatIsNotADiffIsPlain()
    {
        // The preview's own notes, and a line that happens to start with a sign.
        foreach (var text in new[] { "Loading diff...", "Select a changed file to preview its diff.", "- not a diff\n+ either" })
        {
            Assert.All(GitDiffLines.Parse(text), line => Assert.Equal(GitDiffLineKind.Context, line.Kind));
        }

        Assert.Empty(GitDiffLines.Parse(""));
        Assert.Empty(GitDiffLines.Parse(null));
    }

    [Fact]
    public void Parse_KeepsEmptyLinesAndReadsWindowsLineEnds()
    {
        var lines = GitDiffLines.Parse("@@ -1,2 +1,3 @@\r\n one\r\n\r\n+two\r\n");

        Assert.Equal(["@@ -1,2 +1,3 @@", " one", "", "+two"], lines.Select(line => line.Text));
        Assert.Equal(GitDiffLineKind.Context, lines[2].Kind);
        Assert.Equal(GitDiffLineKind.Added, lines[3].Kind);
    }

    [Fact]
    public void Parse_ACombinedDiffsRangeLineIsAHunkToo()
    {
        var lines = GitDiffLines.Parse("diff --cc a.txt\n@@@ -1,2 -1,2 +1,3 @@@\n  same\n");

        Assert.Equal([GitDiffLineKind.Header, GitDiffLineKind.Hunk, GitDiffLineKind.Context], lines.Select(line => line.Kind));
    }
}
