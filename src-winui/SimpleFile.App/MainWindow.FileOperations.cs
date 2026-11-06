using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    // One shared timer: a transfer still running after this delay brings the Transfers window up.
    private static readonly TimeSpan TransferWindowRevealDelay = TimeSpan.FromSeconds(3);
    private DispatcherQueueTimer? _transferRevealTimer;

    private ListView ActiveFileList
        => _workspace?.ActivePane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;

    private string[]? SelectedPaths
    {
        get
        {
            var rows = ActiveSelectedRows;
            return rows.Count == 0 ? null : rows.Select(row => row.Path).ToArray();
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

    // Row-menu "Create shortcut": the selected item becomes the target and the name is suggested from it.
    private async Task PromptAndCreateShortcutForSelection()
    {
        if (_workspace is null || ActiveSelectedRow is not { } row)
        {
            return;
        }

        await _fileOperationDialogs.PromptAndCreateShortcutAsync(_workspace.ActivePane, row.Path);
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
        // Details view renames in place; other views (or no editable row) use the dialog.
        if (TryBeginInlineRename())
        {
            return;
        }

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
            var exported = await TrySetWindowsFileClipboardAsync(
                paths,
                ClipboardOperation.Copy,
                _workspace?.Clipboard.Stamp ?? "");
            SetStatusText(exported
                ? $"Copied {paths.Length} item(s)"
                : $"Copied {paths.Length} item(s) in SumaFile");
            RefreshView();
        }
    }

    private async Task CutToClipboard()
    {
        var paths = SelectedPaths;
        if (paths is not null && paths.Length > 0)
        {
            _workspace?.Clipboard.SetCut(paths);
            _workspace?.RememberClipboard();
            var exported = await TrySetWindowsFileClipboardAsync(
                paths,
                ClipboardOperation.Cut,
                _workspace?.Clipboard.Stamp ?? "");
            SetStatusText(exported
                ? $"Cut {paths.Length} item(s)"
                : $"Cut {paths.Length} item(s) in SumaFile");
            RefreshView();
        }
    }

    private async Task PasteFromClipboard(string? destinationOverride = null)
    {
        if (_workspace is null) return;

        var clipboard = _workspace.Clipboard;
        clipboard.ReconcileWith(await TryReadWindowsClipboardStampAsync());
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
            RefreshView();
        }
    }

    /// <summary>
    /// Shows the Transfers window. It is brought to the front only when the user asks for it
    /// (Status Center, the Transfers command) or when a job is still running after a few
    /// seconds; auto-created windows stay unactivated so background starts, retries and
    /// dequeues never steal focus or cover a conflict prompt on the main window.
    /// </summary>
    private void ShowTransferProgressWindow(bool activate = true)
    {
        if (_transfer is null)
        {
            return;
        }

        var window = EnsureTransferProgressWindow(out _);
        window.Start(_transfer, _workspace?.FileOps);
        if (activate)
        {
            window.Activate();
        }
    }

    private void StartTransferProgress(TransferOperationViewModel operation, string operationId)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => StartTransferProgress(operation, operationId));
            return;
        }

        operation.SetOperationId(operationId);
        ShowTransferProgressWindow(activate: false);
        ScheduleTransferWindowReveal(operation);
    }

    private void ScheduleTransferWindowReveal(TransferOperationViewModel operation)
    {
        if (_transferRevealTimer is null)
        {
            _transferRevealTimer = DispatcherQueue.CreateTimer();
            _transferRevealTimer.Interval = TransferWindowRevealDelay;
            _transferRevealTimer.IsRepeating = false;
            _transferRevealTimer.Tick += OnTransferRevealTimerTick;
        }

        if (!_transferRevealTimer.IsRunning)
        {
            _transferRevealTimer.Start();
        }

        // A job that finishes first cancels the reveal (unless another one is still running).
        _ = operation.CompletionTask.ContinueWith(
            _ => DispatcherQueue.TryEnqueue(CancelTransferWindowRevealIfIdle),
            TaskScheduler.Default);
    }

    private void OnTransferRevealTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!HasRunningTransfer())
        {
            return;
        }

        if (DialogHost.IsDialogOpen)
        {
            // A conflict or retry prompt is waiting on the main window; revealing the Transfers
            // window now would cover it. Try again after another delay.
            sender.Start();
            return;
        }

        ShowTransferProgressWindow(activate: true);
    }

    private void CancelTransferWindowRevealIfIdle()
    {
        if (_transferRevealTimer is { IsRunning: true } timer && !HasRunningTransfer())
        {
            timer.Stop();
        }
    }

    private bool HasRunningTransfer() =>
        _transfer?.Operations.Any(operation => operation.Status == TransferOperationStatus.Running) == true;

    private void OnTransferProgress(TransferOperationViewModel operation, ProgressUpdate update)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnTransferProgress(operation, update));
            return;
        }

        operation.ApplyProgress(update);
    }

    private void CompleteTransferProgress(
        bool move,
        int itemCount,
        string? destination = null,
        int skipped = 0,
        bool undoable = false)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => CompleteTransferProgress(move, itemCount, destination, skipped, undoable));
            return;
        }

        var receipt = TransferProgressFormatter.CompletionReceipt(move, itemCount, destination, skipped);
        SetStatusText(receipt);
        if (undoable)
        {
            ShowUndoReceipt(move ? "Move" : "Copy", receipt);
        }
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

    private TransferProgressWindow EnsureTransferProgressWindow(out bool created)
    {
        created = false;
        if (_transferProgressWindow is { IsClosed: false } existing)
        {
            return existing;
        }

        created = true;

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
        RefreshGitUiVisibility();
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
        await RunUiActionAsync("Large files and duplicates", ShowDiskCleanupAsync);

    private async Task ShowDiskCleanupAsync()
    {
        await _fileOperationDialogs.ShowDiskCleanupAsync();
    }

    private async void OnSetColorLabelClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Set label", SetColorLabelAsync);

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
