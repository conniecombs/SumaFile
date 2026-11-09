using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private enum TransferRunStatus
    {
        NoOp,
        Completed,
        Cancelled,
        Failed,
        Skipped,
    }

    private string[] _dragPaths = [];
    private bool _sidebarDragging;
    private bool _previewDragging;
    private bool _sidebarMoved;
    private bool _previewMoved;
    private bool _columnDragging;
    private string? _columnDragId;
    private PaneId _columnDragPane = PaneId.Primary;
    private double _columnDragStartX;
    private double _columnDragStartWidth;

    // True when the last finished transfer replaced files, so no undo entry was recorded.
    private bool _lastTransferReplaced;

    private void OnFileDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (!PopulateFileDragData(e.Items.OfType<FileRow>(), e.Data))
        {
            e.Cancel = true;
        }
    }

    private void OnDetailsRowsDragStarting(object? sender, DetailsFileRowsDragStartingEventArgs e)
    {
        e.Cancel = !PopulateFileDragData(e.Rows, e.Data);
    }

    private void OnDetailsRowsDragCompleted(object? sender, EventArgs e)
    {
        _dragPaths = [];
    }

    private bool PopulateFileDragData(IEnumerable<FileRow> rows, DataPackage data)
    {
        _dragPaths = rows.Select(row => row.Path).ToArray();
        if (_dragPaths.Length == 0)
        {
            return false;
        }

        data.SetText($"{InternalDragFormat}|{string.Join('\n', _dragPaths)}");
        data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

        // Defer StorageItem resolution so we don't block the UI thread on slow/network paths.
        // The provider callback runs asynchronously when an external drop target requests the data.
        var paths = _dragPaths;
        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
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
        return true;
    }

    private void OnFileDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        ClearPaneDragHighlights();
        _dragPaths = [];
    }

    private void SetPaneDragHighlight(PaneId pane, bool active)
    {
        var primaryHighlighted = active && pane == PaneId.Primary;
        var secondaryHighlighted = active && pane == PaneId.Secondary;
        PrimaryPaneRoot.BorderBrush = Brush(primaryHighlighted ? "SfAccentBrush" : "SfBorderBrush");
        PrimaryPaneRoot.BorderThickness = new Thickness(primaryHighlighted ? 1.5 : 0);
        SecondaryPaneRoot.BorderBrush = Brush(secondaryHighlighted ? "SfAccentBrush" : "SfBorderBrush");
        SecondaryPaneRoot.BorderThickness = new Thickness(secondaryHighlighted ? 1.5 : 0);
    }

    private void ClearPaneDragHighlights()
    {
        PrimaryPaneRoot.BorderBrush = Brush("SfBorderBrush");
        PrimaryPaneRoot.BorderThickness = new Thickness(0);
        SecondaryPaneRoot.BorderBrush = Brush("SfBorderBrush");
        SecondaryPaneRoot.BorderThickness = new Thickness(0);
    }

    private void OnPrimaryFileDragOver(object sender, DragEventArgs e) => HandleFileDragOver(e, PaneId.Primary);

    private void OnSecondaryFileDragOver(object sender, DragEventArgs e) => HandleFileDragOver(e, PaneId.Secondary);

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        ClearPaneDragHighlights();
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
        SetPaneDragHighlight(pane, valid);
        e.AcceptedOperation = valid
            ? (copy ? DataPackageOperation.Copy : DataPackageOperation.Move)
            : DataPackageOperation.None;
        e.DragUIOverride.Caption = $"{(copy ? "Copy" : "Move")} to {target.Destination}";
        e.Handled = true;
    }

    private async Task HandleFileDropAsync(DragEventArgs e, PaneId pane)
    {
        ClearPaneDragHighlights();
        if (_workspace?.FileOps is null)
        {
            return;
        }

        _workspace.ActivatePane(pane);
        var hovered = HoveredFileRow(e, pane);
        var target = DropDestination.Resolve(_workspace.Pane(pane).Path, hovered?.Path, hovered?.IsDir == true);
        var sources = await ReadDroppedPathsAsync(e);
        if (sources.Count == 0)
        {
            return;
        }

        if (!DropDestination.IsValidDrop(sources, target.Destination))
        {
            SetStatusText("Cannot drop into that location");
            return;
        }

        var internalDrag = _dragPaths.Length > 0;
        var move = internalDrag
            && (e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) == 0;
        var transfer = TransferWithConflictAsync(sources.ToArray(), target.Destination, move);
        _dragPaths = [];
        await transfer;
    }

    private FileRow? HoveredFileRow(DragEventArgs e, PaneId pane)
    {
        var details = pane == PaneId.Secondary ? SecondaryDetailsFileList : PrimaryDetailsFileList;
        if (details.Visibility == Visibility.Visible)
        {
            return details.RowFromPoint(e.GetPosition(details));
        }

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

    private async Task<TransferRunStatus> TransferWithConflictAsync(string[] sources, string destination, bool move)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || sources.Length == 0)
        {
            return TransferRunStatus.NoOp;
        }

        if (_transfer is null)
        {
            return TransferRunStatus.NoOp;
        }

        var operation = _transfer.Enqueue(
            sources,
            destination,
            move,
            (queuedOperation, token) => RunTransferOperationAsync(
                queuedOperation,
                workspace,
                fileOps,
                sources,
                destination,
                move,
                token));
        ShowTransferProgressWindow(activate: false);

        var status = await operation.CompletionTask;
        var finalStatus = TransferStatusToRunStatus(status);
        if (finalStatus != TransferRunStatus.NoOp && ReferenceEquals(_workspace, workspace))
        {
            if (finalStatus == TransferRunStatus.Skipped)
            {
                SetStatusText(TransferProgressFormatter.SkippedReceipt(move, sources.Length, destination));
            }

            workspace.RememberOperation(
                move ? "move" : "copy",
                $"{(move ? "Move" : "Copy")} {sources.Length} item(s) to {destination}",
                sources,
                destination,
                move,
                finalStatus.ToString().ToLowerInvariant());
        }

        return finalStatus;
    }

    private async Task<TransferOperationStatus> RunTransferOperationAsync(
        TransferOperationViewModel operation,
        ExplorerWorkspace workspace,
        FileOperationService fileOps,
        string[] sources,
        string destination,
        bool move,
        CancellationToken cancellationToken)
    {
        var conflictSession = new TransferConflictSession();
        // Per-name choices made up front when several top-level names conflict; the engine
        // reports those items as conflicts and they are resolved here without a second prompt.
        var preDecided = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var action = await ChooseConflictActionAsync(
                sources,
                destination,
                move,
                conflictSession,
                preDecided,
                cancellationToken);
            if (action is null)
            {
                return TransferOperationStatus.Cancelled;
            }

            var progress = new Progress<ProgressUpdate>(update => OnTransferProgress(operation, update));
            var tuning = new Progress<TuningUpdate>(operation.ApplyTuning);
            var canRegisterUndo = !string.Equals(action, "replace", StringComparison.OrdinalIgnoreCase);
            // Everything committed across the first pass and any retry passes is undone as one
            // entry, and skipped conflicts are counted so the receipt is honest.
            var committedAll = new List<TransferResult>();
            var skippedCount = 0;
            var undoRegistered = false;
            void PushUndoIfAny()
            {
                if (!canRegisterUndo && committedAll.Count > 0)
                {
                    _lastTransferReplaced = true;
                }

                if (!canRegisterUndo || committedAll.Count == 0 || !ReferenceEquals(_workspace, workspace))
                {
                    return;
                }

                if (move)
                {
                    workspace.Undo.PushMove(committedAll.ToArray(), fileOps);
                }
                else
                {
                    workspace.Undo.PushCopy(committedAll.ToArray(), fileOps);
                }

                _lastTransferReplaced = false;
                undoRegistered = true;
                MarkTransferUndoable(operation, workspace.Undo);
                committedAll.Clear();
            }

            // After the first pass, failures are resolved one file at a time and
            // sent back as exact (source, destination, action) items.
            TransferItemRequest[]? retryItems = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    TransferResult[] results;
                    if (retryItems is not null)
                    {
                        results = move
                            ? await fileOps.MoveItemsAsync(
                                retryItems,
                                destination,
                                progress,
                                operationId => StartTransferProgress(operation, operationId),
                                cancellationToken, tuning, operation.PolicyOverride)
                            : await fileOps.CopyItemsAsync(
                                retryItems,
                                destination,
                                progress,
                                operationId => StartTransferProgress(operation, operationId),
                                cancellationToken, tuning, operation.PolicyOverride);
                    }
                    else if (move)
                    {
                        results = await fileOps.MoveAsync(
                            sources,
                            destination,
                            action,
                            progress,
                            operationId => StartTransferProgress(operation, operationId),
                            cancellationToken, tuning, operation.PolicyOverride);
                    }
                    else
                    {
                        results = await fileOps.CopyAsync(
                            sources,
                            destination,
                            action,
                            progress,
                            operationId => StartTransferProgress(operation, operationId),
                            cancellationToken, tuning, operation.PolicyOverride);
                    }

                    committedAll.AddRange(results);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        PushUndoIfAny();
                    }

                    if (ReferenceEquals(_workspace, workspace) && !cancellationToken.IsCancellationRequested)
                    {
                        CompleteTransferProgress(move, sources.Length - skippedCount, destination, skippedCount, undoRegistered);
                        await workspace.RefreshAffectedPanesAsync(
                            OperationLogController.TransferAffectedPaths(sources, destination, move),
                            cancellationToken);
                    }

                    break;
                }
                catch (TransferCanceledException exception)
                {
                    var committed = exception.Outcome.Committed;
                    committedAll.AddRange(committed);
                    PushUndoIfAny();

                    if (committed.Length > 0 && ReferenceEquals(_workspace, workspace))
                    {
                        await workspace.RefreshAffectedPanesAsync(
                            OperationLogController.TransferAffectedPaths(sources, destination, move),
                            CancellationToken.None);
                    }

                    return TransferOperationStatus.Cancelled;
                }
                catch (TransferOperationException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    var committed = exception.Outcome.Committed;
                    committedAll.AddRange(committed);

                    if (ReferenceEquals(_workspace, workspace))
                    {
                        await workspace.RefreshAffectedPanesAsync(
                            OperationLogController.TransferAffectedPaths(sources, destination, move),
                            cancellationToken);
                    }

                    var failures = exception.Outcome.Failed;
                    if (failures.Length == 0)
                    {
                        // Planning-level failure without per-item detail (older
                        // service): keep the top-level retry path.
                        if (!FileOperationService.IsConflict(exception)) throw;
                        var remaining = exception.RemainingSources(sources);
                        if (remaining.Length == 0)
                        {
                            PushUndoIfAny();
                            break;
                        }
                        var retryAction = await ChooseConflictActionFromBackendConflictAsync(
                            exception.Message, destination, move, remaining, conflictSession, cancellationToken);
                        if (retryAction is null) return TransferOperationStatus.Cancelled;
                        sources = remaining;
                        action = retryAction;
                        canRegisterUndo = !string.Equals(action, "replace", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    var items = await ResolveTransferFailuresAsync(
                        failures, destination, move, sources, conflictSession, cancellationToken, preDecided);
                    if (items is null)
                    {
                        return TransferOperationStatus.Cancelled;
                    }
                    skippedCount += Math.Max(0, failures.Length - items.Length);
                    if (items.Length == 0)
                    {
                        // Every failure was skipped by the user; what was committed stands.
                        PushUndoIfAny();
                        if (ReferenceEquals(_workspace, workspace))
                        {
                            CompleteTransferProgress(move, sources.Length - skippedCount, destination, skippedCount, undoRegistered);
                        }
                        break;
                    }

                    retryItems = items;
                    canRegisterUndo = canRegisterUndo
                        && items.All(item => !string.Equals(item.Action, "replace", StringComparison.OrdinalIgnoreCase));
                }
                catch (IpcException exception) when (FileOperationService.IsConflict(exception) && !cancellationToken.IsCancellationRequested)
                {
                    var retryAction = await ChooseConflictActionFromBackendConflictAsync(
                        exception.Message,
                        destination,
                        move,
                        sources,
                        conflictSession,
                        cancellationToken);
                    if (retryAction is null)
                    {
                        return TransferOperationStatus.Cancelled;
                    }

                    action = retryAction;
                    canRegisterUndo = !string.Equals(action, "replace", StringComparison.OrdinalIgnoreCase);
                }
            }

            return cancellationToken.IsCancellationRequested
                ? TransferOperationStatus.Cancelled
                : TransferOperationStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            return TransferOperationStatus.Cancelled;
        }
        catch (Exception exception)
        {
            var (summary, details) = TransferFailureText.Describe(null, exception.Message, null);
            operation.SetErrorMessage(summary);
            ShowMessage(
                move ? "Move" : "Copy",
                string.IsNullOrWhiteSpace(details) || string.Equals(details, summary, StringComparison.Ordinal)
                    ? summary
                    : $"{summary}{Environment.NewLine}Details: {details}",
                InfoBarSeverity.Error);
            return TransferOperationStatus.Failed;
        }
    }

    private static TransferRunStatus TransferStatusToRunStatus(TransferOperationStatus status) => status switch
    {
        TransferOperationStatus.Completed => TransferRunStatus.Completed,
        TransferOperationStatus.Cancelled => TransferRunStatus.Cancelled,
        TransferOperationStatus.Failed => TransferRunStatus.Failed,
        TransferOperationStatus.Skipped => TransferRunStatus.Skipped,
        _ => TransferRunStatus.NoOp,
    };

    private static IReadOnlyList<string> VisiblePanePaths(ExplorerWorkspace workspace) =>
        workspace.DualPaneEnabled
            ? [workspace.Primary.Path, workspace.Secondary.Path]
            : [workspace.Active.Path];

    private async Task<string?> ChooseConflictActionAsync(
        string[] sources,
        string destination,
        bool move,
        TransferConflictSession session,
        Dictionary<string, string> preDecided,
        CancellationToken cancellationToken)
    {
        if (_workspace is null)
        {
            return null;
        }

        var conflicts = await ProbeDestinationConflictsAsync(sources, destination, cancellationToken);
        if (conflicts is null)
        {
            return "error";
        }

        if (conflicts.Count == 0)
        {
            return "error";
        }

        if (conflicts.Count == 1)
        {
            return await PromptConflictActionAsync(destination, conflicts, move, sources, session, cancellationToken);
        }

        return await PromptEachConflictAsync(destination, conflicts, move, sources, session, preDecided, cancellationToken);
    }

    /// <summary>
    /// Several top-level names conflict: ask once per name (one dialog instance reused) until
    /// "Do this for all remaining conflicts" is ticked. When every answer is the same that action
    /// runs the whole transfer; otherwise the transfer runs with "error" and each reported
    /// conflict is resolved from <paramref name="preDecided"/>.
    /// </summary>
    private async Task<string?> PromptEachConflictAsync(
        string destination,
        IReadOnlyList<string> conflicts,
        bool move,
        string[] sources,
        TransferConflictSession session,
        Dictionary<string, string> preDecided,
        CancellationToken cancellationToken)
    {
        ConflictDialog? dialog = null;
        string? first = null;
        var allSame = true;
        for (var index = 0; index < conflicts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = conflicts[index];
            string? chosen;
            if (session.TryGetSticky(out var sticky))
            {
                chosen = sticky;
            }
            else
            {
                dialog ??= new ConflictDialog { XamlRoot = Content.XamlRoot };
                chosen = await PromptConflictActionAsync(
                    destination,
                    [name],
                    move,
                    sources,
                    session,
                    cancellationToken,
                    existingConflictPath: null,
                    reuse: dialog,
                    position: (index + 1, conflicts.Count));
            }

            if (chosen is null)
            {
                return null;
            }

            preDecided[name] = chosen;
            if (first is null)
            {
                first = chosen;
            }
            else if (!string.Equals(first, chosen, StringComparison.OrdinalIgnoreCase))
            {
                allSame = false;
            }
        }

        if (allSame && first is not null)
        {
            preDecided.Clear();
            return first;
        }

        return "error";
    }

    /// <summary>The up-front choice for a reported conflict, matched by its top-level name under the destination.</summary>
    private static bool TryGetPreDecidedAction(
        IReadOnlyDictionary<string, string>? preDecided,
        string destination,
        TransferFailure failure,
        out string action)
    {
        action = "";
        if (preDecided is null || preDecided.Count == 0 || string.IsNullOrWhiteSpace(failure.Destination))
        {
            return false;
        }

        string relative;
        try
        {
            relative = System.IO.Path.GetRelativePath(destination, failure.Destination);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
        {
            return false;
        }

        var top = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], 2)[0];
        if (top.Length > 0 && preDecided.TryGetValue(top, out var found))
        {
            action = found;
            return true;
        }

        return false;
    }

    private async Task<IReadOnlyList<string>?> ProbeDestinationConflictsAsync(
        string[] sources,
        string destination,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                () => DropDestination.ProbeConflictingTransferNames(
                    sources,
                    destination,
                    NativePath.ExistsNoFollow,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            var names = await LoadDestinationEntryNamesAsync(destination, cancellationToken);
            return names is null
                ? null
                : DropDestination.ConflictingTransferNames(sources, names);
        }
    }

    private async Task<IReadOnlyList<string>?> LoadDestinationEntryNamesAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        if (_backend is not null)
        {
            try
            {
                var listing = await _backend.ListDirectoryAsync(destination, cancellationToken: cancellationToken);
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

    /// <summary>
    /// Turn the engine's per-file failures into exact retry items. Conflicts are
    /// decided one file at a time (or once with "apply to all"); errors are
    /// offered as a group with Retry or Skip. Returns null when the user cancels,
    /// an empty array when nothing is left to retry.
    /// </summary>
    private async Task<TransferItemRequest[]?> ResolveTransferFailuresAsync(
        TransferFailure[] failures,
        string destination,
        bool move,
        string[] sources,
        TransferConflictSession session,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? preDecided = null)
    {
        var items = new List<TransferItemRequest>();
        var errors = new List<TransferFailure>();
        foreach (var failure in failures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!failure.IsConflict)
            {
                if (!string.Equals(failure.Kind, "cancelled", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(failure.Source))
                {
                    errors.Add(failure);
                }
                continue;
            }

            string? chosen;
            // A per-name answer given up front wins over a later "do this for all" choice.
            if (TryGetPreDecidedAction(preDecided, destination, failure, out var decided))
            {
                chosen = decided;
            }
            else if (session.TryGetSticky(out var sticky))
            {
                chosen = sticky;
            }
            else
            {
                var name = PathRules.Basename(string.IsNullOrWhiteSpace(failure.Destination)
                    ? failure.Source
                    : failure.Destination);
                chosen = await PromptConflictActionAsync(
                    destination,
                    [name],
                    move,
                    sources,
                    session,
                    cancellationToken,
                    string.IsNullOrWhiteSpace(failure.Destination) ? null : failure.Destination);
            }

            if (chosen is null)
            {
                return null;
            }
            if (string.Equals(chosen, "skip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            items.Add(new TransferItemRequest
            {
                Source = failure.Source,
                Destination = failure.Destination,
                Action = chosen,
            });
        }

        if (errors.Count > 0)
        {
            var retry = await PromptRetryFailedTransfersAsync(errors, move, cancellationToken);
            if (retry is null)
            {
                return null;
            }
            if (retry.Value)
            {
                items.AddRange(errors.Select(failure => new TransferItemRequest
                {
                    Source = failure.Source,
                    Destination = failure.Destination,
                    Action = "error",
                }));
            }
        }

        return items.ToArray();
    }

    /// <summary>True = retry the failed files, false = skip them, null = cancel the operation.</summary>
    private async Task<bool?> PromptRetryFailedTransfersAsync(
        IReadOnlyList<TransferFailure> errors,
        bool move,
        CancellationToken cancellationToken)
    {
        await _transferPromptGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new StackPanel { Spacing = 4 };
            foreach (var failure in errors.Take(6))
            {
                var (summary, details) = TransferFailureText.Describe(failure.Kind, failure.Error, failure.Source);
                var row = new TextBlock { Text = FailureRowText(failure.Source, summary), TextWrapping = TextWrapping.Wrap };
                if (!string.IsNullOrWhiteSpace(details))
                {
                    // Compact rows keep the raw error in a tooltip.
                    ToolTipService.SetToolTip(row, details);
                }

                rows.Children.Add(row);
            }

            if (errors.Count > 6)
            {
                rows.Children.Add(new TextBlock { Text = $"… and {errors.Count - 6} more", Opacity = 0.8 });
            }

            var dialog = new ContentDialog
            {
                Title = errors.Count == 1
                    ? $"1 item could not be {(move ? "moved" : "copied")}"
                    : $"{errors.Count} items could not be {(move ? "moved" : "copied")}",
                Content = rows,
                PrimaryButtonText = "Retry",
                SecondaryButtonText = "Skip",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            using var cancelDialog = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
            var result = await DialogHost.ShowAsync(dialog);
            cancellationToken.ThrowIfCancellationRequested();
            return result switch
            {
                ContentDialogResult.Primary => true,
                ContentDialogResult.Secondary => false,
                _ => null,
            };
        }
        finally
        {
            _transferPromptGate.Release();
        }
    }

    /// <summary>One retry-prompt row: the plain summary, prefixed with the file name when it does not already name it.</summary>
    private static string FailureRowText(string source, string summary)
    {
        var name = PathRules.Basename(source);
        return string.IsNullOrWhiteSpace(name) || summary.Contains(name, StringComparison.OrdinalIgnoreCase)
            ? summary
            : $"{name} — {summary}";
    }

    private async Task<string?> ChooseConflictActionFromBackendConflictAsync(
        string message,
        string destination,
        bool move,
        string[] sources,
        TransferConflictSession session,
        CancellationToken cancellationToken)
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
        return await PromptConflictActionAsync(destination, conflicts, move, sources, session, cancellationToken, conflictPath);
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
        bool move,
        string[] sources,
        TransferConflictSession session,
        CancellationToken cancellationToken,
        string? existingConflictPath = null,
        ConflictDialog? reuse = null,
        (int Index, int Count)? position = null)
    {
        if (session.TryGetSticky(out var sticky))
        {
            return sticky;
        }

        await _transferPromptGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = reuse ?? new ConflictDialog { XamlRoot = Content.XamlRoot };
            dialog.SetConflict(new TransferConflictPromptContext(
                Move: move,
                SourceCount: sources.Length,
                Destination: destination,
                TargetPane: PaneForDestination(destination),
                Conflicts: conflicts));
            if (position is { } at)
            {
                dialog.Title = $"{dialog.Title} ({at.Index} of {at.Count})";
            }
            var comparisons = await Task.Run(() => TransferConflictComparisonLoader.Load(
                sources, destination, conflicts, cancellationToken, existingConflictPath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            dialog.SetComparisons(comparisons);
            using var cancelDialog = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
            await DialogHost.ShowAsync(dialog);
            cancellationToken.ThrowIfCancellationRequested();
            // The dialog's own Result is the radio choice made with Continue; Stop / Escape is Cancel.
            string? action = dialog.Result switch
            {
                ConflictResolution.Replace => "replace",
                ConflictResolution.Skip => "skip",
                ConflictResolution.KeepBoth => "keep-both",
                _ => null,
            };
            if (action is null)
            {
                return null;
            }

            session.Remember(action, dialog.ApplyToAllChecked);
            return action;
        }
        finally
        {
            _transferPromptGate.Release();
        }
    }

    private PaneId? PaneForDestination(string destination)
    {
        var workspace = _workspace;
        if (workspace is null)
        {
            return null;
        }

        foreach (var pane in new[] { PaneId.Primary, PaneId.Secondary })
        {
            if (PathRules.PathsEqual(workspace.Pane(pane).Path, destination))
            {
                return pane;
            }
        }

        return null;
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
            // The cross-pane buttons are disabled in single-pane mode; a stray command just says why.
            SetStatusText("Turn on dual pane to copy or move to the other pane.");
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
        if (await DialogHost.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }
        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var utilityCts = BeginUtilityOperation("PromptPackIntoFolderAsync");
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

        var utilityCts = BeginUtilityOperation("UnpackSelectedFolderAsync");
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

            if (ArchivePaths.IsEngineArchive(info.Path))
            {
                // Plugin formats run as a Transfers row with progress and Cancel; the built-ins keep the dialog.
                var archivePath = info.Path;
                var extractDestination = destination;
                if (EnqueueArchiveTransfer(new ArchiveTransferRequest(
                    $"Extracting {row.Name}",
                    extractDestination,
                    (progress, operationStarted, token) => fileOps.ExtractArchiveTrackedAsync(
                        archivePath, extractDestination, progress, operationStarted, token),
                    [extractDestination])) is not null)
                {
                    return;
                }
            }

            await DialogHost.RunWithProgressAsync(
                Content.XamlRoot,
                "Extract archive",
                $"Extracting {row.Name} to {destination}…",
                (_, token) => fileOps.ExtractArchiveAsync(info.Path, destination, token),
                archiveCts.Token);
            if (ReferenceEquals(_workspace, workspace) && !archiveCts.IsCancellationRequested)
            {
                await workspace.RefreshAffectedPanesAsync([destination], archiveCts.Token);
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

        var renameWindow = new AdvancedRenameDialog(
            selectedEntries,
            workspace.Active.Path,
            (path, cancellationToken) => backend.ListDirectoryAsync(path, onChunk: null, cancellationToken));

        if (await renameWindow.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var requests = renameWindow.RenameRequests;
        if (requests.Length == 0)
        {
            ShowMessage("Advanced rename", "No names would change.", InfoBarSeverity.Informational);
            return;
        }

        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var utilityCts = BeginUtilityOperation("PromptAdvancedRenameAsync");
        try
        {
            var renamed = await fileOps.BatchRenameAsync(requests, utilityCts.Token);
            utilityCts.Token.ThrowIfCancellationRequested();
            workspace.Undo.PushRename(requests.Select(request => request.Path).ToArray(), renamed, fileOps);
            if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
            {
                await workspace.RefreshAffectedPanesAsync(
                    [.. requests.Select(request => request.Path), .. renamed],
                    utilityCts.Token);
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

    private async Task UndoLastAsync()
    {
        var workspace = _workspace;
        if (workspace is null || !workspace.Undo.CanUndo)
        {
            SetStatusText(_lastTransferReplaced
                ? "Nothing to undo (replaced files can't be restored)"
                : "Nothing to undo");
            return;
        }

        // Captured before undoing: the entry is gone afterwards.
        var description = workspace.Undo.NextUndoDescription;
        var utilityCts = BeginUtilityOperation("UndoLastAsync");
        try
        {
            await workspace.Undo.UndoAsync(utilityCts.Token);
            ClearTransferUndoable();
            if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
            {
                await workspace.RefreshAffectedPanesAsync(VisiblePanePaths(workspace), utilityCts.Token);
                SetStatusText(string.IsNullOrWhiteSpace(description) ? "Undone" : $"Undid: {description}");
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

        var utilityCts = BeginUtilityOperation("RedoLastAsync");
        try
        {
            await workspace.Undo.RedoAsync(utilityCts.Token);
            if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
            {
                await workspace.RefreshAffectedPanesAsync(VisiblePanePaths(workspace), utilityCts.Token);
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
            await RunUiActionAsync("Resize sidebar", () => _workspace.SaveUiSettingsAsync());
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
        _columnDragPane = target.Pane;
        _columnDragStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _columnDragStartWidth = ColumnLayoutHost.For(_columnDragPane).WidthOf(_columnDragId);
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
        var columns = _workspace?.ColumnsFor(_columnDragPane) ?? ColumnLayoutHost.For(_columnDragPane);
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
        var primaryBase = _workspace?.ColumnsFor(PaneId.Primary) ?? ColumnLayoutHost.For(PaneId.Primary);
        var secondaryBase = _workspace?.ColumnsFor(PaneId.Secondary) ?? ColumnLayoutHost.For(PaneId.Secondary);
        var primary = EffectiveColumnsForPane(primaryBase, PaneId.Primary);
        var secondary = EffectiveColumnsForPane(secondaryBase, PaneId.Secondary);
        ColumnLayoutHost.ApplyEffective(primary, secondary);
        ApplyColumnHeader(PrimaryColumnHeader, primary, PaneId.Primary, ref _primaryColumnHeaderKey);
        ApplyColumnHeader(SecondaryColumnHeader, secondary, PaneId.Secondary, ref _secondaryColumnHeaderKey);
        ApplyDetailsSurface(PaneId.Primary, primary);
        ApplyDetailsSurface(PaneId.Secondary, secondary);
    }

    private ColumnLayout EffectiveColumnsForPane(ColumnLayout columns, PaneId pane)
    {
        var paneWidth = pane == PaneId.Secondary ? SecondaryPaneRoot.ActualWidth : PrimaryPaneRoot.ActualWidth;
        return columns.EffectiveForPaneWidth(
            paneWidth,
            _workspace?.DualPaneEnabled == true,
            IsGitIntegrationEnabled);
    }

    private static void ClearDetailsItemWidths(ListView list)
    {
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ContainerFromIndex(index) is ListViewItem item)
            {
                item.MinWidth = 0;
                item.Width = double.NaN;
            }
        }
    }

    private void ApplyDetailsSurface(PaneId pane, ColumnLayout columns)
    {
        var details = EffectiveFileListViewForPane(pane) == "details";
        var scrollBar = pane == PaneId.Secondary ? SecondaryDetailsHorizontalScrollBar : PrimaryDetailsHorizontalScrollBar;
        var surface = pane == PaneId.Secondary ? SecondaryFileSurface : PrimaryFileSurface;
        var viewport = pane == PaneId.Secondary ? SecondaryFileViewport : PrimaryFileViewport;
        var header = pane == PaneId.Secondary ? SecondaryColumnHeader : PrimaryColumnHeader;
        var list = pane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;
        var detailsList = pane == PaneId.Secondary ? SecondaryDetailsFileList : PrimaryDetailsFileList;
        var paneWidth = pane == PaneId.Secondary ? SecondaryPaneRoot.ActualWidth : PrimaryPaneRoot.ActualWidth;
        var viewportWidth = Math.Max(1, FileViewportWidthForPane(pane, paneWidth));

        if (!details)
        {
            list.Width = double.NaN;
            surface.HorizontalAlignment = HorizontalAlignment.Stretch;
            scrollBar.Visibility = Visibility.Collapsed;
            scrollBar.Value = 0;
            ClearDetailsItemWidths(list);
            ApplyDetailsHorizontalOffset(pane, header, 0);
            ResetHiddenListHorizontalScroll(list);
            return;
        }

        var contentWidth = DetailsContentWidth(columns);
        var maxOffset = Math.Max(0, contentWidth - viewportWidth);
        var nextOffset = Math.Min(scrollBar.Value, maxOffset);
        surface.HorizontalAlignment = HorizontalAlignment.Stretch;
        ConfigureDetailsHorizontalScrollBar(scrollBar, viewportWidth, maxOffset, nextOffset);
        ApplyDetailsHorizontalOffset(pane, header, nextOffset);
        detailsList.ApplyDetailsLayout(columns, FileListViewHost.IconSizeFor(pane), viewportWidth, nextOffset);
        surface.InvalidateMeasure();
        viewport.InvalidateMeasure();
        detailsList.InvalidateMeasure();
        ResetHiddenListHorizontalScroll(list);
    }

    private static void ConfigureDetailsHorizontalScrollBar(
        ScrollBar scrollBar,
        double viewportWidth,
        double maxOffset,
        double value)
    {
        scrollBar.Minimum = 0;
        scrollBar.Maximum = maxOffset;
        scrollBar.ViewportSize = viewportWidth;
        scrollBar.SmallChange = 32;
        scrollBar.LargeChange = Math.Max(32, viewportWidth * 0.85);
        scrollBar.Visibility = maxOffset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        if (Math.Abs(scrollBar.Value - value) > 0.5)
        {
            scrollBar.Value = value;
        }
    }

    private void OnPrimaryDetailsHorizontalScrollChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        OnDetailsHorizontalScrollChanged(PaneId.Primary, e.NewValue);

    private void OnSecondaryDetailsHorizontalScrollChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        OnDetailsHorizontalScrollChanged(PaneId.Secondary, e.NewValue);

    private void OnDetailsHorizontalScrollChanged(PaneId pane, double offset)
    {
        var header = pane == PaneId.Secondary ? SecondaryColumnHeader : PrimaryColumnHeader;
        var maxOffset = pane == PaneId.Secondary
            ? SecondaryDetailsHorizontalScrollBar.Maximum
            : PrimaryDetailsHorizontalScrollBar.Maximum;
        ApplyDetailsHorizontalOffset(pane, header, Math.Min(offset, maxOffset));
    }

    private static void ApplyDetailsHorizontalOffset(PaneId pane, Grid header, double offset)
    {
        var nextOffset = Math.Max(0, offset);
        if (header.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            header.RenderTransform = transform;
        }

        transform.X = Math.Abs(nextOffset) > 0.5 ? -nextOffset : 0;
        FileListHorizontalScrollHost.Apply(pane, nextOffset);
    }

    private void QueueDetailsScrollRefresh()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            PrimaryFileSurface.UpdateLayout();
            SecondaryFileSurface.UpdateLayout();
            PrimaryDetailsFileList.UpdateLayout();
            SecondaryDetailsFileList.UpdateLayout();
            ResetHiddenListHorizontalScroll(PrimaryFileList);
            ResetHiddenListHorizontalScroll(SecondaryFileList);
            ApplyColumnWidths();
        });
    }

    private static void ResetHiddenListHorizontalScroll(ListView list)
    {
        if (FindDescendantScrollViewer(list) is not { } scroller)
        {
            return;
        }

        scroller.HorizontalScrollMode = ScrollMode.Disabled;
        scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        if (Math.Abs(scroller.HorizontalOffset) > 0.5)
        {
            scroller.ChangeView(0, null, null, disableAnimation: true);
        }
    }
}
