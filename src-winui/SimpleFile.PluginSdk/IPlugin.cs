namespace SimpleFile.PluginSdk;

/// <summary>
/// The entry type that a plugin's <c>ui.entryType</c> names. It may also implement <see cref="ICommandHandler"/>,
/// <see cref="ISidebarSectionProvider"/> and <see cref="ISettingsPageProvider"/>.
/// </summary>
/// <remarks>
/// The thread contract:
/// <list type="bullet">
/// <item><see cref="ActivateAsync"/>, <see cref="ICommandHandler.ExecuteAsync"/>,
/// <see cref="ISidebarSectionProvider.CreateSidebarSection"/> and <see cref="ISettingsPageProvider.CreateSettingsPage"/>
/// run on the UI thread, so they may create WinUI objects. Their awaits resume there unless they use
/// <c>ConfigureAwait(false)</c>.</item>
/// <item><see cref="DeactivateAsync"/> runs on a thread-pool thread.</item>
/// <item>A provider may be asked again for the same section or page after the host dropped the previous element. It
/// must then return a new element, or one that has no parent.</item>
/// <item>When the host disables, removes or shuts down a plugin, it first closes the windows attached through
/// <see cref="IPluginHost.Windows"/>, then stops the backend, then calls <see cref="DeactivateAsync"/>. From the
/// moment the windows start closing, a window attached through that host is closed at once.</item>
/// </list>
/// </remarks>
public interface IPlugin
{
    /// <summary>Runs on the UI thread, once, before the plugin's first command or provider call.</summary>
    Task ActivateAsync(IPluginHost host, CancellationToken ct);

    /// <summary>
    /// Runs on a thread-pool thread, so a plugin that touches UI objects here must marshal to the UI thread itself.
    /// By then the plugin's attached windows are closed and its backend is stopped.
    /// </summary>
    Task DeactivateAsync();
}
