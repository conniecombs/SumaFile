using SimpleFile.PluginSdk;

namespace SumaFile.Remote.Model;

/// <summary>
/// <see cref="IRemoteBackend"/> over <see cref="IPluginHost.InvokeBackendAsync"/>: the method names and param
/// shapes of plugins/remote/protocol.json. Transfer progress and tuning events are filtered by operation id.
/// </summary>
public sealed class RemoteBackendClient(IPluginHost host) : IRemoteBackend
{
    private readonly IPluginHost _host = host ?? throw new ArgumentNullException(nameof(host));

    public async Task<IReadOnlyList<RemoteProfile>> ListProfilesAsync(CancellationToken ct) =>
        await _host.InvokeBackendAsync<RemoteProfile[]>(RemoteMethods.ListProfiles, null, ct).ConfigureAwait(false);

    public Task<RemoteProfile> SaveProfileAsync(RemoteProfileInput profile, string? secret, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _host.InvokeBackendAsync<RemoteProfile>(RemoteMethods.SaveProfile, new ProfileParams(profile, secret), ct);
    }

    public Task DeleteProfileAsync(string profileId, CancellationToken ct) =>
        _host.InvokeBackendAsync<object?>(RemoteMethods.DeleteProfile, new ProfileIdParams(profileId), ct);

    public Task<RemoteConnectionTestResult> TestProfileAsync(RemoteProfileInput profile, string? secret, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _host.InvokeBackendAsync<RemoteConnectionTestResult>(
            RemoteMethods.TestProfile, new ProfileParams(profile, secret), ct);
    }

    public Task<RemoteSession> ConnectAsync(string profileId, string? secret, CancellationToken ct) =>
        _host.InvokeBackendAsync<RemoteSession>(RemoteMethods.Connect, new ConnectParams(profileId, secret), ct);

    public Task DisconnectAsync(string remoteSessionId, CancellationToken ct) =>
        _host.InvokeBackendAsync<object?>(RemoteMethods.Disconnect, new SessionParams(remoteSessionId), ct);

    public Task<RemoteDirectoryListing> ListDirectoryAsync(string remoteSessionId, string path, CancellationToken ct) =>
        _host.InvokeBackendAsync<RemoteDirectoryListing>(
            RemoteMethods.ListDirectory, new PathParams(remoteSessionId, path), ct);

    public Task<RemoteFileEntry> CreateDirectoryAsync(string remoteSessionId, string path, string name, CancellationToken ct) =>
        _host.InvokeBackendAsync<RemoteFileEntry>(
            RemoteMethods.CreateDirectory, new CreateDirectoryParams(remoteSessionId, path, name), ct);

    public Task<RemoteFileEntry> RenameEntryAsync(string remoteSessionId, string path, string newName, CancellationToken ct) =>
        _host.InvokeBackendAsync<RemoteFileEntry>(RemoteMethods.Rename, new RenameParams(remoteSessionId, path, newName), ct);

    public async Task<IReadOnlyList<string>> DeleteEntriesAsync(
        string remoteSessionId,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return await _host.InvokeBackendAsync<string[]>(
            RemoteMethods.DeleteEntries, new DeleteParams(remoteSessionId, paths.ToArray()), ct).ConfigureAwait(false);
    }

    public async Task<RemoteTransferOutcome> TransferAsync(
        RemoteTransferRequest request,
        IProgress<RemoteTransferProgress>? progress,
        IProgress<RemoteTransferTuning>? tuning,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var operationId = request.OperationId;
        using var progressSubscription = progress is null
            ? null
            : _host.OnBackendEvent<RemoteTransferProgress>(RemoteEvents.TransferProgress, update =>
            {
                if (string.Equals(update.OperationId, operationId, StringComparison.Ordinal)) progress.Report(update);
            });
        using var tuningSubscription = tuning is null
            ? null
            : _host.OnBackendEvent<RemoteTransferTuning>(RemoteEvents.TransferTuning, update =>
            {
                if (string.Equals(update.OperationId, operationId, StringComparison.Ordinal)) tuning.Report(update);
            });

        // Send the request before registering the cancel, so the backend knows the operation before its cancel
        // arrives. The call itself is never cancelled: the backend stops, cleans up its partial files and then
        // replies with the outcome (cancelled: true), which is what the caller waits for.
        var transfer = _host.InvokeBackendAsync<RemoteTransferOutcome>(RemoteMethods.Transfer, request, CancellationToken.None);
        using var registration = ct.Register(() => _ = SendCancelAsync(operationId));
        return await transfer.ConfigureAwait(false);
    }

    public IDisposable OnProfilesChanged(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _host.OnBackendEvent<object>(RemoteEvents.ProfilesChanged, _ => handler());
    }

    private async Task SendCancelAsync(string operationId)
    {
        try
        {
            await _host.InvokeBackendAsync<object?>(RemoteMethods.Cancel, new CancelParams(operationId), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Best effort: the transfer's own reply still ends the call (a stopped backend fails it).
            _host.Logger.Warn($"Could not cancel remote transfer {operationId}: {exception.Message}");
        }
    }
}
