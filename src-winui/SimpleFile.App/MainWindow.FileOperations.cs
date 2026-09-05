using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private ListView ActiveFileList
        => _workspace?.ActivePane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;

    private string[]? SelectedPaths
    {
        get
        {
            var list = ActiveFileList;
            var items = list.SelectedItems;
            if (items == null || items.Count == 0) return null;
            return items.OfType<FileRow>().Select(r => r.Path).ToArray();
        }
    }

    private async Task PromptAndCreateFile(PaneId pane)
    {
        await _fileOperationDialogs.PromptAndCreateFileAsync(pane);
    }

    private async Task PromptAndCreateShortcut(PaneId pane)
    {
        await _fileOperationDialogs.PromptAndCreateShortcutAsync(pane);
    }

    private async Task CreateNewItem(PaneId pane, NewItemTemplate template)
    {
        await _fileOperationDialogs.CreateNewItemFromTemplateAsync(pane, template);
    }

    private async Task RunNewItemCommandAsync(string id, PaneId pane)
    {
        if (NewItemTemplate.Find(id) is not { } template)
        {
            return;
        }

        if (ReferenceEquals(template, NewItemTemplate.EmptyFile))
        {
            await PromptAndCreateFile(pane);
            return;
        }

        if (template.IsShortcut)
        {
            await PromptAndCreateShortcut(pane);
            return;
        }

        await CreateNewItem(pane, template);
    }

    private async Task PromptAndRename()
    {
        await _fileOperationDialogs.PromptAndRenameAsync();
    }

    private async Task TrashSelected()
    {
        await _fileOperationDialogs.TrashSelectedAsync();
    }

    private async Task DeleteSelected()
    {
        await _fileOperationDialogs.DeleteSelectedAsync();
    }

    private async Task CopyToClipboard()
    {
        var paths = SelectedPaths;
        if (paths is not null && paths.Length > 0)
        {
            _workspace?.Clipboard.SetCopy(paths);
            _workspace?.RememberClipboard();
            var exported = await TrySetWindowsFileClipboardAsync(paths, ClipboardOperation.Copy);
            SetStatusText(exported
                ? $"Copied {paths.Length} item(s)"
                : $"Copied {paths.Length} item(s) in SumaFile");
        }
    }

    private async Task CutToClipboard()
    {
        var paths = SelectedPaths;
        if (paths is not null && paths.Length > 0)
        {
            _workspace?.Clipboard.SetCut(paths);
            _workspace?.RememberClipboard();
            var exported = await TrySetWindowsFileClipboardAsync(paths, ClipboardOperation.Cut);
            SetStatusText(exported
                ? $"Cut {paths.Length} item(s)"
                : $"Cut {paths.Length} item(s) in SumaFile");
        }
    }

    private async Task PasteFromClipboard(string? destinationOverride = null)
    {
        if (_workspace is null) return;

        var clipboard = _workspace.Clipboard;
        var payload = clipboard.HasItems
            ? new ClipboardTransferPayload(clipboard.Operation, clipboard.SourcePaths, IsInternal: true)
            : await TryReadWindowsFileClipboardAsync();
        if (payload is null || payload.SourcePaths.Length == 0)
        {
            SetStatusText("Clipboard does not contain files");
            return;
        }

        var destination = destinationOverride ?? _workspace.Active.Path;
        if (!DropDestination.IsValidDrop(payload.SourcePaths, destination))
        {
            SetStatusText("Cannot paste into that location");
            return;
        }

        var outcome = await TransferWithConflictAsync(
            payload.SourcePaths,
            destination,
            payload.Operation == ClipboardOperation.Cut);
        if (payload.IsInternal
            && payload.Operation == ClipboardOperation.Cut
            && outcome == TransferRunStatus.Completed)
        {
            clipboard.Clear();
        }
    }

    private void ShowTransferProgressWindow()
    {
        if (_transfer is null)
        {
            return;
        }

        var window = EnsureTransferProgressWindow();
        window.Start(_transfer);
    }

    private void StartTransferProgress(TransferOperationViewModel operation, string operationId)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => StartTransferProgress(operation, operationId));
            return;
        }

        operation.SetOperationId(operationId);
        ShowTransferProgressWindow();
    }

    private void OnTransferProgress(TransferOperationViewModel operation, ProgressUpdate update)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnTransferProgress(operation, update));
            return;
        }

        operation.ApplyProgress(update);
    }

    private void CompleteTransferProgress(bool move, int itemCount)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => CompleteTransferProgress(move, itemCount));
            return;
        }

        var verb = move ? "Moved" : "Copied";
        var noun = itemCount == 1 ? "item" : "items";
        SetStatusText($"{verb} {itemCount} {noun}");
    }

    private void OnFileProgressCancelRequested(TransferOperationViewModel operation)
    {
        _transfer?.Cancel(operation);
        SetStatusText($"Cancelling {operation.Title.ToLowerInvariant()}");
    }

    private void OnTransferClearCompletedRequested()
    {
        _transfer?.ClearCompleted();
    }

    private void OnTransferWindowCloseRequested()
    {
        CloseTransferProgressWindow();
    }

    private TransferProgressWindow EnsureTransferProgressWindow()
    {
        if (_transferProgressWindow is { IsClosed: false } existing)
        {
            return existing;
        }

        var window = new TransferProgressWindow();
        window.CancelRequested += OnFileProgressCancelRequested;
        window.ClearCompletedRequested += OnTransferClearCompletedRequested;
        window.CloseRequested += OnTransferWindowCloseRequested;
        window.Closed += OnTransferProgressWindowClosed;
        _transferProgressWindow = window;
        return window;
    }

    private void OnTransferProgressWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is TransferProgressWindow window)
        {
            window.CancelRequested -= OnFileProgressCancelRequested;
            window.ClearCompletedRequested -= OnTransferClearCompletedRequested;
            window.CloseRequested -= OnTransferWindowCloseRequested;
            window.Closed -= OnTransferProgressWindowClosed;
        }

        if (ReferenceEquals(_transferProgressWindow, sender))
        {
            _transferProgressWindow = null;
        }
    }

    private void CloseTransferProgressWindow()
    {
        var window = _transferProgressWindow;
        if (window is null)
        {
            return;
        }

        window.CancelRequested -= OnFileProgressCancelRequested;
        window.ClearCompletedRequested -= OnTransferClearCompletedRequested;
        window.CloseRequested -= OnTransferWindowCloseRequested;
        window.Closed -= OnTransferProgressWindowClosed;
        _transferProgressWindow = null;
        if (!window.IsClosed)
        {
            window.Close();
        }
    }

    private async void OnPrimaryNewItemRequested(object? sender, string id) =>
        await RunUiActionAsync("New", () => RunNewItemCommandAsync(id, ActiveUiPane));

    private async void OnPrimaryRename(object sender, RoutedEventArgs e)
    {
        _workspace?.ActivatePane(PaneId.Primary);
        await RunUiActionAsync("Rename", PromptAndRename);
    }

    private async void OnPrimaryDelete(object sender, RoutedEventArgs e)
    {
        _workspace?.ActivatePane(PaneId.Primary);
        await RunUiActionAsync("Trash", TrashSelected);
    }

    private async void OnSecondaryNewFolder(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("New Folder", () => CreateNewItem(PaneId.Secondary, NewItemTemplate.Folder));

    private async void OnSecondaryNewFile(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("New Text Document", () => CreateNewItem(PaneId.Secondary, NewItemTemplate.TextFile));

    private async void OnSecondaryRename(object sender, RoutedEventArgs e)
    {
        _workspace?.ActivatePane(PaneId.Secondary);
        await RunUiActionAsync("Rename", PromptAndRename);
    }

    private async void OnSecondaryDelete(object sender, RoutedEventArgs e)
    {
        _workspace?.ActivatePane(PaneId.Secondary);
        await RunUiActionAsync("Trash", TrashSelected);
    }

    private FileRow[] GetSelectedEntries() => ActiveSelectedRows.ToArray();
    private void RefreshView() => SyncFromWorkspace();

    private async void OnSettingsClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Settings", ShowSettingsAsync);

    private async Task ShowSettingsAsync()
    {
        await _fileOperationDialogs.ShowSettingsAsync();
    }

    private async void OnViewArchiveClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("View archive", ViewSelectedArchiveAsync);

    private async Task ViewSelectedArchiveAsync()
    {
        await _fileOperationDialogs.ViewSelectedArchiveAsync();
    }

    private async void OnExtractArchiveClicked(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Extract archive", () => _fileOperationDialogs.ExtractSelectedArchiveAsync());
    }

    private async void OnCreateArchiveClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Create archive", CreateArchiveAsync);

    private async Task CreateArchiveAsync()
    {
        await _fileOperationDialogs.CreateArchiveAsync();
    }

    private async void OnDuplicateCheckerClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Duplicate checker", ShowDuplicateCheckerAsync);

    private async Task ShowDuplicateCheckerAsync()
    {
        await _fileOperationDialogs.ShowDuplicateCheckerAsync();
    }

    private async void OnDiskCleanupClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Disk cleanup", ShowDiskCleanupAsync);

    private async Task ShowDiskCleanupAsync()
    {
        await _fileOperationDialogs.ShowDiskCleanupAsync();
    }

    private async void OnSetColorLabelClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Color label", SetColorLabelAsync);

    private async Task SetColorLabelAsync()
    {
        await _fileOperationDialogs.SetColorLabelAsync();
    }

    private async void OnOpenTerminalClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Terminal", OpenTerminalInActivePathAsync);

    private async Task OpenTerminalInActivePathAsync()
    {
        if (_workspace?.FileOps == null) return;
        try
        {
            await _workspace.FileOps.OpenTerminalAsync(_workspace.Active.Path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Terminal", exception.Message, InfoBarSeverity.Error);
        }
    }
}
