using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

public sealed partial class ExplorerWorkspace
{

    public async Task RefreshDrivesAsync(bool quiet = false, CancellationToken cancellationToken = default)
    {
        await RefreshDrivesAsync(quiet, cancellationToken, lightweight: false).ConfigureAwait(false);
    }

    public async Task RefreshDriveAsync(
        string path,
        bool quiet = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var drives = await _backend.ListDriveAsync(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ApplyDriveLocked(path, drives, quiet);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate)
            {
                if (!quiet)
                {
                    ErrorMessage = exception.Message;
                }
            }
        }

        RaiseChanged();
    }

    private async Task RefreshDrivesAsync(
        bool quiet,
        CancellationToken cancellationToken,
        bool lightweight)
    {
        try
        {
            var drives = lightweight
                ? await _backend.ListDrivesLightAsync(cancellationToken).ConfigureAwait(false)
                : await _backend.ListDrivesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ApplyDrivesLocked(drives, quiet);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate)
            {
                if (!quiet)
                {
                    ErrorMessage = exception.Message;
                }
            }
        }

        RaiseChanged();
    }

    private void SeedCachedDrives(IReadOnlyList<DriveInfo> drives, bool quiet)
    {
        lock (_gate)
        {
            ApplyDrivesLocked(drives, quiet);
        }

        RaiseChanged();
    }

    private void ApplyDrivesLocked(IReadOnlyList<DriveInfo> drives, bool quiet)
    {
        if (drives.Count > 0)
        {
            _drives = [.. drives];
        }
        else
        {
            var fallback = PathRules.CreateFallbackDriveForPath(
                string.IsNullOrEmpty(HomePath) ? Primary.Path : HomePath);
            _drives = fallback is null ? [] : [fallback];
        }

        if (!quiet)
        {
            var offline = _drives.Count(drive =>
            {
                var status = DrivePresentation.Status(drive);
                return status is "offline" or "stale";
            });
            StatusMessage = offline > 0
                ? $"Drives refreshed · {offline} network mapping{(offline == 1 ? "" : "s")} need attention"
                : "Drives refreshed";
            ErrorMessage = null;
        }
    }

    private void ApplyDriveLocked(string path, IReadOnlyList<DriveInfo> drives, bool quiet)
    {
        var drive = drives.FirstOrDefault(candidate => PathRules.PathsEqual(candidate.Path, path))
            ?? drives.FirstOrDefault();
        if (drive is null)
        {
            return;
        }

        var next = _drives.ToList();
        var existingIndex = next.FindIndex(candidate =>
            PathRules.PathsEqual(candidate.Path, drive.Path)
            || PathRules.PathsEqual(candidate.Path, path));
        if (existingIndex >= 0)
        {
            next[existingIndex] = drive;
        }
        else
        {
            next.Add(drive);
            next.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        }

        _drives = next;
        if (!quiet)
        {
            StatusMessage = DrivePresentation.IsAvailable(drive)
                ? $"{drive.Name} refreshed"
                : drive.StatusDetail ?? $"{drive.Name} is not available";
            ErrorMessage = null;
        }
    }

    public Task NavigateToAsync(
        string path,
        HistoryMode historyMode = HistoryMode.Push,
        CancellationToken cancellationToken = default)
    {
        return NavigatePaneAsync(ActivePane, path, historyMode, activate: false, cancellationToken);
    }

    public async Task NavigatePaneAsync(
        PaneId pane,
        string path,
        HistoryMode historyMode = HistoryMode.Push,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var target = Normalize(pane);
        var state = Pane(target);
        if (activate && DualPaneEnabled)
        {
            ActivePane = target;
        }

        var token = state.NextNavigationToken();

        lock (_gate)
        {
            state.IsNavigating = true;
            state.ListingInProgress = true;
            state.Path = path;
            state.Entries = [];
            state.SelectedPath = null;
            ErrorMessage = null;
            FileOpenUnsupported = false;
            PendingReconnect = null;
            state.PathIsNetwork = PathRules.IsNetworkFsPath(path, _drives);
            ApplyFolderViewSettingsForPathLocked(target, path);
        }

        RaiseChanged();

        try
        {
            var navigation = await _navigator.NavigateAsync(state, path, token, cancellationToken)
                .ConfigureAwait(false);
            if (navigation.IsAbandoned)
            {
                return;
            }

            if (navigation.PartialResultMessage is not null)
            {
                lock (_gate)
                {
                    if (token != state.NavigationToken)
                    {
                        return;
                    }

                    state.RecordHistory(state.Path, historyMode);
                    state.SyncActiveTab();
                    StatusMessage = navigation.PartialResultMessage;
                }

                RaiseChanged();
                return;
            }

            var listing = navigation.Listing;
            if (listing is null || token != state.NavigationToken)
            {
                return;
            }

            lock (_gate)
            {
                state.Path = listing.Path;
                state.Entries = [.. listing.Entries];
                state.PathIsNetwork = listing.IsNetwork || PathRules.IsNetworkFsPath(listing.Path, _drives);
                ApplyFolderViewSettingsForPathLocked(target, listing.Path);
                state.RecordHistory(listing.Path, historyMode);
                state.SyncActiveTab();
                StatusMessage = null;
                RecentPaths = PlacesStore.RecordRecent(RecentPaths, listing.Path);
                PhotoFolderActive = MediaFolder.IsMediaFolder(listing.Entries, Settings.PhotoFolderImageThreshold);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (token != state.NavigationToken)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? refreshDrivePath = null;
            lock (_gate)
            {
                var drive = DrivePresentation.FindDriveForPath(path, _drives);
                ErrorMessage = drive is not null && DrivePresentation.IsNetwork(drive)
                    ? (drive.StatusDetail ?? exception.Message)
                    : exception.Message;
                refreshDrivePath = drive is not null && DrivePresentation.IsNetwork(drive) ? drive.Path : null;
            }

            if (refreshDrivePath is not null)
            {
                await RefreshDriveAsync(refreshDrivePath, quiet: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (token == state.NavigationToken)
            {
                lock (_gate)
                {
                    state.IsNavigating = false;
                    state.ListingInProgress = false;
                }

                RaiseChanged();
            }
        }
    }

    public Task GoBackAsync(CancellationToken cancellationToken = default)
    {
        return GoBackAsync(ActivePane, cancellationToken);
    }

    public Task GoForwardAsync(CancellationToken cancellationToken = default)
    {
        return GoForwardAsync(ActivePane, cancellationToken);
    }

    public Task GoUpAsync(CancellationToken cancellationToken = default)
    {
        return GoUpAsync(ActivePane, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return RefreshAsync(ActivePane, cancellationToken);
    }

    public async Task RefreshAffectedPanesAsync(
        IEnumerable<string?> affectedPaths,
        CancellationToken cancellationToken = default)
    {
        foreach (var pane in AffectedVisiblePanes(affectedPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshAsync(pane, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<PaneId> AffectedVisiblePanes(IEnumerable<string?> affectedPaths)
    {
        ArgumentNullException.ThrowIfNull(affectedPaths);

        var paths = affectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return [];
        }

        List<PaneId> panes = [];
        if (PanePathIsAffected(Primary.Path, paths))
        {
            panes.Add(PaneId.Primary);
        }

        if (DualPaneEnabled && PanePathIsAffected(Secondary.Path, paths))
        {
            panes.Add(PaneId.Secondary);
        }

        return panes;
    }

    private static bool PanePathIsAffected(string? panePath, IReadOnlyList<string> affectedPaths)
    {
        if (string.IsNullOrWhiteSpace(panePath))
        {
            return false;
        }

        return affectedPaths.Any(affectedPath =>
            PathRules.PathsEqual(panePath, affectedPath)
            || PathRules.PathContains(panePath, affectedPath)
            || PathRules.PathContains(affectedPath, panePath));
    }

    public Task NavigateSpecialAsync(string command, CancellationToken cancellationToken = default)
    {
        return NavigateSpecialAsync(command, SidebarTarget, cancellationToken);
    }

    public Task OpenEntryAsync(FileEntry entry, CancellationToken cancellationToken = default)
    {
        return OpenPathAsync(entry.Path, entry.IsDir, ActivePane, cancellationToken);
    }

    public Task OpenPathAsync(
        string path,
        bool? isDirectory = null,
        CancellationToken cancellationToken = default)
    {
        return OpenPathAsync(path, isDirectory, ActivePane, cancellationToken);
    }

    public async Task RetryPendingDriveAsync(CancellationToken cancellationToken = default)
    {
        var pending = PendingReconnect;
        var pane = PendingReconnectPane;
        PendingReconnect = null;
        if (pending is null)
        {
            return;
        }

        await RefreshDriveAsync(pending.Path, quiet: true, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await LoadSmartFoldersAsync(cancellationToken).ConfigureAwait(false);
        await LoadTagsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var updated = DrivePresentation.FindDriveForPath(pending.Path, _drives);
        if (updated is not null && DrivePresentation.IsAvailable(updated))
        {
            StatusMessage = string.IsNullOrEmpty(updated.RemotePath)
                ? "Network drive is available again"
                : $"Connected to {updated.RemotePath}";
            await NavigatePaneAsync(pane, pending.Path, HistoryMode.Push, activate: DualPaneEnabled, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ErrorMessage = updated?.StatusDetail ?? "The network drive is still unavailable.";
        RaiseChanged();
    }

    public void CancelPendingReconnect()
    {
        PendingReconnect = null;
        RaiseChanged();
    }

    public Task GoBackAsync(PaneId pane, CancellationToken cancellationToken = default)
    {
        string? path = null;
        lock (_gate)
        {
            var state = Pane(pane);
            if (!state.CanGoBack)
            {
                return Task.CompletedTask;
            }

            state.HistoryIndex -= 1;
            path = state.History[state.HistoryIndex];
        }

        return NavigatePaneAsync(pane, path!, HistoryMode.None, activate: DualPaneEnabled, cancellationToken);
    }

    public Task GoForwardAsync(PaneId pane, CancellationToken cancellationToken = default)
    {
        string? path = null;
        lock (_gate)
        {
            var state = Pane(pane);
            if (!state.CanGoForward)
            {
                return Task.CompletedTask;
            }

            state.HistoryIndex += 1;
            path = state.History[state.HistoryIndex];
        }

        return NavigatePaneAsync(pane, path!, HistoryMode.None, activate: DualPaneEnabled, cancellationToken);
    }

    public Task GoUpAsync(PaneId pane, CancellationToken cancellationToken = default)
    {
        var parent = PathRules.GetParentPath(Pane(pane).Path);
        return parent is null
            ? Task.CompletedTask
            : NavigatePaneAsync(pane, parent, HistoryMode.Push, activate: DualPaneEnabled, cancellationToken);
    }

    public async Task RefreshAsync(PaneId pane, CancellationToken cancellationToken = default)
    {
        var target = Normalize(pane);
        var state = Pane(target);
        var path = state.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var selected = state.SelectedPath;
        var token = state.NextNavigationToken();

        lock (_gate)
        {
            state.ListingInProgress = true;
        }

        try
        {
            var refresh = await _navigator.RefreshAsync(state, path, token, cancellationToken)
                .ConfigureAwait(false);
            if (refresh.IsAbandoned)
            {
                return;
            }

            var listing = refresh.Listing;
            if (listing is null)
            {
                return;
            }

            lock (_gate)
            {
                if (token != state.NavigationToken || !PathRules.PathsEqual(state.Path, path))
                {
                    return;
                }

                state.Entries = [.. listing.Entries];
                if (!string.IsNullOrEmpty(listing.Path))
                {
                    state.Path = listing.Path;
                }

                state.PathIsNetwork = listing.IsNetwork || PathRules.IsNetworkFsPath(state.Path, _drives);
                state.SyncActiveTab();
                PhotoFolderActive = MediaFolder.IsMediaFolder(listing.Entries, Settings.PhotoFolderImageThreshold);
                state.SelectedPath = selected is not null
                    && state.Entries.Any(entry => PathRules.PathsEqual(entry.Path, selected))
                        ? selected
                        : null;
            }

            RaiseChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate)
            {
                if (PathRules.PathsEqual(state.Path, path))
                {
                    ErrorMessage = exception.Message;
                }
            }

            RaiseChanged();
        }
        finally
        {
            if (token == state.NavigationToken)
            {
                lock (_gate)
                {
                    state.ListingInProgress = false;
                }

                RaiseChanged();
            }
        }
    }

    public Task NavigateSpecialAsync(string command, PaneId pane, CancellationToken cancellationToken = default)
    {
        if (command == "navigateHome")
        {
            return NavigatePaneAsync(pane, HomePath, HistoryMode.Push, activate: DualPaneEnabled, cancellationToken);
        }

        if (command == "navigateRecycleBin")
        {
            return NavigatePaneAsync(pane, PathRules.RecycleBinPath, HistoryMode.Push, activate: DualPaneEnabled, cancellationToken);
        }

        if (SpecialFolders.TryGetValue(command, out var folder))
        {
            return NavigatePaneAsync(
                pane,
                PathRules.JoinPath(HomePath, folder),
                HistoryMode.Push,
                activate: DualPaneEnabled,
                cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task OpenEntryAsync(FileEntry entry, PaneId pane, CancellationToken cancellationToken = default)
    {
        return OpenPathAsync(entry.Path, entry.IsDir, pane, cancellationToken);
    }

    public async Task OpenPathAsync(
        string path,
        bool? isDirectory,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        FileOpenUnsupported = false;
        var target = Normalize(pane);
        var shouldNavigate = isDirectory;
        if (PathRules.IsRecycleBinPath(path))
        {
            shouldNavigate = true;
        }

        var drive = DrivePresentation.FindDriveForPath(path, _drives);
        if (drive is not null && PathRules.PathsEqual(drive.Path, path))
        {
            shouldNavigate = true;
            if (DrivePresentation.IsNetwork(drive) && DrivePresentation.Status(drive) == "unknown")
            {
                await RefreshDriveAsync(drive.Path, quiet: true, cancellationToken).ConfigureAwait(false);
                drive = DrivePresentation.FindDriveForPath(path, _drives);
            }

            if (drive is not null && DrivePresentation.IsNetwork(drive) && !DrivePresentation.IsAvailable(drive))
            {
                PendingReconnect = drive;
                PendingReconnectPane = target;
                RaiseChanged();
                return;
            }
        }

        if (shouldNavigate is null && FileOps is not null)
        {
            try
            {
                var entry = await FileOps.GetEntryInfoAsync(path, cancellationToken).ConfigureAwait(false);
                shouldNavigate = entry.IsDir;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Unknown-path callers still get the old directory-navigation fallback.
            }
        }

        if (shouldNavigate == false && IsSupportedArchivePath(path))
        {
            shouldNavigate = true;
        }

        if (shouldNavigate == true)
        {
            if (Settings.OpenInNewTab && !string.IsNullOrEmpty(Pane(target).Path))
            {
                await OpenNewTabAsync(target, path, cancellationToken).ConfigureAwait(false);
                return;
            }

            await NavigatePaneAsync(target, path, HistoryMode.Push, activate: DualPaneEnabled, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (shouldNavigate == false)
        {
            if (FileOps is not null)
            {
                try
                {
                    await FileOps.OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        Pane(target).SelectedPath = path;
                        StatusMessage = $"Opened {PathRules.Basename(path)}";
                        ErrorMessage = null;
                    }

                    RaiseChanged();
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (_gate)
                    {
                        Pane(target).SelectedPath = path;
                        ErrorMessage = exception.Message;
                    }

                    RaiseChanged();
                    return;
                }
            }

            lock (_gate)
            {
                FileOpenUnsupported = true;
                Pane(target).SelectedPath = path;
                StatusMessage = "No file operation service is available to open this file.";
            }

            RaiseChanged();
            return;
        }

        await NavigatePaneAsync(target, path, HistoryMode.Push, activate: DualPaneEnabled, cancellationToken)
            .ConfigureAwait(false);
    }

}

