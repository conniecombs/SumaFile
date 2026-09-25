using Microsoft.UI.Xaml;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private Dictionary<string, Func<Task>>? _appCommandHandlers;

    private async Task RunAppCommandAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        var commandId = CommandAliasCatalog.Normalize(id);
        if (AppCommandHandlers.TryGetValue(commandId, out var handler))
        {
            await handler();
        }
    }

    private IReadOnlyDictionary<string, Func<Task>> AppCommandHandlers
        => _appCommandHandlers ??= CreateAppCommandHandlers();

    private Dictionary<string, Func<Task>> CreateAppCommandHandlers()
    {
        return new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["go-home"] = () => _workspace!.NavigateSpecialAsync("navigateHome"),
            ["go-recycle-bin"] = () => _workspace!.NavigateSpecialAsync("navigateRecycleBin"),
            ["restore-selected"] = RestoreSelectedAsync,
            ["empty-recycle-bin"] = EmptyRecycleBinAsync,
            ["go-back"] = () => RunIfNotEditingPathAsync(() => _workspace!.GoBackAsync()),
            ["go-forward"] = () => RunIfNotEditingPathAsync(() => _workspace!.GoForwardAsync()),
            ["go-up"] = () => RunIfNotEditingPathAsync(() => _workspace!.GoUpAsync()),
            ["focus-path"] = () => RunSyncCommand(() => FocusOmnibar(OmnibarMode.Navigate)),
            ["refresh"] = () => _workspace!.RefreshAsync(),
            ["copy"] = CopyToClipboard,
            ["cut"] = CutToClipboard,
            ["paste"] = () => PasteFromClipboard(),
            ["copy-path"] = () => RunSyncCommand(CopySelectedPathsToClipboard),
            ["clipboard-history"] = ShowClipboardHistoryAsync,
            ["operation-history"] = ShowOperationHistoryAsync,
            ["transfers"] = () => RunSyncCommand(ShowTransferProgressWindow),
            ["clear-recent-history"] = () => ClearRecentHistoryAsync(),
            ["undo"] = UndoLastAsync,
            ["redo"] = RedoLastAsync,
            ["delete"] = TrashSelected,
            ["delete-permanent"] = DeleteSelected,
            ["rename"] = PromptAndRename,
            ["advanced-rename"] = PromptAdvancedRenameAsync,
            ["new-folder"] = () => CreateNewItem(_workspace!.ActivePane, NewItemTemplate.Folder),
            ["new-file"] = () => CreateNewItem(_workspace!.ActivePane, NewItemTemplate.TextFile),
            ["new-shortcut"] = () => PromptAndCreateShortcut(_workspace!.ActivePane),
            ["create-archive"] = CreateArchiveAsync,
            ["terminal"] = OpenTerminalInActivePathAsync,
            ["ftp-sftp-manager"] = () => RunSyncCommand(() => ShowRemoteManagerWindow()),
            ["powershell-admin"] = OpenPowershellAdminAsync,
            ["preview"] = () => RunSyncCommand(() => OnTogglePreview(this, new RoutedEventArgs())),
            ["toggle-hidden"] = ToggleHiddenFilesAsync,
            ["toggle-side-menu"] = ToggleSidebarAsync,
            ["dual-pane"] = ToggleDualPaneFromUiAsync,
            ["switch-pane"] = SwitchPaneFromCommandAsync,
            ["close-left-pane"] = () => CloseFilePaneFromUiAsync(PaneId.Primary),
            ["close-right-pane"] = () => CloseFilePaneFromUiAsync(PaneId.Secondary),
            ["copy-to-pane"] = () => CopyOrMoveToOtherPaneAsync(move: false),
            ["move-to-pane"] = () => CopyOrMoveToOtherPaneAsync(move: true),
            ["open-selected-tab"] = OpenSelectedInNewTabAsync,
            ["open-other-pane"] = OpenSelectedInOtherPaneAsync,
            ["reopen-closed-tab"] = () => _workspace!.ReopenClosedTabAsync(),
            ["view-details"] = () => ApplyViewOptionAsync("view:details"),
            ["view-list"] = () => ApplyViewOptionAsync("view:list"),
            ["view-tiles"] = () => ApplyViewOptionAsync("view:tiles"),
            ["view-content"] = () => ApplyViewOptionAsync("view:content"),
            ["icon-size-small"] = () => ApplyViewOptionAsync("icon:16"),
            ["icon-size-medium"] = () => ApplyViewOptionAsync("icon:32"),
            ["icon-size-large"] = () => ApplyViewOptionAsync("icon:48"),
            ["icon-size-extra-large"] = () => ApplyViewOptionAsync("icon:96"),
            ["icon-size-jumbo"] = () => ApplyViewOptionAsync("icon:128"),
            ["icon-size-huge"] = () => ApplyViewOptionAsync("icon:192"),
            ["icon-size-maximum"] = () => ApplyViewOptionAsync("icon:256"),
            ["search"] = () => RunSyncCommand(FocusSearchUi),
            ["filter"] = () => RunSyncCommand(FocusFilterUi),
            ["quick-look"] = ShowQuickLookAsync,
            ["properties"] = ShowPropertiesAsync,
            ["color-label"] = SetColorLabelAsync,
            ["clear-tag-filter"] = () => RunSyncCommand(ClearTagFilter),
            ["bookmark-folder"] = BookmarkCurrentFolderAsync,
            ["bookmark-selected-folder"] = BookmarkSelectedFolderAsync,
            ["folder-metrics"] = ShowFolderMetricsAsync,
            ["disk-cleanup"] = ShowDiskCleanupAsync,
            ["duplicate-checker"] = ShowDuplicateCheckerAsync,
            ["settings"] = ShowSettingsAsync,
            ["command-palette"] = () => RunSyncCommand(() => OpenCommandPalette()),
            ["profile-manage"] = ShowWorkspaceProfileManagerAsync,
            ["profile-save"] = PromptSaveWorkspaceProfileAsync,
            ["profile-standard"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.StandardId),
            ["profile-developer"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.DeveloperId),
            ["profile-photos"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.PhotosId),
            ["profile-transfer"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.TransferId),
            ["profile-minimal"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.MinimalId),
            ["customize-toolbar"] = () => _fileOperationDialogs.ShowSettingsAsync("Toolbar"),
            ["toggle-toolbar-labels"] = ToggleToolbarLabelsAsync,
            ["keyboard-help"] = ShowKeyboardHelpAsync,
            ["git-panel"] = OpenGitPanelAsync,
            ["git-refresh"] = () => RefreshGitPanelAsync(),
            ["git-fetch"] = GitFetchAsync,
            ["git-pull"] = GitPullAsync,
            ["git-push"] = GitPushAsync,
            ["git-commit"] = PromptGitCommitAsync,
            ["git-stage-selected"] = GitStageSelectedAsync,
            ["git-unstage-selected"] = GitUnstageSelectedAsync,
            ["git-discard-selected"] = GitDiscardSelectedAsync,
            ["git-diff-selected"] = GitDiffSelectedAsync,
        };
    }

    private Task RunIfNotEditingPathAsync(Func<Task> action)
        => IsEditingPath ? Task.CompletedTask : action();

    private Task RunSyncCommand(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private Task SwitchPaneFromCommandAsync()
    {
        if (_workspace?.DualPaneEnabled == true)
        {
            _workspace.SwitchActivePane();
        }

        return Task.CompletedTask;
    }

    private async Task ToggleToolbarLabelsAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var layout = _workspace.Settings.CommandSurface;
        layout.Normalize();
        layout.ToolbarDisplayMode = layout.ToolbarDisplayMode == ToolbarActionCatalog.IconAndLabelDisplayMode
            ? ToolbarActionCatalog.IconOnlyDisplayMode
            : ToolbarActionCatalog.IconAndLabelDisplayMode;
        layout.Normalize();
        ApplyCommandSurfaceLayout();
        SetStatusText(layout.ToolbarDisplayMode == ToolbarActionCatalog.IconAndLabelDisplayMode
            ? "Toolbar labels shown"
            : "Toolbar labels hidden");
        await _workspace.SaveUiSettingsAsync();
    }
}
