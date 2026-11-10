namespace SimpleFile.PluginSdk;

/// <summary>
/// The entry type that a plugin's <c>ui.entryType</c> names. It may also implement <see cref="ICommandHandler"/>,
/// <see cref="ISidebarSectionProvider"/>, <see cref="ISettingsPageProvider"/>, <see cref="IPanelProvider"/> and
/// <see cref="IRowDecorationProvider"/>.
/// </summary>
/// <remarks>
/// The thread contract:
/// <list type="bullet">
/// <item><see cref="ActivateAsync"/>, <see cref="ICommandHandler.ExecuteAsync"/>,
/// <see cref="ISidebarSectionProvider.CreateSidebarSection"/>, <see cref="ISettingsPageProvider.CreateSettingsPage"/>
/// and <see cref="IPanelProvider.CreatePanel"/> run on the UI thread, so they may create WinUI objects. Their awaits
/// resume there unless they use <c>ConfigureAwait(false)</c>.</item>
/// <item><see cref="IRowDecorationProvider.GetDecorationsAsync"/> runs on a thread-pool thread, so it must not touch
/// WinUI objects. The plugin is activated on the UI thread before the first call.</item>
/// <item><see cref="DeactivateAsync"/> runs on a thread-pool thread.</item>
/// <item>A provider may be asked again for the same section or page after the host dropped the previous element. It
/// must then return a new element, or one that has no parent.</item>
/// <item><see cref="IPanelProvider.CreatePanel"/> may be asked again, once per presentation: docked
/// (<see cref="PluginPanelRequest.Detached"/> false), then in the panel's window (true). Each call needs a new
/// element.</item>
/// <item>When the host disables, removes or shuts down a plugin, it first closes the windows attached through
/// <see cref="IPluginHost.Windows"/>, then stops the backend, then calls <see cref="DeactivateAsync"/>. From the
/// moment the windows start closing, a window attached through that host is closed at once.</item>
/// </list>
/// </remarks>
public interface IPlugin
{
    /// <summary>Runs on the UI thread, once, before the plugin's first command or provider call.</summary>
    /// <remarks>
    /// Keep it short: the command or provider call that caused the activation waits for it. A plugin stopped while it is
    /// still activating is given 5 s to finish before the host abandons the instance.
    /// </remarks>
    /// <param name="host">The host services, which the plugin keeps for its lifetime.</param>
    /// <param name="ct">
    /// The token of the call that caused the activation; an activation a row decoration request caused gets one that is
    /// never cancelled, so it always runs to its end.
    /// </param>
    /// <returns>A task that completes once the plugin is ready; an exception marks the plugin LoadFailed.</returns>
    Task ActivateAsync(IPluginHost host, CancellationToken ct);

    /// <summary>
    /// Runs on a thread-pool thread, so a plugin that touches UI objects here must marshal to the UI thread itself.
    /// By then the plugin's attached windows are closed and its backend is stopped.
    /// </summary>
    /// <remarks>
    /// The host waits at most 5 s for it. Past that it abandons the instance and records
    /// <c>plugin did not deactivate in time</c>; the plugin's code is never unloaded.
    /// </remarks>
    /// <returns>A task that completes once the plugin has released what it holds.</returns>
    Task DeactivateAsync();
}
