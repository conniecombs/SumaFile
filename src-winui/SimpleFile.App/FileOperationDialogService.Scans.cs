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
}

internal sealed partial class FileOperationDialogService
{
    private async Task RunScanDialogAsync<TDialog, TResult>(
        ExplorerWorkspace workspace,
        FileOperationService fileOps,
        TDialog dialog,
        string title,
        Func<TDialog, IProgress<ProgressUpdate>, CancellationToken, Task<TResult>> scanAsync,
        Func<Task> cancelAsync,
        Func<TDialog, TResult, CancellationToken, Task>? afterResultsAsync = null,
        Action<Exception>? showError = null)
        where TDialog : ContentDialog, IScanDialog<TResult>
    {
        dialog.ShowConfiguration();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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

        async void OnScanCancelled(object? sender, EventArgs args)
        {
            scanCts.Cancel();
            await _runUiActionAsync(title, cancelAsync);
        }

        dialog.ScanCancelled += OnScanCancelled;
        try
        {
            dialog.ShowScanning();
            var scanUi = dialog.ShowAsync();
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
            dialog.Hide();
        }
        catch (Exception exception)
        {
            dialog.Hide();
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
