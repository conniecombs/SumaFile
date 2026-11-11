using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

/// <summary>
/// A plugin-format extract or create to run as a Transfers row. <see cref="Run"/> receives the row's progress, the
/// callback that names the row's operation id, and the row's Cancel token. <see cref="RefreshPaths"/> are re-listed
/// once it succeeds.
/// </summary>
internal sealed record ArchiveTransferRequest(
    string Title,
    string Detail,
    Func<IProgress<ProgressUpdate>, Action<string>, CancellationToken, Task> Run,
    IReadOnlyList<string> RefreshPaths);

public sealed partial class MainWindow
{
    // One refresh at a time, so each reads the service's capabilities after the change that queued it and the
    // last refresh to run applies the newest set.
    private readonly SemaphoreSlim _archiveCapabilitiesGate = new(1, 1);

    /// <summary>
    /// Reads the service's archive capabilities, swaps the plugin formats in <see cref="ArchivePaths"/> and
    /// re-lists the panes inside archives whose engine came or went. Runs on the UI thread at startup, on every
    /// plugin change (the plugin manager raises one after each engine-set change, a lost backend included) and
    /// right after a reconnect swaps the IPC client. A failure is logged to plugins.log and ignored.
    /// </summary>
    private async Task RefreshArchiveCapabilitiesAsync()
    {
        var token = _pluginCts?.Token ?? CancellationToken.None;
        try
        {
            await _archiveCapabilitiesGate.WaitAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_workspace is not { FileOps: { } fileOps } workspace)
            {
                return;
            }

            var capabilities = await fileOps.GetArchiveCapabilitiesAsync(token).ConfigureAwait(true);
            var change = ArchivePaths.SetEngineFormats(capabilities.Formats);
            if (change.Added.Count == 0 && change.Removed.Count == 0)
            {
                return;
            }

            AppendPluginLog(
                "host",
                $"[info] Archive formats changed: added [{string.Join(", ", change.Added)}], removed [{string.Join(", ", change.Removed)}]");
            if (ReferenceEquals(_workspace, workspace))
            {
                await workspace.RefreshArchivePanesAsync([.. change.Added, .. change.Removed], token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppendPluginLog("host", $"[error] Archive capability refresh failed: {exception.Message}");
        }
        finally
        {
            _archiveCapabilitiesGate.Release();
        }
    }

    /// <summary>
    /// Runs a plugin-format extract or create as an external Transfers row with byte progress and Cancel, instead
    /// of the modal progress dialog the built-in formats use. Call it on the UI thread. Returns null when there is
    /// no Transfers manager; the caller then keeps its modal path.
    /// </summary>
    internal TransferOperationViewModel? EnqueueArchiveTransfer(ArchiveTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_transfer is null)
        {
            return null;
        }

        var workspace = _workspace;
        var operation = _transfer.EnqueueExternal(request.Title, request.Detail, async (row, token) =>
        {
            // EnqueueExternal starts the runner synchronously on this (UI) thread, so the progress posts every
            // update back to the UI thread.
            var progress = new Progress<ProgressUpdate>(row.ApplyProgress);
            await request.Run(progress, operationId => RunOnUiThread(() => row.SetOperationId(operationId)), token);
            if (workspace is not null && ReferenceEquals(_workspace, workspace) && !token.IsCancellationRequested)
            {
                await workspace.RefreshAffectedPanesAsync(request.RefreshPaths, token);
            }

            return TransferOperationStatus.Completed;
        });
        ShowTransferProgressWindow(activate: false);
        ScheduleTransferWindowReveal(operation);
        return operation;
    }

    private void RunOnUiThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }
}
