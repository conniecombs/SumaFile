using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

public sealed class BackendSession : IExplorerBackend, IAsyncDisposable
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
        return _client ?? throw IpcException.Transport("IPC reconnect did not produce a client.");
    }

    private async Task StartUnlockedAsync(CancellationToken cancellationToken)
    {
        ServicePath = ServiceLocator.FindServiceExecutable()
            ?? throw new FileNotFoundException(
                "simplefile-service.exe was not found. Build it with `cargo build -p simplefile-service`, or set SIMPLEFILE_SERVICE_PATH.");

        _authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId);
        PipeName = _generation == 0
            ? $"SumaFile.{sessionId}.{Environment.ProcessId}"
            : $"SumaFile.{sessionId}.{Environment.ProcessId}.{_generation}";

        _job ??= JobObject.TryCreate();

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
        _service.EnableRaisingEvents = true;
        _service.OutputDataReceived += (_, _) => { };
        _service.ErrorDataReceived += OnServiceStderr;
        _service.BeginOutputReadLine();
        _service.BeginErrorReadLine();
        _job?.TryAssign(_service);
        
        _service.StandardInput.WriteLine(_authToken);
        _service.StandardInput.Close();

        try
        {
            _client = await NamedPipeJsonClient.ConnectAsync(
                    PipeName,
                    Protocol.ConnectTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            _client.Disconnected += OnClientDisconnected;
            Handshake = await _client.HandshakeAsync(_authToken, cancellationToken).ConfigureAwait(false);
            Health = await _client.HealthAsync(cancellationToken).ConfigureAwait(false);
            AppVersion = await _client.GetAppVersionAsync(cancellationToken).ConfigureAwait(false);
            HomeDir = await _client.GetHomeDirAsync(cancellationToken).ConfigureAwait(false);
            Drives = await _client.ListDrivesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
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
