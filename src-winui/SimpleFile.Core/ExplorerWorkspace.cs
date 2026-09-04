using SimpleFile.Ipc;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Core;

/// <summary>
/// Dual-pane navigation + pane-local tabs, ported from
/// frontend/src/lib/app/core.ts (loadDirectory, loadSecondaryDirectory,
/// loadDirectoryForPane, toggleDualPane, openNewTab / switchToTab / closeTab,
/// activatePane) and SidebarShell sidebar targeting.
/// </summary>
public sealed partial class ExplorerWorkspace
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

    private const int ClosedTabLimit = 20;
    private readonly IExplorerBackend _backend;
    private readonly WorkspaceProfileService _profiles;
    private readonly object _gate = new();
    private readonly List<ClosedFileTab> _closedTabs = [];
    private List<DriveInfo> _drives = [];

    private sealed class ClosedFileTab
    {
        public ClosedFileTab(PaneId pane, FileTab tab, int index)
        {
            Pane = pane;
            Tab = tab;
            Index = index;
        }

        public PaneId Pane { get; }
        public FileTab Tab { get; }
        public int Index { get; }
    }

    public ExplorerWorkspace(IExplorerBackend backend, FileOperationService? fileOps = null)
    {
        _backend = backend;
        FileOps = fileOps;
        _profiles = new WorkspaceProfileService(
            () => FileOps,
            () => HomePath,
            CaptureLayout,
            CaptureChromeLayout,
            profileId => ActiveProfileId = profileId);
        Clipboard = new ClipboardState();
        Undo = new UndoStack();
        PrimaryColumns = new ColumnLayout();
        SecondaryColumns = new ColumnLayout();
        Settings = UiSettings.CreateDefault();
        Primary = new ExplorerPane(PaneId.Primary);
        Secondary = new ExplorerPane(PaneId.Secondary);
        ApplyDefaultViewOptionsToPanes();
    }

    public event EventHandler? Changed;

    public FileOperationService? FileOps { get; }
    public ClipboardState Clipboard { get; }
    public UndoStack Undo { get; }
    /// <summary>Primary pane column layout. Prefer <see cref="ColumnsFor"/> for pane-specific access.</summary>
    public ColumnLayout PrimaryColumns { get; }
    /// <summary>Secondary pane column layout.</summary>
    public ColumnLayout SecondaryColumns { get; }
    /// <summary>Legacy alias for <see cref="PrimaryColumns"/>.</summary>
    public ColumnLayout Columns => PrimaryColumns;
    public UiSettings Settings { get; private set; }
    public ExplorerPane Primary { get; }
    public ExplorerPane Secondary { get; }
    public string ActiveProfileId { get; private set; } = "";

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
    public bool CanReopenClosedTab
    {
        get
        {
            lock (_gate)
            {
                return _closedTabs.Count > 0;
            }
        }
    }

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

    public ColumnLayout ColumnsFor(PaneId pane) =>
        Normalize(pane) == PaneId.Secondary ? SecondaryColumns : PrimaryColumns;

    public PaneId Normalize(PaneId pane) =>
        pane == PaneId.Secondary && DualPaneEnabled ? PaneId.Secondary : PaneId.Primary;

    private static bool IsSupportedArchivePath(string path)
    {
        var name = PathRules.Basename(path).ToLowerInvariant();
        return name.EndsWith(".tar.gz", StringComparison.Ordinal)
            || name.EndsWith(".tgz", StringComparison.Ordinal)
            || name.EndsWith(".7z", StringComparison.Ordinal)
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

    public string ResolveStartPath()
    {
        return WorkspaceNavigation.ResolveStartPath(Settings, HomePath, Primary.Path);
    }

    public void ApplyUiSettings(UiSettings settings, bool applyViewDefaultsToPanes = true)
    {
        Settings = settings;
        Settings.Theme = UiSettings.NormalizeTheme(settings.Theme);
        Settings.DefaultView = UiSettings.NormalizeDefaultView(settings.DefaultView);
        Settings.DefaultIconSize = UiSettings.NormalizeIconSize(settings.DefaultIconSize);
        Settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(settings.SidebarWidth);
        Settings.PreviewWidth = UiSettings.NormalizePreviewWidth(settings.PreviewWidth);
        Settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(settings.DualPanePrimaryPercent);
        Settings.DualPanePrimaryWidth = UiSettings.NormalizeDualPanePrimaryWidth(settings.DualPanePrimaryWidth);
        Settings.ShortcutOverrides = KeyboardShortcutMap.NormalizeOverrides(Settings.ShortcutOverrides);
        Settings.FolderViewSettings.Normalize();
        ShowHiddenFiles = settings.ShowHidden;
        if (applyViewDefaultsToPanes)
        {
            ApplyDefaultViewOptionsToPanes();
        }

        ApplyColumnSettingsFromUiSettings();
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

    public FolderViewRule? EffectiveFolderViewRuleFor(PaneId pane)
    {
        lock (_gate)
        {
            return Settings.FolderViewSettings.Resolve(Pane(pane).Path, _drives);
        }
    }

    public async Task<FolderViewRule> SaveFolderViewSettingsAsync(
        FolderViewScope scope,
        PaneId? pane = null,
        CancellationToken cancellationToken = default)
    {
        var target = Normalize(pane ?? ActivePane);
        FolderViewRule saved;
        lock (_gate)
        {
            var state = Pane(target);
            saved = Settings.FolderViewSettings.Upsert(
                scope,
                state.Path,
                _drives,
                CaptureFolderViewOptions(state));
        }

        await SaveUiSettingsAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    private FolderViewOptions CaptureFolderViewOptions(ExplorerPane pane)
    {
        // Folder-view column options are captured for the pane being saved (per-pane layouts).
        var columns = ColumnsFor(pane.Id);
        var preset = pane.Id == PaneId.Secondary ? Settings.SecondaryColumnPreset : Settings.ColumnPreset;
        return new FolderViewOptions
        {
            View = pane.View,
            IconSize = pane.IconSize,
            VisibleColumnIds = columns.SnapshotVisibleIds(),
            ColumnWidths = columns.SnapshotWidths(),
            SortBy = pane.SortBy,
            SortAscending = pane.SortAscending,
            PreviewVisible = Settings.PreviewVisible,
            ShowHidden = ShowHiddenFiles,
            WorkspaceProfileId = ActiveProfileId,
            ColumnPreset = preset,
        };
    }

    private bool ApplyFolderViewSettingsForPathLocked(PaneId pane, string path)
    {
        var rule = Settings.FolderViewSettings.Resolve(path, _drives);
        return rule is not null && ApplyFolderViewOptionsLocked(Pane(pane), rule.Options);
    }

    private bool ApplyFolderViewOptionsLocked(ExplorerPane pane, FolderViewOptions options)
    {
        options.Normalize();
        var changed = false;
        if (!string.IsNullOrWhiteSpace(options.View))
        {
            var view = UiSettings.NormalizeDefaultView(options.View);
            if (!string.Equals(pane.View, view, StringComparison.Ordinal))
            {
                pane.View = view;
                changed = true;
            }
        }

        if (options.IconSize is { } iconSize)
        {
            var normalizedIconSize = UiSettings.NormalizeIconSize(iconSize);
            if (pane.IconSize != normalizedIconSize)
            {
                pane.IconSize = normalizedIconSize;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.SortBy) || options.SortAscending.HasValue)
        {
            var sortBy = string.IsNullOrWhiteSpace(options.SortBy) ? pane.SortBy : options.SortBy.Trim();
            var sortAscending = options.SortAscending ?? pane.SortAscending;
            if (!string.Equals(pane.SortBy, sortBy, StringComparison.OrdinalIgnoreCase)
                || pane.SortAscending != sortAscending)
            {
                pane.SortBy = sortBy;
                pane.SortAscending = sortAscending;
                changed = true;
            }
        }

        // Folder-view column options apply to the pane being viewed, not a shared layout.
        var columns = ColumnsFor(pane.Id);
        if (!string.IsNullOrWhiteSpace(options.ColumnPreset))
        {
            var preset = UiSettings.NormalizeColumnPreset(options.ColumnPreset);
            if (pane.Id == PaneId.Secondary)
            {
                if (!string.Equals(Settings.SecondaryColumnPreset, preset, StringComparison.Ordinal))
                {
                    Settings.SecondaryColumnPreset = preset;
                    changed = true;
                }
            }
            else if (!string.Equals(Settings.ColumnPreset, preset, StringComparison.Ordinal))
            {
                Settings.ColumnPreset = preset;
                changed = true;
            }

            columns.ApplyPreset(preset);
            changed = true;
        }

        if (options.VisibleColumnIds is { Count: > 0 } visibleColumnIds)
        {
            columns.RestoreVisibleIds(visibleColumnIds);
            changed = true;
        }

        if (options.ColumnWidths is { Count: > 0 } columnWidths)
        {
            var widths = new Dictionary<string, double>(columnWidths, StringComparer.Ordinal);
            if (pane.Id == PaneId.Secondary)
            {
                Settings.SecondaryColumnWidths = widths;
            }
            else
            {
                Settings.ColumnWidths = widths;
            }

            columns.RestoreWidths(widths);
            changed = true;
        }

        if (options.PreviewVisible.HasValue && Settings.PreviewVisible != options.PreviewVisible.Value)
        {
            Settings.PreviewVisible = options.PreviewVisible.Value;
            changed = true;
        }

        if (options.ShowHidden.HasValue && ShowHiddenFiles != options.ShowHidden.Value)
        {
            ShowHiddenFiles = options.ShowHidden.Value;
            Settings.ShowHidden = options.ShowHidden.Value;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(options.WorkspaceProfileId)
            && !string.Equals(ActiveProfileId, options.WorkspaceProfileId, StringComparison.OrdinalIgnoreCase))
        {
            ActiveProfileId = options.WorkspaceProfileId.Trim();
            changed = true;
        }

        return changed;
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

    public void SetTagFilter(long? tagId)
    {
        ActiveTagFilter = tagId;
        RaiseChanged();
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
        if (state.PathIsNetwork)
        {
            return;
        }

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
        if (state.PathIsNetwork)
        {
            return;
        }

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
                if (includeSizes && includeItemCounts)
                {
                    var metrics = await FileOps.GetFolderMetricsAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                    if (!IsCurrent())
                    {
                        return;
                    }

                    entry.Size = metrics.Size;
                    entry.ItemCount = metrics.ItemCount;
                    changed = true;
                    continue;
                }

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

    public void RememberOperation(
        string kind,
        string description,
        string[] sources,
        string destination,
        bool move,
        string status = "completed")
    {
        OperationLog.Insert(0, new OperationRecord
        {
            Kind = kind,
            Description = description,
            Sources = sources,
            Destination = destination,
            Move = move,
            Status = status,
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

    public WorkspaceChromeLayout CaptureChromeLayout()
    {
        return WorkspaceChromeLayout.Capture(Settings, PrimaryColumns, SecondaryColumns);
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
            var state = await WorkspaceSettingsStore.LoadAsync(FileOps, cancellationToken).ConfigureAwait(false);
            Settings = state.Settings;
            Bookmarks = state.Bookmarks;
            RecentPaths = state.RecentPaths;
            Settings.FolderViewSettings.Normalize();
            ShowHiddenFiles = Settings.ShowHidden;
            ApplyDefaultViewOptionsToPanes();
            ApplyColumnSettingsFromUiSettings();
        }
        catch
        {
            // Missing keys or a stub IPC keep defaults.
        }
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

    private void ApplyColumnSettingsFromUiSettings()
    {
        PrimaryColumns.ApplyPreset(
            string.IsNullOrWhiteSpace(Settings.ColumnPreset) ? "default" : Settings.ColumnPreset);
        PrimaryColumns.RestoreWidths(Settings.ColumnWidths);
        SecondaryColumns.ApplyPreset(
            string.IsNullOrWhiteSpace(Settings.SecondaryColumnPreset) ? "default" : Settings.SecondaryColumnPreset);
        SecondaryColumns.RestoreWidths(Settings.SecondaryColumnWidths);
    }

    public async Task SaveUiSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (FileOps is null)
        {
            return;
        }

        Settings.ColumnPreset = UiSettings.NormalizeColumnPreset(Settings.ColumnPreset);
        Settings.SecondaryColumnPreset = UiSettings.NormalizeColumnPreset(Settings.SecondaryColumnPreset);
        await WorkspaceSettingsStore.SaveAsync(
            FileOps,
            Settings,
            PrimaryColumns,
            SecondaryColumns,
            ShowHiddenFiles,
            Bookmarks,
            RecentPaths,
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

}

