namespace SimpleFile.PluginSdk;

/// <summary>
/// The plugin's rows in SumaFile's Transfers window, reached through <see cref="IPluginHost.Progress"/>. Callable from
/// any thread.
/// </summary>
public interface IPluginProgress
{
    /// <summary>
    /// Adds a row titled <paramref name="title"/> to the Transfers window and returns its handle. The row has a Cancel
    /// button, which cancels <see cref="IPluginOperation.CancellationToken"/>. End it with
    /// <see cref="IPluginOperation.Complete"/> or <see cref="IPluginOperation.Fail"/>, or by disposing the handle.
    /// </summary>
    /// <param name="title">The row's title, such as <c>Uploading report.pdf</c>.</param>
    /// <returns>The operation's handle.</returns>
    IPluginOperation Begin(string title);
}
