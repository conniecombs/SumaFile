using System.Text.Json.Serialization;
using SimpleFile.PluginSdk;

namespace Example.Plugin;

/// <summary>
/// The entry type <c>plugin.json</c> names (<c>ui.entryType</c>). SumaFile creates it on the first use of one of the
/// plugin's contributions, calls <see cref="ActivateAsync"/> once, then routes the plugin's commands to
/// <see cref="ExecuteAsync"/>.
/// </summary>
public sealed class ExamplePlugin : IPlugin, ICommandHandler
{
    /// <summary>The <c>echo</c> command as SumaFile names it: <c>plugin:&lt;plugin id&gt;:&lt;command id&gt;</c>.</summary>
    public const string EchoCommand = "plugin:com.example.template:echo";

    /// <summary>The declarative setting <c>greeting</c>; Settings &gt; Plugins renders and stores it.</summary>
    public const string GreetingKey = "greeting";

    /// <summary>The setting's <c>default</c> in <c>plugin.json</c>: an unset setting reads as null.</summary>
    public const string DefaultGreeting = "Hello from the example plugin";

    private IPluginHost? _host;

    /// <summary>Runs on the UI thread. Keep the host: it is the plugin's only way into SumaFile.</summary>
    public Task ActivateAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        return Task.CompletedTask;
    }

    /// <summary>Runs on a thread-pool thread, after the backend has stopped.</summary>
    public Task DeactivateAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs on the UI thread. <c>echo</c> reads the <c>greeting</c> setting, sends it to the backend's
    /// <c>example.echo</c> (the first call starts the backend: <c>activateOn: first-call</c>) and shows the reply in
    /// SumaFile's message bar.
    /// </summary>
    public async Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)
    {
        if (context.CommandId != EchoCommand || _host is not { } host)
        {
            return;
        }

        try
        {
            var greeting = await host.Settings.GetAsync(GreetingKey, ct);
            var text = string.IsNullOrEmpty(greeting) ? DefaultGreeting : greeting;
            var reply = await host.InvokeBackendAsync<EchoReply>("example.echo", new EchoRequest(text), ct);
            host.Dialogs.Notify($"The backend echoed \"{reply.Text}\" ({reply.Length} characters).", isError: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A backend error reply (or a backend that did not start) arrives here; plugins.log gets the details.
            host.Logger.Error("example.echo failed", ex);
            host.Dialogs.Notify("The example backend failed: " + ex.Message, isError: true);
        }
    }

    /// <summary>The params of <c>example.echo</c>. Name the JSON properties: the wire format is the backend's.</summary>
    private sealed record EchoRequest([property: JsonPropertyName("text")] string Text);

    /// <summary>The result of <c>example.echo</c>; <c>length</c> counts Unicode scalar values.</summary>
    private sealed record EchoReply(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("length")] int Length);
}
