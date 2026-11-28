namespace SimpleFile.Core;

/// <summary>
/// Runs one request at a time, and of the requests that wait, only the newest. For something that shows the
/// answer to the latest question only, asked of a service that finishes every request it is sent: an older
/// question still waiting is not worth asking.
/// </summary>
public sealed class LatestRequest
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private int _newest;

    /// <summary>
    /// Runs <paramref name="request"/> once no earlier one is running. Null when a newer call arrived while
    /// this one waited: it was not run. Cancelling <paramref name="ct"/> ends the wait; a request that has
    /// started runs to its end.
    /// </summary>
    public async Task<T?> RunAsync<T>(Func<Task<T>> request, CancellationToken ct = default)
        where T : class
    {
        var ticket = Interlocked.Increment(ref _newest);
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ticket != Volatile.Read(ref _newest))
            {
                return null;
            }

            return await request().ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }
}
