namespace SumaFile.Git.Model;

/// <summary>One row of the workbench's changes list, from a <see cref="GitFileStatus"/> (1.0.4 <c>ExplorerRows.GitChangeRow</c>).</summary>
public sealed class GitChangeRow
{
    public string Path { get; set; } = "";
    public string AbsolutePath { get; set; } = "";
    public string? OriginalPath { get; set; }
    public string StatusText { get; set; } = "";
    public string DetailText { get; set; } = "";
    public string StageText { get; set; } = "";

    /// <summary>
    /// Git's index status letter (<see cref="GitFileStatus.IndexStatus"/>); <c>A</c> marks a file staged as added,
    /// which discard deletes from disk like an untracked one.
    /// </summary>
    public string IndexStatus { get; set; } = "";
    public bool CanStage { get; set; }
    public bool CanUnstage { get; set; }
    public bool CanDiscard { get; set; }
    public bool CanDiff { get; set; }

    public static GitChangeRow From(GitFileStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var detail = status.OriginalPath is { Length: > 0 }
            ? $"from {status.OriginalPath}"
            : status.AbsolutePath;
        return new GitChangeRow
        {
            Path = status.Path,
            AbsolutePath = status.AbsolutePath,
            OriginalPath = status.OriginalPath,
            StatusText = status.Status,
            DetailText = detail,
            IndexStatus = status.IndexStatus ?? "",
            StageText = status.Conflicted
                ? "conflict"
                : status.Staged && status.Unstaged
                    ? "staged + unstaged"
                    : status.Staged
                        ? "staged"
                        : status.Untracked
                            ? "untracked"
                            : "unstaged",
            CanStage = status.Untracked || status.Unstaged || status.Conflicted,
            CanUnstage = status.Staged,
            CanDiscard = status.Untracked || status.Unstaged || status.Staged || status.Conflicted,
            CanDiff = !status.Untracked,
        };
    }
}
