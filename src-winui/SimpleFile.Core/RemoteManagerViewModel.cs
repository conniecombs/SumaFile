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
            completedStatus: count => count == 1
                ? "Downloaded 1 remote file"
                : $"Downloaded {count} remote files",
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
            completedStatus: count => count == 1
                ? $"Downloaded 1 remote file to {LocalPath}"
                : $"Downloaded {count} remote files to {LocalPath}",
            ct);
    }

    public async Task UploadLocalFilesAsync(IReadOnlyList<string> localPaths, CancellationToken ct = default)
    {
        await UploadLocalFilesCoreAsync(
            localPaths,
            completedStatus: count => count == 1
                ? "Uploaded 1 file"
                : $"Uploaded {count} files",
            ct);
    }

    public async Task UploadLocalEntriesToRemoteAsync(
        IReadOnlyList<FileEntry> entries,
        CancellationToken ct = default)
    {
        var localPaths = entries
            .Where(entry => !entry.IsDir && !string.IsNullOrWhiteSpace(entry.Path))
            .Select(entry => entry.Path)
            .ToArray();
        await UploadLocalFilesCoreAsync(
            localPaths,
            completedStatus: count => count == 1
                ? $"Uploaded 1 local file to {RemotePath}"
                : $"Uploaded {count} local files to {RemotePath}",
            ct);
    }

    private async Task DownloadRemoteEntriesCoreAsync(
        IReadOnlyList<FileEntry> entries,
        string localDirectory,
        Func<int, string> completedStatus,
        CancellationToken ct = default)
    {
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        var files = entries
            .Where(entry => !entry.IsDir && !string.IsNullOrWhiteSpace(entry.Path))
            .ToArray();
        if (files.Length == 0)
        {
            StatusText = "Select remote files";
            return;
        }

        if (string.IsNullOrWhiteSpace(localDirectory))
        {
            StatusText = "Enter a local download folder";
            return;
        }

        IsBusy = true;
        try
        {
            foreach (var entry in files)
            {
                var localPath = Path.Combine(localDirectory.Trim(), entry.Name);
                var record = AddTransfer("Download", entry.Path, localPath);
                try
                {
                    await _fileOperations.RemoteDownloadFileAsync(
                        CurrentSession.SessionId,
                        entry.Path,
                        localPath,
                        ct);
                    record.Complete();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    record.Fail(exception.Message);
                    throw;
                }
            }

            StatusText = completedStatus(files.Length);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UploadLocalFilesCoreAsync(
        IReadOnlyList<string> localPaths,
        Func<int, string> completedStatus,
        CancellationToken ct = default)
    {
        if (CurrentSession is null)
        {
            StatusText = "Connect to a remote profile first";
            return;
        }

        var files = localPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            StatusText = "Enter local files to upload";
            return;
        }

        IsBusy = true;
        try
        {
            var uploaded = new List<FileEntry>();
            foreach (var localPath in files)
            {
                var name = Path.GetFileName(localPath);
                var remotePath = CombineRemotePath(RemotePath, name);
                var record = AddTransfer("Upload", remotePath, localPath);
                try
                {
                    uploaded.Add(await _fileOperations.RemoteUploadFileAsync(
                        CurrentSession.SessionId,
                        localPath,
                        remotePath,
                        ct));
                    record.Complete();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    record.Fail(exception.Message);
                    throw;
                }
            }

            ReplaceRemoteEntries(RemoteEntries
                .Where(entry => uploaded.All(upload => !StringComparer.Ordinal.Equals(upload.Path, entry.Path)))
                .Concat(uploaded));
            StatusText = completedStatus(uploaded.Count);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshLocalDirectoryCoreAsync(string path, CancellationToken ct)
    {
        var listing = await _fileOperations.ListDirectoryAsync(path, ct).ConfigureAwait(false);
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

    private static string CombineRemotePath(string parent, string name)
    {
        var normalizedParent = NormalizeRemotePath(parent).TrimEnd('/');
        var cleanName = name.Trim().Trim('/');
        return string.IsNullOrEmpty(normalizedParent)
            ? "/" + cleanName
            : normalizedParent + "/" + cleanName;
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
