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

    private void OnGitCloseRequested(object? sender, EventArgs e)
    {
        if (sender is GitWorkbenchView view && IsGitWorkbenchDetached && ReferenceEquals(view, _gitWorkbenchWindow!.WorkbenchView))
        {
            CloseGitPanel();
            return;
        }

        CloseGitPanel();
    }

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
        _gitWorkbenchModel.SetHostMode(detached: false);
        CloseGitWorkbenchWindow(forDock: true);
        GitPanel.Visibility = Visibility.Visible;
        await RefreshGitPanelAsync(silent: true);
    }

    private void CloseGitPanel()
    {
        _gitPanelOpen = false;
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
        catch (Exception exception) when (silent)
        {
            _gitStatus = null;
            _gitStatusPath = workspace.Active.Path;
            _gitWorkbenchModel.SetUnavailable(exception.Message);
            RefreshGitActionButtons();
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

    private void RefreshGitUiVisibility()
    {
        var enabled = IsGitIntegrationEnabled;
        GitToggleButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            CloseGitPanel();
            _gitStatus = null;
            _gitWorkbenchModel.SetDisabled();
            _workspace?.ClearGitStatuses();
            return;
        }

        RefreshGitActionButtons();
        var activePath = _workspace?.Active.Path;
        if (_gitPanelOpen
            && !string.IsNullOrWhiteSpace(activePath)
            && (_gitStatusPath is null || !PathRules.PathsEqual(_gitStatusPath, activePath)))
        {
            _ = RefreshGitPanelAsync(silent: true);
        }
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

        if (!await ConfirmGitDiscardAsync(selectedPaths.Length))
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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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

        var utilityCts = BeginUtilityOperation();
        try
        {
            SetStatusText($"Running git {verb}...");
            var result = await action(fileOps, workspace.Active.Path, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            ShowMessage("Git", GitResultMessage(result), InfoBarSeverity.Success);
            await RefreshAfterGitCommandAsync(workspace, utilityCts.Token, refreshListing);
        }
        finally
        {
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

        var utilityCts = BeginUtilityOperation();
        try
        {
            SetStatusText($"Running git {verb}...");
            var result = await action(fileOps, workspace.Active.Path, selectedPaths, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            ShowMessage("Git", GitResultMessage(result), InfoBarSeverity.Success);
            await RefreshAfterGitCommandAsync(workspace, utilityCts.Token, refreshListing);
        }
        finally
        {
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

    private async Task<bool> ConfirmGitDiscardAsync(int count)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Discard Git changes?",
            Content = count == 1
                ? "This permanently discards the selected change."
                : $"This permanently discards {count} selected changes.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
