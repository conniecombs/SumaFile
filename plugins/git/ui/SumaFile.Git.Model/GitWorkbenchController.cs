using SimpleFile.PluginSdk;

namespace SumaFile.Git.Model;

/// <summary>
/// The Git workbench's logic, ported from 1.0.4's <c>MainWindow.Git.cs</c>: the status refresh, the nine commands the
/// plugin runs (every one but <see cref="GitCommandIds.Panel"/>, which the host handles) and the diff preview.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It never throws to its caller: results and errors go to <see cref="IPluginDialogs.Notify"/>, and a command
/// that ends with <see cref="GitErrors.Cancelled"/> or a cancellation shows "&lt;Command&gt; was cancelled.".</item>
/// <item>It reads the <c>enabled</c> setting first. While it is off, every command and a refresh shows
/// <see cref="GitText.DisabledMessage"/> (not as an error) and calls no backend method.</item>
/// <item>A refresh's result applies only while its <see cref="CancellationTokenSource"/> is still the current one and
/// the active folder is still the one it asked about.</item>
/// <item>The paths a command runs on are the workbench's selected rows, or else the file list's selection filtered by
/// the changes of a status asked for at that moment.</item>
/// <item>After stage, unstage, discard, commit and pull it asks the host to decorate again; after discard and pull it
/// also lists the folder again.</item>
/// </list>
/// The host calls it on the UI thread, and its awaits resume there, so the view model changes on the UI thread.
/// </remarks>
public sealed class GitWorkbenchController : IDisposable
{
    /// <summary>The workbench panel's id in plugin.json.</summary>
    public const string WorkbenchPanelId = "workbench";

    public const string SelectChangedPathsMessage = "Select one or more changed paths first.";
    public const string SelectOneChangedPathMessage = "Select exactly one changed path.";
    public const string UnsupportedSelectionMessage = "The selected Git changes do not support that action.";
    public const string CommitMessageRequired = "Commit message is required.";
    public const string UntrackedDiffMessage = "No Git diff is available for untracked files until they are staged.";

    /// <summary>How long a folder change waits before the shown workbench refreshes.</summary>
    public static readonly TimeSpan WorkspaceDebounce = TimeSpan.FromMilliseconds(250);

    // A refresh whose folder changed while it ran asks again for the new folder at most this often.
    private const int MaxStaleRetries = 3;

    private readonly IGitBackend _backend;
    private readonly IPluginHost _host;
    private readonly GitWorkbenchViewModel _model;
    private readonly IGitPrompts _prompts;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _diffCts;
    private CancellationTokenSource? _debounceCts;
    private GitRepositoryStatus? _status;
    private string? _statusPath;
    private string? _requestedPath;
    private int _running;
    private int _inflight;
    private int _disposed;

    public GitWorkbenchController(IGitBackend backend, IPluginHost host, GitWorkbenchViewModel model, IGitPrompts prompts)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    }

    /// <summary>Refreshes the workbench for the active folder. A silent refresh shows no message.</summary>
    public async Task RefreshAsync(bool silent, CancellationToken ct)
    {
        Interlocked.Increment(ref _inflight);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            if (!await IsEnabledAsync(linked.Token))
            {
                ForgetStatus();
                _model.SetDisabled();
                if (!silent)
                {
                    Notify(GitText.DisabledMessage, isError: false);
                }

                return;
            }

            for (var attempt = 0; attempt < MaxStaleRetries; attempt++)
            {
                var path = _host.Workspace.ActivePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    ForgetStatus();
                    _model.SetUnavailable(GitText.NotARepositoryMessage);
                    return;
                }

                if (GitPaths.IsRecycleBinPath(path))
                {
                    _requestedPath = path;
                    _status = new GitRepositoryStatus();
                    _statusPath = path;
                    _model.SetUnavailable(GitText.UnavailableLocationMessage);
                    UpdateSelection();
                    if (!silent)
                    {
                        Notify(GitText.UnavailableLocationMessage, isError: false);
                    }

                    return;
                }

                if (!await RefreshOnceAsync(path, silent, linked.Token))
                {
                    return;
                }
            }

            // The folder kept changing under the refresh; the next workspace change asks again.
            _model.SetLoadingCancelled();
        }
        catch (OperationCanceledException)
        {
            _model.SetLoadingCancelled();
        }
        catch (Exception exception)
        {
            _host.Logger.Error("The Git status refresh failed.", exception);
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
        }
    }

    /// <summary>Runs one of the nine commands the plugin handles; never throws.</summary>
    public async Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)
    {
        var command = context is null ? null : Describe(context.CommandId);
        if (command is null || context is null)
        {
            _host.Logger.Warn("Unknown command " + context?.CommandId);
            return;
        }

        Interlocked.Increment(ref _inflight);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var token = linked.Token;
            if (!await IsEnabledAsync(token))
            {
                ForgetStatus();
                _model.SetDisabled();
                Notify(GitText.DisabledMessage, isError: false);
                return;
            }

            if (command.Kind == CommandKind.Refresh)
            {
                await RefreshAsync(silent: false, token);
                return;
            }

            var path = !string.IsNullOrWhiteSpace(context.ActivePath) ? context.ActivePath : _host.Workspace.ActivePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                Notify(GitText.NotARepositoryMessage, isError: false);
                return;
            }

            if (GitPaths.IsRecycleBinPath(path))
            {
                Notify(GitText.UnavailableLocationMessage, isError: false);
                return;
            }

            switch (command.Kind)
            {
                case CommandKind.Repository:
                    await RunCommandAsync(command, path, command.Run!, token);
                    break;
                case CommandKind.Commit:
                    await CommitAsync(command, path, token);
                    break;
                case CommandKind.Paths:
                    await RunPathsCommandAsync(command, path, context.SelectedPaths, token);
                    break;
                case CommandKind.Diff:
                    await DiffSelectedAsync(path, context.SelectedPaths, token);
                    break;
            }
        }
        catch (Exception exception) when (IsCancellation(exception))
        {
            Notify($"{command.Name} was cancelled.", isError: false);
        }
        catch (Exception exception)
        {
            Notify(exception.Message, isError: true);
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
        }
    }

    /// <summary>
    /// Shows the diff of the one path in <paramref name="paths"/> in the workbench; clears it for any other count.
    /// An untracked path shows <see cref="UntrackedDiffMessage"/> without asking the backend. Never throws.
    /// </summary>
    public Task ShowDiffAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        ShowDiffCoreAsync(paths, showMessages: false, ct);

    /// <summary>
    /// The workspace's active folder or selection changed. The workbench's actions follow the selection; when the
    /// workbench is shown and the active folder changed, it refreshes after <see cref="WorkspaceDebounce"/>.
    /// </summary>
    public void OnWorkspaceChanged()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            UpdateSelection();
            var active = _host.Workspace.ActivePath;
            if (!_host.Panels.IsVisible(WorkbenchPanelId)
                || string.IsNullOrWhiteSpace(active)
                || GitPaths.PathsEqual(active, _requestedPath ?? _statusPath))
            {
                return;
            }

            _debounceCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _debounceCts = cts;
            _ = DebouncedRefreshAsync(cts);
        }
        catch (Exception exception)
        {
            _host.Logger.Error("The Git workbench could not follow the workspace.", exception);
        }
    }

    /// <summary>The workbench's list selection changed: the actions follow it and a single row previews its diff.</summary>
    public void OnPanelSelectionChanged(IReadOnlyList<GitChangeRow> rows)
    {
        try
        {
            var paths = SelectedPaths(rows ?? [], _host.Workspace.SelectedPaths);
            _model.UpdateSelection(rows ?? [], paths);
            _ = PreviewSelectionAsync();
        }
        catch (Exception exception)
        {
            _host.Logger.Error("The Git workbench selection could not be applied.", exception);
        }
    }

    /// <summary>Runs a command from the workbench's own buttons, on the workspace's active folder and selection.</summary>
    public Task RunFromPanelAsync(string commandId, CancellationToken ct) =>
        ExecuteAsync(new PluginCommandContext(commandId, _host.Workspace.ActivePath, _host.Workspace.SelectedPaths), ct);

    /// <summary>Cancels everything running and waits at most <paramref name="timeout"/> for it to end.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        Dispose();
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _inflight) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (Exception)
        {
            // A callback that threw must not stop the plugin from deactivating.
        }
    }

    /// <summary>One status call for <paramref name="path"/>; false when done, true when the folder changed meanwhile.</summary>
    private async Task<bool> RefreshOnceAsync(string path, bool silent, CancellationToken ct)
    {
        _refreshCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _refreshCts = cts;
        _requestedPath = path;
        try
        {
            if (!silent)
            {
                _model.SetLoading();
            }

            var status = await _backend.StatusAsync(path, cts.Token);
            if (!ReferenceEquals(_refreshCts, cts) || cts.IsCancellationRequested)
            {
                return false;
            }

            if (!GitPaths.PathsEqual(_host.Workspace.ActivePath, path))
            {
                // A result for a folder the user already left: ignore it and ask about the new one.
                return true;
            }

            _status = status ?? new GitRepositoryStatus();
            _statusPath = path;
            var summary = GitText.StatusSummary(_status);
            _model.SetStatus(_status, summary);
            UpdateSelection();
            if (!silent)
            {
                Notify(summary, isError: false);
            }

            _ = PreviewSelectionAsync();
            return false;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (ReferenceEquals(_refreshCts, cts) && ct.IsCancellationRequested)
            {
                _model.SetLoadingCancelled();
            }

            return false;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _status = null;
                _statusPath = path;
                _model.SetUnavailable(exception.Message);
                UpdateSelection();
                if (!silent)
                {
                    Notify(exception.Message, isError: true);
                }
            }

            return false;
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _refreshCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task DebouncedRefreshAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(WorkspaceDebounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_debounceCts, cts))
            {
                _debounceCts = null;
            }

            cts.Dispose();
        }

        await RefreshAsync(silent: true, CancellationToken.None);
    }

    private async Task CommitAsync(Command command, string path, CancellationToken ct)
    {
        var message = await _prompts.PromptCommitMessageAsync(ct);
        if (message is null)
        {
            return;
        }

        message = message.Trim();
        if (message.Length == 0)
        {
            Notify(CommitMessageRequired, isError: false);
            return;
        }

        await RunCommandAsync(command, path, (backend, repo, _, token) => backend.CommitAsync(repo, message, token), ct);
    }

    private async Task RunPathsCommandAsync(Command command, string path, IReadOnlyList<string> fileSelection, CancellationToken ct)
    {
        var (rows, paths) = await ResolveSelectionAsync(path, fileSelection, ct);
        var panelRows = PanelSelection(path);
        if (panelRows.Count > 0 && !panelRows.Any(command.Applies!))
        {
            Notify(UnsupportedSelectionMessage, isError: false);
            return;
        }

        if (paths.Count == 0)
        {
            Notify(SelectChangedPathsMessage, isError: false);
            return;
        }

        if (command.Id == GitCommandIds.DiscardSelected && !await _prompts.ConfirmDiscardAsync(rows, ct))
        {
            return;
        }

        await RunCommandAsync(command, path, (backend, repo, _, token) => command.RunPaths!(backend, repo, paths, token), ct);
    }

    private async Task DiffSelectedAsync(string path, IReadOnlyList<string> fileSelection, CancellationToken ct)
    {
        var (_, paths) = await ResolveSelectionAsync(path, fileSelection, ct);
        if (paths.Count != 1)
        {
            Notify(SelectOneChangedPathMessage, isError: false);
            return;
        }

        await _host.Panels.ShowAsync(WorkbenchPanelId, ct);
        await ShowDiffCoreAsync(paths, showMessages: true, ct);
    }

    private async Task RunCommandAsync(
        Command command,
        string path,
        Func<IGitBackend, string, IReadOnlyList<string>, CancellationToken, Task<GitCommandResult>> run,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _running);
        _model.SetCommandRunning(true);
        try
        {
            var result = await run(_backend, path, [], ct);
            Notify(GitText.ResultMessage(command.Verb, result ?? new GitCommandResult()), isError: false);
            if (command.Invalidates)
            {
                _host.Decorations.Invalidate();
            }

            if (command.RefreshesListing)
            {
                await RefreshWorkspaceQuietlyAsync(ct);
            }
        }
        catch (Exception exception) when (IsCancellation(exception))
        {
            Notify($"{command.Name} was cancelled.", isError: false);
            return;
        }
        catch (Exception exception)
        {
            Notify(exception.Message, isError: true);
            return;
        }
        finally
        {
            if (Interlocked.Decrement(ref _running) == 0)
            {
                _model.SetCommandRunning(false);
            }
        }

        // The command changed the repository: the workbench shows the new status, and a closed one asks again next time.
        if (_host.Panels.IsVisible(WorkbenchPanelId))
        {
            await RefreshAsync(silent: true, CancellationToken.None);
        }
        else
        {
            ForgetStatus();
        }
    }

    private async Task RefreshWorkspaceQuietlyAsync(CancellationToken ct)
    {
        try
        {
            await _host.Workspace.RefreshAsync(ct);
        }
        catch (Exception exception)
        {
            _host.Logger.Warn("The folder could not be listed again after the Git command: " + exception.Message);
        }
    }

    private async Task PreviewSelectionAsync()
    {
        try
        {
            var path = _host.Workspace.ActivePath;
            var paths = path is not null && GitPaths.PathsEqual(path, _statusPath)
                ? SelectedPaths(PanelSelection(path), _host.Workspace.SelectedPaths)
                : [];
            if (paths.Count == 1)
            {
                await ShowDiffCoreAsync(paths, showMessages: false, CancellationToken.None);
            }
            else
            {
                _diffCts?.Cancel();
                _model.ClearDiff();
            }
        }
        catch (Exception exception)
        {
            _host.Logger.Error("The Git diff preview failed.", exception);
        }
    }

    private async Task ShowDiffCoreAsync(IReadOnlyList<string> paths, bool showMessages, CancellationToken ct)
    {
        Interlocked.Increment(ref _inflight);
        CancellationTokenSource? cts = null;
        var title = "";
        try
        {
            if (paths is not { Count: 1 } || string.IsNullOrWhiteSpace(paths[0]))
            {
                _diffCts?.Cancel();
                _model.ClearDiff();
                return;
            }

            var filePath = paths[0];
            title = GitPaths.Basename(filePath);
            var repoPath = _host.Workspace.ActivePath ?? _statusPath;
            if (string.IsNullOrWhiteSpace(repoPath))
            {
                _model.ClearDiff();
                return;
            }

            var change = (_status?.Changes ?? []).FirstOrDefault(c => GitPaths.PathsEqual(c?.AbsolutePath, filePath));
            if (change is { Untracked: true })
            {
                _diffCts?.Cancel();
                _model.SetDiff(title, UntrackedDiffMessage);
                return;
            }

            _diffCts?.Cancel();
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            _diffCts = cts;
            _model.SetDiffLoading(title);
            var diff = await _backend.DiffAsync(repoPath, filePath, cts.Token);
            if (ReferenceEquals(_diffCts, cts) && !cts.IsCancellationRequested)
            {
                _model.SetDiff(title, diff ?? "");
            }
        }
        catch (Exception exception) when (IsCancellation(exception))
        {
        }
        catch (Exception exception)
        {
            if (cts is null || ReferenceEquals(_diffCts, cts))
            {
                _model.SetDiff(title, exception.Message);
            }

            if (showMessages)
            {
                Notify(exception.Message, isError: true);
            }
        }
        finally
        {
            if (cts is not null)
            {
                if (ReferenceEquals(_diffCts, cts))
                {
                    _diffCts = null;
                }

                cts.Dispose();
            }

            Interlocked.Decrement(ref _inflight);
        }
    }

    /// <summary>
    /// The rows and paths a path command runs on: the workbench's selected rows, or else the file list's selection
    /// filtered by a status of <paramref name="path"/> asked for now. The last status can be stale (the repository
    /// changed while the workbench was hidden), and the backend caches status for 1.5 s, so asking again is cheap.
    /// </summary>
    private async Task<(IReadOnlyList<GitChangeRow> Rows, IReadOnlyList<string> Paths)> ResolveSelectionAsync(
        string path,
        IReadOnlyList<string> fileSelection,
        CancellationToken ct)
    {
        var panelRows = PanelSelection(path);
        if (panelRows.Count > 0)
        {
            return (panelRows, SelectedPaths(panelRows, []));
        }

        _status = await _backend.StatusAsync(path, ct) ?? new GitRepositoryStatus();
        _statusPath = path;

        var rows = MatchingChanges(fileSelection ?? []);
        return (rows, rows.Select(row => row.AbsolutePath).ToArray());
    }

    /// <summary>The workbench's selected rows, when it is shown and its status is for <paramref name="path"/>.</summary>
    private IReadOnlyList<GitChangeRow> PanelSelection(string? path) =>
        _model.SelectedChanges.Count > 0
        && GitPaths.PathsEqual(path, _statusPath)
        && _host.Panels.IsVisible(WorkbenchPanelId)
            ? _model.SelectedChanges
            : [];

    /// <summary>The panel rows' paths, or else the file selection's paths that the last status lists as changed.</summary>
    private IReadOnlyList<string> SelectedPaths(IReadOnlyList<GitChangeRow> panelRows, IReadOnlyList<string> fileSelection)
    {
        if (panelRows.Count > 0)
        {
            return panelRows
                .Select(row => row.AbsolutePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return MatchingChanges(fileSelection ?? []).Select(row => row.AbsolutePath).ToArray();
    }

    private IReadOnlyList<GitChangeRow> MatchingChanges(IReadOnlyList<string> fileSelection)
    {
        var changes = _status?.Changes ?? [];
        var rows = new List<GitChangeRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selected in fileSelection)
        {
            if (string.IsNullOrWhiteSpace(selected))
            {
                continue;
            }

            var change = changes.FirstOrDefault(c => GitPaths.PathsEqual(c.AbsolutePath, selected));
            if (change is not null && seen.Add(change.AbsolutePath))
            {
                rows.Add(GitChangeRow.From(change));
            }
        }

        return rows;
    }

    private void UpdateSelection()
    {
        var path = _host.Workspace.ActivePath;
        var panelRows = PanelSelection(path);
        var paths = GitPaths.PathsEqual(path, _statusPath) ? SelectedPaths(panelRows, _host.Workspace.SelectedPaths) : [];
        _model.UpdateSelection(panelRows, paths);
    }

    private void ForgetStatus()
    {
        _status = null;
        _statusPath = null;
        _requestedPath = null;
    }

    private async Task<bool> IsEnabledAsync(CancellationToken ct) => await GitSettings.IsEnabledAsync(_host.Settings, ct);

    private void Notify(string message, bool isError)
    {
        try
        {
            _host.Dialogs.Notify(message, isError);
        }
        catch (Exception exception)
        {
            _host.Logger.Warn($"Could not show the Git message '{message}': {exception.Message}");
        }
    }

    private static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException
        || string.Equals(exception.Message?.Trim(), GitErrors.Cancelled, StringComparison.Ordinal);

    private static Command? Describe(string commandId) => commandId switch
    {
        GitCommandIds.Refresh => new Command(commandId, "Refresh", "refresh", CommandKind.Refresh),
        GitCommandIds.Fetch => new Command(commandId, "Fetch", "fetch", CommandKind.Repository)
        {
            Run = (backend, path, _, ct) => backend.FetchAsync(path, ct),
        },
        GitCommandIds.Pull => new Command(commandId, "Pull", "pull", CommandKind.Repository)
        {
            Run = (backend, path, _, ct) => backend.PullAsync(path, ct),
            Invalidates = true,
            RefreshesListing = true,
        },
        GitCommandIds.Push => new Command(commandId, "Push", "push", CommandKind.Repository)
        {
            Run = (backend, path, _, ct) => backend.PushAsync(path, ct),
        },
        GitCommandIds.Commit => new Command(commandId, "Commit", "commit", CommandKind.Commit) { Invalidates = true },
        GitCommandIds.StageSelected => new Command(commandId, "Stage", "stage", CommandKind.Paths)
        {
            RunPaths = (backend, path, paths, ct) => backend.StageAsync(path, paths, ct),
            Applies = row => row.CanStage,
            Invalidates = true,
        },
        GitCommandIds.UnstageSelected => new Command(commandId, "Unstage", "unstage", CommandKind.Paths)
        {
            RunPaths = (backend, path, paths, ct) => backend.UnstageAsync(path, paths, ct),
            Applies = row => row.CanUnstage,
            Invalidates = true,
        },
        GitCommandIds.DiscardSelected => new Command(commandId, "Discard", "discard", CommandKind.Paths)
        {
            RunPaths = (backend, path, paths, ct) => backend.DiscardAsync(path, paths, ct),
            Applies = row => row.CanDiscard,
            Invalidates = true,
            RefreshesListing = true,
        },
        GitCommandIds.DiffSelected => new Command(commandId, "Diff", "diff", CommandKind.Diff),
        _ => null,
    };

    private enum CommandKind
    {
        Refresh,
        Repository,
        Commit,
        Paths,
        Diff,
    }

    /// <param name="Name">The command as "&lt;Name&gt; was cancelled." shows it.</param>
    /// <param name="Verb">The git verb "Git &lt;verb&gt; completed." shows.</param>
    private sealed record Command(string Id, string Name, string Verb, CommandKind Kind)
    {
        public Func<IGitBackend, string, IReadOnlyList<string>, CancellationToken, Task<GitCommandResult>>? Run { get; init; }
        public Func<IGitBackend, string, IReadOnlyList<string>, CancellationToken, Task<GitCommandResult>>? RunPaths { get; init; }
        public Func<GitChangeRow, bool>? Applies { get; init; }
        public bool Invalidates { get; init; }
        public bool RefreshesListing { get; init; }
    }
}
