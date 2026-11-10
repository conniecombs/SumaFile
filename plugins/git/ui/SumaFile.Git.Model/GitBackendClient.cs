using SimpleFile.PluginSdk;

namespace SumaFile.Git.Model;

/// <summary>
/// <see cref="IGitBackend"/> over <see cref="IPluginHost.InvokeBackendAsync"/>: the method names and param shapes of
/// plugins/git/protocol.json. Commit, fetch, pull and push carry an operation id and are cancelled with the
/// protocol's <c>cancel</c>, the <c>RemoteBackendClient.TransferAsync</c> pattern, so the backend ends git (and the
/// processes it started) and fails the call with <see cref="GitErrors.Cancelled"/>.
/// </summary>
public sealed class GitBackendClient(IPluginHost host) : IGitBackend
{
    private readonly IPluginHost _host = host ?? throw new ArgumentNullException(nameof(host));

    public Task<GitRepositoryStatus> StatusAsync(string path, CancellationToken ct) =>
        _host.InvokeBackendAsync<GitRepositoryStatus>(GitMethods.Status, new PathParams(path), ct);

    public Task<GitFolderDecorations> DecorationsAsync(string folder, CancellationToken ct) =>
        _host.InvokeBackendAsync<GitFolderDecorations>(GitMethods.Decorations, new FolderParams(folder), ct);

    public Task<GitCommandResult> StageAsync(string path, IReadOnlyList<string> paths, CancellationToken ct) =>
        PathsAsync(GitMethods.Stage, path, paths, ct);

    public Task<GitCommandResult> UnstageAsync(string path, IReadOnlyList<string> paths, CancellationToken ct) =>
        PathsAsync(GitMethods.Unstage, path, paths, ct);

    public Task<GitCommandResult> DiscardAsync(string path, IReadOnlyList<string> paths, CancellationToken ct) =>
        PathsAsync(GitMethods.Discard, path, paths, ct);

    public Task<string> DiffAsync(string path, string filePath, CancellationToken ct) =>
        _host.InvokeBackendAsync<string>(GitMethods.Diff, new DiffParams(path, filePath), ct);

    public Task<GitCommandResult> CommitAsync(string path, string message, CancellationToken ct) =>
        CancellableAsync(GitMethods.Commit, operationId => new CommitParams(path, message, operationId), ct);

    public Task<GitCommandResult> FetchAsync(string path, CancellationToken ct) =>
        CancellableAsync(GitMethods.Fetch, operationId => new OperationParams(path, operationId), ct);

    public Task<GitCommandResult> PullAsync(string path, CancellationToken ct) =>
        CancellableAsync(GitMethods.Pull, operationId => new OperationParams(path, operationId), ct);

    public Task<GitCommandResult> PushAsync(string path, CancellationToken ct) =>
        CancellableAsync(GitMethods.Push, operationId => new OperationParams(path, operationId), ct);

    private Task<GitCommandResult> PathsAsync(string method, string path, IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return _host.InvokeBackendAsync<GitCommandResult>(method, new PathsParams(path, paths.ToArray()), ct);
    }

    private async Task<GitCommandResult> CancellableAsync(string method, Func<string, object> args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var operationId = "git-" + Guid.NewGuid().ToString("N");

        // Send the request before registering the cancel, so the backend knows the operation before its cancel
        // arrives. The call itself is never cancelled: the backend ends git and its children, then fails the call
        // with GitErrors.Cancelled, which is what the caller waits for.
        var call = _host.InvokeBackendAsync<GitCommandResult>(method, args(operationId), CancellationToken.None);
        using var registration = ct.Register(() => _ = SendCancelAsync(operationId));
        return await call.ConfigureAwait(false);
    }

    private async Task SendCancelAsync(string operationId)
    {
        try
        {
            await _host.InvokeBackendAsync<object?>(GitMethods.Cancel, new CancelParams(operationId), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Best effort: the operation's own reply still ends the call (a stopped backend fails it).
            _host.Logger.Warn($"Could not cancel Git operation {operationId}: {exception.Message}");
        }
    }
}
