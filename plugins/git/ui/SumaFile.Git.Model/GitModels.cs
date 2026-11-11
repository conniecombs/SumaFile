using System.Text.Json.Serialization;

namespace SumaFile.Git.Model;

// The wire shapes of plugins/git/protocol.json. Every property names its JSON field, because the host serializes with
// no naming policy: results keep the 1.0.4 snake_case shapes, params are camelCase (GitProtocol.cs).

/// <summary>The result of <c>repo.status</c>: the repository the path is in, its branch and its changes.</summary>
public sealed class GitRepositoryStatus
{
    [JsonPropertyName("is_repo")] public bool IsRepo { get; set; }
    [JsonPropertyName("root")] public string? Root { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("upstream")] public string? Upstream { get; set; }
    [JsonPropertyName("head")] public string? Head { get; set; }
    [JsonPropertyName("ahead")] public int Ahead { get; set; }
    [JsonPropertyName("behind")] public int Behind { get; set; }
    [JsonPropertyName("staged")] public int Staged { get; set; }
    [JsonPropertyName("unstaged")] public int Unstaged { get; set; }
    [JsonPropertyName("untracked")] public int Untracked { get; set; }
    [JsonPropertyName("conflicted")] public int Conflicted { get; set; }
    [JsonPropertyName("changes")] public List<GitFileStatus> Changes { get; set; } = [];
}

/// <summary>One changed path of a <see cref="GitRepositoryStatus"/>; <see cref="AbsolutePath"/> uses native separators.</summary>
public sealed class GitFileStatus
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("absolute_path")] public string AbsolutePath { get; set; } = "";
    [JsonPropertyName("original_path")] public string? OriginalPath { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("index_status")] public string IndexStatus { get; set; } = "";
    [JsonPropertyName("worktree_status")] public string WorktreeStatus { get; set; } = "";
    [JsonPropertyName("staged")] public bool Staged { get; set; }
    [JsonPropertyName("unstaged")] public bool Unstaged { get; set; }
    [JsonPropertyName("untracked")] public bool Untracked { get; set; }
    [JsonPropertyName("conflicted")] public bool Conflicted { get; set; }
}

/// <summary>The result of the stage, unstage, discard, commit, fetch, pull and push methods.</summary>
public sealed class GitCommandResult
{
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("exit_code")] public int ExitCode { get; set; }
    [JsonPropertyName("stdout")] public string Stdout { get; set; } = "";
    [JsonPropertyName("stderr")] public string Stderr { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
}

/// <summary>
/// The result of <c>repo.decorations</c>: whether the folder is inside a repository, its root, and the folder's
/// changed direct children.
/// </summary>
public sealed class GitFolderDecorations
{
    [JsonPropertyName("in_repository")] public bool InRepository { get; set; }
    [JsonPropertyName("root")] public string? Root { get; set; }
    [JsonPropertyName("rows")] public List<GitDecorationRow> Rows { get; set; } = [];
}

/// <summary>One decorated child: its native full path and its status label (<c>modified</c>, <c>untracked</c>, ...).</summary>
public sealed class GitDecorationRow
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}
