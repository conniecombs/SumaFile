namespace SumaFile.Git.Model;

/// <summary>The Git plugin's backend, as its UI sees it: one method per backend method. <see cref="GitBackendClient"/> is the real one.</summary>
public interface IGitBackend
{
    /// <summary><c>repo.status</c> for the repository <paramref name="path"/> is in.</summary>
    Task<GitRepositoryStatus> StatusAsync(string path, CancellationToken ct);

    /// <summary><c>repo.decorations</c>: the changed direct children of <paramref name="folder"/>.</summary>
    Task<GitFolderDecorations> DecorationsAsync(string folder, CancellationToken ct);

    Task<GitCommandResult> StageAsync(string path, IReadOnlyList<string> paths, CancellationToken ct);
    Task<GitCommandResult> UnstageAsync(string path, IReadOnlyList<string> paths, CancellationToken ct);
    Task<GitCommandResult> DiscardAsync(string path, IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary><c>paths.diff</c>: the staged and working diff of <paramref name="filePath"/>.</summary>
    Task<string> DiffAsync(string path, string filePath, CancellationToken ct);

    /// <summary>
    /// <c>repo.commit</c>, sent with <see cref="CancellationToken.None"/> and an operation id. When
    /// <paramref name="ct"/> fires it sends one <c>cancel {operationId}</c> and keeps awaiting the backend's reply,
    /// which then fails with <see cref="GitErrors.Cancelled"/>. Fetch, pull and push work the same way.
    /// </summary>
    Task<GitCommandResult> CommitAsync(string path, string message, CancellationToken ct);

    Task<GitCommandResult> FetchAsync(string path, CancellationToken ct);
    Task<GitCommandResult> PullAsync(string path, CancellationToken ct);
    Task<GitCommandResult> PushAsync(string path, CancellationToken ct);
}
