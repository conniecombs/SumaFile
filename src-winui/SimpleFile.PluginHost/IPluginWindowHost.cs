using SimpleFile.PluginSdk;

namespace SimpleFile.PluginHost;

/// <summary>
/// One plugin runtime's windows on the host side: <see cref="IPluginWindows.Attach"/> for the plugin, and
/// <see cref="CloseAllAsync"/> for the manager, which awaits it (bounded by
/// <see cref="PluginManager.WindowCloseTimeout"/>) before it stops the backend and deactivates the plugin.
/// </summary>
/// <remarks>
/// A host serves one runtime. The manager asks <see cref="PluginManagerOptions.WindowsFor"/> for a new host whenever
/// it creates a runtime, and calls <see cref="CloseAllAsync"/> only on a runtime it has already retired, so a sealed
/// host is never handed to a re-enabled plugin. <see cref="PluginWindowSet{TWindow}"/> holds the bookkeeping and the
/// seal.
/// </remarks>
public interface IPluginWindowHost : IPluginWindows
{
    /// <summary>
    /// Seals the host, then closes every window attached so far; completes once they are closed.
    /// </summary>
    /// <remarks>
    /// The seal takes effect when this is called, before the returned task completes, and lasts for good: from then
    /// on <see cref="IPluginWindows.Attach"/> closes the window it is handed at once and tracks nothing. A plugin that
    /// opens a window while it is being stopped (an error window after its backend died, say) therefore cannot leave
    /// one open past <see cref="IPlugin.DeactivateAsync"/>. A window attached concurrently with this call is either
    /// closed with the rest or closed at once by <c>Attach</c>, never left open.
    /// </remarks>
    Task CloseAllAsync();
}

/// <summary>The default <see cref="IPluginWindowHost"/> when the App supplies none: it ignores <c>Attach</c>.</summary>
internal sealed class NullPluginWindowHost : IPluginWindowHost
{
    public static readonly NullPluginWindowHost Instance = new();

    public void Attach(object window, PluginWindowOptions options)
    {
    }

    public Task CloseAllAsync() => Task.CompletedTask;
}
