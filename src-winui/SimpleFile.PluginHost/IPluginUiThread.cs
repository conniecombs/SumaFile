namespace SimpleFile.PluginHost;

/// <summary>
/// Runs plugin code on the UI thread: activation, command execution and the sidebar and settings-page providers
/// (the SDK's thread contract, on <c>IPlugin</c>). The App supplies one over its dispatcher; without one the work runs
/// inline on the calling thread.
/// </summary>
public interface IPluginUiThread
{
    /// <summary>Runs <paramref name="work"/> on the UI thread and completes with its outcome, on any thread.</summary>
    Task<T> RunAsync<T>(Func<Task<T>> work);
}

/// <summary>The default <see cref="IPluginUiThread"/>: runs the work inline on the calling thread.</summary>
internal sealed class InlinePluginUiThread : IPluginUiThread
{
    public static readonly InlinePluginUiThread Instance = new();

    public Task<T> RunAsync<T>(Func<Task<T>> work) => work();
}
