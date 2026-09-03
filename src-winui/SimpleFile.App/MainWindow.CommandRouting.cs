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
            ["focus-path"] = () => RunSyncCommand(() => BeginPathEdit(_workspace!.ActivePane)),
            ["refresh"] = () => _workspace!.RefreshAsync(),
            ["copy"] = CopyToClipboard,
            ["cut"] = CutToClipboard,
            ["paste"] = () => PasteFromClipboard(),
            ["copy-path"] = () => RunSyncCommand(CopySelectedPathsToClipboard),
            ["clipboard-history"] = ShowClipboardHistoryAsync,
            ["operation-history"] = ShowOperationHistoryAsync,
            ["clear-recent-history"] = () => ClearRecentHistoryAsync(),
            ["undo"] = UndoLastAsync,
            ["redo"] = RedoLastAsync,
            ["delete"] = TrashSelected,
            ["delete-permanent"] = DeleteSelected,
            ["rename"] = PromptAndRename,
            ["advanced-rename"] = PromptAdvancedRenameAsync,
            ["new-folder"] = () => CreateNewItem(_workspace!.ActivePane, NewItemTemplate.Folder),
            ["new-file"] = () => CreateNewItem(_workspace!.ActivePane, NewItemTemplate.TextFile),
            ["create-archive"] = CreateArchiveAsync,
            ["terminal"] = OpenTerminalInActivePathAsync,
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
            ["bookmark-folder"] = BookmarkCurrentFolderAsync,
            ["bookmark-selected-folder"] = BookmarkSelectedFolderAsync,
            ["folder-metrics"] = ShowFolderMetricsAsync,
            ["disk-cleanup"] = ShowDiskCleanupAsync,
            ["duplicate-checker"] = ShowDuplicateCheckerAsync,
            ["settings"] = ShowSettingsAsync,
            ["command-palette"] = () => RunSyncCommand(OpenCommandPalette),
            ["profile-manage"] = ShowWorkspaceProfileManagerAsync,
            ["profile-save"] = PromptSaveWorkspaceProfileAsync,
            ["profile-standard"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.StandardId),
            ["profile-developer"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.DeveloperId),
            ["profile-photos"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.PhotosId),
            ["profile-transfer"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.TransferId),
            ["profile-minimal"] = () => ApplyWorkspaceProfileByIdAsync(WorkspaceProfileTemplates.MinimalId),
            ["keyboard-help"] = ShowKeyboardHelpAsync,
            ["git-pull"] = () => RunGitAsync(pull: true),
            ["git-push"] = () => RunGitAsync(pull: false),
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
}
