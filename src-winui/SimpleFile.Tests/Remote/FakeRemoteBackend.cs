using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

/// <summary>
/// An in-memory <see cref="IRemoteBackend"/> (and local folder source) for the view model tests; the port of
/// the old <c>RemoteProfileIpc : NullIpc</c> fake.
/// </summary>
internal sealed class FakeRemoteBackend(RemoteProfile[] profiles) : IRemoteBackend, ILocalFolderSource
{
    public string[] LastTransferSources { get; private set; } = [];
    public string? LastConflictAction { get; private set; }
    public string? CancelledOperation { get; private set; }
    public RemoteTransferRequest? LastTransfer { get; private set; }
    public Func<string[], Task<RemoteTransferOutcome>>? TransferHandler { get; set; }

    /// <summary>A transfer handler that sees the transfer's token (it wins over <see cref="TransferHandler"/>).</summary>
    public Func<string[], CancellationToken, Task<RemoteTransferOutcome>>? CancellableTransferHandler { get; set; }

    /// <summary>Runs before each disconnect; throwing models a backend that refuses it (a busy session).</summary>
    public Action<string>? DisconnectHandler { get; set; }

    /// <summary>When each disconnect was attempted (<see cref="System.Diagnostics.Stopwatch"/> timestamps).</summary>
    public List<long> DisconnectAttempts { get; } = [];
    public int ListCalls { get; private set; }
    public string? ConnectedProfileId { get; private set; }
    public string? ConnectedSecret { get; private set; }
    public RemoteProfileInput? SavedProfile { get; private set; }
    public string? SavedSecret { get; private set; }
    public string? DeletedProfileId { get; private set; }
    public RemoteProfileInput? TestedProfile { get; private set; }
    public string? TestSecret { get; private set; }
    public string? DisconnectedSessionId { get; private set; }
    public (string RemoteSessionId, string Path, string Name)? CreatedDirectory { get; private set; }
    public (string RemoteSessionId, string Path, string NewName)? RenamedEntry { get; private set; }
    public (string RemoteSessionId, string[] Paths)? DeletedEntries { get; private set; }
    public (string RemoteSessionId, string RemotePath, string LocalPath)? DownloadedFile { get; private set; }
    public (string RemoteSessionId, string LocalPath, string RemotePath)? UploadedFile { get; private set; }
    public List<string> ListedPaths { get; } = [];
    public List<string> LocalListedPaths { get; } = [];
    public Dictionary<string, RemoteDirectoryListing> ListingsByPath { get; init; } = [];
    public Dictionary<string, RemoteDirectoryListing> LocalListingsByPath { get; init; } = [];
    public RemoteSession Session { get; init; } = new();
    public RemoteDirectoryListing Listing { get; init; } = new();
    public RemoteConnectionTestResult TestResult { get; init; } = new()
    {
        Ok = true,
        Message = "Profile settings are valid.",
        Capabilities = [],
    };
    public Queue<string> ConnectFailures { get; } = new();
    public int ConnectCalls { get; private set; }

    /// <summary>
    /// Set to model a backend that restarted: every call naming a session fails as the real backend does,
    /// with <c>remote session not found: &lt;id&gt;</c>.
    /// </summary>
    public bool SessionsLost { get; set; }

    private void ThrowIfSessionLost(string remoteSessionId)
    {
        if (SessionsLost)
        {
            throw new InvalidOperationException($"remote session not found: {remoteSessionId}");
        }
    }

    public Task<IReadOnlyList<RemoteProfile>> ListProfilesAsync(CancellationToken ct)
    {
        ListCalls++;
        return Task.FromResult<IReadOnlyList<RemoteProfile>>(profiles);
    }

    public Task<RemoteProfile> SaveProfileAsync(RemoteProfileInput profile, string? secret, CancellationToken ct)
    {
        SavedProfile = profile;
        SavedSecret = secret;
        return Task.FromResult(new RemoteProfile
        {
            Id = string.IsNullOrWhiteSpace(profile.Id) ? "prod" : profile.Id,
            Name = profile.Name,
            Protocol = profile.Protocol,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            RootPath = profile.RootPath,
            AuthKind = profile.AuthKind,
            InsecurePlainFtp = profile.InsecurePlainFtp,
            PassiveMode = profile.PassiveMode,
            CredentialTarget = profile.CredentialTarget,
            PrivateKeyPath = profile.PrivateKeyPath,
            TrustedHostFingerprint = profile.TrustedHostFingerprint,
        });
    }

    public Task DeleteProfileAsync(string profileId, CancellationToken ct)
    {
        DeletedProfileId = profileId;
        return Task.CompletedTask;
    }

    public Task<RemoteConnectionTestResult> TestProfileAsync(RemoteProfileInput profile, string? secret, CancellationToken ct)
    {
        TestedProfile = profile;
        TestSecret = secret;
        return Task.FromResult(TestResult);
    }

    public Task<RemoteSession> ConnectAsync(string profileId, string? secret, CancellationToken ct)
    {
        ConnectedProfileId = profileId;
        ConnectedSecret = secret;
        ConnectCalls++;
        if (ConnectFailures.Count > 0)
        {
            throw new InvalidOperationException(ConnectFailures.Dequeue());
        }

        return Task.FromResult(Session);
    }

    public Task DisconnectAsync(string remoteSessionId, CancellationToken ct)
    {
        lock (DisconnectAttempts)
        {
            DisconnectAttempts.Add(System.Diagnostics.Stopwatch.GetTimestamp());
        }

        ThrowIfSessionLost(remoteSessionId);
        DisconnectHandler?.Invoke(remoteSessionId);
        DisconnectedSessionId = remoteSessionId;
        return Task.CompletedTask;
    }

    public Task<RemoteDirectoryListing> ListDirectoryAsync(string remoteSessionId, string path, CancellationToken ct)
    {
        ThrowIfSessionLost(remoteSessionId);
        Assert.Equal(Session.SessionId, remoteSessionId);
        ListedPaths.Add(path);
        if (ListingsByPath.TryGetValue(path, out var listing))
        {
            return Task.FromResult(listing);
        }

        Assert.Equal(string.IsNullOrWhiteSpace(Listing.Path) ? "/" : Listing.Path, path);
        return Task.FromResult(Listing);
    }

    public Task<RemoteFileEntry> CreateDirectoryAsync(string remoteSessionId, string path, string name, CancellationToken ct)
    {
        ThrowIfSessionLost(remoteSessionId);
        CreatedDirectory = (remoteSessionId, path, name);
        return Task.FromResult(new RemoteFileEntry
        {
            Name = name,
            Path = "/" + name,
            IsDir = true,
            Modified = "2026-09-21T00:00:00Z",
        });
    }

    public Task<RemoteFileEntry> RenameEntryAsync(string remoteSessionId, string path, string newName, CancellationToken ct)
    {
        ThrowIfSessionLost(remoteSessionId);
        RenamedEntry = (remoteSessionId, path, newName);
        return Task.FromResult(new RemoteFileEntry
        {
            Name = newName,
            Path = "/" + newName,
            IsDir = true,
            Modified = "2026-09-21T00:00:00Z",
        });
    }

    public Task<IReadOnlyList<string>> DeleteEntriesAsync(string remoteSessionId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        ThrowIfSessionLost(remoteSessionId);
        DeletedEntries = (remoteSessionId, paths.ToArray());
        return Task.FromResult(paths);
    }

    public async Task<RemoteTransferOutcome> TransferAsync(
        RemoteTransferRequest request,
        IProgress<RemoteTransferProgress>? progress,
        IProgress<RemoteTransferTuning>? tuning,
        CancellationToken ct)
    {
        ThrowIfSessionLost(request.RemoteSessionId);
        LastTransfer = request;
        LastTransferSources = request.Sources;
        LastConflictAction = request.ConflictAction;
        // As the real client: a cancel is one message naming the request's operation id, and the outcome is
        // still awaited.
        using var registration = ct.Register(() => CancelledOperation = request.OperationId);
        if (CancellableTransferHandler is not null)
        {
            return await CancellableTransferHandler(request.Sources, ct);
        }

        if (TransferHandler is not null)
        {
            return await TransferHandler(request.Sources);
        }

        var items = new List<RemoteTransferItem>();
        foreach (var source in request.Sources)
        {
            var download = request.Direction == "download";
            var target = download
                ? Path.Combine(request.Destination, source.Split('/').Last())
                : request.Destination.TrimEnd('/') + "/" + Path.GetFileName(source);
            if (download)
            {
                DownloadedFile = (request.RemoteSessionId, source, target);
            }
            else
            {
                UploadedFile = (request.RemoteSessionId, source, target);
                var entry = new RemoteFileEntry
                {
                    Name = Path.GetFileName(source),
                    Path = target,
                    Modified = "2026-09-21T00:00:00Z",
                    Size = 11,
                };
                Listing.Entries = Listing.Entries.Where(old => old.Path != target).Append(entry).ToList();
            }

            items.Add(new RemoteTransferItem { Source = source, Destination = target, Status = "completed" });
        }

        return new RemoteTransferOutcome { Items = items.ToArray() };
    }

    public IDisposable OnProfilesChanged(Action handler) => new NoSubscription();

    public Task<RemoteDirectoryListing> ListAsync(string path, CancellationToken ct)
    {
        LocalListedPaths.Add(path);
        if (LocalListingsByPath.TryGetValue(path, out var listing))
        {
            return Task.FromResult(listing);
        }

        throw new InvalidOperationException($"No local listing configured for {path}.");
    }

    private sealed class NoSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
