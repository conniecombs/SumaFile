using SimpleFile.Core;
using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

/// <summary>
/// A backend with listing views, for workspace tests: it keeps each view sorted and filtered (with the same rules
/// the workspace applies itself), serves pages, and reports a refresh as a delta. Folders come from
/// <see cref="Folders"/>; a test changes one and calls <see cref="RefreshListingViewAsync"/> or raises a delta.
/// </summary>
internal sealed class FakeListingViewBackend : IExplorerBackend, IListingViewBackend
{
    private readonly object _lock = new();
    private readonly Dictionary<string, View> _views = new(StringComparer.Ordinal);
    private ulong _revision;

    public string Home { get; set; } = @"C:\work";
    public Dictionary<string, List<FileEntry>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ListingViewRequest> OpenRequests { get; } = [];
    public List<(string ViewId, uint Offset, uint Count)> RangeRequests { get; } = [];
    public int ListDirectoryCalls { get; private set; }
    public int RefreshCalls { get; private set; }
    public int EnumerationCount { get; private set; }

    /// <summary>When set, the next refresh fails with this message (the view is gone).</summary>
    public string? RefreshFailure { get; set; }

    /// <summary>When set, page requests wait for it: a test changes the view while a fill is under way.</summary>
    public TaskCompletionSource? RangeGate { get; set; }

    /// <summary>Preview chunks an open sends before its page.</summary>
    public List<DirectoryListingChunk> PreviewChunks { get; } = [];

    /// <summary>When set, an open waits for it before it answers.</summary>
    public TaskCompletionSource? OpenGate { get; set; }

    public IListingViewBackend? ListingViews => this;

    public event Action<ListingViewDelta>? ListingViewChanged;

    public static FileEntry Entry(string folder, string name, bool isDir = false, ulong size = 0, bool hidden = false) => new()
    {
        Name = name,
        Path = folder.TrimEnd('\\') + "\\" + name,
        IsDir = isDir,
        IsHidden = hidden,
        Size = size,
        Extension = isDir ? "" : System.IO.Path.GetExtension(name).TrimStart('.'),
        Modified = "2026-11-01 10:00",
    };

    public void AddFolder(string path, params FileEntry[] entries) => Folders[path] = [.. entries];

    /// <summary>The view's current revision.</summary>
    public ulong RevisionOf(string viewId)
    {
        lock (_lock)
        {
            return _views[viewId].Revision;
        }
    }

    public void Raise(ListingViewDelta delta) => ListingViewChanged?.Invoke(delta);

    /// <summary>
    /// Changes a folder and the views of it the way the service does for a watched folder: returns the deltas it
    /// would send (already raised when <paramref name="raise"/> is true).
    /// </summary>
    public IReadOnlyList<ListingViewDelta> ChangeFolder(string path, Action<List<FileEntry>> change, bool raise = true)
    {
        List<ListingViewDelta> deltas = [];
        lock (_lock)
        {
            change(Folders[path]);
            foreach (var view in _views.Values.Where(view => PathRules.PathsEqual(view.Path, path)))
            {
                deltas.Add(Rebuild(view));
            }
        }

        if (raise)
        {
            foreach (var delta in deltas)
            {
                Raise(delta);
            }
        }

        return deltas;
    }

    public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default) => Task.FromResult(Home);

    public Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DriveInfo>>([]);

    public Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null)
    {
        lock (_lock)
        {
            ListDirectoryCalls++;
            if (!Folders.TryGetValue(path, out var entries))
            {
                throw new IpcException(Protocol.ErrApplication, $"Path is not a directory: {path}");
            }

            return Task.FromResult(new DirectoryListing { Path = path, Entries = [.. entries] });
        }
    }

    public async Task<ListingViewPage> OpenListingViewAsync(
        ListingViewRequest request,
        Action<DirectoryListingChunk>? onPreview = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            OpenRequests.Add(request);
        }

        foreach (var chunk in PreviewChunks)
        {
            onPreview?.Invoke(chunk);
        }

        if (OpenGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            if (!Folders.TryGetValue(request.Path, out var entries))
            {
                throw new IpcException(Protocol.ErrApplication, $"Path is not a directory: {request.Path}");
            }

            var shown = _views.TryGetValue(request.ViewId, out var existing) && PathRules.PathsEqual(existing.Path, request.Path);
            if (request.Reuse && !shown)
            {
                throw new IpcException(Protocol.ErrApplication, $"Listing view does not show {request.Path}: {request.ViewId}");
            }

            if (!request.Reload && shown && existing is not null)
            {
                if (!existing.Request.SameViewAs(request))
                {
                    existing.Request = request;
                    existing.Rows = Order(existing.Raw, request);
                    existing.Revision = ++_revision;
                }

                return Page(existing, 0, request.PageSize);
            }

            EnumerationCount++;
            var view = new View
            {
                ViewId = request.ViewId,
                Path = request.Path,
                Request = request,
                Raw = [.. entries],
                Revision = ++_revision,
            };
            view.Rows = Order(view.Raw, request);
            _views[request.ViewId] = view;
            return Page(view, 0, request.PageSize);
        }
    }

    public async Task<ListingViewPage> GetListingViewRangeAsync(
        string viewId,
        uint offset,
        uint count,
        CancellationToken cancellationToken = default)
    {
        if (RangeGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            RangeRequests.Add((viewId, offset, count));
            return _views.TryGetValue(viewId, out var view)
                ? Page(view, offset, count)
                : throw new IpcException(Protocol.ErrApplication, $"Listing view is not open: {viewId}");
        }
    }

    public Task<ListingViewDelta> RefreshListingViewAsync(string viewId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            RefreshCalls++;
            if (RefreshFailure is { } failure)
            {
                _views.Remove(viewId);
                throw new IpcException(Protocol.ErrApplication, failure);
            }

            if (!_views.TryGetValue(viewId, out var view))
            {
                throw new IpcException(Protocol.ErrApplication, $"Listing view is not open: {viewId}");
            }

            EnumerationCount++;
            return Task.FromResult(Rebuild(view));
        }
    }

    public Task CloseListingViewAsync(string viewId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _views.Remove(viewId);
        }

        return Task.CompletedTask;
    }

    // Reads the folder again and expresses the difference as removes then inserts, the way the service lists them.
    private ListingViewDelta Rebuild(View view)
    {
        var before = view.Rows;
        view.Raw = [.. Folders[view.Path]];
        var after = Order(view.Raw, view.Request);
        var changes = new List<ListingViewChange>();
        var working = new List<FileEntry>(before);
        for (var index = working.Count - 1; index >= 0; index--)
        {
            if (!after.Any(entry => ReferenceEquals(entry, working[index])))
            {
                changes.Add(new ListingViewChange { Kind = ListingViewChange.Remove, Index = (uint)index });
                working.RemoveAt(index);
            }
        }

        for (var index = 0; index < after.Count; index++)
        {
            if (index >= working.Count || !ReferenceEquals(working[index], after[index]))
            {
                changes.Add(new ListingViewChange { Kind = ListingViewChange.Insert, Index = (uint)index, Entry = after[index] });
                working.Insert(index, after[index]);
            }
        }

        var baseRevision = view.Revision;
        view.Rows = after;
        if (changes.Count > 0)
        {
            view.Revision = ++_revision;
        }

        return new ListingViewDelta
        {
            ViewId = view.ViewId,
            BaseRevision = baseRevision,
            Revision = view.Revision,
            Total = (uint)after.Count,
            Changes = changes,
        };
    }

    private static List<FileEntry> Order(IEnumerable<FileEntry> raw, ListingViewRequest request) =>
        [.. EntryPresentation.VisibleEntries(
            raw,
            request.Filter,
            request.IncludeHidden,
            request.SortBy,
            request.SortAscending,
            request.FoldersFirst)];

    private static ListingViewPage Page(View view, uint offset, uint count)
    {
        var start = (int)Math.Min(offset, (uint)view.Rows.Count);
        var rows = count == 0 ? ListingViewController.FirstPageRows : count;
        return new ListingViewPage
        {
            ViewId = view.ViewId,
            Path = view.Path,
            Total = (uint)view.Rows.Count,
            Revision = view.Revision,
            Offset = (uint)start,
            Entries = [.. view.Rows.Skip(start).Take((int)rows)],
        };
    }

    private sealed class View
    {
        public required string ViewId { get; init; }
        public required string Path { get; init; }
        public required ListingViewRequest Request { get; set; }
        public required List<FileEntry> Raw { get; set; }
        public List<FileEntry> Rows { get; set; } = [];
        public ulong Revision { get; set; }
    }
}
