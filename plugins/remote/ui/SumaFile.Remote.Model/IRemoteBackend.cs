namespace SumaFile.Remote.Model;

/// <summary>The remote plugin's backend, as its UI sees it. <see cref="RemoteBackendClient"/> is the real one.</summary>
public interface IRemoteBackend
{
    Task<IReadOnlyList<RemoteProfile>> ListProfilesAsync(CancellationToken ct);
    Task<RemoteProfile> SaveProfileAsync(RemoteProfileInput profile, string? secret, CancellationToken ct);
    Task DeleteProfileAsync(string profileId, CancellationToken ct);
    Task<RemoteConnectionTestResult> TestProfileAsync(RemoteProfileInput profile, string? secret, CancellationToken ct);
    Task<RemoteSession> ConnectAsync(string profileId, string? secret, CancellationToken ct);
    Task DisconnectAsync(string remoteSessionId, CancellationToken ct);
    Task<RemoteDirectoryListing> ListDirectoryAsync(string remoteSessionId, string path, CancellationToken ct);
    Task<RemoteFileEntry> CreateDirectoryAsync(string remoteSessionId, string path, string name, CancellationToken ct);
    Task<RemoteFileEntry> RenameEntryAsync(string remoteSessionId, string path, string newName, CancellationToken ct);

    /// <summary>Deletes <paramref name="paths"/> and returns the paths the backend deleted.</summary>
    Task<IReadOnlyList<string>> DeleteEntriesAsync(string remoteSessionId, IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>
    /// Sends <c>transfer.run</c> with <see cref="CancellationToken.None"/>. When <paramref name="ct"/> fires it
    /// sends one <c>cancel {operationId}</c> with the request's id and keeps awaiting the outcome, which then
    /// reports <see cref="RemoteTransferOutcome.Cancelled"/>.
    /// </summary>
    Task<RemoteTransferOutcome> TransferAsync(
        RemoteTransferRequest request,
        IProgress<RemoteTransferProgress>? progress,
        IProgress<RemoteTransferTuning>? tuning,
        CancellationToken ct);

    /// <summary>Calls <paramref name="handler"/> on each <c>remote.profiles-changed</c> until disposed.</summary>
    IDisposable OnProfilesChanged(Action handler);
}

/// <summary>Lists local folders for the manager's local pane.</summary>
public interface ILocalFolderSource
{
    Task<RemoteDirectoryListing> ListAsync(string path, CancellationToken ct);
}
