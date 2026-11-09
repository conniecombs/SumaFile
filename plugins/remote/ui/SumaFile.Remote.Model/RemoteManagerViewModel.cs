using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SumaFile.Remote.Model;

/// <summary>
/// The FTP/SFTP manager's state: profiles, the connected session, the local and remote listings and the
/// transfer list. It talks to the plugin's backend through <see cref="IRemoteBackend"/> and lists local folders
/// through <see cref="ILocalFolderSource"/>.
/// </summary>
public sealed class RemoteManagerViewModel : ObservableObject
{
    /// <summary>The start of the backend's error for a session it no longer has (it restarted, or the session ended).</summary>
    public const string SessionNotFoundPrefix = "remote session not found";

    /// <summary>The status after a call found the session gone.</summary>
    public const string ConnectionLostText = "The connection was lost. Connect again.";

    private readonly IRemoteBackend _backend;
    private readonly ILocalFolderSource _localFolders;
    private RemoteProfile? _selectedProfile;
    private RemoteSession? _currentSession;
    private bool _isBusy;
    private string _statusText = "No remote profiles";
    private string? _requestedProfileId;
    private string _localPath = "";
    private string? _localParentPath;
    private string _remotePath = "/";
    private CancellationTokenSource? _transferCancellation;
    private string[] _retrySources = [];
    private string _retryDirection = "";
    private string _retryDestination = "";
    private string _retryProfileId = "";
    private string _transferProgressText = "";
    private string _transferTuningText = "";
    public string TransferTuningText { get => _transferTuningText; private set => SetProperty(ref _transferTuningText, value); }
    public string ConflictAction { get; set; } = "error";
    public bool CanCancelTransfer => _transferCancellation is not null && !_transferCancellation.IsCancellationRequested;
    public bool CanRetryTransfers => !IsBusy && CurrentSession is not null
        && CurrentSession.ProfileId == _retryProfileId && _retrySources.Length > 0;
    public string TransferProgressText { get => _transferProgressText; private set => SetProperty(ref _transferProgressText, value); }

    public void CancelTransfer()
    {
        _transferCancellation?.Cancel();
        OnPropertyChanged(nameof(CanCancelTransfer));
        if (_transferCancellation is not null) TransferProgressText = "Cancelling and cleaning up partial files...";
    }

    public Task RetryTransfersAsync(CancellationToken ct = default) => CanRetryTransfers
        ? RunTransfersAsync(_retryDirection, _retrySources, _retryDestination, ct)
        : Task.CompletedTask;

    public RemoteManagerViewModel(IRemoteBackend backend, ILocalFolderSource localFolders)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _localFolders = localFolders ?? throw new ArgumentNullException(nameof(localFolders));
    }

    public ObservableCollection<RemoteProfile> Profiles { get; } = [];

    public ObservableCollection<RemoteFileEntry> LocalEntries { get; } = [];

    public ObservableCollection<RemoteFileEntry> RemoteEntries { get; } = [];

    public ObservableCollection<RemoteTransferRecord> Transfers { get; } = [];

    public RemoteProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                OnPropertyChanged(nameof(SelectedProfileSummary));
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDeleteProfile));
                OnPropertyChanged(nameof(CanEditProfile));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanCreateProfile));
                OnPropertyChanged(nameof(CanDisconnect));
                OnPropertyChanged(nameof(CanDeleteProfile));
                OnPropertyChanged(nameof(CanEditProfile));
                OnPropertyChanged(nameof(CanMutateRemoteEntries));
                OnPropertyChanged(nameof(CanUseWorkspaceTransfers));
                OnPropertyChanged(nameof(CanRetryTransfers));
            }
        }
    }

    public RemoteSession? CurrentSession
    {
        get => _currentSession;
        private set
        {
            if (SetProperty(ref _currentSession, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanCreateProfile));
                OnPropertyChanged(nameof(CanDisconnect));
                OnPropertyChanged(nameof(CanDeleteProfile));
                OnPropertyChanged(nameof(CanEditProfile));
                OnPropertyChanged(nameof(CanMutateRemoteEntries));
                OnPropertyChanged(nameof(CanUseWorkspaceTransfers));
                OnPropertyChanged(nameof(CanRetryTransfers));
            }
        }
    }

    public string LocalPath
    {
        get => _localPath;
        private set
        {
            if (SetProperty(ref _localPath, value))
            {
                OnPropertyChanged(nameof(CanUseWorkspaceTransfers));
                OnPropertyChanged(nameof(CanRetryTransfers));
            }
        }
    }

    public string RemotePath
    {
        get => _remotePath;
        private set => SetProperty(ref _remotePath, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SelectedProfileSummary => SelectedProfile is null
        ? "No profile selected"
        : FormatProfileSummary(SelectedProfile);

    public bool IsConnected => CurrentSession is not null;

    public bool CanConnect => SelectedProfile is not null && !IsBusy && !IsConnected;

    public bool CanDisconnect => IsConnected && !IsBusy;

    public bool CanCreateProfile => !IsBusy && !IsConnected;

    public bool CanDeleteProfile => SelectedProfile is not null && !IsBusy && !IsConnected;

    public bool CanEditProfile => SelectedProfile is not null && !IsBusy && !IsConnected;

    public bool CanMutateRemoteEntries => IsConnected && !IsBusy;

    public bool CanUseWorkspaceTransfers => CanMutateRemoteEntries && !string.IsNullOrWhiteSpace(LocalPath);

    public async Task LoadProfilesAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var selectedId = string.IsNullOrWhiteSpace(_requestedProfileId)
                ? SelectedProfile?.Id
                : _requestedProfileId;
            var profiles = await _backend.ListProfilesAsync(ct);

            Profiles.Clear();
            foreach (var profile in profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
            {
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == selectedId)
                ?? Profiles.FirstOrDefault();
            if (SelectedProfile is not null && StringComparer.Ordinal.Equals(SelectedProfile.Id, _requestedProfileId))
            {
                _requestedProfileId = null;
            }

            StatusText = FormatProfileCount(Profiles.Count);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RequestProfileSelection(string? profileId)
    {
        _requestedProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim();
        if (_requestedProfileId is null)
        {
            return;
        }

        var profile = Profiles.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, _requestedProfileId));
        if (profile is not null)
        {
            SelectedProfile = profile;
            _requestedProfileId = null;
        }
    }

    /// <summary>True for a plain-FTP profile whose unencrypted connection the user confirmed.</summary>
    public static bool IsInsecureProfile(string? protocol, bool insecurePlainFtp) =>
        insecurePlainFtp && string.Equals(protocol?.Trim(), "ftp", StringComparison.OrdinalIgnoreCase);

    public static bool IsInsecureProfile(RemoteProfile? profile) =>
        profile is not null && IsInsecureProfile(profile.Protocol, profile.InsecurePlainFtp);

    public const string UntrustedFingerprintPrefix = "SFTP host fingerprint is not trusted";
    public const string FingerprintMismatchPrefix = "SFTP host fingerprint mismatch";

    /// <summary>
    /// Connects the selected profile. Host-key trust failures are returned as an outcome
    /// (so the window can ask the user) instead of thrown; every other failure still throws.
    /// </summary>
    public async Task<ConnectOutcome> ConnectSelectedProfileAsync(string? secret = null, CancellationToken ct = default)
    {
        if (SelectedProfile is null)
        {
            StatusText = "Select a remote profile";
            return new ConnectOutcome(false, null, null, null, StatusText);
        }

        IsBusy = true;
        try
        {
            var profileName = SelectedProfile.Name;
            CurrentSession = await _backend.ConnectAsync(SelectedProfile.Id, secret, ct);
            RemotePath = NormalizeRemotePath(CurrentSession.RootPath);
            await RefreshRemoteDirectoryCoreAsync(RemotePath, ct);
            StatusText = $"Connected to {profileName}";
            return ConnectOutcome.Success;
        }
        catch (Exception exception) when (IsSessionLost(exception))
        {
            DropLostSession();
            return new ConnectOutcome(false, null, null, null, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && ParseConnectError(exception.Message) is { IsHostKeyProblem: true } trustOutcome)
        {
            StatusText = trustOutcome.UntrustedFingerprint is not null
                ? "The server's key is not trusted yet"
                : "The server's key changed";
            return trustOutcome;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves <paramref name="fingerprint"/> as the selected profile's trusted host key and connects again.
    /// </summary>
    public async Task<ConnectOutcome> TrustFingerprintAndConnectAsync(string fingerprint, CancellationToken ct = default)
    {
        if (SelectedProfile is null)
        {
            StatusText = "Select a remote profile";
            return new ConnectOutcome(false, null, null, null, StatusText);
        }

        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("A fingerprint is required.", nameof(fingerprint));
        }

        var profile = SelectedProfile;
        await SaveProfileAsync(new RemoteProfileInput
        {
            Id = profile.Id,
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
            TrustedHostFingerprint = fingerprint.Trim(),
        }, secret: null, ct);
        return await ConnectSelectedProfileAsync(null, ct);
    }

    /// <summary>
    /// Reads the stable SFTP host-key prefixes the backend emits. The returned outcome's
    /// fingerprint fields are null when the message is not a host-key trust failure.
    /// </summary>
    public static ConnectOutcome ParseConnectError(string? message)
    {
        var text = message ?? "";
        var untrusted = text.IndexOf(UntrustedFingerprintPrefix, StringComparison.Ordinal);
        if (untrusted >= 0)
        {
            var tail = text[untrusted..];
            var separator = tail.LastIndexOf(": ", StringComparison.Ordinal);
            var fingerprint = separator >= 0 ? tail[(separator + 2)..].Trim() : "";
            return new ConnectOutcome(false, fingerprint.Length == 0 ? null : fingerprint, null, null, text);
        }

        var mismatch = text.IndexOf(FingerprintMismatchPrefix, StringComparison.Ordinal);
        if (mismatch >= 0)
        {
            const string expectedMarker = "Expected ";
            const string gotMarker = ", got ";
            var tail = text[mismatch..];
            var expectedStart = tail.LastIndexOf(expectedMarker, StringComparison.Ordinal);
            if (expectedStart >= 0)
            {
                var pair = tail[(expectedStart + expectedMarker.Length)..];
                var gotIndex = pair.IndexOf(gotMarker, StringComparison.Ordinal);
                if (gotIndex >= 0)
                {
                    var expected = pair[..gotIndex].Trim();
                    var actual = pair[(gotIndex + gotMarker.Length)..].Trim();
                    if (expected.Length > 0 && actual.Length > 0)
                    {
                        return new ConnectOutcome(false, null, expected, actual, text);
                    }
                }
            }
        }

        return new ConnectOutcome(false, null, null, null, text);
    }

    public async Task SaveProfileAsync(
        RemoteProfileInput profile,
        string? secret = null,
        CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var saved = await _backend.SaveProfileAsync(profile, secret, ct);
            var existing = Profiles.FirstOrDefault(current => StringComparer.Ordinal.Equals(current.Id, saved.Id));
            if (existing is not null)
            {
                Profiles.Remove(existing);
            }

            Profiles.Add(saved);
            SortProfiles();
            SelectedProfile = Profiles.FirstOrDefault(current => StringComparer.Ordinal.Equals(current.Id, saved.Id));
            StatusText = $"Saved {saved.Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteSelectedProfileAsync(CancellationToken ct = default)
    {
        if (SelectedProfile is null)
        {
            StatusText = "Select a remote profile";
            return;
        }

        IsBusy = true;
        try
        {
            var deleted = SelectedProfile;
            await _backend.DeleteProfileAsync(deleted.Id, ct);
            Profiles.Remove(deleted);
            SelectedProfile = Profiles.FirstOrDefault();
            StatusText = $"Deleted {deleted.Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<RemoteConnectionTestResult> TestProfileAsync(
        RemoteProfileInput profile,
        string? secret = null,
        CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var result = await _backend.TestProfileAsync(profile, secret, ct);
            StatusText = string.IsNullOrWhiteSpace(result.Message)
                ? (result.Ok ? "Profile test passed" : "Profile test failed")
                : result.Message;
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshRemoteDirectoryAsync(CancellationToken ct = default)
    {
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshRemoteDirectoryCoreAsync(RemotePath, ct);
        }
        catch (Exception exception) when (IsSessionLost(exception))
        {
            DropLostSession();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task NavigateLocalPathAsync(string path, CancellationToken ct = default)
    {
        var trimmed = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            StatusText = "Enter a local folder";
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshLocalDirectoryCoreAsync(trimmed, ct);
            StatusText = $"Local path: {LocalPath}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshLocalDirectoryAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            StatusText = "Enter a local folder";
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshLocalDirectoryCoreAsync(LocalPath, ct);
            StatusText = $"Local path: {LocalPath}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task NavigateLocalEntryAsync(RemoteFileEntry? entry, CancellationToken ct = default)
    {
        if (entry is null)
        {
            StatusText = "Select a local folder";
            return;
        }

        if (!entry.IsDir)
        {
            StatusText = entry.Name;
            return;
        }

        await NavigateLocalPathAsync(entry.Path, ct);
    }

    public async Task GoToParentLocalDirectoryAsync(CancellationToken ct = default)
    {
        var parent = _localParentPath;
        if (string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(LocalPath))
        {
            parent = Directory.GetParent(LocalPath)?.FullName;
        }

        if (string.IsNullOrWhiteSpace(parent))
        {
            StatusText = "No local parent folder";
            return;
        }

        await NavigateLocalPathAsync(parent, ct);
    }

    public async Task NavigateRemoteEntryAsync(RemoteFileEntry? entry, CancellationToken ct = default)
    {
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        if (entry is null)
        {
            StatusText = "Select a remote folder";
            return;
        }

        if (!entry.IsDir)
        {
            StatusText = entry.Name;
            return;
        }

        await NavigateRemotePathAsync(entry.Path, ct);
    }

    public async Task NavigateRemotePathAsync(string path, CancellationToken ct = default)
    {
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshRemoteDirectoryCoreAsync(path, ct);
            StatusText = $"Remote path: {RemotePath}";
        }
        catch (Exception exception) when (IsSessionLost(exception))
        {
            DropLostSession();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task GoToParentRemoteDirectoryAsync(CancellationToken ct = default)
    {
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        await NavigateRemotePathAsync(ParentRemotePath(RemotePath), ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (CurrentSession is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _backend.DisconnectAsync(CurrentSession.SessionId, ct);
            CurrentSession = null;
            RemoteEntries.Clear();
            RemotePath = "/";
            StatusText = FormatProfileCount(Profiles.Count);
        }
        catch (Exception exception) when (IsSessionLost(exception))
        {
            DropLostSession();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CreateRemoteDirectoryAsync(string name, CancellationToken ct = default)
    {
        var trimmedName = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            StatusText = "Enter a remote folder name";
            return;
        }

        IsBusy = true;
        try
        {
            var created = await _backend.CreateDirectoryAsync(
                CurrentSession.SessionId,
                RemotePath,
                trimmedName,
                ct);
            ReplaceRemoteEntries(RemoteEntries
                .Where(entry => !StringComparer.Ordinal.Equals(entry.Path, created.Path))
                .Append(created));
            StatusText = $"Created {created.Name}";
        }
        catch (Exception exception) when (IsSessionLost(exception))
        {
            DropLostSession();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameRemoteEntryAsync(RemoteFileEntry? entry, string newName, CancellationToken ct = default)
    {
        var trimmedName = string.IsNullOrWhiteSpace(newName) ? "" : newName.Trim();
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        if (entry is null)
        {
            StatusText = "Select a remote item";
            return;
        }

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            StatusText = "Enter a remote item name";
            return;
        }

        IsBusy = true;
        try
        {
            var originalName = entry.Name;
            var renamed = await _backend.RenameEntryAsync(
                CurrentSession.SessionId,
                entry.Path,
                trimmedName,
                ct);
            ReplaceRemoteEntries(RemoteEntries
                .Where(current => !StringComparer.Ordinal.Equals(current.Path, entry.Path))
                .Append(renamed));
            StatusText = $"Renamed {originalName} to {renamed.Name}";
        }
        catch (Exception exception) when (IsSessionLost(exception))
        {
            DropLostSession();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteRemoteEntriesAsync(IReadOnlyList<RemoteFileEntry> entries, CancellationToken ct = default)
    {
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        var paths = entries
            .Select(entry => entry.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
        {
            StatusText = "Select remote items";
            return;
        }

        IsBusy = true;
        try
        {
            var deleted = await _backend.DeleteEntriesAsync(
                CurrentSession.SessionId,
                paths,
                ct);
            var deletedPaths = deleted.ToHashSet(StringComparer.Ordinal);
            ReplaceRemoteEntries(RemoteEntries
                .Where(entry => !deletedPaths.Contains(entry.Path)));
            StatusText = deletedPaths.Count == 1
                ? "Deleted 1 remote item"
                : $"Deleted {deletedPaths.Count} remote items";
        }
        catch (Exception exception) when (IsSessionLost(exception))
        {
            DropLostSession();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DownloadRemoteEntriesAsync(
        IReadOnlyList<RemoteFileEntry> entries,
        string localDirectory,
        CancellationToken ct = default)
    {
        await DownloadRemoteEntriesCoreAsync(
            entries,
            localDirectory,
            ct);
    }

    public async Task DownloadRemoteEntriesToLocalAsync(
        IReadOnlyList<RemoteFileEntry> entries,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            StatusText = "Enter a local download folder";
            return;
        }

        await DownloadRemoteEntriesCoreAsync(
            entries,
            LocalPath,
            ct);
    }

    public async Task UploadLocalFilesAsync(IReadOnlyList<string> localPaths, CancellationToken ct = default)
    {
        await UploadLocalFilesCoreAsync(
            localPaths,
            ct);
    }

    public async Task UploadLocalEntriesToRemoteAsync(
        IReadOnlyList<RemoteFileEntry> entries,
        CancellationToken ct = default)
    {
        var localPaths = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .Select(entry => entry.Path)
            .ToArray();
        await UploadLocalFilesCoreAsync(
            localPaths,
            ct);
    }

    private async Task DownloadRemoteEntriesCoreAsync(
        IReadOnlyList<RemoteFileEntry> entries, string localDirectory,
        CancellationToken ct = default)
    {
        await RunTransfersAsync("download", entries.Select(entry => entry.Path).ToArray(), localDirectory, ct);
    }

    private async Task UploadLocalFilesCoreAsync(
        IReadOnlyList<string> localPaths, CancellationToken ct = default)
    {
        await RunTransfersAsync("upload", localPaths.ToArray(), RemotePath, ct);
    }

    private async Task RunTransfersAsync(string direction, string[] sources, string destination, CancellationToken ct)
    {
        if (CurrentSession is null) { StatusText = "Connect to a remote profile first"; return; }
        if (IsBusy) return;
        sources = sources.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(direction == "upload" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (sources.Length == 0) { StatusText = "Select files or folders to transfer"; return; }
        if (string.IsNullOrWhiteSpace(destination)) { StatusText = "Choose a destination folder"; return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transferCancellation = cancellation;
        _retryProfileId = CurrentSession.ProfileId;
        _retrySources = sources;
        _retryDirection = direction;
        _retryDestination = destination;
        IsBusy = true;
        OnPropertyChanged(nameof(CanCancelTransfer));
        TransferProgressText = "Preparing transfer...";
        try
        {
            var progress = new Progress<RemoteTransferProgress>(update =>
            {
                if (!ReferenceEquals(_transferCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                TransferProgressText = $"{update.CurrentFiles} files | {update.Current:N0} bytes | {update.CurrentItem}";
            });
            var request = new RemoteTransferRequest
            {
                RemoteSessionId = CurrentSession.SessionId,
                Direction = direction,
                Sources = sources,
                Destination = destination,
                ConflictAction = ConflictAction,
                OperationId = NewOperationId(),
            };
            var outcome = await _backend.TransferAsync(request, progress,
                new Progress<RemoteTransferTuning>(update => TransferTuningText = $"{update.Mode} · {update.Explanation}"),
                cancellation.Token);
            _retrySources = outcome.RetrySources;
            foreach (var item in outcome.Items)
            {
                var remotePath = direction == "upload" ? item.Destination : item.Source;
                var localPath = direction == "upload" ? item.Source : item.Destination;
                var record = AddTransfer(direction == "upload" ? "Upload" : "Download", remotePath, localPath);
                record.Apply(item.Status, item.Error ?? $"{item.Bytes:N0} bytes");
            }
            var completed = outcome.Items.Count(item => item.Status == "completed" && !item.IsDir);
            var skipped = outcome.Items.Count(item => item.Status == "skipped");
            StatusText = outcome.Cancelled
                ? $"Transfer cancelled: {completed} files completed; retry is available"
                : $"Transfer finished: {completed} files completed, {skipped} skipped, {outcome.RetrySources.Length} selections need retry";
            TransferProgressText = StatusText;
            // Refresh only after the backend has committed results and released the session.
            try
            {
                if (direction == "upload") await RefreshRemoteDirectoryCoreAsync(RemotePath, CancellationToken.None);
                else if (LocalPathsEqual(LocalPath, destination)) await RefreshLocalDirectoryCoreAsync(LocalPath, CancellationToken.None);
            }
            catch (Exception error) { StatusText += $". Refresh failed: {error.Message}"; }
        }
        catch (Exception error) when (IsSessionLost(error))
        {
            DropLostSession();
            TransferProgressText = StatusText;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Transfer cancelled; retry is available";
            TransferProgressText = StatusText;
        }
        catch (Exception error)
        {
            StatusText = $"Transfer failed: {error.Message}. Retry is available";
            TransferProgressText = StatusText;
            throw;
        }
        finally
        {
            _transferCancellation = null;
            IsBusy = false;
            OnPropertyChanged(nameof(CanCancelTransfer));
            OnPropertyChanged(nameof(CanRetryTransfers));
        }
    }

    private async Task RefreshLocalDirectoryCoreAsync(string path, CancellationToken ct)
    {
        var listing = await _localFolders.ListAsync(path, ct);
        LocalPath = string.IsNullOrWhiteSpace(listing.Path) ? path : listing.Path;
        _localParentPath = listing.Parent;
        ReplaceLocalEntries(listing.Entries);
    }

    private async Task RefreshRemoteDirectoryCoreAsync(string path, CancellationToken ct)
    {
        if (CurrentSession is null)
        {
            return;
        }

        var listing = await _backend.ListDirectoryAsync(
            CurrentSession.SessionId,
            NormalizeRemotePath(path),
            ct);
        RemotePath = NormalizeRemotePath(listing.Path);
        RemoteEntries.Clear();
        ReplaceRemoteEntries(listing.Entries);
    }

    private void ReplaceRemoteEntries(IEnumerable<RemoteFileEntry> entries)
    {
        RemoteEntries.Clear();
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            RemoteEntries.Add(entry);
        }
    }

    private void ReplaceLocalEntries(IEnumerable<RemoteFileEntry> entries)
    {
        LocalEntries.Clear();
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            LocalEntries.Add(entry);
        }
    }

    private void SortProfiles()
    {
        var ordered = Profiles
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Host, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Profiles.Clear();
        foreach (var profile in ordered)
        {
            Profiles.Add(profile);
        }
    }

    private static string FormatProfileCount(int count)
    {
        return count switch
        {
            0 => "No remote profiles",
            1 => "1 remote profile",
            _ => $"{count} remote profiles",
        };
    }

    private static string FormatProfileSummary(RemoteProfile profile)
    {
        var protocol = string.IsNullOrWhiteSpace(profile.Protocol)
            ? "remote"
            : profile.Protocol.Trim().ToLowerInvariant();
        var user = string.IsNullOrWhiteSpace(profile.Username)
            ? ""
            : $"{profile.Username.Trim()}@";
        var rootPath = string.IsNullOrWhiteSpace(profile.RootPath)
            ? "/"
            : profile.RootPath.Trim();
        if (!rootPath.StartsWith("/", StringComparison.Ordinal))
        {
            rootPath = "/" + rootPath;
        }

        return $"{profile.Name} - {protocol}://{user}{profile.Host}:{profile.Port}{rootPath}";
    }

    private static string NormalizeRemotePath(string path)
    {
        var trimmed = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        return trimmed.StartsWith("/", StringComparison.Ordinal)
            ? trimmed
            : "/" + trimmed;
    }

    private RemoteTransferRecord AddTransfer(string direction, string remotePath, string localPath)
    {
        var record = new RemoteTransferRecord(direction, remotePath, localPath);
        Transfers.Insert(0, record);
        return record;
    }

    private static bool IsSessionLost(Exception exception) =>
        exception is not OperationCanceledException
        && exception.Message.StartsWith(SessionNotFoundPrefix, StringComparison.Ordinal);

    /// <summary>The backend no longer has the session (it restarted): forget it and ask the user to reconnect.</summary>
    private void DropLostSession()
    {
        CurrentSession = null;
        RemoteEntries.Clear();
        RemotePath = "/";
        StatusText = ConnectionLostText;
    }

    private static string NewOperationId() => $"remote-{Guid.NewGuid():N}";

    private static bool LocalPathsEqual(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string ParentRemotePath(string path)
    {
        var normalized = NormalizeRemotePath(path).TrimEnd('/');
        if (string.IsNullOrEmpty(normalized) || string.Equals(normalized, "/", StringComparison.Ordinal))
        {
            return "/";
        }

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : normalized[..lastSlash];
    }
}
