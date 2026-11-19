using SimpleFile.Core;
using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

/// <summary>
/// A backend that lists folders whole (over a <see cref="FakeExplorerBackend"/>) and reads lazy column values:
/// each request is recorded, answered by <see cref="Answer"/> at once when it is set, or held until the test
/// delivers batches itself and completes it.
/// </summary>
internal sealed class FakeEntryFactsBackend : IExplorerBackend, IEntryFactsBackend
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<int>> _pending = new(StringComparer.Ordinal);

    public FakeExplorerBackend Listings { get; } = new();
    public List<(string Id, string[] Paths, string[] Kinds)> Requests { get; } = [];
    public List<string> Cancelled { get; } = [];
    public List<EntryFact> Put { get; } = [];

    /// <summary>When set, every (path, kind) of a request is answered with it right away; null leaves the pair unsettled.</summary>
    public Func<string, string, EntryFact?>? Answer { get; set; }

    public static EntryFact Ready(string path, string kind, string text, double? sort) =>
        new() { Path = path, Kind = kind, State = EntryFact.Ready, Text = text, Sort = sort };

    public static EntryFact Settled(string path, string kind, string state) =>
        new() { Path = path, Kind = kind, State = state };

    public void AddFolder(string path, bool isNetwork = false, params FileEntry[] entries) =>
        Listings.Listings[path] = new DirectoryListing { Path = path, Entries = [.. entries], IsNetwork = isNetwork };

    public static FileEntry Entry(string folder, string name, bool isDir = false) => new()
    {
        Name = name,
        Path = folder.TrimEnd('\\') + "\\" + name,
        IsDir = isDir,
        Extension = isDir ? "" : System.IO.Path.GetExtension(name).TrimStart('.'),
        Modified = "2026-11-01 10:00",
    };

    /// <summary>Raises a batch for a request the test holds open.</summary>
    public void Deliver(string requestId, params EntryFact[] facts) =>
        EntryFactsReceived?.Invoke(new EntryFactsBatch { RequestId = requestId, Facts = [.. facts] });

    /// <summary>Completes a held request with how many facts settled.</summary>
    public void Complete(string requestId, int settled)
    {
        TaskCompletionSource<int>? pending;
        lock (_lock)
        {
            _pending.Remove(requestId, out pending);
        }

        pending?.TrySetResult(settled);
    }

    public string? CachedHomeDir => Listings.Home;
    public IReadOnlyList<DriveInfo>? CachedDrives => Listings.Drives;
    public IEntryFactsBackend? EntryFacts => this;

    public event Action<EntryFactsBatch>? EntryFactsReceived;

    public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default) => Listings.GetHomeDirAsync(cancellationToken);

    public Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default) => Listings.ListDrivesAsync(cancellationToken);

    public Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null) =>
        Listings.ListDirectoryAsync(path, onChunk, cancellationToken, options);

    public async Task<int> GetEntryFactsAsync(
        string requestId,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> kinds,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Requests.Add((requestId, [.. paths], [.. kinds]));
        }

        if (Answer is { } answer)
        {
            var facts = paths
                .SelectMany(path => kinds.Select(kind => answer(path, kind)))
                .Where(fact => fact is not null)
                .Cast<EntryFact>()
                .ToArray();
            await Task.Yield();
            if (facts.Length > 0)
            {
                Deliver(requestId, facts);
            }

            return facts.Length;
        }

        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pending[requestId] = pending;
        }

        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        return await pending.Task.ConfigureAwait(false);
    }

    public Task CancelEntryFactsAsync(string requestId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Cancelled.Add(requestId);
        }

        Complete(requestId, 0);
        return Task.CompletedTask;
    }

    public Task PutEntryFactsAsync(IReadOnlyList<EntryFact> facts, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Put.AddRange(facts);
        }

        return Task.CompletedTask;
    }
}
