using System.Text.Json;
using SimpleFile.PluginSdk;

namespace SimpleFile.Tests.Fixtures.HelloPlugin;

public static class HelloMarkers
{
    public static string Directory =>
        Environment.GetEnvironmentVariable("SUMAFILE_HELLO_MARKER_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Path.GetTempPath(), "sumafile-hello");
}

public sealed class HelloPlugin : IPlugin, ICommandHandler
{
    private IPluginHost? _host;

    public Task ActivateAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        System.IO.Directory.CreateDirectory(HelloMarkers.Directory);
        File.WriteAllText(Path.Combine(HelloMarkers.Directory, "activated.txt"), "activated");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync() => Task.CompletedTask;

    public async Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(HelloMarkers.Directory);
        File.AppendAllText(Path.Combine(HelloMarkers.Directory, "commands.log"), context.CommandId + Environment.NewLine);

        if (context.CommandId == "plugin:com.sumafile.hello:backend-ping" && _host is { } host)
        {
            var result = await host.InvokeBackendAsync<JsonElement>("ping", null, ct);
            host.Logger.Info("ping -> " + result.ToString());
        }
    }
}
