namespace SimpleFile.PluginSdk;

public interface IPlugin
{
    Task ActivateAsync(IPluginHost host, CancellationToken ct);
    /// <summary>Runs on a thread-pool thread, so a plugin that touches UI objects here must marshal to the UI thread itself.</summary>
    Task DeactivateAsync();
}
