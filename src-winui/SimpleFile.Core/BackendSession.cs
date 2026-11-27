using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

public sealed class BackendSession : IExplorerBackend, IListingViewBackend, IEntryFactsBackend, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _service;
    private NamedPipeJsonClient? _client;
    private JobObject? _job;
    private string _authToken = "";
    private int _generation;
    private bool _started;
    private bool _disposed;

    public string PipeName { get; private set; } = "";
    public HandshakeResult? Handshake { get; private set; }
    public HealthResult? Health { get; private set; }
    public string? AppVersion { get; private set; }
    public string? HomeDir { get; private set; }
    public IReadOnlyList<DriveInfo>? Drives { get; private set; }
    public string? ServicePath { get; private set; }
    public int ReconnectCount { get; private set; }

    /// <summary>The running service's process id, or null before a start and after a stop. The UI smoke kills it.</summary>
    public int? ServiceProcessId => _service?.Id;

    public string? CachedHomeDir => HomeDir;
    public IReadOnlyList<DriveInfo>? CachedDrives => Drives;
    public ISimpleFileIpc? Client => _client;

    public event EventHandler<Exception?>? Disconnected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true })
            {
                return;
            }

            await StartUnlockedAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopUnlockedAsync(sendShutdown: false).ConfigureAwait(false);
            _generation++;
            ReconnectCount++;
            await StartUnlockedAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task<HealthResult> HealthAsync(CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.HealthAsync(cancellationToken), cancellationToken);
    }

    public Task<string> GetAppVersionAsync(CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.GetAppVersionAsync(cancellationToken), cancellationToken);
    }

    public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.GetHomeDirAsync(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.ListDrivesAsync(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<DriveInfo>> ListDrivesLightAsync(CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.ListDrivesLightAsync(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<DriveInfo>> ListDriveAsync(string path, CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.ListDriveAsync(path, cancellationToken), cancellationToken);
    }

    public Task<DirectoryListing> ListDirectoryAsync(
        string path,
        Action<DirectoryListingChunk>? onChunk = null,
        CancellationToken cancellationToken = default,
        ListDirectoryOptions? options = null)
    {
        return UseClientAsync(
            client => client.ListDirectoryAsync(path, onChunk, cancellationToken, options),
            cancellationToken);
    }

    public IListingViewBackend? ListingViews => this;

    public event Action<ListingViewDelta>? ListingViewChanged;

    public Task<ListingViewPage> OpenListingViewAsync(
        ListingViewRequest request,
        Action<DirectoryListingChunk>? onPreview = null,
        CancellationToken cancellationToken = default)
    {
        return UseClientAsync(
            client => client.OpenListingViewAsync(request, onPreview, cancellationToken),
            cancellationToken);
    }

    public Task<ListingViewPage> GetListingViewRangeAsync(
        string viewId,
        uint offset,
        uint count,
        CancellationToken cancellationToken = default)
    {
        return UseClientAsync(
            client => client.GetListingViewRangeAsync(viewId, offset, count, cancellationToken),
            cancellationToken);
    }

    public Task<ListingViewDelta> RefreshListingViewAsync(string viewId, CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.RefreshListingViewAsync(viewId, cancellationToken), cancellationToken);
    }

    public Task CloseListingViewAsync(string viewId, CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.CloseListingViewAsync(viewId, cancellationToken), cancellationToken);
    }

    public IEntryFactsBackend? EntryFacts => this;

    public event Action<EntryFactsBatch>? EntryFactsReceived;

    public async Task<int> GetEntryFactsAsync(
        string requestId,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> kinds,
        CancellationToken cancellationToken = default)
    {
        var result = await UseClientAsync(
                client => client.GetEntryFactsAsync(requestId, [.. paths], [.. kinds], cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        return (int)Math.Min(result.Settled, int.MaxValue);
    }

    public Task CancelEntryFactsAsync(string requestId, CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.CancelEntryFactsAsync(requestId, cancellationToken), cancellationToken);
    }

    public Task PutEntryFactsAsync(IReadOnlyList<EntryFact> facts, CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.PutEntryFactsAsync([.. facts], cancellationToken), cancellationToken);
    }

    public Task SelectDirectoryAsync(string? defaultPath = null, CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.SelectDirectoryAsync(defaultPath, cancellationToken), cancellationToken);
    }

    public Task ShowMainWindowAsync(CancellationToken cancellationToken = default)
    {
        return UseClientAsync(client => client.ShowMainWindowAsync(cancellationToken), cancellationToken);
    }

    internal void KillServiceForTests()
    {
        if (_service is { HasExited: false })
        {
            try
            {
                _service.Kill(entireProcessTree: false);
                _service.WaitForExit(2000);
            }
            catch
            {
            }
        }
    }

    private async Task<T> UseClientAsync<T>(
        Func<NamedPipeJsonClient, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var client = await RequireClientAsync(cancellationToken).ConfigureAwait(false);
        return await action(client).ConfigureAwait(false);
    }

    private async Task UseClientAsync(Func<NamedPipeJsonClient, Task> action, CancellationToken cancellationToken)
    {
        await UseClientAsync(
                async client =>
                {
                    await action(client).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<NamedPipeJsonClient> RequireClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is { IsConnected: true })
        {
            return _client;
        }

        if (!_started)
        {
            throw new InvalidOperationException("BackendSession has not been started.");
        }

        await ReconnectAsync(cancellationToken).ConfigureAwait(false);
        return _client ?? throw IpcException.Transport("Reconnecting to SumaFile's file service did not produce a client.");
    }

    private async Task StartUnlockedAsync(CancellationToken cancellationToken)
    {
        var timer = new StartupTimer("BackendSession.Start");
        timer.Mark("begin");
        ServicePath = ServiceLocator.FindServiceExecutable()
            ?? throw new FileNotFoundException(
                "simplefile-service.exe was not found. Build it with `cargo build -p simplefile-service`, or set SIMPLEFILE_SERVICE_PATH.");
        timer.Mark("service-located", ServicePath);

        _authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId);
        PipeName = _generation == 0
            ? $"SumaFile.{sessionId}.{Environment.ProcessId}"
            : $"SumaFile.{sessionId}.{Environment.ProcessId}.{_generation}";

        // Job-object attach is required so the service dies with the host. Silent
        // fallback to PID polling risks orphaned services if the PID is reused.
        _job ??= JobObject.Create();

        var start = new ProcessStartInfo
        {
            FileName = ServicePath,
            Arguments = $"--pipe-name {PipeName} --parent-pid {Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        _service = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start simplefile-service.");
        timer.Mark("process-started", $"pid={_service.Id}");
        _service.EnableRaisingEvents = true;
        _service.OutputDataReceived += (_, _) => { };
        _service.ErrorDataReceived += OnServiceStderr;
        _service.BeginOutputReadLine();
        _service.BeginErrorReadLine();
        try
        {
            _job!.Assign(_service);
        }
        catch
        {
            try
            {
                if (!_service.HasExited)
                {
                    _service.Kill(entireProcessTree: false);
                    _service.WaitForExit(1500);
                }
            }
            catch
            {
                // Best-effort: do not mask the job-object failure.
            }

            _service.Dispose();
            _service = null;
            throw;
        }

        _service.StandardInput.WriteLine(_authToken);
        _service.StandardInput.Close();
        timer.Mark("auth-token-written");

        try
        {
            _client = await NamedPipeJsonClient.ConnectAsync(
                    PipeName,
                    Protocol.ConnectTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            timer.Mark("pipe-connected", PipeName);
            _client.Disconnected += OnClientDisconnected;
            // A restarted service has no views: the subscription belongs to this client, and the workspace opens
            // its views again when a page or refresh of the old ones fails.
            _ = _client.On<ListingViewDelta>(Protocol.ListingViewChangedEvent, delta => ListingViewChanged?.Invoke(delta));
            _ = _client.On<EntryFactsBatch>(Protocol.EntryFactsEvent, batch => EntryFactsReceived?.Invoke(batch));
            Handshake = await _client.HandshakeAsync(_authToken, cancellationToken).ConfigureAwait(false);
            timer.Mark("handshake", $"methods={Handshake.MethodCount}");
            // A service that writes another binary frame version than this app reads would end the connection on
            // its first binary frame, usually a folder listing, with nothing but "IPC pipe closed" to say why.
            if (Handshake.BinaryHotFrames && Handshake.BinaryFrameVersion != Protocol.BinaryFrameVersion)
            {
                throw new InvalidOperationException(
                    $"The file service writes binary frame version {Handshake.BinaryFrameVersion}, and this app reads "
                    + $"version {Protocol.BinaryFrameVersion}. The service is from another build of SumaFile.");
            }
            Health = await _client.HealthAsync(cancellationToken).ConfigureAwait(false);
            timer.Mark("health");
            AppVersion = await _client.GetAppVersionAsync(cancellationToken).ConfigureAwait(false);
            timer.Mark("app-version", AppVersion);
            HomeDir = await _client.GetHomeDirAsync(cancellationToken).ConfigureAwait(false);
            timer.Mark("home-dir", HomeDir);
            Drives = await _client.ListDrivesLightAsync(cancellationToken).ConfigureAwait(false);
            timer.Mark("drives-light", $"count={Drives.Count}");
            timer.Mark("ready");
        }
        catch
        {
            timer.Mark("failed");
            await StopUnlockedAsync(sendShutdown: false).ConfigureAwait(false);
            throw;
        }
    }

    private void OnClientDisconnected(object? sender, Exception? error)
    {
        Disconnected?.Invoke(this, error);
    }

    private async Task StopUnlockedAsync(bool sendShutdown)
    {
        if (_client is not null)
        {
            _client.Disconnected -= OnClientDisconnected;
            if (sendShutdown && _client.IsConnected)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _client.ShutdownAsync(timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    // The process may already be gone.
                }
            }

            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        if (_service is { HasExited: false })
        {
            try
            {
                if (!_service.WaitForExit(1500))
                {
                    _service.Kill(entireProcessTree: false);
                    _service.WaitForExit(1500);
                }
            }
            catch
            {
                // Best-effort teardown.
            }
        }

        _service?.Dispose();
        _service = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopUnlockedAsync(sendShutdown: true).ConfigureAwait(false);
            _job?.Dispose();
            _job = null;
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    private static void OnServiceStderr(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
        {
            return;
        }

        try
        {
            var logPath = ServiceLogPath();
            RotateLogIfNeeded(logPath);
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {e.Data}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort: stderr logging must never crash the host.
        }
    }

    private static string ServiceLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SumaFile");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "service.log");
    }

    private static void RotateLogIfNeeded(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length < 1_048_576) // 1 MB
            {
                return;
            }

            var backup = logPath + ".1";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(logPath, backup);
        }
        catch
        {
            // Best-effort rotation.
        }
    }
}
