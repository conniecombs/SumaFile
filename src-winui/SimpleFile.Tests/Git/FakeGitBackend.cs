using SumaFile.Git.Model;

namespace SimpleFile.Tests.Git;

/// <summary>
/// An <see cref="IGitBackend"/> that records every call as <c>method path [args]</c> and answers from per-method
/// delegates. A method without a delegate answers an empty result (a status that is not a repository).
/// </summary>
internal sealed class FakeGitBackend : IGitBackend
{
    private readonly List<string> _calls = [];

    public Func<string, CancellationToken, Task<GitRepositoryStatus>> Status { get; set; } =
        (_, _) => Task.FromResult(new GitRepositoryStatus());

    public Func<string, CancellationToken, Task<GitFolderDecorations>> Decorations { get; set; } =
        (_, _) => Task.FromResult(new GitFolderDecorations());

    public Func<string, IReadOnlyList<string>, CancellationToken, Task<GitCommandResult>> Stage { get; set; } = Done;
    public Func<string, IReadOnlyList<string>, CancellationToken, Task<GitCommandResult>> Unstage { get; set; } = Done;
    public Func<string, IReadOnlyList<string>, CancellationToken, Task<GitCommandResult>> Discard { get; set; } = Done;

    public Func<string, string, CancellationToken, Task<string>> Diff { get; set; } = (_, _, _) => Task.FromResult("");

    public Func<string, string, CancellationToken, Task<GitCommandResult>> Commit { get; set; } =
        (_, _, _) => Task.FromResult(new GitCommandResult { Summary = "Committed." });

    public Func<string, CancellationToken, Task<GitCommandResult>> Fetch { get; set; } = (_, _) => Task.FromResult(new GitCommandResult());
    public Func<string, CancellationToken, Task<GitCommandResult>> Pull { get; set; } = (_, _) => Task.FromResult(new GitCommandResult());
    public Func<string, CancellationToken, Task<GitCommandResult>> Push { get; set; } = (_, _) => Task.FromResult(new GitCommandResult());

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>The recorded calls of one method, such as <c>stage</c>.</summary>
    public IReadOnlyList<string> CallsOf(string method) =>
        Calls.Where(call => call.StartsWith(method + " ", StringComparison.Ordinal)).ToArray();

    public Task<GitRepositoryStatus> StatusAsync(string path, CancellationToken ct) => Record("status " + path, () => Status(path, ct));

    public Task<GitFolderDecorations> DecorationsAsync(string folder, CancellationToken ct) =>
        Record("decorations " + folder, () => Decorations(folder, ct));

    public Task<GitCommandResult> StageAsync(string path, IReadOnlyList<string> paths, CancellationToken ct) =>
        Record($"stage {path} [{string.Join("|", paths)}]", () => Stage(path, paths, ct));

    public Task<GitCommandResult> UnstageAsync(string path, IReadOnlyList<string> paths, CancellationToken ct) =>
        Record($"unstage {path} [{string.Join("|", paths)}]", () => Unstage(path, paths, ct));

    public Task<GitCommandResult> DiscardAsync(string path, IReadOnlyList<string> paths, CancellationToken ct) =>
        Record($"discard {path} [{string.Join("|", paths)}]", () => Discard(path, paths, ct));

    public Task<string> DiffAsync(string path, string filePath, CancellationToken ct) =>
        Record($"diff {path} [{filePath}]", () => Diff(path, filePath, ct));

    public Task<GitCommandResult> CommitAsync(string path, string message, CancellationToken ct) =>
        Record($"commit {path} [{message}]", () => Commit(path, message, ct));

    public Task<GitCommandResult> FetchAsync(string path, CancellationToken ct) => Record("fetch " + path, () => Fetch(path, ct));
    public Task<GitCommandResult> PullAsync(string path, CancellationToken ct) => Record("pull " + path, () => Pull(path, ct));
    public Task<GitCommandResult> PushAsync(string path, CancellationToken ct) => Record("push " + path, () => Push(path, ct));

    private static Task<GitCommandResult> Done(string path, IReadOnlyList<string> paths, CancellationToken ct) =>
        Task.FromResult(new GitCommandResult { Summary = "Done." });

    private Task<T> Record<T>(string call, Func<Task<T>> answer)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }

        return answer();
    }
}
