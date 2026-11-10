namespace SumaFile.Git.Model;

/// <summary>The plugin's user-facing texts and the status label to brush key mapping.</summary>
public static class GitText
{
    /// <summary>What every command, a refresh and the open workbench show while the <c>enabled</c> setting is off.</summary>
    public const string DisabledMessage = "Git integration is turned off. Turn it on in Settings > Plugins > Git.";

    public const string NotARepositoryMessage = "Current folder is not inside a Git repository.";

    /// <summary>The workbench and the commands on a location git cannot read, such as the Recycle Bin.</summary>
    public const string UnavailableLocationMessage = "Git status is not available for this location.";

    /// <summary>A one-line summary of a status: every non-zero count, "Working tree clean." or not a repository.</summary>
    public static string StatusSummary(GitRepositoryStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.IsRepo)
        {
            return NotARepositoryMessage;
        }

        var parts = new List<string>();
        if (status.Staged > 0)
        {
            parts.Add($"{status.Staged} staged");
        }

        if (status.Unstaged > 0)
        {
            parts.Add($"{status.Unstaged} unstaged");
        }

        if (status.Untracked > 0)
        {
            parts.Add($"{status.Untracked} untracked");
        }

        if (status.Conflicted > 0)
        {
            parts.Add($"{status.Conflicted} conflicted");
        }

        if (status.Ahead > 0)
        {
            parts.Add($"{status.Ahead} ahead");
        }

        if (status.Behind > 0)
        {
            parts.Add($"{status.Behind} behind");
        }

        return parts.Count == 0 ? "Working tree clean." : string.Join(", ", parts);
    }

    /// <summary>
    /// What a finished command shows: git's output (at most 220 characters), else the backend's summary, else
    /// "Git &lt;command&gt; completed.".
    /// </summary>
    public static string ResultMessage(string command, GitCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = result.Stdout?.Trim();
        if (!string.IsNullOrWhiteSpace(output))
        {
            return output.Length > 220 ? $"{output[..220]}..." : output;
        }

        return string.IsNullOrWhiteSpace(result.Summary)
            ? $"Git {command} completed."
            : result.Summary;
    }

    /// <summary>
    /// The discard confirmation: at most five paths, "and n more", that it cannot be undone, and how many files are
    /// deleted from disk rather than moved to the Recycle Bin: the untracked ones and those staged as added (index
    /// status <c>A</c>), which discard unstages and then removes with <c>git clean -fd</c>.
    /// </summary>
    public static string DiscardConfirmationText(IReadOnlyList<GitChangeRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        const int listed = 5;
        var lines = rows
            .Take(listed)
            .Select(row => string.IsNullOrWhiteSpace(row.Path) ? row.AbsolutePath : row.Path)
            .ToList();
        if (rows.Count > listed)
        {
            lines.Add($"and {rows.Count - listed} more");
        }

        lines.Add("");
        lines.Add("This cannot be undone.");
        var untracked = rows.Count(IsUntracked);
        var added = rows.Count(row => !IsUntracked(row) && IsAdded(row));
        var deleted = untracked + added;
        if (deleted > 0)
        {
            var kind = added > 0 ? "untracked or added" : "untracked";
            lines.Add(deleted == 1
                ? $"1 {kind} file will be deleted from disk, not moved to the Recycle Bin."
                : $"{deleted} {kind} files will be deleted from disk, not moved to the Recycle Bin.");
        }

        return string.Join("\n", lines);

        static bool IsUntracked(GitChangeRow row) =>
            string.Equals(row.StageText, "untracked", StringComparison.OrdinalIgnoreCase);

        static bool IsAdded(GitChangeRow row) =>
            string.Equals(row.IndexStatus?.Trim(), "A", StringComparison.Ordinal);
    }

    /// <summary>The host theme brush for a backend status label; the host falls back to the muted brush itself too.</summary>
    public static string DecorationBrushKey(string status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "added" or "staged" => "SfDecorationAddedBrush",
        "modified" or "renamed" or "staged + modified" => "SfDecorationModifiedBrush",
        "untracked" => "SfDecorationUntrackedBrush",
        "deleted" or "conflicted" => "SfDecorationDeletedBrush",
        _ => "SfTextMutedBrush",
    };
}
