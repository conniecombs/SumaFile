using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost;

/// <summary>
/// Tells the file service which plugin backend serves which archive formats (spec section 6.4). The App
/// implements it over the service connection; <see cref="PluginManager"/> calls it after a backend handshake
/// (retrying a failed attempt), again after a file service restart, and when the backend stops.
/// </summary>
public interface IArchiveEngineRegistrar
{
    /// <param name="authToken">The token the backend was launched with; the service sends it in its handshake.</param>
    Task RegisterAsync(
        string pluginId,
        string pipeName,
        string authToken,
        IReadOnlyList<PluginArchiveFormatSpec> formats,
        CancellationToken ct);

    Task UnregisterAsync(string pluginId, CancellationToken ct);
}
