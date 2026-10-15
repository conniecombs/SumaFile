using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

public sealed class RemoteManagerViewModel : ObservableObject
{
    private readonly FileOperationService _fileOperations;
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

    public RemoteManagerViewModel(FileOperationService fileOperations)
    {
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
    }

    public ObservableCollection<RemoteProfile> Profiles { get; } = [];

    public ObservableCollection<FileEntry> LocalEntries { get; } = [];

    public ObservableCollection<FileEntry> RemoteEntries { get; } = [];

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
            var profiles = await _fileOperations.RemoteListProfilesAsync(ct);

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

    public async Task ConnectSelectedProfileAsync(string? secret = null, CancellationToken ct = default)
    {
        if (SelectedProfile is null)
        {
            StatusText = "Select a remote profile";
            return;
        }

        IsBusy = true;
        try
        {
            var profileName = SelectedProfile.Name;
            CurrentSession = await _fileOperations.RemoteConnectAsync(SelectedProfile.Id, secret, ct);
            RemotePath = NormalizeRemotePath(CurrentSession.RootPath);
            await RefreshRemoteDirectoryCoreAsync(RemotePath, ct);
            StatusText = $"Connected to {profileName}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveProfileAsync(
        RemoteProfileInput profile,
        string? secret = null,
        CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var saved = await _fileOperations.RemoteSaveProfileAsync(profile, secret, ct);
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
            await _fileOperations.RemoteDeleteProfileAsync(deleted.Id, ct);
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
            var result = await _fileOperations.RemoteTestProfileAsync(profile, secret, ct);
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

    public async Task NavigateLocalEntryAsync(FileEntry? entry, CancellationToken ct = default)
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

    public async Task NavigateRemoteEntryAsync(FileEntry? entry, CancellationToken ct = default)
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
            await _fileOperations.RemoteDisconnectAsync(CurrentSession.SessionId, ct);
            CurrentSession = null;
            RemoteEntries.Clear();
            RemotePath = "/";
            StatusText = FormatProfileCount(Profiles.Count);
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
            var created = await _fileOperations.RemoteCreateDirectoryAsync(
                CurrentSession.SessionId,
                RemotePath,
                trimmedName,
                ct);
            ReplaceRemoteEntries(RemoteEntries
                .Where(entry => !StringComparer.Ordinal.Equals(entry.Path, created.Path))
                .Append(created));
            StatusText = $"Created {created.Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameRemoteEntryAsync(FileEntry? entry, string newName, CancellationToken ct = default)
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
            var renamed = await _fileOperations.RemoteRenameEntryAsync(
                CurrentSession.SessionId,
                entry.Path,
                trimmedName,
                ct);
            ReplaceRemoteEntries(RemoteEntries
                .Where(current => !StringComparer.Ordinal.Equals(current.Path, entry.Path))
                .Append(renamed));
            StatusText = $"Renamed {originalName} to {renamed.Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteRemoteEntriesAsync(IReadOnlyList<FileEntry> entries, CancellationToken ct = default)
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
            var deleted = await _fileOperations.RemoteDeleteEntriesAsync(
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
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DownloadRemoteEntriesAsync(
        IReadOnlyList<FileEntry> entries,
        string localDirectory,
        CancellationToken ct = default)
    {
        await DownloadRemoteEntriesCoreAsync(
            entries,
            localDirectory,
            ct);
    }

    public async Task DownloadRemoteEntriesToLocalAsync(
        IReadOnlyList<FileEntry> entries,
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
        IReadOnlyList<FileEntry> entries,
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
        IReadOnlyList<FileEntry> entries, string localDirectory,
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
            var progress = new Progress<ProgressUpdate>(update =>
            {
                if (!ReferenceEquals(_transferCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                TransferProgressText = $"{update.CurrentFiles} files | {update.Current:N0} bytes | {update.CurrentItem}";
            });
            var outcome = await _fileOperations.RemoteTransferEntriesAsync(CurrentSession.SessionId,
                direction, sources, destination, ConflictAction, progress, cancellation.Token);
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
            // Refresh only after the service has committed results and released the session.
            try
            {
                if (direction == "upload") await RefreshRemoteDirectoryCoreAsync(RemotePath, CancellationToken.None);
                else if (PathRules.PathsEqual(LocalPath, destination)) await RefreshLocalDirectoryCoreAsync(LocalPath, CancellationToken.None);
            }
            catch (Exception error) { StatusText += $". Refresh failed: {error.Message}"; }
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
        var listing = await _fileOperations.ListDirectoryAsync(path, ct);
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

        var listing = await _fileOperations.RemoteListDirectoryAsync(
            CurrentSession.SessionId,
            NormalizeRemotePath(path),
            ct);
        RemotePath = NormalizeRemotePath(listing.Path);
        RemoteEntries.Clear();
        ReplaceRemoteEntries(listing.Entries);
    }

    private void ReplaceRemoteEntries(IEnumerable<FileEntry> entries)
    {
        RemoteEntries.Clear();
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            RemoteEntries.Add(entry);
        }
    }

    private void ReplaceLocalEntries(IEnumerable<FileEntry> entries)
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

public sealed class RemoteTransferRecord : ObservableObject
{
    private string _status = "Pending";
    private string _detail = "";

    public RemoteTransferRecord(string direction, string remotePath, string localPath)
    {
        Direction = direction;
        RemotePath = remotePath;
        LocalPath = localPath;
    }

    public string Direction { get; }
    public string RemotePath { get; }
    public string LocalPath { get; }
    public string FileName => string.IsNullOrWhiteSpace(RemotePath)
        ? Path.GetFileName(LocalPath)
        : RemotePath.TrimEnd('/').Split('/').LastOrDefault() ?? RemotePath;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public void Apply(string status, string detail)
    {
        Status = status.Length == 0 ? "Unknown" : char.ToUpperInvariant(status[0]) + status[1..];
        Detail = detail;
    }

    public void Complete()
    {
        Status = "Completed";
        Detail = "";
    }

    public void Fail(string message)
    {
        Status = "Failed";
        Detail = message;
    }
}
