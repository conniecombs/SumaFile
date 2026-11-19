using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// A backend that reads lazy column values (folder size and items, dimensions, duration, date taken, camera, pages)
/// for a list of paths and streams them back as they settle.
/// </summary>
public interface IEntryFactsBackend
{
    /// <summary>
    /// Reads <paramref name="kinds"/> for <paramref name="paths"/>, in that order. The values arrive through
    /// <see cref="EntryFactsReceived"/> with <paramref name="requestId"/>; the task completes with how many settled.
    /// </summary>
    Task<int> GetEntryFactsAsync(
        string requestId,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> kinds,
        CancellationToken cancellationToken = default);

    Task CancelEntryFactsAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>Keeps values the app found itself (the Windows property system's duration) for next time.</summary>
    Task PutEntryFactsAsync(IReadOnlyList<EntryFact> facts, CancellationToken cancellationToken = default);

    /// <summary>A batch of values for one request. Raised on any thread.</summary>
    event Action<EntryFactsBatch>? EntryFactsReceived;
}
