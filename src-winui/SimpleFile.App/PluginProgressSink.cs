using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using SimpleFile.Core;
using SimpleFile.PluginHost;

namespace SimpleFile.App;

/// <summary>
/// Shows a plugin's long-running work as a row in the Transfers window and the Status Center.
/// Plugins call it from any thread; every view-model change runs on the UI thread in call order.
/// </summary>
internal sealed class PluginProgressSink : IPluginProgressSink
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<TransferManagerViewModel?> _transfers;
    private readonly Func<string, string> _pluginName;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>How long a cancelled row waits for the plugin to stop before it reads Cancelled anyway.</summary>
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(30);

    /// <param name="transfers">Read when the row is created, since the transfer manager exists only after workspace start.</param>
    public PluginProgressSink(
        DispatcherQueue dispatcher,
        Func<TransferManagerViewModel?> transfers,
        Func<string, string> pluginName)
    {
        _dispatcher = dispatcher;
        _transfers = transfers;
        _pluginName = pluginName;
    }

    public string Begin(string pluginId, string title, Action cancel)
    {
        var id = $"plugin-{Guid.NewGuid():n}";
        var entry = new Entry(cancel);
        _entries[id] = entry;
        var detail = _pluginName(pluginId);
        var rowTitle = string.IsNullOrWhiteSpace(title) ? detail : title;
        if (!_dispatcher.TryEnqueue(() => CreateRow(id, entry, rowTitle, detail)))
        {
            _entries.TryRemove(id, out _);
        }

        return id;
    }

    public void Report(string operationId, long current, long total, string? detail)
    {
        if (!_entries.TryGetValue(operationId, out var entry))
        {
            return;
        }

        entry.SetLatest(current, total, detail);
        // Coalesce: a plugin may report far faster than the UI can draw, so only one update waits at a time.
        if (Interlocked.Exchange(ref entry.ReportQueued, 1) == 0
            && !_dispatcher.TryEnqueue(() => ApplyLatest(entry)))
        {
            Volatile.Write(ref entry.ReportQueued, 0);
        }
    }

    public void Complete(string operationId)
    {
        if (_entries.TryGetValue(operationId, out var entry))
        {
            entry.Finished.TrySetResult();
        }
    }

    public void Fail(string operationId, string message)
    {
        if (_entries.TryGetValue(operationId, out var entry))
        {
            entry.Finished.TrySetException(new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "The plugin operation failed." : message));
        }
    }

    private void CreateRow(string id, Entry entry, string title, string detail)
    {
        if (_transfers() is not { } transfers)
        {
            _entries.TryRemove(id, out _);
            return;
        }

        // Runs until the plugin completes or fails the operation. Cancel cancels the token and asks the plugin to
        // stop; the row then stays Cancelling until the plugin has stopped (or CancelGrace passes), then reads Cancelled.
        var operation = transfers.EnqueueExternal(title, detail, async (_, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(entry.RequestCancel);
            return await PluginOperationWait.WaitAsync(entry.Finished.Task, cancellationToken, CancelGrace);
        });
        operation.SetOperationId(id);
        entry.Operation = operation;
        ApplyLatest(entry);
        _ = operation.CompletionTask.ContinueWith(
            completed => _entries.TryRemove(id, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ApplyLatest(Entry entry)
    {
        Volatile.Write(ref entry.ReportQueued, 0);
        if (entry.Operation is { } operation && entry.TryGetLatest(out var current, out var total, out var detail))
        {
            operation.ApplyExternalProgress(current, total, detail);
        }
    }

    private sealed class Entry(Action cancel)
    {
        private readonly object _gate = new();
        private bool _hasLatest;
        private long _current;
        private long _total;
        private string? _detail;
        private int _cancelRequested;

        public int ReportQueued;

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set on the UI thread once the row exists.</summary>
        public TransferOperationViewModel? Operation { get; set; }

        public void SetLatest(long current, long total, string? detail)
        {
            lock (_gate)
            {
                _hasLatest = true;
                _current = current;
                _total = total;
                _detail = detail;
            }
        }

        public bool TryGetLatest(out long current, out long total, out string? detail)
        {
            lock (_gate)
            {
                current = _current;
                total = _total;
                detail = _detail;
                return _hasLatest;
            }
        }

        public void RequestCancel()
        {
            if (Interlocked.Exchange(ref _cancelRequested, 1) != 0)
            {
                return;
            }

            // Off the UI thread: the plugin's cancel may talk to its backend.
            _ = Task.Run(() =>
            {
                try
                {
                    cancel();
                }
                catch
                {
                    // The plugin's cancel callback must never take the window down.
                }
            });
        }
    }
}
