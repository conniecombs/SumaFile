namespace SimpleFile.PluginSdk;

public interface IPlugin
{
    Task ActivateAsync(IPluginHost host, CancellationToken ct);
    Task DeactivateAsync();
}
