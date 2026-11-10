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

        using var foregroundScope = workspace.ForegroundScheduler.BeginForegroundScope(ForegroundActivityKind.Navigation);
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
            state.Decorations = PaneDecorations.Empty;
            state.SelectedPath = null;
            workspace.SetErrorMessageCore(null);
            workspace.SetFileOpenUnsupportedCore(false);
            workspace.DrivesController.ClearPendingReconnect();
            state.PathIsNetwork = workspace.DrivesController.IsNetworkFsPath(path);
            workspace.ApplyFolderViewSettingsForPathLocked(target, path);
        }

        workspace.NotifyChanged();
        var published = false;
        var generation = 0;

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

            // The decorations are asked before the publish and waited for at most the coordinator's budget, so the
            // listing appears with them; the progressive chunks above never wait. Late results apply when they arrive.
            generation = workspace.RowDecorations.Generation;
            var decorations = await DecorateListingAsync(workspace, target, state, token, listing, listing.Path)
                .ConfigureAwait(false);

            lock (workspace.Gate)
            {
                if (token != state.NavigationToken)
                {
                    return;
                }

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
                PublishDecorationsLocked(state, decorations);
                published = true;
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
                RedecorateIfGenerationChanged(workspace, target, published, generation);
            }
        }
    }

    /// <summary>
    /// Asks the decorating plugins about a listing that is about to be published, under a new request number. A
    /// result that arrives after the budget goes through <see cref="ExplorerWorkspace.ApplyLateDecorations"/>.
    /// </summary>
    private static async Task<(long Request, PaneDecorations Decorations)> DecorateListingAsync(
        ExplorerWorkspace workspace,
        PaneId target,
        ExplorerPane state,
        int token,
        DirectoryListing listing,
        string folderPath)
    {
        long request;
        bool isNetwork;
        CancellationToken cancellation;
        lock (workspace.Gate)
        {
            if (token != state.NavigationToken)
            {
                // A newer navigation owns the pane: its publish check drops this listing, so ask nothing and leave
                // the newer navigation's decoration request alone.
                return (0, PaneDecorations.Empty);
            }

            request = state.NextDecorationRequest();
            cancellation = state.DecorationCancellation;
            isNetwork = listing.IsNetwork || workspace.DrivesController.IsNetworkFsPath(folderPath);
        }

        var decorations = await workspace.RowDecorations.DecorateAsync(
                folderPath,
                isNetwork,
                listing.Entries.Select(entry => entry.Path).ToList(),
                late => workspace.ApplyLateDecorations(target, token, request, folderPath, late),
                cancellation)
            .ConfigureAwait(false);
        return (request, decorations);
    }

    // Under the gate, with the listing: the result is kept unless the same request's late merge already landed. A
    // same-folder publish (the re-list after a rename, delete or create) keeps each pending plugin's shown part, as an
    // invalidation does, so a slow provider does not blank the badges and folder flags until its late merge. A
    // navigation cleared the pane's decorations when it started, and KeepingPendingFrom keeps nothing of another
    // folder, so a publish for a new folder is applied as it is.
    private static void PublishDecorationsLocked(ExplorerPane state, (long Request, PaneDecorations Decorations) result)
    {
        if (ExplorerWorkspace.ShouldApplyDecorationsLocked(state, result.Request, late: false))
        {
            state.Decorations = result.Decorations.KeepingPendingFrom(state.Decorations);
            state.AppliedDecorationRequest = result.Request;
            state.AppliedDecorationLate = false;
        }

        state.BumpContentVersion();
    }

    // A listing whose decorations were asked before startup became ready, or before the decorating plugins changed,
    // is decorated again as soon as it is published, without listing it again.
    private static void RedecorateIfGenerationChanged(ExplorerWorkspace workspace, PaneId target, bool published, int generation)
    {
        if (published && workspace.RowDecorations.Generation != generation)
        {
            workspace.StartDecorationRefresh(target);
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

    /// <summary>
    /// Re-lists the visible panes whose path is, or is inside, a file ending with one of
    /// <paramref name="extensions"/>: the archives whose engine was just registered or unregistered.
    /// </summary>
    public async Task RefreshArchivePanesAsync(
        ExplorerWorkspace workspace,
        IReadOnlyCollection<string> extensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(extensions);
        var normalized = extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim())
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return;
        }

        List<PaneId> panes = [];
        if (PaneIsInsideArchive(workspace.Primary.Path, normalized))
        {
            panes.Add(PaneId.Primary);
        }

        if (workspace.DualPaneEnabled && PaneIsInsideArchive(workspace.Secondary.Path, normalized))
        {
            panes.Add(PaneId.Secondary);
        }

        foreach (var pane in panes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshAsync(workspace, pane, engineChanged: true, cancellationToken).ConfigureAwait(false);
        }
    }

    // True when one of the path's segments (the archive file itself, or an ancestor of the pane's folder inside
    // it) ends with one of the extensions.
    private static bool PaneIsInsideArchive(string? panePath, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(panePath))
        {
            return false;
        }

        return panePath
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => extensions.Any(extension =>
                segment.Length > extension.Length
                && segment.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));
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

    public Task RefreshAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        CancellationToken cancellationToken = default) =>
        RefreshAsync(workspace, pane, engineChanged: false, cancellationToken);

    /// <param name="engineChanged">
    /// The pane is inside an archive whose plugin engine came or went. The re-list then stands in for navigating
    /// to the folder again: a failure drops the rows the gone engine had listed, and a success clears the error
    /// (the plugin note) an earlier listing of the folder left.
    /// </param>
    private async Task RefreshAsync(
        ExplorerWorkspace workspace,
        PaneId pane,
        bool engineChanged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var target = workspace.Normalize(pane);
        var state = workspace.Pane(target);
        var path = state.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        using var foregroundScope = workspace.ForegroundScheduler.BeginForegroundScope(ForegroundActivityKind.Navigation);
        var selected = state.SelectedPath;
        var token = state.NextNavigationToken();

        lock (workspace.Gate)
        {
            state.ListingInProgress = true;
        }

        var published = false;
        var generation = 0;
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

            generation = workspace.RowDecorations.Generation;
            var decorations = await DecorateListingAsync(
                    workspace,
                    target,
                    state,
                    token,
                    listing,
                    string.IsNullOrEmpty(listing.Path) ? path : listing.Path)
                .ConfigureAwait(false);

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
                if (engineChanged)
                {
                    workspace.SetErrorMessageCore(null);
                }

                PublishDecorationsLocked(state, decorations);
                published = true;
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
                    if (engineChanged && token == state.NavigationToken)
                    {
                        state.Entries = [];
                        state.SelectedPath = null;
                        state.SyncActiveTab();
                    }
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
                RedecorateIfGenerationChanged(workspace, target, published, generation);
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

    public static bool IsSupportedArchivePath(string path) => ArchivePaths.IsBrowsableArchive(path);
}
