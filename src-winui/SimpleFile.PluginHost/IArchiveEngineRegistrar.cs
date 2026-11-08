using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost;

/// <summary>
/// Tells the file service which plugin backend serves which archive formats (spec section 6.4). The App
/// implements it over the service connection; <see cref="PluginManager"/> calls it after a backend handshake
/// and again when the backend stops.
/// </summary>
public interface IArchiveEngineRegistrar
{
    Task RegisterAsync(string pluginId, string pipeName, IReadOnlyList<PluginArchiveFormatSpec> formats, CancellationToken ct);

    Task UnregisterAsync(string pluginId, CancellationToken ct);
}
