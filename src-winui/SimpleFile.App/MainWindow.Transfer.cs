using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private const string InternalDragFormat = "simplefile-internal";
    private string[] _dragPaths = [];
    private bool _sidebarDragging;
    private bool _previewDragging;
    private bool _sidebarMoved;
    private bool _previewMoved;
    private bool _columnDragging;
    private string? _columnDragId;
    private double _columnDragStartX;
    private double _columnDragStartWidth;

    private void OnFileDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragPaths = e.Items.OfType<FileRow>().Select(row => row.Path).ToArray();
        if (_dragPaths.Length == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetText($"{InternalDragFormat}|{string.Join('\n', _dragPaths)}");
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

        // Defer StorageItem resolution so we don't block the UI thread on slow/network paths.
        // The provider callback runs asynchronously when an external drop target requests the data.
        var paths = _dragPaths;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var storageItems = new List<IStorageItem>();
                foreach (var path in paths)
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                        }
                        else if (File.Exists(path))
                        {
                            storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
                        }
                    }
                    catch
                    {
                        // Skip items that cannot be resolved to StorageItems (e.g. permission issues).
                    }
                }

                request.SetData(storageItems);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void OnFileDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        _dragPaths = [];
    }

    private void OnPrimaryFileDragOver(object sender, DragEventArgs e) => HandleFileDragOver(e, PaneId.Primary);

    private void OnSecondaryFileDragOver(object sender, DragEventArgs e) => HandleFileDragOver(e, PaneId.Secondary);

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.None;
    }

    private async void OnPrimaryFileDrop(object sender, DragEventArgs e) =>
        await RunUiActionAsync("Drop files", () => HandleFileDropAsync(e, PaneId.Primary));

    private async void OnSecondaryFileDrop(object sender, DragEventArgs e) =>
        await RunUiActionAsync("Drop files", () => HandleFileDropAsync(e, PaneId.Secondary));

    private void HandleFileDragOver(DragEventArgs e, PaneId pane)
    {
        if (_workspace is null)
        {
            return;
        }

        _workspace.ActivatePane(pane);
        var hovered = HoveredFileRow(e, pane);
        var target = DropDestination.Resolve(_workspace.Pane(pane).Path, hovered?.Path, hovered?.IsDir == true);
        var sources = _dragPaths.Length > 0 ? _dragPaths : [];
        var valid = sources.Length == 0 || DropDestination.IsValidDrop(sources, target.Destination);
        var copy = (e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) != 0
            || sources.Length == 0;
        e.AcceptedOperation = valid
            ? (copy ? DataPackageOperation.Copy : DataPackageOperation.Move)
            : DataPackageOperation.None;
        e.DragUIOverride.Caption = $"{(copy ? "Copy" : "Move")} to {target.Destination}";
        e.Handled = true;
    }

    private async Task HandleFileDropAsync(DragEventArgs e, PaneId pane)
    {
        if (_workspace?.FileOps is null)
        {
            return;
        }

        _workspace.ActivatePane(pane);
        var hovered = HoveredFileRow(e, pane);
        var target = DropDestination.Resolve(_workspace.Pane(pane).Path, hovered?.Path, hovered?.IsDir == true);
        var sources = await ReadDroppedPathsAsync(e);
        if (sources.Count == 0 || !DropDestination.IsValidDrop(sources, target.Destination))
        {
            return;
        }

        var internalDrag = _dragPaths.Length > 0;
        var move = internalDrag
            && (e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) == 0;
        await TransferWithConflictAsync(sources.ToArray(), target.Destination, move);
        _dragPaths = [];
    }

    private FileRow? HoveredFileRow(DragEventArgs e, PaneId pane)
    {
        var list = pane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;
        var rows = pane == PaneId.Secondary ? SecondaryFiles : PrimaryFiles;
        var point = e.GetPosition(list);
        foreach (var row in rows)
        {
            if (list.ContainerFromItem(row) is not ListViewItem container)
            {
                continue;
            }

            var bounds = container.TransformToVisual(list)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(point))
            {
                return row;
            }
        }

        return null;
    }

    private async Task<List<string>> ReadDroppedPathsAsync(DragEventArgs e)
    {
        if (_dragPaths.Length > 0)
        {
            return [.. _dragPaths];
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            if (e.DataView.Contains(StandardDataFormats.Text))
            {
                var text = await e.DataView.GetTextAsync();
                if (text.StartsWith(InternalDragFormat, StringComparison.Ordinal))
                {
                    return text.Split('\n').Skip(1).Where(path => path.Length > 0).ToList();
                }
            }

            return [];
        }

        var items = await e.DataView.GetStorageItemsAsync();
        return items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
    }

    private async Task TransferWithConflictAsync(string[] sources, string destination, bool move)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || sources.Length == 0)
        {
            return;
        }

        var conflictSession = new TransferConflictSession();
        var action = await ChooseConflictActionAsync(sources, destination, conflictSession);
        if (action is null)
        {
            return;
        }

        var transferCts = _transfer?.BeginTransfer() ?? new CancellationTokenSource();
        try
        {
            var progress = new Progress<ProgressUpdate>(OnTransferProgress);
            while (true)
            {
                try
                {
                    if (move)
                    {
                        var results = await fileOps.MoveAsync(
                            sources,
                            destination,
                            action,
                            progress,
                            operationId => StartTransferProgress(operationId, move: true, sources, destination),
                            transferCts.Token);
                        if (ReferenceEquals(_workspace, workspace) && !transferCts.IsCancellationRequested)
                        {
                            workspace.Undo.PushMove(results, fileOps);
                        }
                    }
                    else
                    {
                        var results = await fileOps.CopyAsync(
                            sources,
                            destination,
                            action,
                            progress,
                            operationId => StartTransferProgress(operationId, move: false, sources, destination),
                            transferCts.Token);
                        if (ReferenceEquals(_workspace, workspace) && !transferCts.IsCancellationRequested)
                        {
                            workspace.Undo.PushCopy(results, fileOps);
                        }
                    }

                    if (ReferenceEquals(_workspace, workspace) && !transferCts.IsCancellationRequested)
                    {
                        await workspace.RefreshAsync(transferCts.Token);
                    }

                    break;
                }
                catch (IpcException exception) when (FileOperationService.IsConflict(exception) && !transferCts.IsCancellationRequested)
                {
                    var retryAction = await ChooseConflictActionFromBackendConflictAsync(
                        exception.Message,
                        destination,
                        conflictSession);
                    if (retryAction is null)
                    {
                        _transfer?.ClearCurrentOperation();
                        CloseTransferProgressWindow();
                        return;
                    }

                    action = retryAction;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage(move ? "Move" : "Copy", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (_transfer?.FinishTransfer(transferCts) != true)
            {
                transferCts.Dispose();
            }
        }
    }

    private async Task<string?> ChooseConflictActionAsync(
        string[] sources,
        string destination,
        TransferConflictSession session)
    {
        if (_workspace is null)
        {
            return null;
        }

        var names = await LoadDestinationEntryNamesAsync(destination);
        if (names is null)
        {
            return "error";
        }

        var conflicts = DropDestination.ConflictingTransferNames(sources, names);
        if (conflicts.Count == 0)
        {
            return "error";
        }

        return await PromptConflictActionAsync(destination, conflicts, session);
    }

    private async Task<IReadOnlyList<string>?> LoadDestinationEntryNamesAsync(string destination)
    {
        if (_backend is not null)
        {
            try
            {
                var listing = await _backend.ListDirectoryAsync(destination);
                return listing.Entries.Select(entry => entry.Name).ToArray();
            }
            catch
            {
                // Fall back to loaded panes below; otherwise the backend "error" action remains safe.
            }
        }

        if (_workspace is null)
        {
            return null;
        }

        foreach (var pane in new[] { PaneId.Primary, PaneId.Secondary })
        {
            var state = _workspace.Pane(pane);
            if (PathRules.PathsEqual(state.Path, destination))
            {
                return state.Entries.Select(entry => entry.Name).ToArray();
            }
        }

        return null;
    }

    private async Task<string?> ChooseConflictActionFromBackendConflictAsync(
        string message,
        string destination,
        TransferConflictSession session)
    {
        if (session.TryGetSticky(out var sticky))
        {
            return sticky;
        }

        var conflictPath = ConflictPathFromMessage(message);
        var conflictName = string.IsNullOrWhiteSpace(conflictPath)
            ? PathRules.Basename(destination)
            : PathRules.Basename(conflictPath);
        IReadOnlyList<string> conflicts = string.IsNullOrWhiteSpace(conflictName)
            ? Array.Empty<string>()
            : [conflictName];
        return await PromptConflictActionAsync(destination, conflicts, session);
    }

    private static string ConflictPathFromMessage(string message)
    {
        var detail = message.StartsWith(Protocol.PrefixConflict, StringComparison.Ordinal)
            ? message[Protocol.PrefixConflict.Length..].Trim()
            : message.Trim();
        foreach (var marker in new[]
                 {
                     "destination already exists:",
                     "multiple sources would replace the same destination:",
                 })
        {
            var index = detail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return detail[(index + marker.Length)..].Trim();
            }
        }

        return detail;
    }

    private async Task<string?> PromptConflictActionAsync(
        string destination,
        IReadOnlyList<string> conflicts,
        TransferConflictSession session)
    {
        if (session.TryGetSticky(out var sticky))
        {
            return sticky;
        }

        var dialog = new ConflictDialog { XamlRoot = Content.XamlRoot };
        dialog.SetConflict(destination, conflicts);
        var result = await dialog.ShowAsync();
        string? action = dialog.Result == ConflictResolution.KeepBoth
            ? "keep-both"
            : result switch
            {
                ContentDialogResult.Primary => "replace",
                ContentDialogResult.Secondary => "skip",
                _ => null,
            };
        if (action is null)
        {
            return null;
        }

        session.Remember(action, dialog.ApplyToAllChecked);
        return action;
    }

    private async Task CopyOrMoveToOtherPaneAsync(bool move)
    {
        var paths = SelectedPaths;
        if (_workspace is null || paths is null || paths.Length == 0)
        {
            return;
        }

        var destination = _workspace.OtherPanePath();
        if (destination is null)
        {
            ShowMessage("Dual pane", "Enable dual pane to copy or move to the other pane.", InfoBarSeverity.Informational);
            return;
        }

        await TransferWithConflictAsync(paths, destination, move);
        await _workspace.NavigatePaneAsync(_workspace.OtherPane().Id, destination, HistoryMode.None, activate: false);
    }

    private async Task PromptPackIntoFolderAsync()
    {
        var workspace = _workspace;
        var paths = SelectedPaths;
        if (workspace is null || paths is null || paths.Length == 0)
        {
            return;
        }

        var input = new TextBox { PlaceholderText = "Folder name" };
        var dialog = new ContentDialog
        {
            Title = "Pack into Folder",
            Content = input,
            PrimaryButtonText = "Pack",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }
        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.PackIntoFolderAsync(paths, input.Text.Trim(), utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Pack into folder", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task UnpackSelectedFolderAsync()
    {
        var workspace = _workspace;
        if (workspace is null || ActiveSelectedRow is not { IsDir: true } row)
        {
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.UnpackFolderAsync(row.Path, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Unpack folder", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task ExtractSelectedArchiveAsync(string mode)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || ActiveSelectedRow is not { } row || !ArchivePaths.IsArchiveFile(row.Path))
        {
            return;
        }

        var archiveCts = BeginArchiveOperation();
        try
        {
            var info = await fileOps.ListArchiveAsync(row.Path, archiveCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || archiveCts.IsCancellationRequested)
            {
                return;
            }

            var destination = workspace.Active.Path;
            if (mode == "ctx-extract-folder")
            {
                destination = PathRules.JoinPath(workspace.Active.Path, ArchivePaths.ExtractFolderName(row.Name));
            }
            else if (mode == "ctx-extract-to")
            {
                var picked = await PickFolderAsync(workspace.Active.Path);
                if (picked is null
                    || !ReferenceEquals(_workspace, workspace)
                    || archiveCts.IsCancellationRequested)
                {
                    return;
                }

                destination = picked;
            }

            await fileOps.ExtractArchiveAsync(info.Path, destination, archiveCts.Token);
            if (ReferenceEquals(_workspace, workspace) && !archiveCts.IsCancellationRequested)
            {
                await workspace.RefreshAsync(archiveCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Extract archive", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishArchiveOperation(archiveCts);
        }
    }

    private async Task<string?> PickFolderAsync(string? defaultPath)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        if (!string.IsNullOrWhiteSpace(defaultPath))
        {
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task PromptAdvancedRenameAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        var backend = _backend;
        var selected = ActiveSelectedRows;
        if (workspace is null || fileOps is null || backend is null || selected.Count == 0)
        {
            return;
        }

        var selectedEntries = selected.Select(row => new FileEntry
        {
            Name = row.Name,
            Path = row.Path,
            IsDir = row.IsDir,
            Size = row.Size,
            Extension = row.Extension,
        }).ToArray();

        var dialog = new AdvancedRenameDialog(
            selectedEntries,
            workspace.Active.Path,
            (path, cancellationToken) => backend.ListDirectoryAsync(path, onChunk: null, cancellationToken));

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var requests = dialog.RenameRequests;
        if (requests.Length == 0)
        {
            ShowMessage("Advanced rename", "No names would change.", InfoBarSeverity.Informational);
            return;
        }

        if (!ReferenceEquals(_workspace, workspace)
            || !await ConfirmAdvancedRenameAsync(dialog.ChangedRows, requests.Length))
        {
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await fileOps.BatchRenameAsync(requests, utilityCts.Token);
            if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
            {
                await workspace.RefreshAsync(utilityCts.Token);
            }

            SetStatusText(requests.Length == 1
                ? "Renamed 1 item"
                : $"Renamed {requests.Length} items");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Advanced rename", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task<bool> ConfirmAdvancedRenameAsync(
        IReadOnlyList<AdvancedRenamePreviewRow> changedRows,
        int requestCount)
    {
        var body = new StackPanel { Spacing = 10, Width = 460 };
        body.Children.Add(new TextBlock
        {
            Text = requestCount == 1 ? "1 item will be renamed." : $"{requestCount} items will be renamed.",
            TextWrapping = TextWrapping.Wrap,
        });

        var previewRows = changedRows.Take(8).ToArray();
        var list = new StackPanel { Spacing = 4 };
        foreach (var row in previewRows)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"{row.OldName} -> {row.NewName}",
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        body.Children.Add(list);

        var extra = requestCount - previewRows.Length;
        if (extra > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = extra == 1 ? "And 1 more rename." : $"And {extra} more renames.",
            });
        }

        var confirm = new ContentDialog
        {
            Title = requestCount == 1 ? "Rename 1 Item" : $"Rename {requestCount} Items",
            Content = body,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Back",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        return await confirm.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task UndoLastAsync()
    {
        var workspace = _workspace;
        if (workspace is null || !workspace.Undo.CanUndo)
        {
            SetStatusText("Nothing to undo");
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.Undo.UndoAsync(utilityCts.Token);
            if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
            {
                await workspace.RefreshAsync(utilityCts.Token);
                SetStatusText("Undone");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Undo", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task RedoLastAsync()
    {
        var workspace = _workspace;
        if (workspace is null || !workspace.Undo.CanRedo)
        {
            SetStatusText("Nothing to redo");
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.Undo.RedoAsync(utilityCts.Token);
            if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
            {
                await workspace.RefreshAsync(utilityCts.Token);
                SetStatusText("Redone");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Redo", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private void OnSidebarDividerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_workspace?.Settings.SidebarVisible != true)
        {
            return;
        }

        _sidebarDragging = true;
        _sidebarMoved = false;
        SidebarDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSidebarDividerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_sidebarDragging || _workspace is null)
        {
            return;
        }

        var width = UiSettings.NormalizeSidebarWidth(e.GetCurrentPoint(RootGrid).Position.X);
        if (Math.Abs(width - _workspace.Settings.SidebarWidth) > 1)
        {
            _sidebarMoved = true;
        }

        _workspace.Settings.SidebarWidth = width;
        ApplySidebarLayout();
        e.Handled = true;
    }

    private async void OnSidebarDividerReleased(object sender, PointerRoutedEventArgs e)
    {
        var wasDragging = _sidebarDragging;
        _sidebarDragging = false;
        SidebarDivider.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (wasDragging && _sidebarMoved && _workspace is not null)
        {
            await RunUiActionAsync("Resize side menu", () => _workspace.SaveUiSettingsAsync());
        }
    }

    private async void OnSidebarDividerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_workspace?.Settings.SidebarVisible != true)
        {
            return;
        }

        await ToggleSidebarAsync();
    }

    private void OnPreviewDividerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_workspace?.Settings.PreviewVisible != true)
        {
            return;
        }

        _previewDragging = true;
        _previewMoved = false;
        PreviewDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPreviewDividerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_previewDragging || _workspace is null)
        {
            return;
        }

        var hostWidth = ExplorerContentGrid.ActualWidth;
        if (hostWidth <= 0)
        {
            return;
        }

        var width = UiSettings.NormalizePreviewWidth(
            hostWidth - e.GetCurrentPoint(ExplorerContentGrid).Position.X);
        if (Math.Abs(width - _workspace.Settings.PreviewWidth) > 1)
        {
            _previewMoved = true;
        }

        _workspace.Settings.PreviewWidth = width;
        ApplyPreviewVisibility();
        e.Handled = true;
    }

    private async void OnPreviewDividerReleased(object sender, PointerRoutedEventArgs e)
    {
        var wasDragging = _previewDragging;
        _previewDragging = false;
        PreviewDivider.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        var workspace = _workspace;
        if (wasDragging && _previewMoved && workspace is not null)
        {
            await RunUiActionAsync("Resize preview pane", () => workspace.SaveUiSettingsAsync());
        }
    }

    private void OnPreviewDividerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_workspace?.Settings.PreviewVisible != true)
        {
            return;
        }

        OnTogglePreview(sender, e);
    }

    private void OnColumnThumbPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ColumnResizeTarget target)
        {
            return;
        }

        _columnDragging = true;
        _columnDragId = target.ColumnId;
        _columnDragStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _columnDragStartWidth = ColumnLayoutHost.Shared.WidthOf(_columnDragId);
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnColumnThumbMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_columnDragging || _columnDragId is null)
        {
            return;
        }

        var delta = e.GetCurrentPoint(RootGrid).Position.X - _columnDragStartX;
        var columns = _workspace?.Columns ?? ColumnLayoutHost.Shared;
        columns.Resize(_columnDragId, _columnDragStartWidth + delta);
        ApplyColumnWidths();
        e.Handled = true;
    }

    private async void OnColumnThumbReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_columnDragging)
        {
            return;
        }

        _columnDragging = false;
        _columnDragId = null;
        if (sender is FrameworkElement element)
        {
            element.ReleasePointerCapture(e.Pointer);
        }

        e.Handled = true;
        if (_workspace is not null)
        {
            await RunUiActionAsync("Resize columns", () => _workspace.SaveUiSettingsAsync());
        }
    }

    private void OnColumnThumbDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ColumnResizeTarget target)
        {
            return;
        }

        e.Handled = true;
        _columnDragging = false;
        _columnDragId = null;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (shift)
        {
            SizeAllColumnsToFit(target.Pane);
        }
        else
        {
            SizeColumnToFit(target.ColumnId, target.Pane, save: true);
        }
    }

    private void ApplyColumnWidths()
    {
        var columns = _workspace?.Columns ?? ColumnLayoutHost.Shared;
        ApplyColumnHeader(PrimaryColumnHeader, columns, PaneId.Primary, ref _primaryColumnHeaderKey);
        ApplyColumnHeader(SecondaryColumnHeader, columns, PaneId.Secondary, ref _secondaryColumnHeaderKey);
        ApplyDetailsItemMinWidths(PrimaryFileList, columns.VisibleWidth);
        ApplyDetailsItemMinWidths(SecondaryFileList, columns.VisibleWidth);
    }

    private static void ApplyDetailsItemMinWidths(ListView list, double width)
    {
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ContainerFromIndex(index) is ListViewItem item)
            {
                item.MinWidth = width;
            }
        }
    }
}
