using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace SimpleFile.App;

public sealed partial class MainWindow : Window
{
    private BackendSession? _backend;
    private ExplorerWorkspace? _workspace;
    private readonly PreviewPresenter _previewPresenter;
    private readonly FileOperationDialogService _fileOperationDialogs;
    private int _backendReconnectToken;
    private SearchViewModel? _search;
    private TransferManagerViewModel? _transfer;
    private ToolbarViewModel? _toolbar;
    private bool _quickAccessCollapsed;
    private bool _myPcCollapsed;
    private bool _editingPrimaryPath;
    private bool _editingSecondaryPath;
    private bool _reconnectDialogOpen;
    private CancellationTokenSource? _networkReconnectCts;
    private bool _dividerDragging;
    private bool _dividerMoved;
    private bool _applyingToolbarOverflow;
    private readonly HashSet<string> _primaryToolbarOverflow = new(StringComparer.Ordinal);
    private IDisposable? _fileChangeSubscription;
    private string? _watchTargetPath;
    private string? _watchedPath;
    private readonly SemaphoreSlim _watchGate = new(1, 1);
    private readonly SemaphoreSlim _transferPromptGate = new(1, 1);
    private int _watchRequestToken;
    private TransferProgressWindow? _transferProgressWindow;
    private CancellationTokenSource? _archiveCts;
    private CancellationTokenSource? _utilityCts;
    private bool _applyingWorkspace;
    private int _folderRefreshToken;
    private CancellationTokenSource? _folderRefreshCts;
    private string? _primaryColumnHeaderKey;
    private string? _secondaryColumnHeaderKey;
    private ScrollViewer? _primaryFileListScroller;
    private ScrollViewer? _secondaryFileListScroller;
    private readonly Dictionary<int, Style> _tileItemStyles = new();
    private string? _columnEnrichmentSignature;
    private CancellationTokenSource? _columnEnrichmentCts;
    private int _columnEnrichmentToken;
    private bool _acceptingPathSuggestion;
    private int _pathLostFocusToken;
    private Flyout? _pathSuggestFlyout;
    private ListView? _pathSuggestList;
    private PaneId _pathSuggestPane;

    public ObservableCollection<FileRow> PrimaryFiles { get; } = [];
    public ObservableCollection<FileRow> SecondaryFiles { get; } = [];
    public ObservableCollection<DriveRow> Drives { get; } = [];
    public ObservableCollection<QuickAccessRow> QuickAccess { get; } = [];

    public MainWindow()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            App.LogCrash("MainWindow.InitializeComponent", exception);
            throw;
        }

        AttachControlEvents();
        ApplyKeyboardShortcuts();

        _previewPresenter = new PreviewPresenter(
            () => _workspace,
            () => ActiveSelectedRow,
            () => ActiveSelectedRows,
            () => Content.XamlRoot,
            BeginUtilityOperation,
            FinishUtilityOperation,
            ShowOpenWithChooserAsync,
            ShowMessage,
            PreviewTitle,
            PreviewSubtitle,
            PreviewOpenButton,
            PreviewOpenWithButton,
            PreviewRevealButton,
            PreviewCompareButton,
            PreviewChecksumButton,
            PreviewIconPanel,
            PreviewIconImage,
            PreviewIconLabel,
            PreviewImage,
            PreviewPdfWebView,
            PreviewMediaPlayer,
            PreviewVideoFrameControls,
            PreviewVideoFrameImage,
            PreviewVideoFramePresets,
            PreviewTextBox,
            PreviewEmptyText,
            PreviewMetadataRows,
            PreviewChecksumText);
        _fileOperationDialogs = new FileOperationDialogService(
            () => _workspace,
            () => Content.XamlRoot,
            () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            () => ActiveFileList.SelectedItem as FileRow,
            GetSelectedEntries,
            () => SelectedPaths,
            BeginUtilityOperation,
            FinishUtilityOperation,
            BeginArchiveOperation,
            FinishArchiveOperation,
            PickFolderAsync,
            PickFileAsync,
            RunUiActionAsync,
            ShowMessage,
            QueuePreview,
            ToFileRow,
            RefreshView,
            ApplyTheme,
            ApplyKeyboardShortcuts,
            ClearRecentHistoryAsync,
            action => DispatcherQueue.TryEnqueue(() => action()));

        RootGrid.ActualThemeChanged += OnRootActualThemeChanged;
        Title = "SumaFile";
        AppIcon.ApplyTo(this);
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyCaptionButtonColors(ElementTheme.Default);
        AppWindow.Resize(new SizeInt32(1280, 840));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        PrimaryFileList.ItemsSource = PrimaryFiles;
        SecondaryFileList.ItemsSource = SecondaryFiles;
        AttachPaneActivationHandlers();
        DriveList.ItemsSource = Drives;
        QuickAccessList.ItemsSource = QuickAccess;
        Closed += OnClosed;
        Activated += OnActivated;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            SetStatusText("Starting SumaFile service...");
            var backend = new BackendSession();
            backend.Disconnected += OnBackendDisconnected;
            _backend = backend;
            await backend.StartAsync();
            var client = backend.Client
                ?? throw new InvalidOperationException("IPC service started without an active client.");
            var fileOps = new FileOperationService(client, OperationJournal.CreateDefault());
            _workspace = new ExplorerWorkspace(_backend, fileOps);
            AppServices.Configure(_workspace);
            _search = AppServices.GetRequired<SearchViewModel>();
            _transfer = AppServices.GetRequired<TransferManagerViewModel>();
            _toolbar = AppServices.GetRequired<ToolbarViewModel>();
            AttachViewModels();
            FileListThumbnailHost.Configure(LoadFileListImageThumbnailAsync);
            ColumnLayoutHost.Attach(_workspace.PrimaryColumns, _workspace.SecondaryColumns);
            _workspace.Changed += OnWorkspaceChanged;
            _fileChangeSubscription = client.On<FileChangeEvent>(Protocol.FileChangeEvent, OnFileChange);
            await _workspace.InitializeAsync();
            ApplyKeyboardShortcuts();
            await LoadOpenWithPreferencesAsync(fileOps, CancellationToken.None);
            ApplyTheme(_workspace.Settings.Theme);
            SyncSidebarCollapseStateFromSettings();
            ApplyPreviewVisibility();
            ApplyColumnWidths();
            SyncFromWorkspace();
        }
        catch (Exception exception)
        {
            await CleanupSessionAsync(saveWorkspace: false, unwatchDirectory: true);
            ShowMessage(
                "Could not start or reach the IPC service.",
                exception.Message
                    + Environment.NewLine
                    + Environment.NewLine
                    + "Build the service first:"
                    + Environment.NewLine
                    + "  cargo build -p simplefile-service"
                    + Environment.NewLine
                    + "or set SIMPLEFILE_SERVICE_PATH to simplefile-service.exe.",
                InfoBarSeverity.Error);
        }
    }

    private void AttachViewModels()
    {
        if (_search is not null)
        {
            _search.ResultsChanged += OnSearchResultsChanged;
            _search.Cleared += OnSearchCleared;
            _search.MessageRequested += OnViewModelMessageRequested;
            _search.PropertyChanged += OnSearchPropertyChanged;
        }

    }

    private void DetachViewModels()
    {
        if (_search is not null)
        {
            _search.ResultsChanged -= OnSearchResultsChanged;
            _search.Cleared -= OnSearchCleared;
            _search.MessageRequested -= OnViewModelMessageRequested;
            _search.PropertyChanged -= OnSearchPropertyChanged;
        }

    }

    private void OnBackendDisconnected(object? sender, Exception? error)
    {
        if (sender is not BackendSession backend)
        {
            return;
        }

        var token = Interlocked.Increment(ref _backendReconnectToken);
        DispatcherQueue.TryEnqueue(() => _ = ReconnectBackendAsync(backend, error, token));
    }

    private async Task ReconnectBackendAsync(BackendSession backend, Exception? error, int token)
    {
        if (!ReferenceEquals(_backend, backend) || _workspace is null || token != _backendReconnectToken)
        {
            return;
        }

        _fileChangeSubscription?.Dispose();
        _fileChangeSubscription = null;
        _watchTargetPath = null;
        _watchedPath = null;
        Interlocked.Increment(ref _watchRequestToken);
        CancelNetworkReconnectPrompt();
        CancelUtilityOperation();
        CancelArchiveOperation();
        _transfer?.Reset();
        CloseTransferProgressWindow();
        ClearSearchState();
        SyncFromWorkspace();
        ShowMessage(
            "IPC service disconnected",
            error is null
                ? "The background service disconnected. Reconnecting..."
                : $"The background service disconnected: {error.Message}{Environment.NewLine}Reconnecting...",
            InfoBarSeverity.Warning);

        try
        {
            await backend.ReconnectAsync();
            if (!ReferenceEquals(_backend, backend) || _workspace is null || token != _backendReconnectToken)
            {
                return;
            }

            if (backend.Client is not null)
            {
                _workspace.FileOps?.ReplaceIpc(backend.Client);
                _fileChangeSubscription = backend.Client.On<FileChangeEvent>(Protocol.FileChangeEvent, OnFileChange);
            }

            QueueWatchActiveDirectory();
            await _workspace.RefreshDrivesAsync();
            await _workspace.RefreshAsync();
            ShowMessage("IPC service reconnected", "The background service is running again.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_backend, backend) && token == _backendReconnectToken)
            {
                ShowMessage("IPC service disconnected", exception.Message, InfoBarSeverity.Error);
            }
        }
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(SyncFromWorkspace);
    }

    private void DispatchToUi(Action action)
    {
        DispatcherQueue.TryEnqueue(() => action());
    }

    private void SyncFromWorkspace()
    {
        if (_workspace is null || _applyingWorkspace)
        {
            return;
        }

        _applyingWorkspace = true;
        try
        {
            SyncFromWorkspaceCore();
        }
        finally
        {
            _applyingWorkspace = false;
        }
    }

    private void SyncFromWorkspaceCore()
    {
        if (_workspace is null)
        {
            return;
        }

        _search?.CheckSearchRootDrift();

        ReplaceIfChanged(
            PrimaryFiles,
            (_search?.IsActiveForPane(PaneId.Primary) == true
                ? _search.Results.Select(result => SearchRowFrom(result, PaneId.Primary))
                : _workspace.VisibleEntriesFor(PaneId.Primary).Select(entry => ToFileRow(entry, PaneId.Primary))).ToList(),
            SameFileRow);
        ReplaceIfChanged(
            SecondaryFiles,
            (_search?.IsActiveForPane(PaneId.Secondary) == true
                ? _search.Results.Select(result => SearchRowFrom(result, PaneId.Secondary))
                : _workspace.VisibleEntriesFor(PaneId.Secondary).Select(entry => ToFileRow(entry, PaneId.Secondary))).ToList(),
            SameFileRow);
        ReplaceIfChanged(
            Drives,
            _workspace.Drives.Select(drive => DriveRow.From(drive, _workspace.Pane(_workspace.SidebarTarget).Path)).ToList(),
            SameDriveRow);
        ReplaceIfChanged(
            QuickAccess,
            ExplorerWorkspace.QuickAccessLocations.Select(item => new QuickAccessRow
            {
                Name = item.Name,
                Icon = item.Icon,
                Command = item.Command,
                Path = QuickAccessRow.ResolvePath(item.Command, _workspace.HomePath),
            }).ToList(),
            SameQuickAccessRow);

        RebuildBreadcrumbs(PrimaryBreadcrumbHost, _workspace.Primary.Breadcrumbs, PaneId.Primary);
        RebuildBreadcrumbs(SecondaryBreadcrumbHost, _workspace.Secondary.Breadcrumbs, PaneId.Secondary);
        RebuildTabs(PrimaryTabHost, _workspace.Primary, PaneId.Primary);
        RebuildTabs(SecondaryTabHost, _workspace.Secondary, PaneId.Secondary);

        _toolbar?.SyncFromWorkspace();
        ApplyToolbarState();

        SetExpandGlyph(QuickAccessCollapseButton, _quickAccessCollapsed);
        SetExpandGlyph(MyPcCollapseButton, _myPcCollapsed);
        RefreshSmartFolders();
        BindItemsSource(FolderTreeList, _workspace.FolderTreeRows);
        BindItemsSource(BookmarksList, _workspace.Bookmarks);
        BindItemsSource(RecentsList, _workspace.RecentPaths);
        ApplySidebarLayout();
        UpdateSidebarEmptyStates();
        ApplySidebarSectionVisibility();
        ApplyPreviewVisibility();
        ApplyFileListViewPresentation();
        ApplyFileListThumbnailPolicy();
        ApplyColumnWidths();
        ApplyTheme(_workspace.Settings.Theme);
        UpdateEmptyStates();

        ApplyDualPaneLayout();
        SidebarTargetSwitch.Visibility = _workspace.DualPaneEnabled ? Visibility.Visible : Visibility.Collapsed;
        HighlightSidebarTarget();
        HighlightActivePane();
        SyncQuickFilterFromWorkspace();
        UpdateSearchCancelButtons();
        UpdateDualPaneButton(_toolbar?.IsDualPaneEnabled ?? _workspace.DualPaneEnabled);

        if (!_editingPrimaryPath)
        {
            PrimaryPathInput.Text = _toolbar?.PrimaryPath ?? _workspace.Primary.Path;
        }

        if (!_editingSecondaryPath)
        {
            SecondaryPathInput.Text = _toolbar?.SecondaryPath ?? _workspace.Secondary.Path;
        }

        SelectRow(PrimaryFileList, PrimaryFiles, _workspace.Primary.SelectedPath);
        SelectRow(SecondaryFileList, SecondaryFiles, _workspace.Secondary.SelectedPath);
        UpdateSelectionStatus();

        if (!string.IsNullOrEmpty(_workspace.ErrorMessage))
        {
            ShowMessage("Could not open folder", _workspace.ErrorMessage, InfoBarSeverity.Error);
        }
        else if (_workspace.FileOpenUnsupported)
        {
            ShowMessage(
                "Open file",
                _workspace.StatusMessage ?? "No file operation service is available to open this file.",
                InfoBarSeverity.Informational);
        }
        else
        {
            MessageBar.IsOpen = false;
        }

        QueueWatchActiveDirectory();
        QueueColumnEnrichment();

        if (_workspace.PendingReconnect is { } drive && !_reconnectDialogOpen)
        {
            var workspace = _workspace;
            _ = RunUiActionAsync(
                "Network drive",
                () => PromptNetworkReconnectAsync(workspace, drive.Name, drive.StatusDetail, drive.RemotePath, drive.Path));
        }

        QueuePreviewFromSelection();
    }

    private void ApplyToolbarState()
    {
        if (_toolbar is null)
        {
            return;
        }

        PrimaryBackButton.IsEnabled = _toolbar.CanGoBack;
        PrimaryForwardButton.IsEnabled = _toolbar.CanGoForward;
        PrimaryUpButton.IsEnabled = _toolbar.CanGoUp;
        CountText.Text = _toolbar.CountText;
        StatusText.Text = _toolbar.StatusText;
    }

    private void ApplyStatusBarState()
    {
        if (_toolbar is null)
        {
            return;
        }

        CountText.Text = _toolbar.CountText;
        StatusText.Text = _toolbar.StatusText;
    }

    private void SetCountText(string text)
    {
        if (_toolbar is not null)
        {
            _toolbar.SetCountText(text);
            CountText.Text = _toolbar.CountText;
            return;
        }

        CountText.Text = text;
    }

    private void SetStatusText(string text)
    {
        if (_toolbar is not null)
        {
            _toolbar.SetStatusText(text);
            StatusText.Text = _toolbar.StatusText;
            return;
        }

        StatusText.Text = text;
    }

    private void ApplyDualPaneLayout()
    {
        var dual = _workspace?.DualPaneEnabled == true;
        SecondaryPaneRoot.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        PaneDivider.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        DividerColumn.Width = dual ? new GridLength(UiSettings.DualPaneDividerWidth) : new GridLength(0);
        if (dual)
        {
            var available = PanesGrid.ActualWidth;
            var width = UiSettings.ResolveDualPanePrimaryWidth(
                _workspace?.Settings.DualPanePrimaryWidth ?? 0,
                _workspace?.Settings.DualPanePrimaryPercent ?? UiSettings.DualPaneDefaultPercent,
                available);
            if (_workspace is not null)
            {
                if (width > 0)
                {
                    _workspace.Settings.DualPanePrimaryWidth = width;
                }

                if (available > 0)
                {
                    _workspace.Settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(
                        width / available * 100);
                }
            }

            PrimaryColumn.MinWidth = UiSettings.FilePaneMinWidth;
            SecondaryColumn.MinWidth = UiSettings.FilePaneMinWidth;
            if (width > 0)
            {
                PrimaryColumn.Width = new GridLength(width);
                SecondaryColumn.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                PrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
                SecondaryColumn.Width = new GridLength(1, GridUnitType.Star);
            }
        }
        else
        {
            PrimaryColumn.MinWidth = 0;
            SecondaryColumn.MinWidth = 0;
            PrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondaryColumn.Width = new GridLength(0);
        }

        ApplyToolbarOverflow();
    }

    private void OnPanesGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_dividerDragging || _workspace?.DualPaneEnabled != true)
        {
            return;
        }

        ApplyDualPaneLayout();
    }

    private void OnPrimaryToolbarSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyPrimaryToolbarOverflow();

    private void ApplyToolbarOverflow()
    {
        ApplyPrimaryToolbarOverflow();
    }

    private void ApplyPrimaryToolbarOverflow()
    {
        var toolbarWidth = PrimaryToolbar.ActualWidth;
        if (_applyingToolbarOverflow || toolbarWidth <= 0)
        {
            return;
        }

        ApplyResponsiveToolbarSizing(toolbarWidth);

        var reserved = MeasuredWidth(PrimaryNavHost)
            + MeasuredWidth(PrimaryMoreButton)
            + (PrimaryToolbar.Padding.Left + PrimaryToolbar.Padding.Right)
            + (4 * ToolbarOverflowPlanner.ColumnSpacing);

        var widths = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ToolbarOverflowPlanner.Filter] = ToolbarOverflowPlanner.FilterOverflowWidthFor(toolbarWidth),
            [ToolbarOverflowPlanner.Search] = ToolbarOverflowPlanner.SearchOverflowWidthFor(toolbarWidth),
            [ToolbarOverflowPlanner.Settings] = 32,
            [ToolbarOverflowPlanner.Profiles] = 32,
            [ToolbarOverflowPlanner.DualPane] = 32,
            [ToolbarOverflowPlanner.ViewOptions] = 32,
            [ToolbarOverflowPlanner.New] = 32,
        };

        var overflowed = ToolbarOverflowPlanner.OverflowIds(
            toolbarWidth,
            reserved,
            widths,
            ToolbarOverflowPlanner.PrimaryHideOrder);

        ApplyOverflowSet(_primaryToolbarOverflow, overflowed, ApplyPrimaryOverflowVisibility);
    }

    private void ApplyResponsiveToolbarSizing(double toolbarWidth)
    {
        SetWidthIfChanged(PrimarySearchHost, ToolbarOverflowPlanner.SearchWidthFor(toolbarWidth));
        SetWidthIfChanged(QuickFilterBox, ToolbarOverflowPlanner.FilterWidthFor(toolbarWidth));
    }

    private void ApplyOverflowSet(
        HashSet<string> target,
        HashSet<string> overflowed,
        Action<IReadOnlySet<string>> applyVisibility)
    {
        if (target.SetEquals(overflowed))
        {
            return;
        }

        _applyingToolbarOverflow = true;
        try
        {
            target.Clear();
            foreach (var id in overflowed)
            {
                target.Add(id);
            }

            applyVisibility(target);
        }
        finally
        {
            _applyingToolbarOverflow = false;
        }
    }

    private void ApplyPrimaryOverflowVisibility(IReadOnlySet<string> overflowed)
    {
        SetOverflowVisible(QuickFilterBox, !overflowed.Contains(ToolbarOverflowPlanner.Filter));
        var searchVisible = !overflowed.Contains(ToolbarOverflowPlanner.Search);
        SetOverflowVisible(PrimarySearchHost, searchVisible);
        PrimarySearchColumn.Width = searchVisible ? GridLength.Auto : new GridLength(0);
        SetOverflowVisible(PrimarySettingsButton, !overflowed.Contains(ToolbarOverflowPlanner.Settings));
        SetOverflowVisible(WorkspaceProfileButton, !overflowed.Contains(ToolbarOverflowPlanner.Profiles));
        var dualOverflowed = overflowed.Contains(ToolbarOverflowPlanner.DualPane);
        SetOverflowVisible(DualPaneButton, !dualOverflowed);
        SetOverflowVisible(ClosePrimaryPaneButton, false);
        SetOverflowVisible(PrimaryViewButton, !overflowed.Contains(ToolbarOverflowPlanner.ViewOptions));
        SetOverflowVisible(PrimaryNewButton, !overflowed.Contains(ToolbarOverflowPlanner.New));
        var actionsVisible = PrimaryNewButton.Visibility == Visibility.Visible
            || DualPaneButton.Visibility == Visibility.Visible
            || ClosePrimaryPaneButton.Visibility == Visibility.Visible
            || WorkspaceProfileButton.Visibility == Visibility.Visible
            || PrimaryViewButton.Visibility == Visibility.Visible
            || PrimarySettingsButton.Visibility == Visibility.Visible
            || QuickFilterBox.Visibility == Visibility.Visible;
        SetOverflowVisible(PrimaryActionsHost, actionsVisible);
        PrimaryActionsColumn.Width = actionsVisible ? GridLength.Auto : new GridLength(0);
    }

    private static void SetOverflowVisible(FrameworkElement element, bool visible)
    {
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetWidthIfChanged(FrameworkElement element, double width)
    {
        if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > 0.5)
        {
            element.Width = width;
        }
    }

    private static double MeasuredWidth(FrameworkElement element)
    {
        var width = element.ActualWidth;
        if (width <= 0)
        {
            width = element.DesiredSize.Width;
        }

        return width + element.Margin.Left + element.Margin.Right;
    }

    private async void OnToggleDualPane(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Dual pane", ToggleDualPaneFromUiAsync);
    }

    private async void OnCloseDualPane(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Close right pane", () => CloseFilePaneFromUiAsync(PaneId.Secondary));
    }

    private async void OnClosePrimaryPane(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Close left pane", () => CloseFilePaneFromUiAsync(PaneId.Primary));
    }

    private async Task ToggleDualPaneFromUiAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var closing = _workspace.DualPaneEnabled;
        await _workspace.ToggleDualPaneAsync();
        SetStatusText(closing ? "Right pane closed" : "Second pane opened");
    }

    private async Task CloseDualPaneFromUiAsync()
    {
        await CloseFilePaneFromUiAsync(PaneId.Secondary);
    }

    private async Task CloseFilePaneFromUiAsync(PaneId pane)
    {
        if (_workspace?.DualPaneEnabled != true)
        {
            return;
        }

        var closingLeft = _workspace.Normalize(pane) == PaneId.Primary;
        await _workspace.CloseFilePaneAsync(pane);
        SetStatusText(closingLeft ? "Left pane closed" : "Right pane closed");
    }

    private async void OnToggleSidebar(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Side menu", ToggleSidebarAsync);

    private async Task ToggleSidebarAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        _workspace.Settings.SidebarVisible = !_workspace.Settings.SidebarVisible;
        ApplySidebarLayout();
        await _workspace.SaveUiSettingsAsync();
        SetStatusText(_workspace.Settings.SidebarVisible ? "Side menu shown" : "Side menu hidden");
    }

    private void OnSidebarLeft(object sender, RoutedEventArgs e) => _workspace?.ActivatePane(PaneId.Primary);

    private void OnSidebarRight(object sender, RoutedEventArgs e) => _workspace?.ActivatePane(PaneId.Secondary);

    private void AttachPaneActivationHandlers()
    {
        PrimaryPaneRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPrimaryPanePressed), true);
        SecondaryPaneRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSecondaryPanePressed), true);
        PrimaryFileList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPrimaryFileWheelChanged), true);
        SecondaryFileList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnSecondaryFileWheelChanged), true);
    }

    private FileRow? ActiveSelectedRow =>
        ActiveFileList.SelectedItem as FileRow
        ?? ActiveFileList.SelectedItems.OfType<FileRow>().LastOrDefault();

    private IReadOnlyList<FileRow> ActiveSelectedRows =>
        ActiveFileList.SelectedItems.OfType<FileRow>().ToArray();

    private void OnDividerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_workspace?.DualPaneEnabled != true)
        {
            return;
        }

        _dividerDragging = true;
        _dividerMoved = false;
        PaneDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDividerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dividerDragging || _workspace is null)
        {
            return;
        }

        var available = PanesGrid.ActualWidth;
        if (available <= 0)
        {
            return;
        }

        var width = UiSettings.ResolveDualPanePrimaryWidth(
            e.GetCurrentPoint(PanesGrid).Position.X,
            _workspace.Settings.DualPanePrimaryPercent,
            available);
        if (Math.Abs(width - _workspace.Settings.DualPanePrimaryWidth) > 1)
        {
            _dividerMoved = true;
        }

        _workspace.Settings.DualPanePrimaryWidth = width;
        _workspace.Settings.DualPanePrimaryPercent = UiSettings.NormalizeDualPanePrimaryPercent(width / available * 100);
        ApplyDualPaneLayout();
        e.Handled = true;
    }

    private async void OnDividerReleased(object sender, PointerRoutedEventArgs e)
    {
        var wasDragging = _dividerDragging;
        _dividerDragging = false;
        PaneDivider.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        var workspace = _workspace;
        if (wasDragging && _dividerMoved && workspace is not null)
        {
            await RunUiActionAsync("Resize panes", () => workspace.SaveUiSettingsAsync());
        }
    }

    private async void OnDividerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_workspace?.DualPaneEnabled != true || PanesGrid.ActualWidth <= 0)
        {
            return;
        }

        e.Handled = true;
        _workspace.Settings.DualPanePrimaryWidth = 0;
        _workspace.Settings.DualPanePrimaryPercent = UiSettings.DualPaneDefaultPercent;
        ApplyDualPaneLayout();
        await RunUiActionAsync("Reset pane split", () => _workspace.SaveUiSettingsAsync());
    }

    private async Task PromptNetworkReconnectAsync(
        ExplorerWorkspace workspace,
        string name,
        string? detail,
        string? remote,
        string path)
    {
        if (!ReferenceEquals(_workspace, workspace) || _reconnectDialogOpen)
        {
            return;
        }

        _reconnectDialogOpen = true;
        var reconnectCts = new CancellationTokenSource();
        _networkReconnectCts?.Cancel();
        _networkReconnectCts = reconnectCts;
        var dialog = new ContentDialog
        {
            Title = "Network drive unavailable",
            Content = string.Join(
                Environment.NewLine,
                new[]
                {
                    $"{name} is currently unavailable.",
                    detail ?? "",
                    string.IsNullOrEmpty(remote) ? "" : $"Share: {remote}",
                    $"Path: {path}",
                    "Retry probes the mapping again. Check VPN or credentials if it stays offline.",
                }.Where(line => line.Length > 0)),
            PrimaryButtonText = "Retry",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        try
        {
            var result = await dialog.ShowAsync();
            if (!ReferenceEquals(_workspace, workspace) || reconnectCts.IsCancellationRequested)
            {
                return;
            }

            if (result == ContentDialogResult.Primary)
            {
                await workspace.RetryPendingDriveAsync(reconnectCts.Token);
            }
            else
            {
                workspace.CancelPendingReconnect();
            }
        }
        finally
        {
            if (ReferenceEquals(_networkReconnectCts, reconnectCts))
            {
                _networkReconnectCts = null;
            }

            reconnectCts.Dispose();
            _reconnectDialogOpen = false;
        }
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
        SetStatusText(message);
    }

    private async Task RunUiActionAsync(string title, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage(title, exception.Message, InfoBarSeverity.Error);
        }
    }

    private CancellationTokenSource BeginArchiveOperation()
    {
        _archiveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _archiveCts = cts;
        return cts;
    }

    private void FinishArchiveOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_archiveCts, cts))
        {
            _archiveCts = null;
        }

        cts.Dispose();
    }

    private void CancelArchiveOperation()
    {
        _archiveCts?.Cancel();
        _archiveCts = null;
    }

    private CancellationTokenSource BeginUtilityOperation()
    {
        _utilityCts?.Cancel();
        var cts = new CancellationTokenSource();
        _utilityCts = cts;
        return cts;
    }

    private void FinishUtilityOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_utilityCts, cts))
        {
            _utilityCts = null;
        }

        cts.Dispose();
    }

    private void CancelUtilityOperation()
    {
        _utilityCts?.Cancel();
        _utilityCts = null;
    }

    private void CancelNetworkReconnectPrompt()
    {
        _networkReconnectCts?.Cancel();
        _networkReconnectCts = null;
    }

    private void QueueWatchActiveDirectory()
    {
        if (_workspace?.FileOps is null)
        {
            return;
        }

        var path = _workspace.Active.Path;
        if (string.IsNullOrWhiteSpace(path)
            || string.Equals(path, _watchTargetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip filesystem watching on network paths — ReadDirectoryChangesW
        // is unreliable on many NAS devices and adds background SMB traffic.
        if (_workspace.Active.PathIsNetwork)
        {
            return;
        }

        _watchTargetPath = path;
        var token = Interlocked.Increment(ref _watchRequestToken);
        _ = WatchDirectoryAsync(path, token);
    }

    private async Task WatchDirectoryAsync(string path, int token)
    {
        await _watchGate.WaitAsync();
        try
        {
            if (token != _watchRequestToken || _workspace?.FileOps is null)
            {
                return;
            }

            await _workspace.FileOps.WatchDirectoryAsync(path);
            if (token == _watchRequestToken
                && string.Equals(path, _watchTargetPath, StringComparison.OrdinalIgnoreCase))
            {
                _watchedPath = path;
            }
        }
        catch (OperationCanceledException)
        {
            if (token == _watchRequestToken)
            {
                _watchedPath = null;
            }
        }
        catch (Exception exception)
        {
            if (token == _watchRequestToken
                && string.Equals(path, _watchTargetPath, StringComparison.OrdinalIgnoreCase))
            {
                _watchTargetPath = null;
                _watchedPath = null;
                SetStatusText(exception.Message);
            }
        }
        finally
        {
            _watchGate.Release();
        }
    }

    private void OnFileChange(FileChangeEvent change)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_workspace is null || string.IsNullOrEmpty(_watchedPath))
            {
                return;
            }

            if (!string.IsNullOrEmpty(_previewPresenter.CurrentPath)
                && string.Equals(change.Path, _previewPresenter.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_search?.IsActive == true)
            {
                return;
            }

            if (!PathRules.PathContains(_workspace.Active.Path, change.Path)
                && !string.Equals(_workspace.Active.Path, _watchedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var name = System.IO.Path.GetFileName(change.Path);
            SetStatusText(string.IsNullOrEmpty(name)
                ? $"{change.Kind}: {change.Path}"
                : $"{change.Kind}: {name}");
            ScheduleInPlaceRefresh();
        });
    }

    private async void ScheduleInPlaceRefresh()
    {
        _folderRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _folderRefreshCts = cts;
        var token = Interlocked.Increment(ref _folderRefreshToken);
        try
        {
            var cancellationToken = cts.Token;
            await Task.Delay(350, cancellationToken);
            if (token != _folderRefreshToken || _workspace is null)
            {
                return;
            }

            await _workspace.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatusText(exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_folderRefreshCts, cts))
            {
                _folderRefreshCts = null;
            }

            cts.Dispose();
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        Interlocked.Increment(ref _watchRequestToken);
        Interlocked.Increment(ref _backendReconnectToken);
        Interlocked.Increment(ref _folderRefreshToken);
        Interlocked.Increment(ref _viewIconSizeSaveToken);
        _folderRefreshCts?.Cancel();
        _folderRefreshCts = null;
        _previewPresenter.CancelPending();
        CancelNetworkReconnectPrompt();
        CancelUtilityOperation();
        CancelArchiveOperation();
        _transfer?.Reset();
        CloseTransferProgressWindow();
        _search?.ClearState(notifyHost: false);
        Interlocked.Increment(ref _columnEnrichmentToken);
        _columnEnrichmentCts?.Cancel();
        _columnEnrichmentCts = null;
        _watchTargetPath = null;
        _watchedPath = null;

        await CleanupSessionAsync(saveWorkspace: true, unwatchDirectory: true);
    }

    private async Task CleanupSessionAsync(bool saveWorkspace, bool unwatchDirectory)
    {
        _watchTargetPath = null;
        _watchedPath = null;

        _fileChangeSubscription?.Dispose();
        _fileChangeSubscription = null;

        if (unwatchDirectory && await _watchGate.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            try
            {
                if (_workspace?.FileOps is not null)
                {
                    await _workspace.FileOps.UnwatchDirectoryAsync();
                }
            }
            catch
            {
                // Best-effort shutdown cleanup.
            }
            finally
            {
                _watchGate.Release();
            }
        }

        if (_workspace is not null)
        {
            var workspace = _workspace;
            try
            {
                if (saveWorkspace)
                {
                    await workspace.SaveWorkspaceLayoutAsync();
                    await workspace.SaveUiSettingsAsync();
                }
            }
            catch
            {
                // Best-effort cleanup and persistence.
            }

            workspace.Changed -= OnWorkspaceChanged;
            ColumnLayoutHost.Detach(workspace.PrimaryColumns, workspace.SecondaryColumns);
            FileListThumbnailHost.Configure(null);
            DetachViewModels();
            _search?.ClearState(notifyHost: false);
            _transfer?.Reset();
            _search = null;
            _transfer = null;
            _toolbar = null;
            _workspace = null;
        }

        if (_backend is not null)
        {
            var backend = _backend;
            _backend = null;
            backend.Disconnected -= OnBackendDisconnected;
            try
            {
                await backend.DisposeAsync();
            }
            catch
            {
                // Best-effort service teardown.
            }
        }
    }

    private readonly record struct PanePath(PaneId Pane, string Path);

    private readonly record struct PaneTab(PaneId Pane, string TabId);

    private readonly record struct PaneSort(PaneId Pane, string Sort);

    private readonly record struct ColumnResizeTarget(string ColumnId, PaneId Pane);

}
