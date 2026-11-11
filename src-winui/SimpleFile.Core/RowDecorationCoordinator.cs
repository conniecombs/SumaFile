using System.Text.RegularExpressions;

namespace SimpleFile.Core;

/// <summary>
/// Asks the decorating plugins about a folder once per folder sync and waits at most <see cref="DefaultBudget"/> for
/// them, so a slow provider never holds a listing back. Results that arrive after the budget are merged with the rest
/// and handed to the caller's <c>onLate</c> callback once: when every plugin settled, or at <see cref="DefaultLateLimit"/>
/// with the plugins that still had not settled left pending, so a provider that ignores its token and never answers
/// cannot hold the other plugins' late answers back.
/// </summary>
/// <remarks>
/// <para>
/// Every call into the source starts on the thread pool (<see cref="Task.Run(Func{Task})"/>), never inline on the
/// caller's thread. A provider's first activation (its assembly load and <c>ActivateAsync</c>) is therefore queued to
/// the UI thread by the plugin manager and never nests inside a host layout pass or a workspace sync (carry-over #82).
/// </para>
/// <para>
/// Nothing is asked before <see cref="MarkStartupReady"/>, without a source, or for network and Recycle Bin folders.
/// <see cref="Generation"/> changes when startup becomes ready and when the plugin set changes, so a listing whose
/// decorations were asked before either can be decorated again once it is published.
/// </para>
/// </remarks>
public sealed partial class RowDecorationCoordinator
{
    /// <summary>The time a listing waits for decorations before it is published without the missing ones.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long, from the request, the late merge waits for the plugins the budget cut. A plugin still unsettled then
    /// is logged and stays pending in that merge (the caller keeps its shown part); its later answer is dropped, and the
    /// next request for the folder asks it again.
    /// </summary>
    public static readonly TimeSpan DefaultLateLimit = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _budget;
    private readonly TimeSpan _lateLimit;
    private readonly object _lock = new();
    private IRowDecorationSource? _source;
    private string[] _pluginIds = [];
    private int _generation;
    private bool _startupReady;
    private DateTimeOffset? _firstRequestAt;

    public RowDecorationCoordinator(TimeSpan? budget = null, TimeSpan? lateLimit = null)
    {
        var value = budget ?? DefaultBudget;
        _budget = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var late = lateLimit ?? DefaultLateLimit;
        _lateLimit = late < _budget ? _budget : late;
    }

    /// <summary>Receives <c>[warn]</c> lines for failed sources. It may be called from any thread.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>True when a source with at least one plugin id is set.</summary>
    public bool HasProviders
    {
        get
        {
            lock (_lock)
            {
                return _source is not null && _pluginIds.Length > 0;
            }
        }
    }

    /// <summary>True once <see cref="MarkStartupReady"/> ran.</summary>
    public bool IsStartupReady
    {
        get
        {
            lock (_lock)
            {
                return _startupReady;
            }
        }
    }

    /// <summary>Bumped by <see cref="MarkStartupReady"/> and by a <see cref="SetSource"/> that changed the plugin set.</summary>
    public int Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    /// <summary>When the first request reached the source (startup evidence for the smokes), or null.</summary>
    public DateTimeOffset? FirstRequestAt
    {
        get
        {
            lock (_lock)
            {
                return _firstRequestAt;
            }
        }
    }

    /// <summary>
    /// Replaces the source. Returns true, and bumps <see cref="Generation"/>, when the decorating plugin ids (in
    /// precedence order) changed.
    /// </summary>
    public bool SetSource(IRowDecorationSource? source)
    {
        var ids = source?.PluginIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray()
            ?? [];
        lock (_lock)
        {
            var changed = !ids.SequenceEqual(_pluginIds, StringComparer.Ordinal);
            _source = source;
            _pluginIds = ids;
            if (changed)
            {
                _generation++;
            }

            return changed;
        }
    }

    /// <summary>Opens the startup gate: until then <see cref="DecorateAsync"/> returns Empty and asks nothing.</summary>
    public void MarkStartupReady()
    {
        lock (_lock)
        {
            _startupReady = true;
            _generation++;
        }
    }

    /// <summary>
    /// Decorates the rows <paramref name="paths"/> of <paramref name="folderPath"/>. Returns the merge of what answered
    /// within the budget; when the rest settle later, <paramref name="onLate"/> gets the merge of all, once, even when
    /// none of them had anything (at the late limit, with the ones still unsettled pending). A source that throws or
    /// returns null is skipped and logged; one cancelled through <paramref name="ct"/> is skipped silently. A request
    /// cancelled before its result is handed back gets an incomplete result, in which every plugin without an answer is
    /// pending (never a complete one, which would drop the caller's shown part), and no late merge.
    /// </summary>
    public async Task<PaneDecorations> DecorateAsync(
        string folderPath,
        bool isNetwork,
        IReadOnlyList<string> paths,
        Action<PaneDecorations> onLate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(onLate);
        IRowDecorationSource? source;
        string[] pluginIds;
        lock (_lock)
        {
            if (!_startupReady
                || _source is null
                || _pluginIds.Length == 0
                || isNetwork
                || string.IsNullOrWhiteSpace(folderPath)
                || PathRules.IsRecycleBinPath(folderPath))
            {
                return PaneDecorations.Empty;
            }

            source = _source;
            pluginIds = _pluginIds;
            _firstRequestAt ??= DateTimeOffset.UtcNow;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var requested = paths.ToArray();
        var calls = new Task<FolderDecorationBatch?>[pluginIds.Length];
        for (var index = 0; index < pluginIds.Length; index++)
        {
            var pluginId = pluginIds[index];
            var call = Task.Run(() => source.DecorateAsync(pluginId, folderPath, requested, ct));
            calls[index] = SettleAsync(pluginId, call, ct);
        }

        var all = Task.WhenAll(calls);
        if (!all.IsCompleted)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(all, Task.Delay(_budget, budget.Token)).ConfigureAwait(false);
            await budget.CancelAsync().ConfigureAwait(false);
        }

        // A snapshot: a call may settle between these lines, and it must count as pending in the result handed back.
        var settled = calls.Select(call => call.IsCompleted).ToArray();
        var answered = calls.Where((_, index) => settled[index]).Select(call => call.Result).OfType<FolderDecorationBatch>().ToList();
        if (ct.IsCancellationRequested)
        {
            // A cancelled call settles as "nothing", which is not an answer: every plugin without a batch is pending, so
            // the caller keeps its shown part until the newer request (the one that cancelled this) decides.
            var unanswered = pluginIds.Where((_, index) => !settled[index] || calls[index].Result is null).ToArray();
            return PaneDecorations.Merge(folderPath, pluginIds, answered, unanswered);
        }

        var pendingIds = pluginIds.Where((_, index) => !settled[index]).ToArray();
        if (pendingIds.Length == 0)
        {
            return PaneDecorations.Merge(folderPath, pluginIds, answered, pendingPlugins: null);
        }

        var lateWait = _lateLimit - System.Diagnostics.Stopwatch.GetElapsedTime(started);
        _ = DeliverLateAsync(folderPath, pluginIds, calls, all, lateWait, onLate, ct);
        return PaneDecorations.Merge(folderPath, pluginIds, answered, pendingIds);
    }

    // The final merge is delivered whenever the request was not cancelled, even when every late plugin answered
    // nothing: a caller that kept older decorations while those plugins were pending must learn they have none now.
    // It waits until every plugin settled or the late limit passed, whichever comes first; at the limit the plugins
    // still unsettled stay pending in the merge, so the caller keeps their shown part, and a source that ignores its
    // token can no longer hold the others' answers back for good.
    private async Task DeliverLateAsync(
        string folderPath,
        string[] pluginIds,
        Task<FolderDecorationBatch?>[] calls,
        Task<FolderDecorationBatch?[]> all,
        TimeSpan lateWait,
        Action<PaneDecorations> onLate,
        CancellationToken ct)
    {
        if (!all.IsCompleted)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = lateWait > TimeSpan.Zero ? lateWait : TimeSpan.Zero;
            await Task.WhenAny(all, Task.Delay(delay, limit.Token)).ConfigureAwait(false);
            await limit.CancelAsync().ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        // A snapshot, as in DecorateAsync: a call settling between these lines counts as pending.
        var settled = calls.Select(call => call.IsCompleted).ToArray();
        var batches = calls.Where((_, index) => settled[index]).Select(call => call.Result).OfType<FolderDecorationBatch>();
        var unsettled = pluginIds.Where((_, index) => !settled[index]).ToArray();
        foreach (var pluginId in unsettled)
        {
            WriteLog($"[warn] Row decorations from {pluginId} did not settle within {_lateLimit.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s; its shown decorations stay until the folder is decorated again.");
        }

        var merged = PaneDecorations.Merge(folderPath, pluginIds, batches, unsettled);
        try
        {
            onLate(merged);
        }
        catch (Exception exception)
        {
            WriteLog($"[warn] Applying late row decorations failed: {exception.Message}");
        }
    }

    // Never faults: a failed source gives null and one log line; a cancelled one gives null silently.
    private async Task<FolderDecorationBatch?> SettleAsync(string pluginId, Task<FolderDecorationBatch?> call, CancellationToken ct)
    {
        try
        {
            var batch = await call.ConfigureAwait(false);
            if (batch is null)
            {
                if (!ct.IsCancellationRequested)
                {
                    WriteLog($"[warn] Row decorations from {pluginId} failed: the source returned no result.");
                }

                return null;
            }

            return Sanitize(pluginId, batch);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            WriteLog($"[warn] Row decorations from {pluginId} failed: {exception.Message}");
            return null;
        }
    }

    // Tags the batch with the plugin it was asked for and keeps only that plugin's well-formed folder flags.
    private static FolderDecorationBatch Sanitize(string pluginId, FolderDecorationBatch batch)
    {
        var prefix = pluginId + ":";
        var flags = (batch.FolderFlags ?? [])
            .Where(flag => flag is not null
                && flag.StartsWith(prefix, StringComparison.Ordinal)
                && FlagName().IsMatch(flag[prefix.Length..]))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var rows = batch.Rows ?? new Dictionary<string, RowDecorationEntry>();
        return new FolderDecorationBatch(pluginId, rows, flags);
    }

    private void WriteLog(string line)
    {
        try
        {
            Log?.Invoke(line);
        }
        catch
        {
            // Logging is best effort.
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FlagName();
}
