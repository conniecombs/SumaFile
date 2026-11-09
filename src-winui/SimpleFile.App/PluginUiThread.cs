using Microsoft.UI.Dispatching;
using SimpleFile.PluginHost;

namespace SimpleFile.App;

/// <summary>
/// The App's UI thread for plugin code (the SDK's thread contract): activation, commands and the providers run on
/// the main window's dispatcher. Inline when the caller is already on it, otherwise queued, with the outcome handed
/// back to the caller's thread. Shares <see cref="PluginUiDispatch.RunAsync"/> with <see cref="PluginDialogs"/>.
/// </summary>
internal sealed class PluginUiThread(DispatcherQueue dispatcher) : IPluginUiThread
{
    public Task<T> RunAsync<T>(Func<Task<T>> work) => PluginUiDispatch.RunAsync(dispatcher, work);
}
