namespace SumaFile.Git.Model;

/// <summary>What a line of a unified diff is, which decides its colour in the preview.</summary>
public enum GitDiffLineKind
{
    /// <summary>An unchanged line of a hunk, or text that is not a diff at all ("Loading diff...").</summary>
    Context,

    /// <summary>A line the change adds: <c>+</c> inside a hunk.</summary>
    Added,

    /// <summary>A line the change removes: <c>-</c> inside a hunk.</summary>
    Removed,

    /// <summary>A hunk's range line: <c>@@ -1,4 +1,6 @@</c>.</summary>
    Hunk,

    /// <summary>A file's header lines (<c>diff --git</c>, <c>index</c>, <c>---</c>, <c>+++</c>) and git's notes.</summary>
    Header,
}

public readonly record struct GitDiffLine(GitDiffLineKind Kind, string Text);

/// <summary>
/// Splits a diff into lines and says what each is. A <c>+</c> or <c>-</c> counts as a change only inside a hunk: the
/// <c>---</c> and <c>+++</c> lines above the first hunk name the two files and are header lines, and so is everything
/// else between <c>diff</c> and the first <c>@@</c>.
/// </summary>
public static class GitDiffLines
{
    /// <summary>
    /// The longest diff the preview colours line by line. A longer one is shown as plain text, in one piece: a text
    /// element per line of a huge diff would stall the panel, which shares the app's UI thread.
    /// </summary>
    public const int MaxColoredLines = 4000;

    public static IReadOnlyList<GitDiffLine> Parse(string? text)
    {
        var lines = new List<GitDiffLine>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var inHeader = false;
        var inHunk = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("diff ", StringComparison.Ordinal))
            {
                inHeader = true;
                inHunk = false;
                lines.Add(new GitDiffLine(GitDiffLineKind.Header, line));
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHeader = false;
                inHunk = true;
                lines.Add(new GitDiffLine(GitDiffLineKind.Hunk, line));
            }
            else if (inHeader)
            {
                lines.Add(new GitDiffLine(GitDiffLineKind.Header, line));
            }
            else if (inHunk)
            {
                lines.Add(new GitDiffLine(KindInHunk(line), line));
            }
            else
            {
                lines.Add(new GitDiffLine(GitDiffLineKind.Context, line));
            }
        }

        // The text's last line break ends its last line; it does not start an empty one.
        if (lines.Count > 1 && lines[^1].Text.Length == 0 && (text.EndsWith('\n')))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static GitDiffLineKind KindInHunk(string line) => line.Length == 0
        ? GitDiffLineKind.Context
        : line[0] switch
        {
            '+' => GitDiffLineKind.Added,
            '-' => GitDiffLineKind.Removed,
            // "\ No newline at end of file"
            '\\' => GitDiffLineKind.Header,
            _ => GitDiffLineKind.Context,
        };
}
