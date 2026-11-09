namespace SimpleFile.PluginSdk;

/// <summary>Runs the plugin's <c>contributions.commands</c>; implemented by the entry type.</summary>
public interface ICommandHandler
{
    /// <summary>Runs on the UI thread (the thread contract is on <see cref="IPlugin"/>).</summary>
    Task ExecuteAsync(PluginCommandContext context, CancellationToken ct);
}
