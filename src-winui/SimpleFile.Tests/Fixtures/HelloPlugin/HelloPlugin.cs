using System.Text.Json;
using SimpleFile.PluginSdk;

namespace SimpleFile.Tests.Fixtures.HelloPlugin;

public static class HelloMarkers
{
    public static string Directory =>
        Environment.GetEnvironmentVariable("SUMAFILE_HELLO_MARKER_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Path.GetTempPath(), "sumafile-hello");

    public static void Write(string name, string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, name), text);
    }

    public static void Append(string name, string line)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.AppendAllText(Path.Combine(Directory, name), line + Environment.NewLine);
    }
}

public sealed class HelloPlugin : IPlugin, ICommandHandler, ISidebarSectionProvider, ISettingsPageProvider
{
    private const string OpenWindowCommand = "plugin:com.sumafile.hello:open-window";
    private const string ShowDialogCommand = "plugin:com.sumafile.hello:show-dialog";

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
            // The UI smoke cannot rely on plugins.log, so record the round trip next to the command marker.
            var greeting = result.TryGetProperty("greeting", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : "";
            File.AppendAllText(Path.Combine(HelloMarkers.Directory, "commands.log"), "ping:" + greeting + Environment.NewLine);
        }
        else if (context.CommandId == OpenWindowCommand && _host is { } windowHost)
        {
            // A code-built Window subclass from this load context, attached to the host (window.txt).
            HelloUi.OpenWindow(windowHost);
        }
        else if (context.CommandId == ShowDialogCommand && _host is { } dialogHost)
        {
            // A plugin-built ContentDialog through the host's dialog gate, on the main window; dialog.txt gets the result.
            var result = await HelloUi.ShowDialogAsync(dialogHost, ct);
            HelloMarkers.Append("dialog.txt", result.ToString());
        }
    }

    /// <summary>Hello's one section, <c>hello</c>: a code-built TextBlock. Writes <c>sidebar.txt</c> with the section id.</summary>
    public object CreateSidebarSection(PluginSidebarSectionRequest request)
    {
        System.IO.Directory.CreateDirectory(HelloMarkers.Directory);
        File.WriteAllText(Path.Combine(HelloMarkers.Directory, "sidebar.txt"), request.SectionId);
        return HelloUi.CreateSidebarSection();
    }

    /// <summary>Hello's settings page (provider <c>hello-page</c>). Writes <c>settings-page.txt</c> with the provider.</summary>
    public object CreateSettingsPage(PluginSettingsPageRequest request)
    {
        HelloMarkers.Write("settings-page.txt", request.Provider);
        return HelloUi.CreateSettingsPage();
    }
}
