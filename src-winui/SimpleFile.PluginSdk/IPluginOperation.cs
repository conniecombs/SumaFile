namespace SimpleFile.PluginSdk;

/// <summary>
/// One row in the Transfers window, from <see cref="IPluginProgress.Begin"/>. Callable from any thread. After the first
/// <see cref="Complete"/> or <see cref="Fail"/>, later calls do nothing; <see cref="IDisposable.Dispose"/> completes a
/// row that has not ended.
/// </summary>
public interface IPluginOperation : IDisposable
{
    /// <summary>
    /// Cancelled when the user presses the row's Cancel button. Honour it, then call <see cref="Complete"/> or
    /// <see cref="Fail"/>: the row reads Cancelling until the plugin ends the operation, or until 30 s pass, and only
    /// then Cancelled.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Updates the row's progress. Ignored once the operation has ended.</summary>
    /// <param name="current">The work done so far, such as bytes or items.</param>
    /// <param name="total">The whole work, in the same unit as <paramref name="current"/>.</param>
    /// <param name="detail">Optional text under the title, such as the current file; null for none.</param>
    void Report(long current, long total, string? detail);

    /// <summary>Ends the operation as Completed (or Cancelled after a cancel).</summary>
    void Complete();

    /// <summary>Ends the operation as Failed.</summary>
    /// <param name="message">What went wrong, shown on the row.</param>
    void Fail(string message);
}
