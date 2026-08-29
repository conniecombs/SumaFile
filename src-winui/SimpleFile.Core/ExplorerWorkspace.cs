using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

/// <summary>
/// Dual-pane navigation + pane-local tabs, ported from
/// frontend/src/lib/app/core.ts (loadDirectory, loadSecondaryDirectory,
/// loadDirectoryForPane, toggleDualPane, openNewTab / switchToTab / closeTab,
/// activatePane) and SidebarShell sidebar targeting.
/// </summary>
public sealed class ExplorerWorkspace
{
    private const string BookmarksSettingsKey = "places.bookmarks";
    private const string RecentPathsSettingsKey = "places.recents";

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

    private readonly IExplorerBackend _backend;
    private readonly object _gate = new();
    private List<DriveInfo> _drives = [];

    public ExplorerWorkspace(IExplorerBackend backend, FileOperationService? fileOps = null)
    {
        _backend = backend;
        FileOps = fileOps;
        Clipboard = new ClipboardState();
        Undo = new UndoStack();
        Columns = new ColumnLayout();
        Settings = UiSettings.CreateDefault();
        Primary = new ExplorerPane(PaneId.Primary);
        Secondary = new ExplorerPane(PaneId.Secondary);
        ApplyDefaultViewOptionsToPanes();
    }

    public event EventHandler? Changed;

    public FileOperationService? FileOps { get; }
    public ClipboardState Clipboard { get; }
    public UndoStack Undo { get; }
    public ColumnLayout Columns { get; }
    public UiSettings Settings { get; private set; }
    public ExplorerPane Primary { get; }
    public ExplorerPane Secondary { get; }

    public string HomePath { get; private set; } = "";
    public bool DualPaneEnabled { get; private set; }
    public PaneId ActivePane { get; private set; } = PaneId.Primary;
    public string SortBy => SortByFor(ActivePane);
    public bool SortAscending => SortAscendingFor(ActivePane);
    public bool ShowHiddenFiles { get; private set; }
    private string _primaryFilterQuery = "";
    private string _secondaryFilterQuery = "";
    public string? ErrorMessage { get; private set; }
    public string? StatusMessage { get; private set; }
    public DriveInfo? PendingReconnect { get; private set; }
    public PaneId PendingReconnectPane { get; private set; } = PaneId.Primary;
    public bool FileOpenUnsupported { get; private set; }

    public List<SmartFolder> SmartFolders { get; private set; } = [];
    public List<Tag> AllTags { get; private set; } = [];
    public Dictionary<string, Tag> FileTags { get; private set; } = new();
    public List<BookmarkItem> Bookmarks { get; private set; } = [];
    public List<string> RecentPaths { get; private set; } = [];
    public List<FolderTreeItem> FolderTreeRows { get; private set; } = [];
    public ClipboardHistory ClipboardHistory { get; } = new();
    public List<OperationRecord> OperationLog { get; } = [];
    public long? ActiveTagFilter { get; private set; }
    public bool PhotoFolderActive { get; private set; }
    public TypeAheadBuffer TypeAheadBuffer { get; } = new();

    public IReadOnlyList<DriveInfo> Drives => _drives;

    public PaneId SidebarTarget =>
        DualPaneEnabled && ActivePane == PaneId.Secondary ? PaneId.Secondary : PaneId.Primary;

    public ExplorerPane Active => Pane(ActivePane);

    public ExplorerPane Pane(PaneId pane) =>
        Normalize(pane) == PaneId.Secondary ? Secondary : Primary;

    public PaneId Normalize(PaneId pane) =>
        pane == PaneId.Secondary && DualPaneEnabled ? PaneId.Secondary : PaneId.Primary;

    private static bool IsSupportedArchivePath(string path)
    {
        var name = PathRules.Basename(path).ToLowerInvariant();
        return name.EndsWith(".tar.gz", StringComparison.Ordinal)
            || name.EndsWith(".tgz", StringComparison.Ordinal)
            || name.EndsWith(".zip", StringComparison.Ordinal)
            || name.EndsWith(".tar", StringComparison.Ordinal)
            || name.EndsWith(".rar", StringComparison.Ordinal);
    }

    public string CurrentPath => Primary.Path;
    public IReadOnlyList<FileEntry> Entries => Primary.Entries;
    public IReadOnlyList<string> History => Primary.History;
    public int HistoryIndex => Primary.HistoryIndex;
    public IReadOnlyList<FileEntry> VisibleEntries =>
        Primary.VisibleEntries(ShowHiddenFiles, FilterQueryFor(PaneId.Primary), Settings.KeepFoldersOnTop);
    public IReadOnlyList<BreadcrumbSegment> Breadcrumbs => Primary.Breadcrumbs;
    public bool IsNavigating => Primary.IsNavigating;
    public bool ListingInProgress => Primary.ListingInProgress;
    public bool PathIsNetwork => Primary.PathIsNetwork;
    public string? SelectedPath => Active.SelectedPath;
    public bool CanGoBack => Active.CanGoBack;
    public bool CanGoForward => Active.CanGoForward;
    public bool CanGoUp => Active.CanGoUp;

    public string? ActivePaneLabel =>
        DualPaneEnabled ? (ActivePane == PaneId.Secondary ? "Right pane" : "Left pane") : null;

    public string FilterQuery => FilterQueryFor(ActivePane);
    public string FileListView => ViewFor(ActivePane);
    public int FileListIconSize => IconSizeFor(ActivePane);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        HomePath = await _backend.GetHomeDirAsync(cancellationToken).ConfigureAwait(false);
        await RefreshDrivesAsync(quiet: true, cancellationToken).ConfigureAwait(false);

        await LoadSmartFoldersAsync(cancellationToken).ConfigureAwait(false);
        await LoadTagsAsync(cancellationToken).ConfigureAwait(false);
        await LoadUiSettingsAsync(cancellationToken).ConfigureAwait(false);

        var startMode = UiSettings.NormalizeStartLocation(Settings.StartLocation);
        if (startMode == "last" && await TryRestoreWorkspaceLayoutAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var startPath = ResolveStartPath();
        await NavigatePaneAsync(PaneId.Primary, startPath, HistoryMode.Push, activate: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public string ResolveStartPath()
    {
        var mode = UiSettings.NormalizeStartLocation(Settings.StartLocation);
        if (mode == "custom" && !string.IsNullOrWhiteSpace(Settings.CustomPath))
        {
            return Settings.CustomPath.Trim();
        }

        if (mode == "last" && !string.IsNullOrWhiteSpace(Settings.LastPath))
        {
            return Settings.LastPath.Trim();
        }

        return string.IsNullOrEmpty(HomePath) ? Primary.Path : HomePath;
    }

    public void ApplyUiSettings(UiSettings settings, bool applyViewDefaultsToPanes = true)
    {
        Settings = settings;
        Settings.DefaultView = UiSettings.NormalizeDefaultView(settings.DefaultView);
        Settings.DefaultIconSize = UiSettings.NormalizeIconSize(settings.DefaultIconSize);
        Settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(settings.SidebarWidth);
        Settings.PreviewWidth = UiSettings.NormalizePreviewWidth(settings.PreviewWidth);
        Settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(settings.DualPanePrimaryPercent);
        Settings.DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(settings.DualPanePrimaryWidth);
        ShowHiddenFiles = settings.ShowHidden;
        if (applyViewDefaultsToPanes)
        {
            ApplyDefaultViewOptionsToPanes();
        }

        Columns.ApplyPreset(string.IsNullOrWhiteSpace(settings.ColumnPreset) ? "default" : settings.ColumnPreset);
        Columns.RestoreWidths(settings.ColumnWidths);
        RaiseChanged();
    }

    public void SetShowHidden(bool showHidden)
    {
        ShowHiddenFiles = showHidden;
        Settings.ShowHidden = showHidden;
        RaiseChanged();
    }

    public void SetFileListView(string view)
    {
        SetFileListView(ActivePane, view);
    }

    public void SetFileListView(PaneId pane, string view)
    {
        var target = Pane(pane);
        var next = UiSettings.NormalizeDefaultView(view);
        if (string.Equals(target.View, next, StringComparison.Ordinal))
        {
            return;
        }

        target.View = next;
        RaiseChanged();
    }

    public void SetFileListIconSize(int iconSize)
    {
        SetFileListIconSize(ActivePane, iconSize);
    }

    public void SetFileListIconSize(PaneId pane, int iconSize)
    {
        var target = Pane(pane);
        var next = UiSettings.NormalizeIconSize(iconSize);
        if (target.IconSize == next)
        {
            return;
        }

        target.IconSize = next;
        RaiseChanged();
    }

    public int NudgeFileListIconSize(PaneId pane, int steps)
    {
        var target = Pane(pane);
        var next = UiSettings.NormalizeIconSize(target.IconSize + (steps * UiSettings.IconSizeStep));
        SetFileListIconSize(pane, next);
        return target.IconSize;
    }

    public bool ToggleShowHidden()
    {
        SetShowHidden(!ShowHiddenFiles);
        return ShowHiddenFiles;
    }

    public void ApplyViewOptionsToBothPanes(PaneId sourcePane)
    {
        var source = Pane(sourcePane);
        var primaryChanged = !SameViewOptions(Primary, source);
        var secondaryChanged = !SameViewOptions(Secondary, source);
        Primary.CopyViewOptionsFrom(source);
        Secondary.CopyViewOptionsFrom(source);
        if (primaryChanged || secondaryChanged)
        {
            RaiseChanged();
        }
    }

    public string ViewFor(PaneId pane)
    {
        return Pane(pane).View;
    }

    public int IconSizeFor(PaneId pane)
    {
        return Pane(pane).IconSize;
    }

    public string SortByFor(PaneId pane)
    {
        return Pane(pane).SortBy;
    }

    public bool SortAscendingFor(PaneId pane)
    {
        return Pane(pane).SortAscending;
    }

    private static bool SameViewOptions(ExplorerPane left, ExplorerPane right)
    {
        return string.Equals(left.View, right.View, StringComparison.Ordinal)
            && left.IconSize == right.IconSize
            && string.Equals(left.SortBy, right.SortBy, StringComparison.OrdinalIgnoreCase)
            && left.SortAscending == right.SortAscending;
    }

    private void ApplyDefaultViewOptionsToPanes()
    {
        Primary.ApplyViewDefaults(Settings);
        Secondary.ApplyViewDefaults(Settings);
    }

    public void SetSort(string sortBy)
    {
        SetSort(ActivePane, sortBy);
    }

    public void SetSort(PaneId pane, string sortBy)
    {
        var target = Pane(pane);
        var next = string.IsNullOrWhiteSpace(sortBy) ? "name" : sortBy;
        var currentSort = target.SortBy;
        var currentAscending = target.SortAscending;
        target.SetSort(next);
        if (!string.Equals(target.SortBy, currentSort, StringComparison.OrdinalIgnoreCase)
            || target.SortAscending != currentAscending)
        {
            RaiseChanged();
        }
    }

    public ExplorerPane OtherPane()
    {
        return ActivePane == PaneId.Secondary ? Primary : Secondary;
    }

    public string? OtherPanePath()
    {
        if (!DualPaneEnabled)
        {
            return null;
        }

        var path = OtherPane().Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public async Task RefreshDrivesAsync(bool quiet = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var drives = await _backend.ListDrivesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
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
        List<FileEntry> progressive = [];

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
        }

        RaiseChanged();

        try
        {
            DirectoryListing listing;
            try
            {
                listing = await _backend.ListDirectoryAsync(
                        path,
                        chunk =>
                        {
                            if (token != state.NavigationToken)
                            {
                                return;
                            }

                            lock (_gate)
                            {
                                if (chunk.IsNetwork)
                                {
                                    state.PathIsNetwork = true;
                                }

                                if (!string.IsNullOrEmpty(chunk.Path))
                                {
                                    state.Path = chunk.Path;
                                }

                                progressive.AddRange(chunk.Entries);
                                state.Entries = [.. progressive];
                                if (progressive.Count > 0)
                                {
                                    state.IsNavigating = false;
                                }
                            }

                            RaiseChanged();
                        },
                        cancellationToken,
                        BuildStreamedListingOptions(state))
                    .ConfigureAwait(false);
            }
            catch (IpcException exception) when (exception.IsResultTooLarge && progressive.Count > 0)
            {
                lock (_gate)
                {
                    if (token != state.NavigationToken)
                    {
                        return;
                    }

                    state.RecordHistory(state.Path, historyMode);
                    state.SyncActiveTab();
                    StatusMessage = exception.Message;
                }

                RaiseChanged();
                return;
            }

            if (token != state.NavigationToken)
            {
                return;
            }

            lock (_gate)
            {
                state.Path = listing.Path;
                state.Entries = [.. listing.Entries];
                state.PathIsNetwork = listing.IsNetwork || PathRules.IsNetworkFsPath(listing.Path, _drives);
                state.RecordHistory(listing.Path, historyMode);
                state.SyncActiveTab();
                StatusMessage = null;
                RecentPaths = PlacesStore.RecordRecent(RecentPaths, listing.Path);
                PhotoFolderActive = PhotoFolder.IsPhotoFolder(listing.Entries, Settings.PhotoFolderImageThreshold);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (token != state.NavigationToken)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var refreshNetworkStatus = false;
            lock (_gate)
            {
                var drive = DrivePresentation.FindDriveForPath(path, _drives);
                ErrorMessage = drive is not null && DrivePresentation.IsNetwork(drive)
                    ? (drive.StatusDetail ?? exception.Message)
                    : exception.Message;
                refreshNetworkStatus = drive is not null && DrivePresentation.IsNetwork(drive);
            }

            if (refreshNetworkStatus)
            {
                await RefreshDrivesAsync(quiet: true, cancellationToken).ConfigureAwait(false);
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

    public Task GoForwardAsync(CancellationToken cancellationToken = default)
    {
        return GoForwardAsync(ActivePane, cancellationToken);
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

    public Task GoUpAsync(CancellationToken cancellationToken = default)
    {
        return GoUpAsync(ActivePane, cancellationToken);
    }

    public Task GoUpAsync(PaneId pane, CancellationToken cancellationToken = default)
    {
        var parent = PathRules.GetParentPath(Pane(pane).Path);
        return parent is null
            ? Task.CompletedTask
            : NavigatePaneAsync(pane, parent, HistoryMode.Push, activate: DualPaneEnabled, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return RefreshAsync(ActivePane, cancellationToken);
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

        var token = state.NextNavigationToken();
        var selected = state.SelectedPath;
        List<FileEntry> progressive = [];

        lock (_gate)
        {
            state.ListingInProgress = true;
        }

        try
        {
            var listing = await _backend.ListDirectoryAsync(
                    path,
                    chunk =>
                    {
                        if (token != state.NavigationToken || !PathRules.PathsEqual(state.Path, path))
                        {
                            return;
                        }

                        lock (_gate)
                        {
                            progressive.AddRange(chunk.Entries);
                            state.Entries = [.. progressive];
                        }

                        RaiseChanged();
                    },
                    cancellationToken,
                    BuildStreamedListingOptions(state))
                .ConfigureAwait(false);

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
                PhotoFolderActive = PhotoFolder.IsPhotoFolder(listing.Entries, Settings.PhotoFolderImageThreshold);
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

    public Task NavigateSpecialAsync(string command, CancellationToken cancellationToken = default)
    {
        return NavigateSpecialAsync(command, SidebarTarget, cancellationToken);
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

    public Task OpenEntryAsync(FileEntry entry, CancellationToken cancellationToken = default)
    {
        return OpenPathAsync(entry.Path, entry.IsDir, ActivePane, cancellationToken);
    }

    public Task OpenEntryAsync(FileEntry entry, PaneId pane, CancellationToken cancellationToken = default)
    {
        return OpenPathAsync(entry.Path, entry.IsDir, pane, cancellationToken);
    }

    public Task OpenPathAsync(
        string path,
        bool? isDirectory = null,
        CancellationToken cancellationToken = default)
    {
        return OpenPathAsync(path, isDirectory, ActivePane, cancellationToken);
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
            if (DrivePresentation.IsNetwork(drive) && !DrivePresentation.IsAvailable(drive))
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

    public async Task RetryPendingDriveAsync(CancellationToken cancellationToken = default)
    {
        var pending = PendingReconnect;
        var pane = PendingReconnectPane;
        PendingReconnect = null;
        if (pending is null)
        {
            return;
        }

        await RefreshDrivesAsync(quiet: true, cancellationToken).ConfigureAwait(false);

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

    public async Task ToggleDualPaneAsync(CancellationToken cancellationToken = default)
    {
        if (DualPaneEnabled)
        {
            DualPaneEnabled = false;
            ActivePane = PaneId.Primary;
            RaiseChanged();
            return;
        }

        DualPaneEnabled = true;
        if (string.IsNullOrEmpty(Secondary.Path))
        {
            await NavigatePaneAsync(
                    PaneId.Secondary,
                    Primary.Path,
                    HistoryMode.ReplaceCurrent,
                    activate: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            lock (_gate)
            {
                Secondary.EnsureActiveTab(Primary.Path);
            }
        }

        ActivePane = PaneId.Primary;
        RaiseChanged();
    }

    public Task CloseFilePaneAsync(PaneId pane, CancellationToken cancellationToken = default)
    {
        if (!DualPaneEnabled)
        {
            return Task.CompletedTask;
        }

        if (Normalize(pane) == PaneId.Primary)
        {
            SwapFilePanes();
        }

        DualPaneEnabled = false;
        ActivePane = PaneId.Primary;
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void SwapFilePanes()
    {
        Primary.SwapContents(Secondary);
        (_primaryFilterQuery, _secondaryFilterQuery) = (_secondaryFilterQuery, _primaryFilterQuery);
    }

    public void ActivatePane(PaneId pane)
    {
        var next = DualPaneEnabled && pane == PaneId.Secondary ? PaneId.Secondary : PaneId.Primary;
        if (ActivePane == next)
        {
            return;
        }

        ActivePane = next;
        RaiseChanged();
    }

    public void SwitchActivePane()
    {
        if (!DualPaneEnabled)
        {
            return;
        }

        ActivatePane(ActivePane == PaneId.Primary ? PaneId.Secondary : PaneId.Primary);
    }

    public async Task FocusSecondaryAsync(CancellationToken cancellationToken = default)
    {
        if (!DualPaneEnabled)
        {
            await ToggleDualPaneAsync(cancellationToken).ConfigureAwait(false);
        }

        ActivatePane(PaneId.Secondary);
    }

    public async Task OpenNewTabAsync(PaneId? pane = null, string? path = null, CancellationToken cancellationToken = default)
    {
        var target = Normalize(pane ?? ActivePane);
        var state = Pane(target);
        var targetPath = path ?? state.Path;
        if (string.IsNullOrEmpty(targetPath))
        {
            targetPath = HomePath;
        }

        if (string.IsNullOrEmpty(targetPath))
        {
            return;
        }

        var tab = ExplorerPane.CreateTab(targetPath);
        lock (_gate)
        {
            state.Tabs.Add(tab);
            state.ApplyTabHistory(tab);
        }

        await NavigatePaneAsync(target, targetPath, HistoryMode.ReplaceCurrent, activate: DualPaneEnabled, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SwitchToTabAsync(string tabId, PaneId pane, CancellationToken cancellationToken = default)
    {
        var target = Normalize(pane);
        FileTab? tab;
        lock (_gate)
        {
            tab = Pane(target).Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            if (tab is null)
            {
                return;
            }

            Pane(target).ApplyTabHistory(tab);
        }

        await NavigatePaneAsync(target, tab.Path, HistoryMode.None, activate: DualPaneEnabled, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CloseTabAsync(string tabId, PaneId pane, CancellationToken cancellationToken = default)
    {
        var target = Normalize(pane);
        string? nextId = null;
        string? homeFallback = null;
        PaneId? paneToClose = null;
        lock (_gate)
        {
            var state = Pane(target);
            var closingIndex = state.Tabs.FindIndex(tab => tab.Id == tabId);
            if (closingIndex < 0)
            {
                return;
            }

            state.Tabs.RemoveAt(closingIndex);
            if (state.Tabs.Count == 0)
            {
                state.ActiveTabId = null;
                if (DualPaneEnabled)
                {
                    paneToClose = target;
                }
                else
                {
                    homeFallback = HomePath;
                    if (string.IsNullOrEmpty(homeFallback))
                    {
                        homeFallback = state.Path;
                    }

                    if (string.IsNullOrEmpty(homeFallback))
                    {
                        homeFallback = Primary.Path;
                    }
                }
            }
            else if (state.ActiveTabId == tabId)
            {
                var next = state.Tabs[Math.Min(closingIndex, state.Tabs.Count - 1)];
                nextId = next.Id;
            }
        }

        if (paneToClose is not null)
        {
            await CloseFilePaneAsync(paneToClose.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (homeFallback is not null)
        {
            await OpenNewTabAsync(target, homeFallback, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (nextId is not null)
        {
            await SwitchToTabAsync(nextId, target, cancellationToken).ConfigureAwait(false);
            return;
        }

        RaiseChanged();
    }

    public Task SwitchTabByAsync(int delta, CancellationToken cancellationToken = default)
    {
        var state = Active;
        if (state.Tabs.Count == 0)
        {
            return Task.CompletedTask;
        }

        var activeIndex = Math.Max(0, state.Tabs.FindIndex(tab => tab.Id == state.ActiveTabId));
        var next = state.Tabs[(activeIndex + delta % state.Tabs.Count + state.Tabs.Count) % state.Tabs.Count];
        return SwitchToTabAsync(next.Id, ActivePane, cancellationToken);
    }

    public Task SwitchToTabAtAsync(int oneBasedIndex, CancellationToken cancellationToken = default)
    {
        var state = Active;
        if (state.Tabs.Count == 0 || oneBasedIndex < 1)
        {
            return Task.CompletedTask;
        }

        var index = oneBasedIndex >= 9
            ? state.Tabs.Count - 1
            : oneBasedIndex - 1;
        if (index < 0 || index >= state.Tabs.Count)
        {
            return Task.CompletedTask;
        }

        return SwitchToTabAsync(state.Tabs[index].Id, ActivePane, cancellationToken);
    }

    public async Task OpenInOtherPaneAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!DualPaneEnabled)
        {
            await ToggleDualPaneAsync(cancellationToken).ConfigureAwait(false);
        }

        var other = ActivePane == PaneId.Primary ? PaneId.Secondary : PaneId.Primary;
        var destination = isDirectory ? path : PathRules.GetParentPath(path) ?? path;
        await NavigatePaneAsync(other, destination, HistoryMode.Push, activate: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public void SelectPath(string? path)
    {
        SelectPath(path, ActivePane);
    }

    public void SelectPath(string? path, PaneId pane)
    {
        var target = Pane(pane);
        var nextPane = DualPaneEnabled ? Normalize(pane) : PaneId.Primary;
        var paneChanged = ActivePane != nextPane;
        target.SelectedPath = path;
        ActivePane = nextPane;
        if (paneChanged)
        {
            RaiseChanged();
        }
    }

    public void SetFilterQuery(string query)
    {
        SetFilterQuery(ActivePane, query);
    }

    public void SetFilterQuery(PaneId pane, string query)
    {
        var target = Normalize(pane);
        var normalized = query ?? "";
        var existing = FilterQueryFor(target);
        var state = Pane(target);
        var selectionCleared = false;
        if (!string.IsNullOrEmpty(state.SelectedPath))
        {
            var visible = state.VisibleEntries(ShowHiddenFiles, normalized, Settings.KeepFoldersOnTop);
            if (!visible.Any(entry => PathRules.PathsEqual(entry.Path, state.SelectedPath)))
            {
                state.SelectedPath = null;
                selectionCleared = true;
            }
        }

        if (target == PaneId.Secondary)
        {
            _secondaryFilterQuery = normalized;
        }
        else
        {
            _primaryFilterQuery = normalized;
        }

        if (string.Equals(existing, normalized, StringComparison.Ordinal) && !selectionCleared)
        {
            return;
        }

        RaiseChanged();
    }

    public void ClearStatus()
    {
        StatusMessage = null;
        ErrorMessage = null;
        FileOpenUnsupported = false;
        RaiseChanged();
    }

    // --- Smart Folders ---

    private async Task LoadSmartFoldersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ops = RequireFileOps();
            var folders = await ops.LoadSmartFoldersAsync(cancellationToken).ConfigureAwait(false);
            SmartFolders = [.. folders];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SmartFolders = [];
        }
    }

    public async Task SaveSmartFolderAsync(SmartFolder folder, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        var updated = await ops.SaveSmartFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        SmartFolders = [.. updated];
        RaiseChanged();
    }

    public async Task DeleteSmartFolderAsync(string id, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        var updated = await ops.DeleteSmartFolderAsync(id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        SmartFolders = [.. updated];
        RaiseChanged();
    }

    // --- Tags ---

    private static readonly Tag[] DefaultTags =
    [
        new() { Name = "Red", Color = "#ef4444" },
        new() { Name = "Orange", Color = "#f97316" },
        new() { Name = "Yellow", Color = "#eab308" },
        new() { Name = "Green", Color = "#22c55e" },
        new() { Name = "Blue", Color = "#3b82f6" },
        new() { Name = "Purple", Color = "#a855f7" },
    ];

    private async Task LoadTagsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ops = RequireFileOps();
            var tags = await ops.GetAllTagsAsync(cancellationToken).ConfigureAwait(false);
            if (tags.Length == 0)
            {
                foreach (var dt in DefaultTags)
                {
                    await ops.CreateTagAsync(dt.Name, dt.Color, cancellationToken).ConfigureAwait(false);
                }
                tags = await ops.GetAllTagsAsync(cancellationToken).ConfigureAwait(false);
            }
            AllTags = [.. tags];
            FileTags = await ops.GetAllFileTagsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AllTags = [];
            FileTags = new();
        }
    }

    public async Task SetColorLabelAsync(string[] paths, long tagId, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        foreach (var path in paths)
        {
            await ops.SetTagsForPathAsync(path, [tagId], cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        FileTags = await ops.GetAllFileTagsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RaiseChanged();
    }

    public async Task RemoveColorLabelAsync(string[] paths, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        foreach (var path in paths)
        {
            await ops.SetTagsForPathAsync(path, [], cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        FileTags = await ops.GetAllFileTagsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right);
        }

        return PathRules.PathsEqual(left, right);
    }

    private FileOperationService RequireFileOps()
        => FileOps ?? throw new InvalidOperationException(
            "FileOperationService is required for file operations.");

    public async Task<string> CreateFolderInCurrentPaneAsync(string name, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        var path = ActivePane == PaneId.Primary ? Primary.Path : Secondary.Path;
        var result = await ops.CreateFolderAsync(path, name, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<string> CreateFileInCurrentPaneAsync(string name, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        var path = ActivePane == PaneId.Primary ? Primary.Path : Secondary.Path;
        var result = await ops.CreateFileAsync(path, name, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task TrashSelectedAsync(string[] selectedPaths, CancellationToken cancellationToken = default)
    {
        await RequireFileOps().TrashAsync(selectedPaths, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken cancellationToken = default)
    {
        var restored = await RequireFileOps().RestoreRecycleBinAsync(paths, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return restored;
    }

    public async Task EmptyRecycleBinAsync(CancellationToken cancellationToken = default)
    {
        await RequireFileOps().EmptyRecycleBinAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSelectedAsync(string path, CancellationToken cancellationToken = default)
    {
        await RequireFileOps().DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RenameSelectedAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        var ops = RequireFileOps();
        var result = await ops.RenameAsync(path, newName, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await RequireFileOps().OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevealInFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        await RequireFileOps().RevealInFolderAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public void AddBookmark(string path)
    {
        Bookmarks = PlacesStore.AddBookmark(Bookmarks, path);
        RaiseChanged();
    }

    public void RemoveBookmark(string path)
    {
        Bookmarks = PlacesStore.RemoveBookmark(Bookmarks, path);
        RaiseChanged();
    }

    public void ClearRecentHistory()
    {
        if (RecentPaths.Count == 0)
        {
            return;
        }

        RecentPaths = [];
        RaiseChanged();
    }

    public void SetTagFilter(long? tagId)
    {
        ActiveTagFilter = tagId;
        RaiseChanged();
    }

    public IReadOnlyList<FileEntry> VisibleEntriesFor(PaneId pane)
    {
        var target = Normalize(pane);
        var state = Pane(target);
        var keepFoldersOnTop = Settings.KeepFoldersOnTop;
        var canUsePresorted = state.ListingInProgress
            && keepFoldersOnTop
            && string.Equals(state.SortBy, "name", StringComparison.OrdinalIgnoreCase)
            && state.SortAscending;
        var entries = canUsePresorted
            ? EntryPresentation.VisibleEntriesPreSorted(state.Entries, FilterQueryFor(target), ShowHiddenFiles)
            : state.VisibleEntries(ShowHiddenFiles, FilterQueryFor(target), keepFoldersOnTop);

        if (ActiveTagFilter is long tagId)
        {
            var tagged = FileTags
                .Where(pair => pair.Value.Id == tagId)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries = entries.Where(entry => tagged.Contains(entry.Path)).ToList();
        }

        return entries;
    }

    private static ListDirectoryOptions BuildStreamedListingOptions(ExplorerPane pane)
    {
        return new ListDirectoryOptions
        {
            Mode = "light",
            FinalEntries = false,
            SortBy = pane.SortBy,
            SortAscending = pane.SortAscending,
            IncludeHidden = true,
        };
    }

    public string FilterQueryFor(PaneId pane)
    {
        return Normalize(pane) == PaneId.Secondary ? _secondaryFilterQuery : _primaryFilterQuery;
    }

    public async Task SaveCurrentSearchAsSmartFolderAsync(
        string name,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var folder = new SmartFolder
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Icon = "search",
            SearchOptions = options,
        };
        await SaveSmartFolderAsync(folder, cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadTreeChildrenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (FileOps is null)
        {
            return;
        }

        try
        {
            var children = await FileOps.ListSubdirectoriesAsync(path, cancellationToken).ConfigureAwait(false);
            var expanded = new HashSet<string>(
                FolderTreeRows.Where(row => row.Expanded).Select(row => row.Path),
                StringComparer.OrdinalIgnoreCase)
            {
                path,
            };
            var roots = FolderTreeRows.Count == 0
                ? children.ToList()
                : MergeTree(FolderTreeRows, path, children);
            FolderTreeRows = FolderTree.Flatten(roots, expanded);
        }
        catch
        {
            // Tree is optional; listing still works.
        }

        RaiseChanged();
    }

    public void ToggleTreeExpanded(string path)
    {
        var expanded = new HashSet<string>(
            FolderTreeRows.Where(row => row.Expanded).Select(row => row.Path),
            StringComparer.OrdinalIgnoreCase);
        if (!expanded.Add(path))
        {
            expanded.Remove(path);
        }

        var roots = ReconstructRoots(FolderTreeRows);
        FolderTreeRows = FolderTree.Flatten(roots, expanded);
        RaiseChanged();
    }

    public async Task ApplyGitStatusesAsync(CancellationToken cancellationToken = default)
    {
        await ApplyGitStatusesAsync(ActivePane, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyGitStatusesAsync(PaneId pane, CancellationToken cancellationToken = default)
    {
        if (FileOps is null || !Settings.EnableGitIntegration)
        {
            return;
        }

        var target = Normalize(pane);
        var state = Pane(target);
        var path = state.Path;
        var navigationToken = state.NavigationToken;
        try
        {
            var statuses = await FileOps.GetGitFileStatusesAsync(path, cancellationToken).ConfigureAwait(false);
            var map = statuses.ToDictionary(entry => entry.Path, entry => entry.GitStatus ?? "", StringComparer.OrdinalIgnoreCase);
            if (navigationToken != state.NavigationToken || !PathRules.PathsEqual(state.Path, path))
            {
                return;
            }

            foreach (var entry in state.Entries)
            {
                if (map.TryGetValue(entry.Path, out var status))
                {
                    entry.GitStatus = status;
                }
            }

            RaiseChanged();
        }
        catch
        {
            // Git is optional.
        }
    }

    public async Task FillFolderSizesAsync(CancellationToken cancellationToken = default)
    {
        await FillFolderMetricsAsync(ActivePane, includeSizes: Settings.ShowFolderSizes, includeItemCounts: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task FillFolderMetricsAsync(
        PaneId pane,
        bool includeSizes,
        bool includeItemCounts,
        CancellationToken cancellationToken = default)
    {
        if (FileOps is null || (!includeSizes && !includeItemCounts))
        {
            return;
        }

        var target = Normalize(pane);
        var state = Pane(target);
        var path = state.Path;
        var navigationToken = state.NavigationToken;
        var folders = state.Entries.Where(item => item.IsDir).Take(32).ToList();
        var changed = false;
        bool IsCurrent() =>
            !cancellationToken.IsCancellationRequested
            && navigationToken == state.NavigationToken
            && PathRules.PathsEqual(state.Path, path);

        foreach (var entry in folders)
        {
            if (!IsCurrent())
            {
                return;
            }

            try
            {
                if (includeSizes)
                {
                    var size = await FileOps.CalculateFolderSizeAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                    if (!IsCurrent())
                    {
                        return;
                    }

                    entry.Size = size;
                    changed = true;
                }

                if (includeItemCounts)
                {
                    var itemCount = await FileOps.CountFolderItemsAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                    if (!IsCurrent())
                    {
                        return;
                    }

                    entry.ItemCount = itemCount;
                    changed = true;
                }
            }
            catch
            {
                // Skip folders that cannot be measured or counted.
            }
        }

        if (changed && IsCurrent())
        {
            RaiseChanged();
        }
    }

    public void RememberClipboard()
    {
        if (Clipboard.HasItems)
        {
            ClipboardHistory.Push(Clipboard.Operation, Clipboard.SourcePaths);
        }
    }

    public void RememberOperation(string kind, string description, string[] sources, string destination, bool move)
    {
        OperationLog.Insert(0, new OperationRecord
        {
            Kind = kind,
            Description = description,
            Sources = sources,
            Destination = destination,
            Move = move,
        });
        if (OperationLog.Count > 50)
        {
            OperationLog.RemoveRange(50, OperationLog.Count - 50);
        }
    }

    public async Task RetryOperationAsync(OperationRecord record, CancellationToken cancellationToken = default)
    {
        if (FileOps is null || record.Sources.Length == 0 || string.IsNullOrWhiteSpace(record.Destination))
        {
            return;
        }

        if (record.Move)
        {
            await FileOps.MoveAsync(record.Sources, record.Destination, "keep-both", ct: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await FileOps.CopyAsync(record.Sources, record.Destination, "keep-both", ct: cancellationToken).ConfigureAwait(false);
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public FileEntry? MatchTypeAhead(char character)
    {
        var buffer = TypeAheadBuffer.Append(character, TimeSpan.FromSeconds(1));
        var index = TypeAhead.MatchIndex(VisibleEntriesFor(ActivePane), buffer);
        return index >= 0 ? VisibleEntriesFor(ActivePane)[index] : null;
    }

    public async Task UpdateTagAsync(long id, string name, string color)
    {
        await RequireFileOps().UpdateTagAsync(id, name, color).ConfigureAwait(false);
        await LoadTagsAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task DeleteTagDefinitionAsync(long id)
    {
        await RequireFileOps().DeleteTagAsync(id).ConfigureAwait(false);
        await LoadTagsAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    public IReadOnlyList<string> FilesWithTag(long tagId)
    {
        return FileTags.Where(pair => pair.Value.Id == tagId).Select(pair => pair.Key).ToList();
    }

    private static List<TreeNode> MergeTree(IReadOnlyList<FolderTreeItem> rows, string parent, TreeNode[] children)
    {
        var roots = ReconstructRoots(rows);
        AttachChildren(roots, parent, children);
        return roots;
    }

    private static List<TreeNode> ReconstructRoots(IReadOnlyList<FolderTreeItem> rows)
    {
        return rows
            .Where(row => row.Depth == 0)
            .Select(row => new TreeNode
            {
                Name = row.Name,
                Path = row.Path,
                HasChildren = row.HasChildren,
            })
            .ToList();
    }

    private static bool AttachChildren(IEnumerable<TreeNode> nodes, string parent, TreeNode[] children)
    {
        foreach (var node in nodes)
        {
            if (PathRules.PathsEqual(node.Path, parent))
            {
                node.Children = [.. children];
                node.HasChildren = children.Length > 0;
                return true;
            }

            if (AttachChildren(node.Children, parent, children))
            {
                return true;
            }
        }

        return false;
    }

    public WorkspaceLayout CaptureLayout()
    {
        return new WorkspaceLayout
        {
            DualPaneEnabled = DualPaneEnabled,
            ActivePane = ActivePane,
            SortBy = SortBy,
            SortAscending = SortAscending,
            Primary = CapturePane(Primary),
            Secondary = CapturePane(Secondary),
        };
    }

    public async Task ApplyLayoutAsync(WorkspaceLayout layout, CancellationToken cancellationToken = default)
    {
        DualPaneEnabled = layout.DualPaneEnabled;
        var legacySortBy = string.IsNullOrWhiteSpace(layout.SortBy) ? "name" : layout.SortBy;
        RestorePaneViewOptions(Primary, layout.Primary, legacySortBy, layout.SortAscending);
        RestorePaneViewOptions(Secondary, layout.Secondary, legacySortBy, layout.SortAscending);
        RestorePaneTabs(Primary, layout.Primary);
        RestorePaneTabs(Secondary, layout.Secondary);

        var primaryPath = string.IsNullOrWhiteSpace(layout.Primary.Path) ? HomePath : layout.Primary.Path;
        if (!string.IsNullOrWhiteSpace(primaryPath))
        {
            await NavigatePaneAsync(PaneId.Primary, primaryPath, HistoryMode.ReplaceCurrent, activate: false, cancellationToken)
                .ConfigureAwait(false);
        }

        if (DualPaneEnabled && !string.IsNullOrWhiteSpace(layout.Secondary.Path))
        {
            await NavigatePaneAsync(PaneId.Secondary, layout.Secondary.Path, HistoryMode.ReplaceCurrent, activate: false, cancellationToken)
                .ConfigureAwait(false);
        }

        if (DualPaneEnabled)
        {
            ActivatePane(layout.ActivePane);
        }
        else
        {
            ActivatePane(PaneId.Primary);
        }
    }

    public async Task SaveWorkspaceLayoutAsync(CancellationToken cancellationToken = default)
    {
        if (FileOps is null)
        {
            return;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(CaptureLayout());
        await FileOps.SetSettingAsync(WorkspaceLayout.SettingsKey, json, cancellationToken).ConfigureAwait(false);
        Settings.LastPath = Active.Path;
        await FileOps.SetSettingAsync("lastPath", Settings.LastPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryRestoreWorkspaceLayoutAsync(CancellationToken cancellationToken = default)
    {
        if (FileOps is null)
        {
            return false;
        }

        try
        {
            var json = await FileOps.GetSettingAsync(WorkspaceLayout.SettingsKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var layout = System.Text.Json.JsonSerializer.Deserialize<WorkspaceLayout>(json);
            if (layout is null || string.IsNullOrWhiteSpace(layout.Primary.Path))
            {
                return false;
            }

            await ApplyLayoutAsync(layout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task SaveUiSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (FileOps is null)
        {
            return;
        }

        Settings.ShowHidden = ShowHiddenFiles;
        Settings.DefaultView = UiSettings.NormalizeDefaultView(Settings.DefaultView);
        Settings.DefaultIconSize = UiSettings.NormalizeIconSize(Settings.DefaultIconSize);
        Settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(Settings.SidebarWidth);
        Settings.PreviewWidth = UiSettings.NormalizePreviewWidth(Settings.PreviewWidth);
        Settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(Settings.DualPanePrimaryPercent);
        Settings.DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(Settings.DualPanePrimaryWidth);
        Settings.ColumnPreset = UiSettings.NormalizeColumnPreset(Settings.ColumnPreset);
        Settings.ColumnWidths = Columns.SnapshotWidths();
        await FileOps.SetSettingAsync("theme", Settings.Theme, cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("defaultView", Settings.DefaultView, cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("defaultIconSize", Settings.DefaultIconSize.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("showHidden", Settings.ShowHidden ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("confirmDelete", Settings.ConfirmDelete ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("keepFoldersOnTop", Settings.KeepFoldersOnTop ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("startLocation", Settings.StartLocation, cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("customPath", Settings.CustomPath, cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("openInNewTab", Settings.OpenInNewTab ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("enableGitIntegration", Settings.EnableGitIntegration ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("showFolderSizes", Settings.ShowFolderSizes ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("previewVisible", Settings.PreviewVisible ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("preview.width", Settings.PreviewWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("dualPane.primaryPercent", Settings.DualPanePrimaryPercent.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("dualPane.primaryWidth", Settings.DualPanePrimaryWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("columnPreset", Settings.ColumnPreset, cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync(
            "columnWidths",
            System.Text.Json.JsonSerializer.Serialize(Settings.ColumnWidths),
            cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.showQuickAccess", Settings.ShowQuickAccess ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.showFolders", Settings.ShowFolderTree ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.showBookmarks", Settings.ShowBookmarks ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.showRecent", Settings.ShowRecentLocations ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.showSmartFolders", Settings.ShowSmartFolders ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.visible", Settings.SidebarVisible ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.width", Settings.SidebarWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.quickAccessCollapsed", Settings.QuickAccessCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("sidebar.myPcCollapsed", Settings.MyPcCollapsed ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync("lastPath", Settings.LastPath, cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync(
            BookmarksSettingsKey,
            System.Text.Json.JsonSerializer.Serialize(Bookmarks),
            cancellationToken).ConfigureAwait(false);
        await FileOps.SetSettingAsync(
            RecentPathsSettingsKey,
            System.Text.Json.JsonSerializer.Serialize(RecentPaths),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyOrMoveToOtherPaneAsync(string[] sources, bool move, string conflictAction = "keep-both", CancellationToken cancellationToken = default)
    {
        var destination = OtherPanePath();
        if (destination is null || sources.Length == 0 || FileOps is null)
        {
            return;
        }

        if (move)
        {
            var results = await FileOps.MoveAsync(sources, destination, conflictAction, ct: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Undo.PushMove(results, FileOps);
        }
        else
        {
            var results = await FileOps.CopyAsync(sources, destination, conflictAction, ct: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Undo.PushCopy(results, FileOps);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (DualPaneEnabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await NavigatePaneAsync(OtherPane().Id, destination, HistoryMode.None, activate: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task PackIntoFolderAsync(string[] sources, string folderName, CancellationToken cancellationToken = default)
    {
        if (FileOps is null || sources.Length == 0 || string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        var created = await FileOps.CreateFolderAsync(Active.Path, folderName, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await FileOps.MoveAsync(sources, created, "keep-both", ct: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UnpackFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (FileOps is null || string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var listing = await _backend.ListDirectoryAsync(folderPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var parent = PathRules.GetParentPath(folderPath);
        if (parent is null)
        {
            return;
        }

        var children = listing.Entries.Select(entry => entry.Path).ToArray();
        if (children.Length > 0)
        {
            await FileOps.MoveAsync(children, parent, "keep-both", ct: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await FileOps.DeleteAsync(folderPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadUiSettingsAsync(CancellationToken cancellationToken)
    {
        if (FileOps is null)
        {
            return;
        }

        try
        {
            Settings.Theme = UiSettings.NormalizeTheme(await FileOps.GetSettingAsync("theme", cancellationToken).ConfigureAwait(false));
            Settings.DefaultView = UiSettings.NormalizeDefaultView(await FileOps.GetSettingAsync("defaultView", cancellationToken).ConfigureAwait(false));
            Settings.DefaultIconSize = UiSettings.NormalizeIconSize(await FileOps.GetSettingAsync("defaultIconSize", cancellationToken).ConfigureAwait(false));
            Settings.ShowHidden = await ReadBoolSettingAsync("showHidden", false, cancellationToken).ConfigureAwait(false);
            Settings.ConfirmDelete = await ReadBoolSettingAsync("confirmDelete", true, cancellationToken).ConfigureAwait(false);
            Settings.KeepFoldersOnTop = await ReadBoolSettingAsync("keepFoldersOnTop", true, cancellationToken).ConfigureAwait(false);
            Settings.StartLocation = UiSettings.NormalizeStartLocation(
                await FileOps.GetSettingAsync("startLocation", cancellationToken).ConfigureAwait(false));
            Settings.CustomPath = await FileOps.GetSettingAsync("customPath", cancellationToken).ConfigureAwait(false) ?? "";
            Settings.LastPath = await FileOps.GetSettingAsync("lastPath", cancellationToken).ConfigureAwait(false) ?? "";
            Settings.OpenInNewTab = await ReadBoolSettingAsync("openInNewTab", false, cancellationToken).ConfigureAwait(false);
            Settings.EnableGitIntegration = await ReadBoolSettingAsync("enableGitIntegration", true, cancellationToken).ConfigureAwait(false);
            Settings.ShowFolderSizes = await ReadBoolSettingAsync("showFolderSizes", false, cancellationToken).ConfigureAwait(false);
            Settings.PreviewVisible = await ReadBoolSettingAsync("previewVisible", true, cancellationToken).ConfigureAwait(false);
            Settings.PreviewWidth = UiSettings.NormalizePreviewWidth(
                await ReadDoubleSettingAsync("preview.width", UiSettings.PreviewDefaultWidth, cancellationToken).ConfigureAwait(false));
            Settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(
                await ReadDoubleSettingAsync("dualPane.primaryPercent", UiSettings.DualPaneDefaultPercent, cancellationToken).ConfigureAwait(false));
            Settings.DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(
                await ReadDoubleSettingAsync("dualPane.primaryWidth", 0, cancellationToken).ConfigureAwait(false));
            Settings.ColumnPreset = UiSettings.NormalizeColumnPreset(
                await FileOps.GetSettingAsync("columnPreset", cancellationToken).ConfigureAwait(false));
            Settings.ColumnWidths = await ReadColumnWidthsAsync(cancellationToken).ConfigureAwait(false);
            Settings.ShowQuickAccess = await ReadBoolSettingAsync("sidebar.showQuickAccess", true, cancellationToken).ConfigureAwait(false);
            Settings.ShowFolderTree = await ReadBoolSettingAsync("sidebar.showFolders", false, cancellationToken).ConfigureAwait(false);
            Settings.ShowBookmarks = await ReadBoolSettingAsync("sidebar.showBookmarks", true, cancellationToken).ConfigureAwait(false);
            Settings.ShowRecentLocations = await ReadBoolSettingAsync("sidebar.showRecent", true, cancellationToken).ConfigureAwait(false);
            Settings.ShowSmartFolders = await ReadBoolSettingAsync("sidebar.showSmartFolders", true, cancellationToken).ConfigureAwait(false);
            Settings.SidebarVisible = await ReadBoolSettingAsync("sidebar.visible", true, cancellationToken).ConfigureAwait(false);
            Settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(
                await ReadDoubleSettingAsync("sidebar.width", UiSettings.SidebarDefaultWidth, cancellationToken).ConfigureAwait(false));
            Settings.QuickAccessCollapsed = await ReadBoolSettingAsync("sidebar.quickAccessCollapsed", false, cancellationToken).ConfigureAwait(false);
            Settings.MyPcCollapsed = await ReadBoolSettingAsync("sidebar.myPcCollapsed", false, cancellationToken).ConfigureAwait(false);
            Bookmarks = await ReadBookmarksAsync(cancellationToken).ConfigureAwait(false);
            RecentPaths = await ReadRecentPathsAsync(cancellationToken).ConfigureAwait(false);
            ShowHiddenFiles = Settings.ShowHidden;
            ApplyDefaultViewOptionsToPanes();
            Columns.ApplyPreset(Settings.ColumnPreset);
            Columns.RestoreWidths(Settings.ColumnWidths);
        }
        catch
        {
            // Missing keys or a stub IPC keep defaults.
        }
    }

    private async Task<Dictionary<string, double>> ReadColumnWidthsAsync(CancellationToken cancellationToken)
    {
        var raw = await FileOps!.GetSettingAsync("columnWidths", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(raw)
                ?? new Dictionary<string, double>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }

    private async Task<List<BookmarkItem>> ReadBookmarksAsync(CancellationToken cancellationToken)
    {
        var raw = await FileOps!.GetSettingAsync(BookmarksSettingsKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<BookmarkItem>>(raw) ?? [];
            var result = new List<BookmarkItem>();
            foreach (var bookmark in saved)
            {
                var path = (bookmark.Path ?? "").Trim();
                if (string.IsNullOrWhiteSpace(path)
                    || result.Any(item => PathRules.PathsEqual(item.Path, path)))
                {
                    continue;
                }

                var name = (bookmark.Name ?? "").Trim();
                result.Add(new BookmarkItem
                {
                    Name = string.IsNullOrWhiteSpace(name) ? PathRules.Basename(path) : name,
                    Path = path,
                });
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<string>> ReadRecentPathsAsync(CancellationToken cancellationToken)
    {
        var raw = await FileOps!.GetSettingAsync(RecentPathsSettingsKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw) ?? [];
            var result = new List<string>();
            foreach (var path in saved)
            {
                var trimmed = (path ?? "").Trim();
                if (string.IsNullOrWhiteSpace(trimmed)
                    || result.Any(item => PathRules.PathsEqual(item, trimmed)))
                {
                    continue;
                }

                result.Add(trimmed);
                if (result.Count >= PlacesStore.RecentLimit)
                {
                    break;
                }
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private async Task<bool> ReadBoolSettingAsync(string key, bool fallback, CancellationToken cancellationToken)
    {
        var raw = await FileOps!.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
    }

    private async Task<double> ReadDoubleSettingAsync(string key, double fallback, CancellationToken cancellationToken)
    {
        var raw = await FileOps!.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static WorkspacePaneLayout CapturePane(ExplorerPane pane)
    {
        return new WorkspacePaneLayout
        {
            Path = pane.Path,
            ActiveTabId = pane.ActiveTabId,
            View = pane.View,
            IconSize = pane.IconSize,
            SortBy = pane.SortBy,
            SortAscending = pane.SortAscending,
            Tabs = pane.Tabs.Select(tab => new WorkspaceTabLayout
            {
                Id = tab.Id,
                Path = tab.Path,
                Title = tab.Title,
                History = [.. tab.History],
                HistoryIndex = tab.HistoryIndex,
            }).ToList(),
        };
    }

    private void RestorePaneViewOptions(
        ExplorerPane pane,
        WorkspacePaneLayout layout,
        string fallbackSortBy,
        bool fallbackSortAscending)
    {
        pane.View = UiSettings.NormalizeDefaultView(
            string.IsNullOrWhiteSpace(layout.View) ? Settings.DefaultView : layout.View);
        pane.IconSize = UiSettings.NormalizeIconSize(layout.IconSize ?? Settings.DefaultIconSize);
        pane.SortBy = string.IsNullOrWhiteSpace(layout.SortBy) ? fallbackSortBy : layout.SortBy;
        pane.SortAscending = layout.SortAscending ?? fallbackSortAscending;
    }

    private static void RestorePaneTabs(ExplorerPane pane, WorkspacePaneLayout layout)
    {
        pane.Tabs.Clear();
        foreach (var tab in layout.Tabs)
        {
            pane.Tabs.Add(new FileTab
            {
                Id = string.IsNullOrEmpty(tab.Id) ? ExplorerPane.CreateTab(tab.Path).Id : tab.Id,
                Path = tab.Path,
                Title = string.IsNullOrEmpty(tab.Title) ? PathRules.Basename(tab.Path) : tab.Title,
                History = tab.History.Count > 0 ? [.. tab.History] : [tab.Path],
                HistoryIndex = tab.HistoryIndex,
            });
        }

        pane.ActiveTabId = layout.ActiveTabId;
        if (pane.ActiveTabId is not null)
        {
            var active = pane.Tabs.FirstOrDefault(tab => tab.Id == pane.ActiveTabId);
            if (active is not null)
            {
                pane.ApplyTabHistory(active);
            }
        }
    }
}

