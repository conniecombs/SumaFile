namespace SimpleFile.PluginSdk;

public interface IPluginSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}
