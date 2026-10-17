using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Thin facade over focused workspace controllers in <c>WorkspaceControllers/</c>.
/// </summary>
public sealed partial class ExplorerWorkspace
{
    public static readonly IReadOnlyList<(string Name, string Icon, string Command)> QuickAccessLocations =
        WorkspaceNavigationController.QuickAccessLocations;

    private readonly IExplorerBackend _backend;
    private readonly PaneNavigator _navigator;
    private readonly WorkspaceProfileService _profiles;
    private readonly PlacesController _places = new();
    private readonly WorkspaceTagController _tags = new();
    private readonly FolderTreeController _folderTree = new();
    private readonly PaneEnrichmentController _enrichment = new();
    private readonly FolderViewController _folderViews = new();
    private readonly WorkspaceLayoutController _layouts = new();
    private readonly DriveController _drives = new();
    private readonly ClosedTabController _closedTabs = new();
    private readonly OperationLogController _operationLog = new();
    private readonly DualPaneTabController _panesTabs = new();
    private readonly WorkspaceNavigationController _navigation = new();
    private readonly WorkspaceFileOpsController _fileOpsController = new();
    private readonly WorkspaceSelectionController _selection = new();
    private readonly object _gate = new();

    public ExplorerWorkspace(IExplorerBackend backend, FileOperationService? fileOps = null)
    {
        _backend = backend;
        _navigator = new PaneNavigator(_backend, _gate, RaiseChanged);
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
        _folderViews.ApplyDefaultViewOptions(Primary, Secondary, Settings);
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
    public string? ErrorMessage { get; private set; }
    public string? StatusMessage { get; private set; }
    public bool FileOpenUnsupported { get; private set; }

    public List<SmartFolder> SmartFolders => _places.SmartFolders;
    public List<Tag> AllTags => _tags.AllTags;
    public Dictionary<string, Tag> FileTags => _tags.FileTags;
    public List<BookmarkItem> Bookmarks => _places.Bookmarks;
    public List<string> RecentPaths => _places.RecentPaths;
    public List<FolderTreeItem> FolderTreeRows => _folderTree.Rows;
    public long? ActiveTagFilter => _tags.ActiveTagFilter;
    public bool PhotoFolderActive { get; private set; }
    public TypeAheadBuffer TypeAheadBuffer { get; } = new();

    public PaneId SidebarTarget =>
        DualPaneEnabled && ActivePane == PaneId.Secondary ? PaneId.Secondary : PaneId.Primary;

    public ExplorerPane Active => Pane(ActivePane);

    public ExplorerPane Pane(PaneId pane) =>
        Normalize(pane) == PaneId.Secondary ? Secondary : Primary;

    public ColumnLayout ColumnsFor(PaneId pane) =>
        Normalize(pane) == PaneId.Secondary ? SecondaryColumns : PrimaryColumns;

    public PaneId Normalize(PaneId pane) =>
        pane == PaneId.Secondary && DualPaneEnabled ? PaneId.Secondary : PaneId.Primary;

    public ExplorerPane OtherPane() =>
        ActivePane == PaneId.Secondary ? Primary : Secondary;

    public string? OtherPanePath() =>
        DualPaneEnabled && !string.IsNullOrWhiteSpace(OtherPane().Path) ? OtherPane().Path : null;

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

    internal object Gate => _gate;
    internal IExplorerBackend Backend => _backend;
    internal PaneNavigator Navigator => _navigator;
    internal PlacesController PlacesController => _places;
    internal WorkspaceTagController TagsController => _tags;
    internal FolderTreeController FolderTreeController => _folderTree;
    internal PaneEnrichmentController EnrichmentController => _enrichment;
    internal FolderViewController FolderViewsController => _folderViews;
    internal WorkspaceLayoutController LayoutsController => _layouts;
    internal DriveController DrivesController => _drives;
    internal ClosedTabController ClosedTabsController => _closedTabs;
    internal OperationLogController OperationLogController => _operationLog;
    internal DualPaneTabController PanesTabsController => _panesTabs;
    internal WorkspaceNavigationController NavigationController => _navigation;
    internal WorkspaceFileOpsController FileOpsController => _fileOpsController;
    internal WorkspaceSelectionController SelectionController => _selection;

    internal void SetSettingsCore(UiSettings value) => Settings = value;
    internal void SetHomePathCore(string value) => HomePath = value;
    internal void SetDualPaneEnabledCore(bool value) => DualPaneEnabled = value;
    internal void SetActivePaneCore(PaneId value) => ActivePane = value;
    internal void SetActiveProfileIdCore(string value) => ActiveProfileId = value;
    internal void SetShowHiddenFilesCore(bool value) => ShowHiddenFiles = value;
    internal void SetErrorMessageCore(string? value) => ErrorMessage = value;
    internal void SetStatusMessageCore(string? value) => StatusMessage = value;
    internal void SetFileOpenUnsupportedCore(bool value) => FileOpenUnsupported = value;
    internal void SetPhotoFolderActiveCore(bool value) => PhotoFolderActive = value;
    internal void NotifyChanged() => RaiseChanged();
    internal bool ApplyFolderViewSettingsForPathLocked(PaneId pane, string path) =>
        _folderViews.ApplyFolderViewSettingsForPathLocked(this, pane, path);
    internal FileOperationService RequireFileOps() =>
        FileOps ?? throw new InvalidOperationException("FileOperationService is required for file operations.");

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
