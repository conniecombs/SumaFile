using SimpleFile.Ipc;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.App;

/// <summary>
/// Registers plugin archive engines with SumaFile's file service (spec section 6.4). The service connects to
/// the plugin's pipe itself, so registration fails when the backend is not serving.
/// </summary>
internal sealed class ArchiveEngineRegistrar(Func<ISimpleFileIpc?> client) : IArchiveEngineRegistrar
{
    public Task RegisterAsync(
        string pluginId,
        string pipeName,
        IReadOnlyList<PluginArchiveFormatSpec> formats,
        CancellationToken ct) =>
        // Shape of RegisterArchiveEngineParams in crates/simplefile-service/src/dispatch/params.rs.
        Client().InvokeAsync(
            Protocol.RegisterArchiveEngineMethod,
            new
            {
                pluginId,
                pipeName,
                formats = formats.Select(format => new
                {
                    extension = format.Extension,
                    canList = format.CanList,
                    canExtract = format.CanExtract,
                    canCreate = format.CanCreate,
                    canModify = format.CanModify,
                    engineName = format.EngineName,
                }).ToArray(),
            },
            ct);

    public Task UnregisterAsync(string pluginId, CancellationToken ct) =>
        Client().InvokeAsync(Protocol.UnregisterArchiveEngineMethod, new { pluginId }, ct);

    private ISimpleFileIpc Client() =>
        client() ?? throw new InvalidOperationException("SumaFile's file service is not connected.");
}
