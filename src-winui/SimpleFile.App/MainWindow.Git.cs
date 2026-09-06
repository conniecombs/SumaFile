using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private bool IsGitIntegrationEnabled => _workspace?.Settings.EnableGitIntegration == true;

    private async void OnToggleGitPanel(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git", ToggleGitPanelAsync);

    private void OnCloseGitPanel(object sender, RoutedEventArgs e) => CloseGitPanel();

    private async void OnGitRefresh(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git", () => RefreshGitPanelAsync());

    private async void OnGitFetch(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git fetch", GitFetchAsync);

    private async void OnGitPull(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git pull", GitPullAsync);

    private async void OnGitPush(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git push", GitPushAsync);

    private async void OnGitCommit(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git commit", PromptGitCommitAsync);

    private async void OnGitStageSelected(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git stage", GitStageSelectedAsync);

    private async void OnGitUnstageSelected(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git unstage", GitUnstageSelectedAsync);

    private async void OnGitDiscardSelected(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git discard", GitDiscardSelectedAsync);

    private async void OnGitDiffSelected(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Git diff", GitDiffSelectedAsync);

    private void OnGitChangesSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshGitActionButtons();

    private async Task ToggleGitPanelAsync()
    {
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
        GitPanel.Visibility = Visibility.Visible;
        await RefreshGitPanelAsync();
    }

    private void CloseGitPanel()
    {
        _gitPanelOpen = false;
        GitPanel.Visibility = Visibility.Collapsed;
        _gitCts?.Cancel();
        _gitCts = null;
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
            Replace(GitChanges, []);
            GitBranchText.Text = "Git";
            GitSummaryText.Text = "Git status is not available for this location.";
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
                GitSummaryText.Text = "Checking Git status...";
            }

            var status = await fileOps.GetGitRepositoryStatusAsync(workspace.Active.Path, cts.Token);
            if (!ReferenceEquals(_workspace, workspace) || cts.IsCancellationRequested)
            {
                return;
            }

            _gitStatus = status;
            Replace(GitChanges, status.Changes.Select(GitChangeRow.From));
            UpdateGitSummary(status);
            RefreshGitActionButtons();
            if (!silent)
            {
                SetStatusText(status.IsRepo ? GitStatusSummary(status) : "Current folder is not inside a Git repository.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (silent)
        {
            _gitStatus = null;
            Replace(GitChanges, []);
            GitBranchText.Text = "Git";
            GitSummaryText.Text = exception.Message;
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
            Replace(GitChanges, []);
            GitBranchText.Text = "Git";
            GitSummaryText.Text = "Git integration is disabled.";
            _workspace?.ClearGitStatuses();
        }

        RefreshGitActionButtons();
    }

    private void UpdateGitSummary(GitRepositoryStatus status)
    {
        if (!status.IsRepo)
        {
            GitBranchText.Text = "Git";
            GitSummaryText.Text = "Current folder is not inside a Git repository.";
            return;
        }

        var branch = !string.IsNullOrWhiteSpace(status.Branch)
            ? status.Branch
            : !string.IsNullOrWhiteSpace(status.Head)
                ? $"detached at {status.Head}"
                : "unknown branch";
        GitBranchText.Text = string.IsNullOrWhiteSpace(status.Upstream)
            ? branch
            : $"{branch} -> {status.Upstream}";
        GitSummaryText.Text = GitStatusSummary(status);
    }

    private static string GitStatusSummary(GitRepositoryStatus status)
    {
        if (!status.IsRepo)
        {
            return "Not a Git repository.";
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
        var selectedChanges = GitChangesList.SelectedItems.OfType<GitChangeRow>().ToArray();
        var selectedPaths = SelectedGitActionPaths();
        var hasSelection = selectedPaths.Length > 0;

        GitRefreshButton.IsEnabled = enabled;
        GitFetchButton.IsEnabled = isRepo;
        GitPullButton.IsEnabled = isRepo;
        GitPushButton.IsEnabled = isRepo;
        GitCommitButton.IsEnabled = isRepo && (_gitStatus?.Staged ?? 0) > 0;
        GitStageButton.IsEnabled = isRepo && hasSelection && (selectedChanges.Length == 0 || selectedChanges.Any(row => row.CanStage));
        GitUnstageButton.IsEnabled = isRepo && hasSelection && (selectedChanges.Length == 0 || selectedChanges.Any(row => row.CanUnstage));
        GitDiscardButton.IsEnabled = isRepo && hasSelection && (selectedChanges.Length == 0 || selectedChanges.Any(row => row.CanDiscard));
        GitDiffButton.IsEnabled = isRepo && selectedPaths.Length == 1
            && (selectedChanges.Length == 0 || selectedChanges.Any(row => row.CanDiff));
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
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || !EnsureGitIntegrationEnabled(showMessage: true))
        {
            return;
        }

        var paths = SelectedGitActionPaths();
        if (paths.Length != 1)
        {
            ShowMessage("Git diff", "Select exactly one changed path.", InfoBarSeverity.Informational);
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            SetStatusText("Loading Git diff...");
            var diff = await fileOps.GitDiffPathAsync(workspace.Active.Path, paths[0], utilityCts.Token);
            if (!utilityCts.IsCancellationRequested)
            {
                await ShowGitDiffDialogAsync(paths[0], diff);
                SetStatusText("Git diff loaded.");
            }
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
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

        var selectedChanges = GitChangesList.SelectedItems.OfType<GitChangeRow>().ToArray();
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
        var panelRows = GitChangesList.SelectedItems.OfType<GitChangeRow>().ToArray();
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

    private async Task ShowGitDiffDialogAsync(string path, string diff)
    {
        var box = new TextBox
        {
            Text = diff,
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinWidth = 680,
            MinHeight = 420,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = PathRules.Basename(path),
            Content = box,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }
}
