using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private bool IsGitIntegrationEnabled => _workspace?.Settings.EnableGitIntegration == true;

    private bool IsGitWorkbenchDetached => _gitWorkbenchWindow is { IsClosed: false };

    private GitWorkbenchView ActiveGitWorkbenchView =>
        IsGitWorkbenchDetached ? _gitWorkbenchWindow!.WorkbenchView : GitWorkbench;

    private async void OnToggleGitPanel(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git", ToggleGitPanelAsync);

    private void OnCloseGitPanel(object sender, RoutedEventArgs e) => CloseGitPanel();

    private void AttachGitWorkbenchView(GitWorkbenchView view)
    {
        view.RefreshRequested += OnGitRefreshRequested;
        view.FetchRequested += OnGitFetchRequested;
        view.PullRequested += OnGitPullRequested;
        view.PushRequested += OnGitPushRequested;
        view.CommitRequested += OnGitCommitRequested;
        view.StageRequested += OnGitStageRequested;
        view.UnstageRequested += OnGitUnstageRequested;
        view.DiscardRequested += OnGitDiscardRequested;
        view.DiffRequested += OnGitDiffRequested;
        view.HostToggleRequested += OnGitHostToggleRequested;
        view.CloseRequested += OnGitCloseRequested;
        view.SelectionChanged += OnGitWorkbenchSelectionChanged;
    }

    private void DetachGitWorkbenchView(GitWorkbenchView view)
    {
        view.RefreshRequested -= OnGitRefreshRequested;
        view.FetchRequested -= OnGitFetchRequested;
        view.PullRequested -= OnGitPullRequested;
        view.PushRequested -= OnGitPushRequested;
        view.CommitRequested -= OnGitCommitRequested;
        view.StageRequested -= OnGitStageRequested;
        view.UnstageRequested -= OnGitUnstageRequested;
        view.DiscardRequested -= OnGitDiscardRequested;
        view.DiffRequested -= OnGitDiffRequested;
        view.HostToggleRequested -= OnGitHostToggleRequested;
        view.CloseRequested -= OnGitCloseRequested;
        view.SelectionChanged -= OnGitWorkbenchSelectionChanged;
    }

    private async void OnGitRefreshRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git", () => RefreshGitPanelAsync());

    private async void OnGitFetchRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git fetch", GitFetchAsync);

    private async void OnGitPullRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git pull", GitPullAsync);

    private async void OnGitPushRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git push", GitPushAsync);

    private async void OnGitCommitRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git commit", PromptGitCommitAsync);

    private async void OnGitStageRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git stage", GitStageSelectedAsync);

    private async void OnGitUnstageRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git unstage", GitUnstageSelectedAsync);

    private async void OnGitDiscardRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git discard", GitDiscardSelectedAsync);

    private async void OnGitDiffRequested(object? sender, EventArgs e) =>
        await RunUiActionAsync("Git diff", GitDiffSelectedAsync);

    private async void OnGitHostToggleRequested(object? sender, EventArgs e)
    {
        if (_gitWorkbenchModel.IsDetached)
        {
            await RunUiActionAsync("Git dock", DockGitWorkbenchAsync);
        }
        else
        {
            await RunUiActionAsync("Git detach", DetachGitWorkbenchAsync);
        }
    }

    private void OnGitCloseRequested(object? sender, EventArgs e) => CloseGitPanel();

    private async void OnGitWorkbenchSelectionChanged(object? sender, EventArgs e)
    {
        RefreshGitActionButtons();
        if (sender is GitWorkbenchView view && ReferenceEquals(view, ActiveGitWorkbenchView))
        {
            await PreviewSelectedGitDiffAsync(showMessages: false);
        }
    }

    private async Task ToggleGitPanelAsync()
    {
        if (IsGitWorkbenchDetached)
        {
            _gitWorkbenchWindow!.Activate();
            return;
        }

        if (_gitPanelOpen)
        {
            CloseGitPanel();
            return;
        }

        await OpenGitPanelAsync();
    }

    private async Task OpenGitPanelAsync()
    {
        if (!EnsureGitIntegrationEnabled(showMessage: true))
        {
            return;
        }

        _gitPanelOpen = true;
        RefreshGitToggleState();
        if (IsGitWorkbenchDetached)
        {
            _gitWorkbenchWindow!.Activate();
        }
        else
        {
            _gitWorkbenchModel.SetHostMode(detached: false);
            GitPanel.Visibility = Visibility.Visible;
        }

        await RefreshGitPanelAsync();
    }

    private async Task DetachGitWorkbenchAsync()
    {
        if (!EnsureGitIntegrationEnabled(showMessage: true))
        {
            return;
        }

        _gitPanelOpen = true;
        RefreshGitToggleState();
        GitPanel.Visibility = Visibility.Collapsed;
        _gitWorkbenchModel.SetHostMode(detached: true);

        if (!IsGitWorkbenchDetached)
        {
            _gitWorkbenchWindow = new GitWorkbenchWindow(_gitWorkbenchModel);
            AttachGitWorkbenchView(_gitWorkbenchWindow.WorkbenchView);
            _gitWorkbenchWindow.Closed += OnGitWorkbenchWindowClosed;
        }

        _gitWorkbenchWindow!.Activate();
        await RefreshGitPanelAsync(silent: true);
    }

    private async Task DockGitWorkbenchAsync()
    {
        if (!EnsureGitIntegrationEnabled(showMessage: true))
        {
            return;
        }

        _gitPanelOpen = true;
        RefreshGitToggleState();
        _gitWorkbenchModel.SetHostMode(detached: false);
        CloseGitWorkbenchWindow(forDock: true);
        GitPanel.Visibility = Visibility.Visible;
        await RefreshGitPanelAsync(silent: true);
    }

    private void CloseGitPanel()
    {
        _gitPanelOpen = false;
        RefreshGitToggleState();
        GitPanel.Visibility = Visibility.Collapsed;
        CloseGitWorkbenchWindow(forDock: false);
        _gitCts?.Cancel();
        _gitCts = null;
        _gitDiffCts?.Cancel();
        _gitDiffCts = null;
        _gitStatusPath = null;
        GitWorkbench.ClearSelection();
        _gitWorkbenchModel.SetHostMode(detached: false);
        _gitWorkbenchModel.ClearDiff();
        RefreshGitActionButtons();
    }

    private void CloseGitWorkbenchWindow(bool forDock)
    {
        var window = _gitWorkbenchWindow;
        if (window is null)
        {
            return;
        }

        if (window.IsClosed)
        {
            _gitWorkbenchWindow = null;
            return;
        }

        _closingGitWorkbenchWindowForDock = forDock;
        try
        {
            window.Close();
        }
        finally
        {
            _closingGitWorkbenchWindowForDock = false;
        }
    }

    private void OnGitWorkbenchWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is GitWorkbenchWindow window)
        {
            DetachGitWorkbenchView(window.WorkbenchView);
        }

        _gitWorkbenchWindow = null;
        if (!_closingGitWorkbenchWindowForDock)
        {
            _gitPanelOpen = false;
        RefreshGitToggleState();
            _gitWorkbenchModel.SetHostMode(detached: false);
            _gitDiffCts?.Cancel();
            _gitDiffCts = null;
            _gitWorkbenchModel.ClearDiff();
        }

        RefreshGitActionButtons();
    }

    private async Task RefreshGitPanelAsync(bool silent = false)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        if (!EnsureGitIntegrationEnabled(showMessage: !silent))
        {
            return;
        }

        if (PathRules.IsRecycleBinPath(workspace.Active.Path) || workspace.Active.PathIsNetwork)
        {
            _gitStatus = new GitRepositoryStatus();
            _gitStatusPath = workspace.Active.Path;
            _gitWorkbenchModel.SetUnavailable("Git status is not available for this location.");
            RefreshGitActionButtons();
            return;
        }

        _gitCts?.Cancel();
        var cts = new CancellationTokenSource();
        _gitCts = cts;
        try
        {
            if (!silent)
            {
                _gitWorkbenchModel.SetLoading();
            }

            var status = await fileOps.GetGitRepositoryStatusAsync(workspace.Active.Path, cts.Token);
            if (!ReferenceEquals(_workspace, workspace) || cts.IsCancellationRequested)
            {
                return;
            }

            _gitStatus = status;
            _gitStatusPath = workspace.Active.Path;
            _gitWorkbenchModel.SetStatus(status, GitStatusSummary(status));
            RefreshGitActionButtons();
            if (!silent)
            {
                SetStatusText(status.IsRepo ? GitStatusSummary(status) : "Current folder is not inside a Git repository.");
            }

            await PreviewSelectedGitDiffAsync(showMessages: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _gitStatus = null;
            _gitStatusPath = workspace.Active.Path;
            _gitWorkbenchModel.SetUnavailable(exception.Message);
            RefreshGitActionButtons();
            if (!silent)
            {
                throw;
            }
        }
        finally
        {
            if (ReferenceEquals(_gitCts, cts))
            {
                _gitCts = null;
            }

            cts.Dispose();
        }
    }

    private void RefreshGitToggleState() =>
        SetToggleChromeState(GitToggleButton, _gitPanelOpen, "Hide Git workbench", "Show Git workbench");

    private void RefreshGitUiVisibility()
    {
        var enabled = IsGitIntegrationEnabled;
        var activePath = _workspace?.Active.Path;
        var inRepository = enabled ? ActivePaneInGitRepository() : false;
        if (enabled
            && (_gitStatusPath is null || string.IsNullOrWhiteSpace(activePath) || !PathRules.PathsEqual(_gitStatusPath, activePath)))
        {
            // The panel's status is stale for this folder (or the panel is closed): keep the
            // repository flag that context menus read in step with the active pane's probe.
            _gitStatus = inRepository == true
                ? new GitRepositoryStatus { IsRepo = true, Root = _workspace?.Active.GitRepositoryRoot }
                : null;
        }

        // Lazy Git: the status-bar button appears only inside a repository (unknown stays hidden),
        // or while the workbench is open so it can still be closed.
        GitToggleButton.Visibility = enabled && (inRepository == true || _gitPanelOpen)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshGitToggleState();
        if (!enabled)
        {
            CloseGitPanel();
            _gitStatus = null;
            _gitWorkbenchModel.SetDisabled();
            _workspace?.ClearGitStatuses();
            return;
        }

        RefreshGitActionButtons();
        if (_gitPanelOpen
            && !string.IsNullOrWhiteSpace(activePath)
            && (_gitStatusPath is null || !PathRules.PathsEqual(_gitStatusPath, activePath)))
        {
            _ = RefreshGitPanelAsync(silent: true);
        }
    }

    /// <summary>
    /// Whether the active pane's folder is inside a Git repository: true/false once a probe
    /// (the workbench status or the pane's enrichment probe) has answered, null while unknown.
    /// </summary>
    private bool? ActivePaneInGitRepository()
    {
        var pane = _workspace?.Active;
        var activePath = pane?.Path;
        if (pane is null || string.IsNullOrWhiteSpace(activePath))
        {
            return null;
        }

        if (_gitStatus is not null
            && _gitStatusPath is not null
            && PathRules.PathsEqual(_gitStatusPath, activePath))
        {
            return _gitStatus.IsRepo;
        }

        if (pane.GitRepositoryRoot is { Length: > 0 } root && PathRules.PathContains(root, activePath))
        {
            return true;
        }

        if (pane.GitProbedPath is { } probed && PathRules.PathsEqual(probed, activePath))
        {
            return pane.GitRepositoryRoot is not null;
        }

        return null;
    }

    private static string GitStatusSummary(GitRepositoryStatus status)
    {
        if (!status.IsRepo)
        {
            return "Current folder is not inside a Git repository.";
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

    private void RefreshGitActionButtons()
    {
        var enabled = IsGitIntegrationEnabled;
        var isRepo = enabled && _gitStatus?.IsRepo == true;
        var selectedChanges = ActiveGitWorkbenchView.SelectedChanges;
        var selectedPaths = SelectedGitActionPaths();
        _gitWorkbenchModel.UpdateSelection(selectedChanges, selectedPaths);
        if (!enabled || !isRepo)
        {
            _gitWorkbenchModel.UpdateSelection([], []);
        }
    }

    private bool EnsureGitIntegrationEnabled(bool showMessage)
    {
        if (IsGitIntegrationEnabled)
        {
            return true;
        }

        RefreshGitUiVisibility();
        if (showMessage)
        {
            ShowMessage("Git", "Git integration is disabled in Settings.", InfoBarSeverity.Informational);
        }

        return false;
    }

    private Task GitFetchAsync() =>
        RunGitRepositoryCommandAsync("fetch", (fileOps, path, ct) => fileOps.GitFetchAsync(path, ct));

    private Task GitPullAsync() =>
        RunGitRepositoryCommandAsync("pull", (fileOps, path, ct) => fileOps.GitPullAsync(path, ct));

    private Task GitPushAsync() =>
        RunGitRepositoryCommandAsync("push", (fileOps, path, ct) => fileOps.GitPushAsync(path, ct));

    private Task GitStageSelectedAsync() =>
        RunGitPathsCommandAsync(
            "stage",
            rows => rows.Length == 0 || rows.Any(row => row.CanStage),
            (fileOps, path, paths, ct) => fileOps.GitStagePathsAsync(path, paths, ct));

    private Task GitUnstageSelectedAsync() =>
        RunGitPathsCommandAsync(
            "unstage",
            rows => rows.Length == 0 || rows.Any(row => row.CanUnstage),
            (fileOps, path, paths, ct) => fileOps.GitUnstagePathsAsync(path, paths, ct));

    private async Task GitDiscardSelectedAsync()
    {
        var selectedPaths = SelectedGitActionPaths();
        if (selectedPaths.Length == 0)
        {
            ShowMessage("Git", "Select one or more changed paths first.", InfoBarSeverity.Informational);
            return;
        }

        if (!await ConfirmGitDiscardAsync(SelectedGitDiscardRows(selectedPaths)))
        {
            return;
        }

        await RunGitPathsCommandAsync(
            "discard",
            rows => rows.Length == 0 || rows.Any(row => row.CanDiscard),
            (fileOps, path, paths, ct) => fileOps.GitDiscardPathsAsync(path, paths, ct),
            refreshListing: true);
    }

    private async Task GitDiffSelectedAsync()
    {
        if (!_gitPanelOpen)
        {
            await OpenGitPanelAsync();
        }

        await PreviewSelectedGitDiffAsync(showMessages: true);
    }

    private async Task PreviewSelectedGitDiffAsync(bool showMessages)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || !EnsureGitIntegrationEnabled(showMessage: showMessages))
        {
            return;
        }

        var selectedChanges = ActiveGitWorkbenchView.SelectedChanges;
        var selectedPaths = SelectedGitActionPaths();
        if (selectedPaths.Length != 1)
        {
            if (showMessages)
            {
                ShowMessage("Git diff", "Select exactly one changed path.", InfoBarSeverity.Informational);
            }

            _gitWorkbenchModel.ClearDiff();
            return;
        }

        if (selectedChanges.Length > 0 && selectedChanges.All(row => !row.CanDiff))
        {
            _gitWorkbenchModel.SetDiff(
                PathRules.Basename(selectedPaths[0]),
                "No Git diff is available for untracked files until they are staged.");
            return;
        }

        _gitDiffCts?.Cancel();
        var cts = new CancellationTokenSource();
        _gitDiffCts = cts;
        var selectedPath = selectedPaths[0];
        var selectedTitle = PathRules.Basename(selectedPath);
        _gitWorkbenchModel.SetDiffLoading(selectedTitle);
        try
        {
            if (showMessages)
            {
                SetStatusText("Loading Git diff...");
            }

            var diff = await fileOps.GitDiffPathAsync(workspace.Active.Path, selectedPath, cts.Token);
            var currentSelection = SelectedGitActionPaths();
            if (!ReferenceEquals(_workspace, workspace)
                || cts.IsCancellationRequested
                || currentSelection.Length != 1
                || !PathRules.PathsEqual(selectedPath, currentSelection[0]))
            {
                return;
            }

            _gitWorkbenchModel.SetDiff(selectedTitle, diff);
            if (showMessages)
            {
                SetStatusText("Git diff loaded.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_gitDiffCts, cts))
            {
                _gitWorkbenchModel.SetDiff(selectedTitle, exception.Message);
            }

            if (showMessages)
            {
                ShowMessage("Git diff", exception.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_gitDiffCts, cts))
            {
                _gitDiffCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task PromptGitCommitAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || !EnsureGitIntegrationEnabled(showMessage: true))
        {
            return;
        }

        var input = new TextBox
        {
            PlaceholderText = "Commit message",
            MinWidth = 360,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Git commit",
            Content = input,
            PrimaryButtonText = "Commit",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);

        if (await DialogHost.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var message = input.Text.Trim();
        if (message.Length == 0)
        {
            ShowMessage("Git commit", "Commit message is required.", InfoBarSeverity.Warning);
            return;
        }

        await RunGitRepositoryCommandAsync(
            "commit",
            (ops, path, ct) => ops.GitCommitAsync(path, message, ct),
            refreshListing: true);
    }

    private async Task RunGitRepositoryCommandAsync(
        string verb,
        Func<FileOperationService, string, CancellationToken, Task<GitCommandResult>> action,
        bool refreshListing = false)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || !EnsureGitIntegrationEnabled(showMessage: true))
        {
            return;
        }

        var utilityCts = BeginUtilityOperation("RunGitRepositoryCommandAsync");
        _gitWorkbenchModel.SetCommandRunning(true);
        try
        {
            SetStatusText($"Running git {verb}...");
            var result = await action(fileOps, workspace.Active.Path, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                SetStatusText($"git {verb} was cancelled.");
                return;
            }

            ShowMessage("Git", GitResultMessage(result), InfoBarSeverity.Success);
            await RefreshAfterGitCommandAsync(workspace, utilityCts.Token, refreshListing);
        }
        catch (OperationCanceledException)
        {
            SetStatusText($"git {verb} was cancelled.");
        }
        finally
        {
            _gitWorkbenchModel.SetCommandRunning(false);
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task RunGitPathsCommandAsync(
        string verb,
        Func<GitChangeRow[], bool> canRun,
        Func<FileOperationService, string, string[], CancellationToken, Task<GitCommandResult>> action,
        bool refreshListing = false)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || !EnsureGitIntegrationEnabled(showMessage: true))
        {
            return;
        }

        var selectedChanges = ActiveGitWorkbenchView.SelectedChanges;
        if (!canRun(selectedChanges))
        {
            ShowMessage("Git", "The selected Git changes do not support that action.", InfoBarSeverity.Informational);
            return;
        }

        var selectedPaths = SelectedGitActionPaths();
        if (selectedPaths.Length == 0)
        {
            ShowMessage("Git", "Select one or more changed paths first.", InfoBarSeverity.Informational);
            return;
        }

        var utilityCts = BeginUtilityOperation("RunGitPathsCommandAsync");
        _gitWorkbenchModel.SetCommandRunning(true);
        try
        {
            SetStatusText($"Running git {verb}...");
            var result = await action(fileOps, workspace.Active.Path, selectedPaths, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                SetStatusText($"git {verb} was cancelled.");
                return;
            }

            ShowMessage("Git", GitResultMessage(result), InfoBarSeverity.Success);
            await RefreshAfterGitCommandAsync(workspace, utilityCts.Token, refreshListing);
        }
        catch (OperationCanceledException)
        {
            SetStatusText($"git {verb} was cancelled.");
        }
        finally
        {
            _gitWorkbenchModel.SetCommandRunning(false);
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task RefreshAfterGitCommandAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken,
        bool refreshListing)
    {
        if (refreshListing)
        {
            await workspace.RefreshAsync().ConfigureAwait(true);
        }
        else
        {
            await workspace.ApplyGitStatusesAsync(ActiveUiPane, cancellationToken).ConfigureAwait(true);
            SyncFromWorkspace();
        }

        if (_gitPanelOpen)
        {
            await RefreshGitPanelAsync(silent: true);
        }
    }

    private string[] SelectedGitActionPaths()
    {
        var panelRows = ActiveGitWorkbenchView.SelectedChanges;
        if (panelRows.Length > 0)
        {
            return panelRows
                .Select(row => row.AbsolutePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return ActiveSelectedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.GitText))
            .Select(row => row.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GitResultMessage(GitCommandResult result)
    {
        var output = result.Stdout?.Trim();
        if (!string.IsNullOrWhiteSpace(output))
        {
            return output.Length > 220 ? $"{output[..220]}..." : output;
        }

        return string.IsNullOrWhiteSpace(result.Summary)
            ? "Git command completed."
            : result.Summary;
    }

    /// <summary>
    /// Rows to confirm for a discard: the workbench selection when there is one, otherwise the
    /// file-list selection matched against the last repository status (falling back to the
    /// row's own Git badge so untracked files are still called out).
    /// </summary>
    private IReadOnlyList<GitChangeRow> SelectedGitDiscardRows(IReadOnlyList<string> selectedPaths)
    {
        var panelRows = ActiveGitWorkbenchView.SelectedChanges;
        if (panelRows.Length > 0)
        {
            return panelRows;
        }

        var known = (_gitStatus?.Changes ?? [])
            .Where(change => !string.IsNullOrWhiteSpace(change.AbsolutePath))
            .GroupBy(change => change.AbsolutePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var badges = ActiveSelectedRows
            .GroupBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().GitText, StringComparer.OrdinalIgnoreCase);
        return selectedPaths
            .Select(path => known.TryGetValue(path, out var change)
                ? GitChangeRow.From(change)
                : new GitChangeRow
                {
                    Path = path,
                    AbsolutePath = path,
                    StageText = badges.TryGetValue(path, out var badge)
                        && string.Equals(badge, "untracked", StringComparison.OrdinalIgnoreCase)
                            ? "untracked"
                            : "unstaged",
                    CanDiscard = true,
                })
            .ToList();
    }

    internal static string GitDiscardConfirmationText(IReadOnlyList<GitChangeRow> rows)
    {
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
        var untracked = rows.Count(row => string.Equals(row.StageText, "untracked", StringComparison.OrdinalIgnoreCase));
        if (untracked > 0)
        {
            lines.Add(untracked == 1
                ? "1 untracked file will be deleted from disk, not moved to the Recycle Bin."
                : $"{untracked} untracked files will be deleted from disk, not moved to the Recycle Bin.");
        }

        return string.Join("\n", lines);
    }

    private async Task<bool> ConfirmGitDiscardAsync(IReadOnlyList<GitChangeRow> rows)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = rows.Count == 1 ? "Discard this change?" : $"Discard {rows.Count} changes?",
            Content = new TextBlock
            {
                Text = GitDiscardConfirmationText(rows),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await DialogHost.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
