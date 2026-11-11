namespace SimpleFile.PluginSdk;

/// <summary>Runs the plugin's <c>contributions.commands</c>; implemented by the entry type.</summary>
public interface ICommandHandler
{
    /// <summary>
    /// Runs one of the plugin's commands, from a menu, the toolbar, a shortcut or the command palette. Runs on the UI
    /// thread (the thread contract is on <see cref="IPlugin"/>), after the plugin is activated.
    /// </summary>
    /// <param name="context">The command and the active folder and selection when it ran.</param>
    /// <param name="ct">Cancelled when SumaFile shuts down its plugins.</param>
    /// <returns>A task the host awaits. An exception other than a cancellation is shown in SumaFile's message bar.</returns>
    Task ExecuteAsync(PluginCommandContext context, CancellationToken ct);
}
