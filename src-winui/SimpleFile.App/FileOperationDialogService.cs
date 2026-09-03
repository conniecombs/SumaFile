using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

internal sealed partial class FileOperationDialogService
{
    private readonly Func<ExplorerWorkspace?> _workspace;
    private readonly Func<XamlRoot> _xamlRoot;
    private readonly Func<nint> _ownerHwnd;
    private readonly Func<FileRow?> _selectedItem;
    private readonly Func<FileRow[]> _selectedEntries;
    private readonly Func<string[]?> _selectedPaths;
    private readonly Func<CancellationTokenSource> _beginUtilityOperation;
    private readonly Action<CancellationTokenSource> _finishUtilityOperation;
    private readonly Func<CancellationTokenSource> _beginArchiveOperation;
    private readonly Action<CancellationTokenSource> _finishArchiveOperation;
    private readonly Func<string?, Task<string?>> _pickFolderAsync;
    private readonly Func<string?, Task<string?>> _pickFileAsync;
    private readonly Func<string, Func<Task>, Task> _runUiActionAsync;
    private readonly Action<string, string, InfoBarSeverity> _showMessage;
    private readonly Action<FileRow> _queuePreview;
    private readonly Func<FileEntry, FileRow> _toFileRow;
    private readonly Action _refreshView;
    private readonly Action<string?> _applyTheme;
    private readonly Action _applyKeyboardShortcuts;
    private readonly Func<CancellationToken, Task> _clearRecentHistoryAsync;
    private readonly Action<Action> _dispatchToUi;

    public FileOperationDialogService(
        Func<ExplorerWorkspace?> workspace,
        Func<XamlRoot> xamlRoot,
        Func<nint> ownerHwnd,
        Func<FileRow?> selectedItem,
        Func<FileRow[]> selectedEntries,
        Func<string[]?> selectedPaths,
        Func<CancellationTokenSource> beginUtilityOperation,
        Action<CancellationTokenSource> finishUtilityOperation,
        Func<CancellationTokenSource> beginArchiveOperation,
        Action<CancellationTokenSource> finishArchiveOperation,
        Func<string?, Task<string?>> pickFolderAsync,
        Func<string?, Task<string?>> pickFileAsync,
        Func<string, Func<Task>, Task> runUiActionAsync,
        Action<string, string, InfoBarSeverity> showMessage,
        Action<FileRow> queuePreview,
        Func<FileEntry, FileRow> toFileRow,
        Action refreshView,
        Action<string?> applyTheme,
        Action applyKeyboardShortcuts,
        Func<CancellationToken, Task> clearRecentHistoryAsync,
        Action<Action> dispatchToUi)
    {
        _workspace = workspace;
        _xamlRoot = xamlRoot;
        _ownerHwnd = ownerHwnd;
        _selectedItem = selectedItem;
        _selectedEntries = selectedEntries;
        _selectedPaths = selectedPaths;
        _beginUtilityOperation = beginUtilityOperation;
        _finishUtilityOperation = finishUtilityOperation;
        _beginArchiveOperation = beginArchiveOperation;
        _finishArchiveOperation = finishArchiveOperation;
        _pickFolderAsync = pickFolderAsync;
        _pickFileAsync = pickFileAsync;
        _runUiActionAsync = runUiActionAsync;
        _showMessage = showMessage;
        _queuePreview = queuePreview;
        _toFileRow = toFileRow;
        _refreshView = refreshView;
        _applyTheme = applyTheme;
        _applyKeyboardShortcuts = applyKeyboardShortcuts;
        _clearRecentHistoryAsync = clearRecentHistoryAsync;
        _dispatchToUi = dispatchToUi;
    }

    public Task PromptAndCreateFileAsync(PaneId pane)
        => PromptForNameAndInvokeAsync(
            pane,
            "Blank File",
            "File name",
            NewItemTemplate.EmptyFile,
            static (workspace, name, cancellationToken) => workspace.CreateFileInCurrentPaneAsync(name, cancellationToken));

    public async Task CreateNewItemFromTemplateAsync(PaneId pane, NewItemTemplate template)
    {
        var workspace = _workspace();
        if (workspace is null)
        {
            return;
        }

        string? createdPath = null;
        var utilityCts = _beginUtilityOperation();
        try
        {
            workspace.ActivatePane(pane);
            createdPath = await workspace.CreateNewItemInCurrentPaneAsync(template, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage("New", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }

        if (createdPath is not null && ReferenceEquals(_workspace(), workspace))
        {
            await PromptRenameCreatedItemAsync(workspace, createdPath, template);
        }
    }

    public async Task PromptAndCreateShortcutAsync(PaneId pane)
    {
        var workspace = _workspace();
        if (workspace is null)
        {
            return;
        }

        var suggestedName = workspace.SuggestedNameForNewItem(NewItemTemplate.Shortcut, pane);
        var nameBox = new TextBox
        {
            Header = "Name",
            Text = suggestedName,
            PlaceholderText = "Shortcut name",
        };
        nameBox.Select(0, NewItemTemplate.RenameSelectionLength(suggestedName, isDirectory: false));

        var targetBox = new TextBox
        {
            Header = "Target",
            PlaceholderText = "Path to a file or folder",
        };
        var argumentsBox = new TextBox
        {
            Header = "Arguments",
            PlaceholderText = "Optional",
        };
        var workingDirectoryBox = new TextBox
        {
            Header = "Start in",
            PlaceholderText = "Optional folder",
        };
        var iconBox = new TextBox
        {
            Header = "Icon",
            PlaceholderText = "Optional file",
        };

        var autoName = true;
        var updatingName = false;
        nameBox.TextChanged += (_, _) =>
        {
            if (!updatingName)
            {
                autoName = false;
            }
        };

        async Task PickTargetFileAsync()
        {
            var picked = await _pickFileAsync(targetBox.Text.Trim());
            ApplyPickedTarget(picked);
        }

        async Task PickTargetFolderAsync()
        {
            var picked = await _pickFolderAsync(targetBox.Text.Trim());
            ApplyPickedTarget(picked);
        }

        async Task PickWorkingDirectoryAsync()
        {
            var picked = await _pickFolderAsync(workingDirectoryBox.Text.Trim());
            if (!string.IsNullOrWhiteSpace(picked))
            {
                workingDirectoryBox.Text = picked;
            }
        }

        async Task PickIconAsync()
        {
            var picked = await _pickFileAsync(iconBox.Text.Trim());
            if (!string.IsNullOrWhiteSpace(picked))
            {
                iconBox.Text = picked;
            }
        }

        void ApplyPickedTarget(string? picked)
        {
            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            targetBox.Text = picked;
            if (autoName || string.IsNullOrWhiteSpace(nameBox.Text))
            {
                var nextName = workspace.SuggestedShortcutNameForTarget(picked, pane);
                updatingName = true;
                nameBox.Text = nextName;
                nameBox.Select(0, NewItemTemplate.RenameSelectionLength(nextName, isDirectory: false));
                updatingName = false;
                autoName = true;
            }
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(nameBox);
        panel.Children.Add(targetBox);
        panel.Children.Add(ButtonRow(
            ("Browse file", (_, _) => _ = PickTargetFileAsync()),
            ("Browse folder", (_, _) => _ = PickTargetFolderAsync())));
        panel.Children.Add(argumentsBox);
        panel.Children.Add(workingDirectoryBox);
        panel.Children.Add(ButtonRow(("Browse folder", (_, _) => _ = PickWorkingDirectoryAsync())));
        panel.Children.Add(iconBox);
        panel.Children.Add(ButtonRow(("Browse icon", (_, _) => _ = PickIconAsync())));

        var dialog = new ContentDialog
        {
            Title = "Create Shortcut",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _xamlRoot(),
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        var targetPath = targetBox.Text.Trim();
        if (name.Length == 0 || targetPath.Length == 0)
        {
            _showMessage("Shortcut", "Shortcut name and target are required.", InfoBarSeverity.Warning);
            return;
        }

        if (!ReferenceEquals(_workspace(), workspace))
        {
            return;
        }

        var utilityCts = _beginUtilityOperation();
        try
        {
            workspace.ActivatePane(pane);
            await workspace.CreateShortcutInCurrentPaneAsync(
                name,
                targetPath,
                TrimToNull(argumentsBox.Text),
                TrimToNull(workingDirectoryBox.Text),
                TrimToNull(iconBox.Text),
                utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage("Shortcut", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }
    }

    private async Task PromptForNameAndInvokeAsync(
        PaneId pane,
        string title,
        string placeholderText,
        NewItemTemplate template,
        Func<ExplorerWorkspace, string, CancellationToken, Task<string>> invokeAsync)
    {
        var workspace = _workspace();
        if (workspace is null)
        {
            return;
        }

        var suggestedName = workspace.SuggestedNameForNewItem(template, pane);
        var textBox = new TextBox { Text = suggestedName, PlaceholderText = placeholderText };
        textBox.Select(0, NewItemTemplate.RenameSelectionLength(suggestedName, template.IsDirectory));

        var dialog = new ContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _xamlRoot(),
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || dialog.Content is not TextBox tb)
        {
            return;
        }

        var name = tb.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (!ReferenceEquals(_workspace(), workspace))
        {
            return;
        }

        var utilityCts = _beginUtilityOperation();
        try
        {
            workspace.ActivatePane(pane);
            await invokeAsync(workspace, name, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage(title, exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }
    }

    private async Task PromptRenameCreatedItemAsync(
        ExplorerWorkspace workspace,
        string path,
        NewItemTemplate template)
    {
        var currentName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(currentName))
        {
            return;
        }

        var tb = new TextBox { Text = currentName };
        tb.Select(0, NewItemTemplate.RenameSelectionLength(currentName, template.IsDirectory));

        var dialog = new ContentDialog
        {
            Title = template.IsDirectory ? "Name Folder" : "Name File",
            Content = tb,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Keep Name",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _xamlRoot(),
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var newName = tb.Text.Trim();
        if (newName.Length == 0 || string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        if (!ReferenceEquals(_workspace(), workspace))
        {
            return;
        }

        var utilityCts = _beginUtilityOperation();
        try
        {
            await workspace.RenameSelectedAsync(path, newName, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage("Rename", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }
    }

    public async Task PromptAndRenameAsync()
    {
        var workspace = _workspace();
        if (workspace is null || _selectedItem() is not { } row)
        {
            return;
        }

        var tb = new TextBox { Text = row.Name };
        tb.SelectAll();

        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = tb,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _xamlRoot(),
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(tb.Text) && tb.Text.Trim() != row.Name)
        {
            if (!ReferenceEquals(_workspace(), workspace))
            {
                return;
            }

            var utilityCts = _beginUtilityOperation();
            try
            {
                await workspace.RenameSelectedAsync(row.Path, tb.Text.Trim(), utilityCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _showMessage("Rename", exception.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _finishUtilityOperation(utilityCts);
            }
        }
    }

    public async Task TrashSelectedAsync()
    {
        var workspace = _workspace();
        if (workspace is null)
        {
            return;
        }

        var paths = _selectedPaths();
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        if (PathRules.IsRecycleBinPath(workspace.Active.Path))
        {
            await DeleteSelectedAsync();
            return;
        }

        var itemText = FormatItemCount(paths.Length);
        if (workspace.Settings.ConfirmDelete)
        {
            var dialog = new ContentDialog
            {
                Title = "Move to Recycle Bin",
                Content = $"Move {itemText} to the Recycle Bin?",
                PrimaryButtonText = "Move to Recycle Bin",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _xamlRoot(),
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        if (!ReferenceEquals(_workspace(), workspace))
        {
            return;
        }

        var utilityCts = _beginUtilityOperation();
        try
        {
            await workspace.TrashSelectedAsync(paths, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (exception is IpcException ipcException && FileOperationService.IsTrashUnavailable(ipcException))
            {
                await PromptPermanentDeleteAfterTrashUnavailableAsync(paths, workspace, ipcException, utilityCts.Token);
                return;
            }

            _showMessage("Trash", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }
    }

    public async Task DeleteSelectedAsync()
    {
        var workspace = _workspace();
        if (workspace is null)
        {
            return;
        }

        var paths = _selectedPaths();
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        var itemText = FormatItemCount(paths.Length);
        var content = paths.Length == 1
            ? $"Permanently delete this item? This cannot be undone.{Environment.NewLine}{paths[0]}"
            : $"Permanently delete {itemText}? This cannot be undone.";

        var dialog = new ContentDialog
        {
            Title = "Delete Permanently",
            Content = content,
            PrimaryButtonText = "Delete Permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _xamlRoot(),
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(_workspace(), workspace))
            {
                return;
            }

            var utilityCts = _beginUtilityOperation();
            try
            {
                foreach (var path in paths)
                {
                    await workspace.DeleteSelectedAsync(path, utilityCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _showMessage("Delete", exception.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _finishUtilityOperation(utilityCts);
            }
        }
    }

    public async Task ShowSettingsAsync()
    {
        var workspace = _workspace();
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var dialog = new SettingsDialog
        {
            XamlRoot = _xamlRoot(),
            OwnerHwnd = _ownerHwnd(),
        };

        var utilityCts = _beginUtilityOperation();
        dialog.ClearRecentHistoryAction = () => _clearRecentHistoryAsync(utilityCts.Token);
        try
        {
            try
            {
                await dialog.LoadSettingsAsync(fileOps, utilityCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _showMessage("Settings", exception.Message, InfoBarSeverity.Error);
                return;
            }

            if (!ReferenceEquals(_workspace(), workspace)
                || utilityCts.IsCancellationRequested
                || await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!ReferenceEquals(_workspace(), workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                dialog.ApplyTo(workspace.Settings);
                workspace.ApplyUiSettings(workspace.Settings, applyViewDefaultsToPanes: false);
                _applyTheme(workspace.Settings.Theme);
                _applyKeyboardShortcuts();
                await workspace.SaveUiSettingsAsync(utilityCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _showMessage("Settings", $"Settings were applied but could not be saved: {exception.Message}", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _finishUtilityOperation(utilityCts);
        }
    }

    public async Task ViewSelectedArchiveAsync()
    {
        var workspace = _workspace();
        if (workspace?.FileOps == null)
        {
            return;
        }

        var selected = _selectedEntries();
        if (selected.Length != 1)
        {
            return;
        }

        var entry = selected[0];
        try
        {
            var info = await workspace.FileOps.ListArchiveAsync(entry.Path);
            var dialog = new ArchiveViewerDialog { XamlRoot = _xamlRoot() };
            dialog.ArchiveData = info;
            await dialog.ShowAsync();
            if (dialog.ExtractRequested)
            {
                await ShowExtractDialogAsync(info);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage("View archive", exception.Message, InfoBarSeverity.Error);
        }
    }

    public async Task ExtractSelectedArchiveAsync()
    {
        var workspace = _workspace();
        if (workspace?.FileOps == null)
        {
            return;
        }

        var selected = _selectedEntries();
        if (selected.Length != 1)
        {
            return;
        }

        try
        {
            var info = await workspace.FileOps.ListArchiveAsync(selected[0].Path);
            await ShowExtractDialogAsync(info);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _showMessage("Extract archive", exception.Message, InfoBarSeverity.Error);
        }
    }

    public async Task CreateArchiveAsync()
    {
        var workspace = _workspace();
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var selected = _selectedEntries();
        if (selected.Length == 0)
        {
            return;
        }

        var dialog = new CreateArchiveDialog { XamlRoot = _xamlRoot() };
        dialog.SelectedPaths = selected.Select(entry => entry.Path).ToArray();
        dialog.SelectedNames = selected.Select(entry => entry.Name).ToArray();
        dialog.TargetDirectory = workspace.Active.Path;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(_workspace(), workspace))
            {
                return;
            }

            var archiveCts = _beginArchiveOperation();
            try
            {
                await fileOps.CreateArchiveAsync(
                    dialog.SelectedPaths,
                    Path.Combine(dialog.TargetDirectory, dialog.ArchiveName),
                    dialog.ArchiveFormat,
                    archiveCts.Token);
                if (ReferenceEquals(_workspace(), workspace) && !archiveCts.IsCancellationRequested)
                {
                    await workspace.RefreshAsync(archiveCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _showMessage("Create archive", exception.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _finishArchiveOperation(archiveCts);
            }
        }
    }

    public async Task ShowDuplicateCheckerAsync()
    {
        var workspace = _workspace();
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var path = workspace.Active.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var dialog = new DuplicateCheckerDialog { XamlRoot = _xamlRoot(), Directory = path };
        dialog.PreviewRequested += (_, filePath) =>
        {
            if (!ReferenceEquals(_workspace(), workspace))
            {
                return;
            }

            _queuePreview(_toFileRow(new FileEntry
            {
                Name = Path.GetFileName(filePath),
                Path = filePath,
            }));
        };
        dialog.OpenRequested += async (_, filePath) =>
        {
            await _runUiActionAsync(
                "Open",
                () => ReferenceEquals(_workspace(), workspace)
                    ? fileOps.OpenFileAsync(filePath)
                    : Task.CompletedTask);
        };
        dialog.RevealRequested += async (_, filePath) =>
        {
            await _runUiActionAsync(
                "Reveal in folder",
                () => ReferenceEquals(_workspace(), workspace)
                    ? fileOps.RevealInFolderAsync(filePath)
                    : Task.CompletedTask);
        };

        await RunScanDialogAsync(
            workspace,
            fileOps,
            dialog,
            "Duplicate checker",
            (scanDialog, progress, token) => fileOps.DuplicateCheckAsync(
                path,
                scanDialog.MinSizeBytes,
                partialHashBytes: null,
                progress: progress,
                ct: token),
            () => fileOps.CancelDuplicateCheckAsync(),
            async (scanDialog, _, token) =>
            {
                if (!scanDialog.DeleteRequested)
                {
                    return;
                }

                var trash = scanDialog.PathsToDelete;
                if (trash.Length == 0)
                {
                    return;
                }

                try
                {
                    await fileOps.TrashAsync(trash, token);
                }
                catch (IpcException ipcException) when (FileOperationService.IsTrashUnavailable(ipcException))
                {
                    await PromptPermanentDeleteAfterTrashUnavailableAsync(trash, workspace, ipcException, token);
                    return;
                }

                if (ReferenceEquals(_workspace(), workspace) && !token.IsCancellationRequested)
                {
                    await workspace.RefreshAsync(token);
                }
            },
            exception =>
            {
                if (exception is IpcException ipcException && FileOperationService.IsTrashUnavailable(ipcException))
                {
                    _showMessage(
                        "Recycle Bin unavailable",
                        FileOperationService.TrashUnavailableMessage(ipcException),
                        InfoBarSeverity.Warning);
                }
                else
                {
                    _showMessage("Duplicate checker", exception.Message, InfoBarSeverity.Error);
                }
            });
    }

    public async Task ShowDiskCleanupAsync()
    {
        var workspace = _workspace();
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var path = workspace.Active.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var dialog = new DiskCleanupDialog { XamlRoot = _xamlRoot(), Directory = path };
        await RunScanDialogAsync(
            workspace,
            fileOps,
            dialog,
            "Disk cleanup",
            (scanDialog, progress, token) => fileOps.DiskCleanupAsync(
                path,
                scanDialog.ThresholdBytes,
                progress,
                token),
            () => fileOps.CancelDiskCleanupAsync());
    }

    public async Task SetColorLabelAsync()
    {
        var workspace = _workspace();
        if (workspace is null)
        {
            return;
        }

        var selected = _selectedEntries();
        if (selected.Length == 0)
        {
            return;
        }

        var dialog = new TagPickerDialog { XamlRoot = _xamlRoot() };
        dialog.SetTags(workspace.AllTags.ToArray());
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(_workspace(), workspace))
            {
                return;
            }

            var paths = selected.Select(entry => entry.Path).ToArray();
            var utilityCts = _beginUtilityOperation();
            try
            {
                if (dialog.SelectedTagId.HasValue)
                {
                    await workspace.SetColorLabelAsync(paths, dialog.SelectedTagId.Value, utilityCts.Token);
                }
                else
                {
                    await workspace.RemoveColorLabelAsync(paths, utilityCts.Token);
                }

                if (ReferenceEquals(_workspace(), workspace) && !utilityCts.IsCancellationRequested)
                {
                    _refreshView();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _showMessage("Color label", exception.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _finishUtilityOperation(utilityCts);
            }
        }
    }

    private async Task ShowExtractDialogAsync(ArchiveInfo info)
    {
        var workspace = _workspace();
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var dialog = new ExtractArchiveDialog
        {
            XamlRoot = _xamlRoot(),
            BrowseFolderAsync = _pickFolderAsync,
        };
        dialog.ArchiveData = info;
        dialog.SetBaseDirectory(workspace.Active.Path);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(_workspace(), workspace))
            {
                return;
            }

            var archiveCts = _beginArchiveOperation();
            try
            {
                await fileOps.ExtractArchiveAsync(info.Path, dialog.Destination, archiveCts.Token);
                if (ReferenceEquals(_workspace(), workspace) && !archiveCts.IsCancellationRequested)
                {
                    await workspace.RefreshAsync(archiveCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _showMessage("Extract archive", exception.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _finishArchiveOperation(archiveCts);
            }
        }
    }

    private async Task PromptPermanentDeleteAfterTrashUnavailableAsync(
        string[] paths,
        ExplorerWorkspace workspace,
        IpcException exception,
        CancellationToken cancellationToken)
    {
        var itemText = paths.Length == 1 ? "this item" : $"{paths.Length} items";
        var dialog = new ContentDialog
        {
            Title = "Recycle Bin unavailable",
            Content = $"{FileOperationService.TrashUnavailableMessage(exception)}{Environment.NewLine}{Environment.NewLine}Permanently delete {itemText} instead? This cannot be undone.",
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _xamlRoot(),
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            _showMessage("Recycle Bin unavailable", "Nothing was permanently deleted.", InfoBarSeverity.Warning);
            return;
        }

        if (!ReferenceEquals(_workspace(), workspace))
        {
            return;
        }

        try
        {
            foreach (var path in paths)
            {
                await workspace.DeleteSelectedAsync(path, cancellationToken);
            }

            _showMessage("Deleted permanently", $"Deleted {itemText}.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception deleteException)
        {
            _showMessage("Delete", deleteException.Message, InfoBarSeverity.Error);
        }
    }

    private static string FormatItemCount(int count) =>
        count == 1 ? "this item" : $"{count} items";

    private static StackPanel ButtonRow(params (string Text, RoutedEventHandler Click)[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        foreach (var (text, click) in buttons)
        {
            var button = new Button { Content = text };
            button.Click += click;
            row.Children.Add(button);
        }

        return row;
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool IsCancellationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("cancel", StringComparison.OrdinalIgnoreCase);
    }
}
