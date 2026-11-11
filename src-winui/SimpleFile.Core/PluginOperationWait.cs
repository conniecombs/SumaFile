namespace SimpleFile.Core;

/// <summary>
/// How a plugin's Transfers row (a <see cref="TransferManagerViewModel.EnqueueExternal"/> row fed by
/// <c>host.progress_*</c>) waits for the plugin, so Cancel never reports Cancelled while the plugin is still writing.
/// </summary>
public static class PluginOperationWait
{
    /// <summary>
    /// The runner of a plugin's Transfers row. Before a cancel: Completed when <paramref name="finished"/> completes,
    /// its exception when it fails. After <paramref name="cancellation"/> fires the row stays Cancelling until
    /// <paramref name="finished"/> ends (completed or failed) or <paramref name="cancelGrace"/> passes, then reports
    /// Cancelled.
    /// </summary>
    public static async Task<TransferOperationStatus> WaitAsync(Task finished, CancellationToken cancellation, TimeSpan cancelGrace)
    {
        ArgumentNullException.ThrowIfNull(finished);
        if (finished.IsCompleted)
        {
            // Ended before the runner started waiting: its own outcome, Completed or its failure.
            await finished.ConfigureAwait(false);
            return TransferOperationStatus.Completed;
        }

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellation.Register(() => cancelled.TrySetResult()))
        {
            await Task.WhenAny(finished, cancelled.Task).ConfigureAwait(false);
        }

        if (!cancellation.IsCancellationRequested)
        {
            // Ended without a cancel: Completed, or its failure.
            await finished.ConfigureAwait(false);
            return TransferOperationStatus.Completed;
        }

        // Cancelled: the plugin was asked to stop. Wait for its answer, bounded by the grace, then report Cancelled
        // whatever the answer was (a cancelled transfer usually ends as a failure such as "Transfer cancelled").
        using (var graceTimer = new CancellationTokenSource())
        {
            var grace = Task.Delay(cancelGrace, graceTimer.Token);
            await Task.WhenAny(finished, grace).ConfigureAwait(false);
            graceTimer.Cancel();
        }

        // Observe a late failure so it never surfaces as an unobserved task exception.
        _ = finished.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return TransferOperationStatus.Cancelled;
    }
}
