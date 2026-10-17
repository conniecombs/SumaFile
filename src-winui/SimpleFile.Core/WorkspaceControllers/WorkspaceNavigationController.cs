using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

internal sealed class WorkspaceNavigationController
{
    public static readonly IReadOnlyList<(string Name, string Icon, string Command)> QuickAccessLocations =
    [
        ("Home", "\uE80F", "navigateHome"),
        ("Desktop", "\uE7F4", "navigateDesktop"),
        ("Downloads", "\uE896", "navigateDownloads"),
        ("Documents", "\uE8A5", "navigateDocuments"),
        ("Pictures", "\uEB9F", "navigatePictures"),
        ("Recycle Bin", "\uE74D", "navigateRecycleBin"),
    ];

    private static readonly Dictionary<string, string> SpecialFolders = new(StringComparer.Ordinal)
    {
        ["navigateDesktop"] = "Desktop",
        ["navigateDocuments"] = "Documents",
        ["navigateDownloads"] = "Downloads",
        ["navigatePictures"] = "Pictures",
    };

    public async Task RefreshDriveAsync(
        ExplorerWorkspace workspace,
        string path,
        bool quiet = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        try
        {
            var drives = await workspace.Backend.ListDriveAsync(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (workspace.Gate)
            {
                ApplyDriveLocked(workspace, path, drives, quiet);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (workspace.Gate)
            {
                if (!quiet)
                {
                    workspace.SetErrorMessageCore(exception.Message);
                }
            }
        }

        workspace.NotifyChanged();
    }

    public async Task RefreshDrivesAsync(
        ExplorerWorkspace workspace,
        bool quiet,
        CancellationToken cancellationToken,
        bool lightweight)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        try
        {
            var drives = lightweight
                ? await workspace.Backend.ListDrivesLightAsync(cancellationToken).ConfigureAwait(false)
                : await workspace.Backend.ListDrivesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (workspace.Gate)
            {
                ApplyDrivesLocked(workspace, drives, quiet);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (workspace.Gate)
            {
                if (!quiet)
                {
                    workspace.SetErrorMessageCore(exception.Message);
                }
            }
        }

        workspace.NotifyChanged();
    }

    public void SeedCachedDrives(
        ExplorerWorkspace workspace,
        IReadOnlyList<DriveInfo> drives,
        bool quiet)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        lock (workspace.Gate)
        {
            ApplyDrivesLocked(workspace, drives, quiet);
        }

        workspace.NotifyChanged();
    }

    private static void ApplyDrivesLocked(
        ExplorerWorkspace workspace,
        IReadOnlyList<DriveInfo> drives,
        bool quiet)
    {
        var fallbackBasePath = string.IsNullOrEmpty(workspace.HomePath)
            ? workspace.Primary.Path
            : workspace.HomePath;
        var statusMessage = workspace.DrivesController.ApplyDrives(drives, fallbackBasePath, quiet);
        if (!quiet)
        {
            workspace.SetStatusMessageCore(statusMessage);
            workspace.SetErrorMessageCore(null);
        }
    }

    private static void ApplyDriveLocked(
        ExplorerWorkspace workspace,
        string path,
        IReadOnlyList<DriveInfo> drives,
        bool quiet)
    {
        if (workspace.DrivesController.TryApplyDrive(path, drives, quiet, out var statusMessage) && !quiet)
        {
            workspace.SetStatusMessageCore(statusMessage);
            workspace.SetErrorMessageCore(null);
        }
    }

    public async Task NavigatePaneAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        string path,
        HistoryMode historyMode = HistoryMode.Push,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var target = workspace.Normalize(pane);
        var state = workspace.Pane(target);
        if (activate && workspace.DualPaneEnabled)
        {
            workspace.SetActivePaneCore(target);
        }

        var token = state.NextNavigationToken();

        lock (workspace.Gate)
        {
            state.IsNavigating = true;
            state.ListingInProgress = true;
            state.Path = path;
            state.Entries = [];
            state.SelectedPath = null;
            workspace.SetErrorMessageCore(null);
            workspace.SetFileOpenUnsupportedCore(false);
            workspace.DrivesController.ClearPendingReconnect();
            state.PathIsNetwork = workspace.DrivesController.IsNetworkFsPath(path);
            workspace.ApplyFolderViewSettingsForPathLocked(target, path);
        }

        workspace.NotifyChanged();

        try
        {
            var navigation = await workspace.Navigator.NavigateAsync(state, path, token, cancellationToken)
                .ConfigureAwait(false);
            if (navigation.IsAbandoned)
            {
                return;
            }

            if (navigation.PartialResultMessage is not null)
            {
                lock (workspace.Gate)
                {
                    if (token != state.NavigationToken)
                    {
                        return;
                    }

                    state.RecordHistory(state.Path, historyMode);
                    state.SyncActiveTab();
                    workspace.SetStatusMessageCore(navigation.PartialResultMessage);
                }

                workspace.NotifyChanged();
                return;
            }

            var listing = navigation.Listing;
            if (listing is null || token != state.NavigationToken)
            {
                return;
            }

            lock (workspace.Gate)
            {
                state.Path = listing.Path;
                state.Entries = [.. listing.Entries];
                state.PathIsNetwork = listing.IsNetwork || workspace.DrivesController.IsNetworkFsPath(listing.Path);
                workspace.ApplyFolderViewSettingsForPathLocked(target, listing.Path);
                state.RecordHistory(listing.Path, historyMode);
                state.SyncActiveTab();
                workspace.SetStatusMessageCore(null);
                workspace.PlacesController.RecordRecent(listing.Path);
                workspace.SetPhotoFolderActiveCore(
                    MediaFolder.IsMediaFolder(listing.Entries, workspace.Settings.PhotoFolderImageThreshold));
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
            lock (workspace.Gate)
            {
                var drive = workspace.DrivesController.FindDriveForPath(path);
                workspace.SetErrorMessageCore(
                    drive is not null && DrivePresentation.IsNetwork(drive)
                        ? (drive.StatusDetail ?? exception.Message)
                        : exception.Message);
                refreshDrivePath = drive is not null && DrivePresentation.IsNetwork(drive) ? drive.Path : null;
            }

            if (refreshDrivePath is not null)
            {
                await RefreshDriveAsync(workspace, refreshDrivePath, quiet: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (token == state.NavigationToken)
            {
                lock (workspace.Gate)
                {
                    state.IsNavigating = false;
                    state.ListingInProgress = false;
                }

                workspace.NotifyChanged();
            }
        }
    }

    public async Task RefreshAffectedPanesAsync(
        ExplorerWorkspace workspace,
        IEnumerable<string?> affectedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        foreach (var pane in AffectedVisiblePanes(workspace, affectedPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshAsync(workspace, pane, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<PaneId> AffectedVisiblePanes(
        ExplorerWorkspace workspace,
        IEnumerable<string?> affectedPaths)
    {
        ArgumentNullException.ThrowIfNull(workspace);
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
        if (PanePathIsAffected(workspace.Primary.Path, paths))
        {
            panes.Add(PaneId.Primary);
        }

        if (workspace.DualPaneEnabled && PanePathIsAffected(workspace.Secondary.Path, paths))
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

    public async Task RetryPendingDriveAsync(
        ExplorerWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var (pending, pane) = workspace.DrivesController.TakePendingReconnect();
        if (pending is null)
        {
            return;
        }

        await RefreshDriveAsync(workspace, pending.Path, quiet: true, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await workspace.PlacesController.LoadSmartFoldersAsync(workspace.FileOps, cancellationToken).ConfigureAwait(false);
        await workspace.TagsController.LoadTagsAsync(workspace.FileOps, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var updated = workspace.DrivesController.FindDriveForPath(pending.Path);
        if (updated is not null && DrivePresentation.IsAvailable(updated))
        {
            workspace.SetStatusMessageCore(
                string.IsNullOrEmpty(updated.RemotePath)
                    ? "Network drive is available again"
                    : $"Connected to {updated.RemotePath}");
            await NavigatePaneAsync(workspace, pane, pending.Path, HistoryMode.Push, activate: workspace.DualPaneEnabled, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        workspace.SetErrorMessageCore(updated?.StatusDetail ?? "The network drive is still unavailable.");
        workspace.NotifyChanged();
    }

    public void CancelPendingReconnect(ExplorerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.DrivesController.ClearPendingReconnect();
        workspace.NotifyChanged();
    }

    public Task GoBackAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string? path;
        lock (workspace.Gate)
        {
            var state = workspace.Pane(pane);
            if (!state.CanGoBack)
            {
                return Task.CompletedTask;
            }

            state.HistoryIndex -= 1;
            path = state.History[state.HistoryIndex];
        }

        return NavigatePaneAsync(workspace, pane, path, HistoryMode.None, activate: workspace.DualPaneEnabled, cancellationToken);
    }

    public Task GoForwardAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string? path;
        lock (workspace.Gate)
        {
            var state = workspace.Pane(pane);
            if (!state.CanGoForward)
            {
                return Task.CompletedTask;
            }

            state.HistoryIndex += 1;
            path = state.History[state.HistoryIndex];
        }

        return NavigatePaneAsync(workspace, pane, path, HistoryMode.None, activate: workspace.DualPaneEnabled, cancellationToken);
    }

    public Task GoUpAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var parent = PathRules.GetParentPath(workspace.Pane(pane).Path);
        return parent is null
            ? Task.CompletedTask
            : NavigatePaneAsync(workspace, pane, parent, HistoryMode.Push, activate: workspace.DualPaneEnabled, cancellationToken);
    }

    public async Task RefreshAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane);
        var state = workspace.Pane(target);
        var path = state.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var selected = state.SelectedPath;
        var token = state.NextNavigationToken();

        lock (workspace.Gate)
        {
            state.ListingInProgress = true;
        }

        try
        {
            var refresh = await workspace.Navigator.RefreshAsync(state, path, token, cancellationToken)
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

            lock (workspace.Gate)
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

                state.PathIsNetwork = listing.IsNetwork || workspace.DrivesController.IsNetworkFsPath(state.Path);
                state.SyncActiveTab();
                workspace.SetPhotoFolderActiveCore(
                    MediaFolder.IsMediaFolder(listing.Entries, workspace.Settings.PhotoFolderImageThreshold));
                state.SelectedPath = selected is not null
                    && state.Entries.Any(entry => PathRules.PathsEqual(entry.Path, selected))
                        ? selected
                        : null;
            }

            workspace.NotifyChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (workspace.Gate)
            {
                if (PathRules.PathsEqual(state.Path, path))
                {
                    workspace.SetErrorMessageCore(exception.Message);
                }
            }

            workspace.NotifyChanged();
        }
        finally
        {
            if (token == state.NavigationToken)
            {
                lock (workspace.Gate)
                {
                    state.ListingInProgress = false;
                }

                workspace.NotifyChanged();
            }
        }
    }

    public Task NavigateSpecialAsync(
        ExplorerWorkspace workspace,
        string command,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (command == "navigateHome")
        {
            return NavigatePaneAsync(workspace, pane, workspace.HomePath, HistoryMode.Push, activate: workspace.DualPaneEnabled, cancellationToken);
        }

        if (command == "navigateRecycleBin")
        {
            return NavigatePaneAsync(workspace, pane, PathRules.RecycleBinPath, HistoryMode.Push, activate: workspace.DualPaneEnabled, cancellationToken);
        }

        if (SpecialFolders.TryGetValue(command, out var folder))
        {
            return NavigatePaneAsync(
                workspace,
                pane,
                PathRules.JoinPath(workspace.HomePath, folder),
                HistoryMode.Push,
                activate: workspace.DualPaneEnabled,
                cancellationToken);
        }

        return Task.CompletedTask;
    }

    public async Task OpenPathAsync(
        ExplorerWorkspace workspace,
        string path,
        bool? isDirectory,
        PaneId pane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.SetFileOpenUnsupportedCore(false);
        var target = workspace.Normalize(pane);
        var shouldNavigate = isDirectory;
        if (PathRules.IsRecycleBinPath(path))
        {
            shouldNavigate = true;
        }

        var drive = workspace.DrivesController.FindDriveForPath(path);
        if (drive is not null && PathRules.PathsEqual(drive.Path, path))
        {
            shouldNavigate = true;
            if (DrivePresentation.IsNetwork(drive) && DrivePresentation.Status(drive) == "unknown")
            {
                await RefreshDriveAsync(workspace, drive.Path, quiet: true, cancellationToken).ConfigureAwait(false);
                drive = workspace.DrivesController.FindDriveForPath(path);
            }

            if (drive is not null && DrivePresentation.IsNetwork(drive) && !DrivePresentation.IsAvailable(drive))
            {
                workspace.DrivesController.BeginPendingReconnect(drive, target);
                workspace.NotifyChanged();
                return;
            }
        }

        if (shouldNavigate is null && workspace.FileOps is not null)
        {
            try
            {
                var entry = await workspace.FileOps.GetEntryInfoAsync(path, cancellationToken).ConfigureAwait(false);
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
            if (workspace.Settings.OpenInNewTab && !string.IsNullOrEmpty(workspace.Pane(target).Path))
            {
                await workspace.OpenNewTabAsync(target, path, cancellationToken).ConfigureAwait(false);
                return;
            }

            await NavigatePaneAsync(workspace, target, path, HistoryMode.Push, activate: workspace.DualPaneEnabled, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (shouldNavigate == false)
        {
            if (workspace.FileOps is not null)
            {
                try
                {
                    await workspace.FileOps.OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
                    lock (workspace.Gate)
                    {
                        workspace.Pane(target).SelectedPath = path;
                        workspace.SetStatusMessageCore($"Opened {PathRules.Basename(path)}");
                        workspace.SetErrorMessageCore(null);
                    }

                    workspace.NotifyChanged();
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (workspace.Gate)
                    {
                        workspace.Pane(target).SelectedPath = path;
                        workspace.SetErrorMessageCore(exception.Message);
                    }

                    workspace.NotifyChanged();
                    return;
                }
            }

            lock (workspace.Gate)
            {
                workspace.SetFileOpenUnsupportedCore(true);
                workspace.Pane(target).SelectedPath = path;
                workspace.SetStatusMessageCore("No file operation service is available to open this file.");
            }

            workspace.NotifyChanged();
            return;
        }

        await NavigatePaneAsync(workspace, target, path, HistoryMode.Push, activate: workspace.DualPaneEnabled, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool IsSupportedArchivePath(string path)
    {
        var name = PathRules.Basename(path).ToLowerInvariant();
        return name.EndsWith(".tar.gz", StringComparison.Ordinal)
            || name.EndsWith(".tgz", StringComparison.Ordinal)
            || name.EndsWith(".7z", StringComparison.Ordinal)
            || name.EndsWith(".zip", StringComparison.Ordinal)
            || name.EndsWith(".tar", StringComparison.Ordinal)
            || name.EndsWith(".rar", StringComparison.Ordinal);
    }
}
