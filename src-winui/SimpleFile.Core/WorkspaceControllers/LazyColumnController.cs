using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Turns the lazy columns a pane shows into <c>get_entry_facts</c> requests: the rows on screen and a look-ahead
/// behind them for every visible lazy column in Automatic mode, one fact for a clicked cell, or a whole folder
/// for a sort. Values land on each <see cref="FileEntry.Facts"/>; folder size and item count also set
/// <see cref="FileEntry.Size"/> and <see cref="FileEntry.ItemCount"/>.
/// </summary>
internal sealed class LazyColumnController
{
    /// <summary>Paths in one viewport request: the rows on screen and the look-ahead.</summary>
    public const int MaxPathsPerRequest = 64;

    /// <summary>Rows past the last visible one that are read ahead of a scroll.</summary>
    public const int LookAhead = 24;

    /// <summary>Rows a sort by a lazy column fills; above it the pane sorts by what is known.</summary>
    public const int MaxFillEntries = 5000;

    private readonly Dictionary<PaneId, ViewportRequest?> _viewportRequests = new();
    private readonly Dictionary<PaneId, CancellationTokenSource> _fills = new();
    private readonly Dictionary<string, Request> _requests = new(StringComparer.Ordinal);
    private int _sequence;

    private sealed class Request
    {
        public required string Id { get; init; }
        public required ExplorerPane Pane { get; init; }
        public required int NavigationToken { get; init; }
        public required Dictionary<string, FileEntry> Entries { get; init; }
        public required IReadOnlyList<string> Kinds { get; init; }
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed record ViewportRequest(Request Request, int First, int Last);

    /// <summary>The Details list shows rows <paramref name="first"/> to <paramref name="last"/> of <paramref name="pane"/>.</summary>
    public void ReportViewport(ExplorerWorkspace workspace, PaneId pane, int first, int last)
    {
        var backend = workspace.Backend.EntryFacts;
        if (backend is null)
        {
            return;
        }

        Request? replaced;
        Request? request;
        lock (workspace.Gate)
        {
            var id = workspace.Normalize(pane);
            var state = workspace.Pane(id);
            var kinds = AutomaticKinds(workspace, id);
            var wanted = state.PathIsNetwork ? [] : MissingFacts(workspace.VisibleEntriesFor(id), first, last, kinds);
            replaced = _viewportRequests.GetValueOrDefault(id)?.Request;
            if (wanted.Count == 0)
            {
                // Everything on screen is known: the request under way (for an earlier range) may finish.
                return;
            }

            request = Register(state, wanted);
            _viewportRequests[id] = new ViewportRequest(request, first, last);
        }

        if (replaced is not null && !replaced.Cancellation.IsCancellationRequested)
        {
            Cancel(backend, replaced);
        }

        _ = RunAsync(workspace, backend, request);
    }

    /// <summary>Reads one fact for one row (a click on a "…" cell), whatever the column's mode; a settled fact is read again.</summary>
    public void RequestFact(ExplorerWorkspace workspace, PaneId pane, string path, string kind)
    {
        var backend = workspace.Backend.EntryFacts;
        if (backend is null || !LazyColumns.Kinds.Contains(kind, StringComparer.Ordinal))
        {
            return;
        }

        Request request;
        lock (workspace.Gate)
        {
            var state = workspace.Pane(workspace.Normalize(pane));
            var entry = state.Entries.FirstOrDefault(candidate => PathRules.PathsEqual(candidate.Path, path));
            if (entry is null || !LazyColumns.CanHaveValue(kind, entry))
            {
                return;
            }

            Forget(entry, kind);
            request = Register(state, [(entry, kind)]);
        }

        _ = RunAsync(workspace, backend, request);
    }

    /// <summary>
    /// Reads <paramref name="kind"/> for every row of the pane that can have it and has none yet, up to
    /// <see cref="MaxFillEntries"/>, in requests of <see cref="MaxPathsPerRequest"/>, reporting the rows done so far.
    /// </summary>
    public async Task<LazyColumnFill> FillFolderFactsAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        string kind,
        IProgress<LazyColumnFill>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var backend = workspace.Backend.EntryFacts;
        if (backend is null || !LazyColumns.Kinds.Contains(kind, StringComparer.Ordinal))
        {
            return new LazyColumnFill(0, 0, 0);
        }

        List<FileEntry> candidates;
        ExplorerPane state;
        int navigationToken;
        lock (workspace.Gate)
        {
            state = workspace.Pane(workspace.Normalize(pane));
            navigationToken = state.NavigationToken;
            candidates = state.Entries
                .Where(entry => LazyColumns.CanHaveValue(kind, entry) && entry.FactFor(kind) is null)
                .ToList();
        }

        var total = Math.Min(candidates.Count, MaxFillEntries);
        var skipped = candidates.Count - total;
        var done = 0;
        progress?.Report(new LazyColumnFill(total, done, skipped));
        for (var offset = 0; offset < total; offset += MaxPathsPerRequest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.NavigationToken != navigationToken)
            {
                // The pane left the folder: the rest is not read.
                break;
            }

            var chunk = candidates.Skip(offset).Take(Math.Min(MaxPathsPerRequest, total - offset)).ToList();
            Request request;
            lock (workspace.Gate)
            {
                request = Register(state, chunk.Select(entry => (entry, kind)).ToList());
            }

            using var registration = cancellationToken.Register(() => Cancel(backend, request));
            await RunAsync(workspace, backend, request).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            done += chunk.Count;
            progress?.Report(new LazyColumnFill(total, done, skipped));
        }

        return new LazyColumnFill(total, done, skipped);
    }

    /// <summary>
    /// Reads <paramref name="kind"/> for the whole folder of <paramref name="pane"/> in the background (a sort by the
    /// column, or "Calculate for this folder"), reporting progress through <see cref="ExplorerWorkspace.LazyColumnFill"/>.
    /// A fill under way for the pane stops first.
    /// </summary>
    public void StartFill(ExplorerWorkspace workspace, PaneId pane, string kind)
    {
        var id = workspace.Normalize(pane);
        CancelFill(workspace, id);
        var cts = new CancellationTokenSource();
        lock (workspace.Gate)
        {
            _fills[id] = cts;
        }

        _ = RunFillAsync(workspace, id, kind, cts);
    }

    /// <summary>Stops the fill under way for <paramref name="pane"/>, if any; what it read stays.</summary>
    public void CancelFill(ExplorerWorkspace workspace, PaneId pane)
    {
        CancellationTokenSource? previous;
        lock (workspace.Gate)
        {
            _fills.Remove(workspace.Normalize(pane), out previous);
        }

        previous?.Cancel();
    }

    private async Task RunFillAsync(ExplorerWorkspace workspace, PaneId pane, string kind, CancellationTokenSource cts)
    {
        var progress = new FillProgress(workspace, pane, kind);
        LazyColumnFill result;
        try
        {
            result = await FillFolderFactsAsync(workspace, pane, kind, progress, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = progress.Last ?? new LazyColumnFill(0, 0, 0);
        }

        lock (workspace.Gate)
        {
            if (_fills.TryGetValue(pane, out var current) && ReferenceEquals(current, cts))
            {
                _fills.Remove(pane);
            }

            workspace.SetLazyColumnFillCore(new LazyColumnFillStatus(pane, kind, result, Done: true, Cancelled: cts.IsCancellationRequested));
        }

        workspace.NotifyChanged();
        cts.Dispose();
    }

    private sealed class FillProgress(ExplorerWorkspace workspace, PaneId pane, string kind) : IProgress<LazyColumnFill>
    {
        public LazyColumnFill? Last { get; private set; }

        public void Report(LazyColumnFill value)
        {
            Last = value;
            lock (workspace.Gate)
            {
                workspace.SetLazyColumnFillCore(new LazyColumnFillStatus(pane, kind, value, Done: false, Cancelled: false));
            }

            workspace.NotifyChanged();
        }
    }

    /// <summary>A batch of values arrived; they belong to a request this controller issued, or are dropped.</summary>
    public void OnBatch(ExplorerWorkspace workspace, EntryFactsBatch batch)
    {
        var fallbacks = new List<(ExplorerPane Pane, FileEntry Entry, EntryFact Fact)>();
        var changed = false;
        lock (workspace.Gate)
        {
            if (!_requests.TryGetValue(batch.RequestId, out var request)
                || request.NavigationToken != request.Pane.NavigationToken)
            {
                return;
            }

            foreach (var fact in batch.Facts)
            {
                if (!request.Entries.TryGetValue(fact.Path, out var entry))
                {
                    continue;
                }

                Apply(entry, fact);
                changed = true;
                if (fact.State == EntryFact.Unsupported && workspace.FactFallback is not null)
                {
                    fallbacks.Add((request.Pane, entry, fact));
                }
            }

            if (changed)
            {
                request.Pane.BumpContentVersion();
            }
        }

        if (changed)
        {
            workspace.NotifyChanged();
        }

        foreach (var (pane, entry, fact) in fallbacks)
        {
            _ = RunFallbackAsync(workspace, pane, entry, fact.Kind);
        }
    }

    /// <summary>The fact kinds of the pane's visible lazy columns that read as rows come into view.</summary>
    private static List<string> AutomaticKinds(ExplorerWorkspace workspace, PaneId pane) =>
        workspace.ColumnsFor(pane).VisibleColumns
            .Where(column => column.IsLazy && workspace.LazyColumnModeFor(column.FactKind!) == LazyColumns.Automatic)
            .Select(column => column.FactKind!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The (row, kind) pairs in the visible range and the look-ahead that can have a value and have none.</summary>
    private static List<(FileEntry Entry, string Kind)> MissingFacts(
        IReadOnlyList<FileEntry> rows,
        int first,
        int last,
        IReadOnlyList<string> kinds)
    {
        var wanted = new List<(FileEntry, string)>();
        if (kinds.Count == 0 || rows.Count == 0)
        {
            return wanted;
        }

        var start = Math.Clamp(first, 0, rows.Count - 1);
        var end = Math.Clamp(Math.Max(last, start) + LookAhead, start, rows.Count - 1);
        var paths = 0;
        for (var index = start; index <= end && paths < MaxPathsPerRequest; index++)
        {
            var entry = rows[index];
            var added = false;
            foreach (var kind in kinds)
            {
                if (LazyColumns.CanHaveValue(kind, entry) && entry.FactFor(kind) is null)
                {
                    wanted.Add((entry, kind));
                    added = true;
                }
            }

            if (added)
            {
                paths++;
            }
        }

        return wanted;
    }

    private Request Register(ExplorerPane pane, IReadOnlyList<(FileEntry Entry, string Kind)> wanted)
    {
        var entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var kinds = new List<string>();
        foreach (var (entry, kind) in wanted)
        {
            entries.TryAdd(entry.Path, entry);
            if (!kinds.Contains(kind, StringComparer.Ordinal))
            {
                kinds.Add(kind);
            }
        }

        var request = new Request
        {
            Id = $"{pane.ListingViewId}:facts:{Interlocked.Increment(ref _sequence)}",
            Pane = pane,
            NavigationToken = pane.NavigationToken,
            Entries = entries,
            Kinds = kinds,
        };
        _requests[request.Id] = request;
        return request;
    }

    private async Task RunAsync(ExplorerWorkspace workspace, IEntryFactsBackend backend, Request request)
    {
        var paths = request.Entries.Values.Select(entry => entry.Path).ToList();
        var completed = false;
        try
        {
            await backend.GetEntryFactsAsync(request.Id, paths, request.Kinds, request.Cancellation.Token).ConfigureAwait(false);
            completed = !request.Cancellation.IsCancellationRequested;
        }
        catch
        {
            // Cancelled, or the service is gone: the rows keep showing "…" and are asked again when they come into view.
        }

        lock (workspace.Gate)
        {
            _requests.Remove(request.Id);
            foreach (var (pane, viewport) in _viewportRequests.ToList())
            {
                if (ReferenceEquals(viewport?.Request, request))
                {
                    _viewportRequests[pane] = null;
                }
            }

            if (completed && request.NavigationToken == request.Pane.NavigationToken)
            {
                // The request ended without a word about these: they are not asked for again and again.
                foreach (var entry in request.Entries.Values)
                {
                    foreach (var kind in request.Kinds)
                    {
                        if (LazyColumns.CanHaveValue(kind, entry) && entry.FactFor(kind) is null)
                        {
                            Apply(entry, new EntryFact { Path = entry.Path, Kind = kind, State = EntryFact.Failed });
                        }
                    }
                }
            }
        }

        request.Cancellation.Dispose();
    }

    private static void Cancel(IEntryFactsBackend backend, Request request)
    {
        try
        {
            request.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await backend.CancelEntryFactsAsync(request.Id).ConfigureAwait(false);
            }
            catch
            {
                // The request ended on its own.
            }
        });
    }

    private static async Task RunFallbackAsync(ExplorerWorkspace workspace, ExplorerPane pane, FileEntry entry, string kind)
    {
        var fallback = workspace.FactFallback;
        var backend = workspace.Backend.EntryFacts;
        if (fallback is null || backend is null)
        {
            return;
        }

        EntryFact? found;
        try
        {
            found = await fallback(entry.Path, kind, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            found = null;
        }

        if (found is null)
        {
            return;
        }

        lock (workspace.Gate)
        {
            Apply(entry, found);
            // The pane's sorted rows are cached by content version: a sort by this column must see the value.
            pane.BumpContentVersion();
        }

        workspace.NotifyChanged();
        if (found.State == EntryFact.Ready)
        {
            try
            {
                await backend.PutEntryFactsAsync([found]).ConfigureAwait(false);
            }
            catch
            {
                // Not cached: it is read again next time.
            }
        }
    }

    private static void Apply(FileEntry entry, EntryFact fact)
    {
        var facts = entry.Facts is { } existing
            ? new Dictionary<string, EntryFact>(existing, StringComparer.Ordinal)
            : new Dictionary<string, EntryFact>(StringComparer.Ordinal);
        facts[fact.Kind] = fact;
        entry.Facts = facts;
        if (fact.State != EntryFact.Ready || fact.Sort is not { } sort || sort < 0)
        {
            return;
        }

        switch (fact.Kind)
        {
            case LazyColumns.FolderSize:
                entry.Size = (ulong)sort;
                break;
            case LazyColumns.ItemCount:
                entry.ItemCount = (ulong)sort;
                break;
        }
    }

    private static void Forget(FileEntry entry, string kind)
    {
        if (entry.Facts is not { } existing || !existing.ContainsKey(kind))
        {
            return;
        }

        var facts = new Dictionary<string, EntryFact>(existing, StringComparer.Ordinal);
        facts.Remove(kind);
        entry.Facts = facts;
    }
}

/// <summary>Progress of a sort's fill: rows it reads, rows done, and rows over the cap it leaves unknown.</summary>
public sealed record LazyColumnFill(int Total, int Done, int Skipped);

/// <summary>The fill under way, or just finished, for a pane: what it reads and how far it is.</summary>
public sealed record LazyColumnFillStatus(PaneId Pane, string Kind, LazyColumnFill Fill, bool Done, bool Cancelled);
