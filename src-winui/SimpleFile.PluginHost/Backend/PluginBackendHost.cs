using System.Text.Json.Serialization;
using SimpleFile.Ipc;
using SimpleFile.PluginHost.Discovery;

namespace SimpleFile.PluginHost.Backend;

public sealed class PluginBackendException : Exception
{
    public PluginBackendException(string message) : base(message) { }

    public PluginBackendException(string message, Exception inner) : base(message, inner) { }
}

public sealed record PluginHandshakeResult(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("pluginVersion")] string PluginVersion,
    [property: JsonPropertyName("sdkVersion")] string SdkVersion);

public sealed class PluginBackendHost
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly IPluginProcessLauncher _launcher;
    private readonly Func<string, string> _pipeNameFor;
    private readonly string _dataRoot;
    private readonly Action<string, string> _log;
    private readonly TimeSpan _connectTimeout;

    public PluginBackendHost(
        IPluginProcessLauncher launcher,
        Func<string, string> pipeNameFor,
        string dataRoot,
        Action<string, string> log)
        : this(launcher, pipeNameFor, dataRoot, log, DefaultConnectTimeout)
    {
    }

    internal PluginBackendHost(
        IPluginProcessLauncher launcher,
        Func<string, string> pipeNameFor,
        string dataRoot,
        Action<string, string> log,
        TimeSpan connectTimeout)
    {
        _launcher = launcher;
        _pipeNameFor = pipeNameFor;
        _dataRoot = dataRoot;
        _log = log;
        _connectTimeout = connectTimeout;
    }

    public async Task<PluginBackendConnection> StartAsync(
        DiscoveredPlugin plugin,
        CancellationToken ct,
        Action<PluginBackendConnection>? configure = null)
    {
        var backend = plugin.Manifest?.Backend
            ?? throw new PluginBackendException($"Plugin '{plugin.Id}' declares no backend.");

        var pipeName = _pipeNameFor(plugin.Id);
        var dataDir = Path.Combine(_dataRoot, plugin.Id);
        Directory.CreateDirectory(dataDir);

        var args = new List<string> { "--pipe-name", pipeName, "--data-dir", dataDir };
        args.AddRange(backend.Args);

        IPluginProcess process;
        try
        {
            process = _launcher.Launch(
                Path.Combine(plugin.Folder, backend.Executable),
                args,
                plugin.Folder,
                line => _log(plugin.Id, line));
        }
        catch (Exception ex)
        {
            throw new PluginBackendException($"Could not start the backend of plugin '{plugin.Id}'.", ex);
        }

        NamedPipeJsonClient? client = null;
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(_connectTimeout);
                try
                {
                    client = await NamedPipeJsonClient.ConnectAsync(pipeName, _connectTimeout, connectCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    throw new PluginBackendException(
                        $"Could not connect to the backend of plugin '{plugin.Id}'.", ex);
                }
            }

            // Built before the handshake so host.* handlers registered by `configure` are in place
            // before the backend can send its first request.
            var connection = new PluginBackendConnection(client, process) { PipeName = pipeName };
            configure?.Invoke(connection);

            PluginHandshakeResult handshake;
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshakeCts.CancelAfter(_connectTimeout);
                try
                {
                    handshake = await client.InvokeAsync<PluginHandshakeResult>(
                        "plugin.handshake",
                        new
                        {
                            pluginId = plugin.Id,
                            hostVersion = typeof(PluginBackendHost).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                            sdkVersion = SimpleFile.PluginSdk.PluginSdk.Version,
                        },
                        handshakeCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    throw new PluginBackendException(
                        $"Handshake with the backend of plugin '{plugin.Id}' failed.", ex);
                }
            }

            if (handshake is null || !string.Equals(handshake.PluginId, plugin.Id, StringComparison.Ordinal))
            {
                throw new PluginBackendException(
                    $"Backend of plugin '{plugin.Id}' identified itself as '{handshake?.PluginId}'.");
            }

            if (!SameMajor(handshake.SdkVersion, SimpleFile.PluginSdk.PluginSdk.Version))
            {
                throw new PluginBackendException(
                    $"Backend of plugin '{plugin.Id}' targets SDK {handshake.SdkVersion}, " +
                    $"host provides {SimpleFile.PluginSdk.PluginSdk.Version}.");
            }

            connection.Handshake = handshake;
            if (process.HasExited)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new PluginBackendException($"Backend of plugin '{plugin.Id}' exited during startup.");
            }

            return connection;
        }
        catch
        {
            try { process.Kill(); } catch { }
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            process.Dispose();
            throw;
        }
    }

    private static bool SameMajor(string? a, string? b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) && va.Major == vb.Major;
}
