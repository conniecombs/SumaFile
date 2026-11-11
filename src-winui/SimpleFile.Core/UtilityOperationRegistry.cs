namespace SimpleFile.Core;

/// <summary>
/// Tracks cancellable "utility" operations (Quick Look, Properties, checksums,
/// scans, Settings) by kind. Starting an operation cancels only a previous operation of the same
/// kind, so unrelated actions no longer cancel each other; <see cref="CancelAll"/> is for session
/// teardown.
/// </summary>
public sealed class UtilityOperationRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<CancellationTokenSource, string> _kinds = new();

    public CancellationTokenSource Begin(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _active.TryGetValue(kind, out previous);
            _active[kind] = cts;
            _kinds[cts] = kind;
        }

        if (previous is not null)
        {
            Cancel(previous);
        }

        return cts;
    }

    public void Finish(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (_kinds.Remove(cts, out var kind)
                && _active.TryGetValue(kind, out var current)
                && ReferenceEquals(current, cts))
            {
                _active.Remove(kind);
            }
        }

        cts.Dispose();
    }

    public void CancelAll()
    {
        CancellationTokenSource[] pending;
        lock (_gate)
        {
            pending = _active.Values.ToArray();
            _active.Clear();
        }

        foreach (var cts in pending)
        {
            Cancel(cts);
        }
    }

    public void Dispose()
    {
        CancelAll();
    }

    private static void Cancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished by its owner.
        }
    }
}
