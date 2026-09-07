using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public interface IScanDialog<TResult>
{
    bool ScanWasCancelled { get; }

    event EventHandler? ScanCancelled;

    void ShowConfiguration();
    void ShowScanning();
    void ShowResults(TResult result);
    void UpdateProgress(ProgressUpdate update);
    Task<ContentDialogResult> ShowScanHostAsync();
    void CloseScanHost();
}

internal sealed partial class FileOperationDialogService
{
    private async Task RunScanDialogAsync<TDialog, TResult>(
        ExplorerWorkspace workspace,
        FileOperationService fileOps,
        TDialog dialog,
        string title,
        Func<TDialog, IProgress<ProgressUpdate>, CancellationToken, Task<TResult>> scanAsync,
        Func<TDialog, TResult, CancellationToken, Task>? afterResultsAsync = null,
        Action<Exception>? showError = null)
        where TDialog : IScanDialog<TResult>
    {
        dialog.ShowConfiguration();
        if (await dialog.ShowScanHostAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!ReferenceEquals(_workspace(), workspace))
        {
            return;
        }

        var utilityCts = _beginUtilityOperation();
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(utilityCts.Token);
        var scanToken = scanCts.Token;
        var progress = new Progress<ProgressUpdate>(update =>
        {
            _dispatchToUi(() =>
            {
                if (ReferenceEquals(_workspace(), workspace) && !scanToken.IsCancellationRequested)
                {
                    dialog.UpdateProgress(update);
                }
            });
        });

        void OnScanCancelled(object? sender, EventArgs args)
        {
            scanCts.Cancel();
        }

        dialog.ScanCancelled += OnScanCancelled;
        try
        {
            dialog.ShowScanning();
            var scanUi = dialog.ShowScanHostAsync();
            var result = await scanAsync(dialog, progress, scanCts.Token);
            if (dialog.ScanWasCancelled
                || !ReferenceEquals(_workspace(), workspace)
                || scanCts.IsCancellationRequested)
            {
                return;
            }

            dialog.ShowResults(result);
            await scanUi;
            if (ReferenceEquals(_workspace(), workspace)
                && !scanCts.IsCancellationRequested
                && afterResultsAsync is not null)
            {
                await afterResultsAsync(dialog, result, scanCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            dialog.CloseScanHost();
        }
        catch (Exception exception)
        {
            dialog.CloseScanHost();
            if (!IsCancellationMessage(exception.Message))
            {
                if (showError is null)
                {
                    _showMessage(title, exception.Message, InfoBarSeverity.Error);
                }
                else
                {
                    showError(exception);
                }
            }
        }
        finally
        {
            dialog.ScanCancelled -= OnScanCancelled;
            _finishUtilityOperation(utilityCts);
        }
    }
}
