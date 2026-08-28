using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Graphics;
using Windows.System;
using Windows.Storage.Streams;
using Windows.UI;

namespace SimpleFile.App;

public sealed partial class MainWindow : Window
{
    private BackendSession? _backend;
    private ExplorerWorkspace? _workspace;
    private int _backendReconnectToken;
    private SearchViewModel? _search;
    private TransferViewModel? _transfer;
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
    private int _watchRequestToken;
    private string? _currentOperationId;
    private CancellationTokenSource? _transferCts;
    private TransferProgressWindow? _transferProgressWindow;
    private CancellationTokenSource? _archiveCts;
    private CancellationTokenSource? _utilityCts;
    private string? _activeSearchId;
    private string? _searchRoot;
    private CancellationTokenSource? _searchCts;
    private int _searchCounter;
    private bool _searchMode;
    private PaneId _searchPane = PaneId.Primary;
    private readonly List<SearchResult> _activeSearchResults = [];
    private int _previewToken;
    private string? _previewPath;
    private CancellationTokenSource? _previewCts;
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
            StatusText.Text = "Starting SumaFile service...";
            var backend = new BackendSession();
            backend.Disconnected += OnBackendDisconnected;
            _backend = backend;
            await backend.StartAsync();
            var client = backend.Client
                ?? throw new InvalidOperationException("IPC service started without an active client.");
            var fileOps = new FileOperationService(client);
            _workspace = new ExplorerWorkspace(_backend, fileOps);
            AppServices.Configure(_workspace);
            _search = AppServices.GetRequired<SearchViewModel>();
            _transfer = AppServices.GetRequired<TransferViewModel>();
            _toolbar = AppServices.GetRequired<ToolbarViewModel>();
            FileListThumbnailHost.Configure(LoadFileListImageThumbnailAsync);
            ColumnLayoutHost.Attach(_workspace.Columns);
            _workspace.Changed += OnWorkspaceChanged;
            _fileChangeSubscription = client.On<FileChangeEvent>(Protocol.FileChangeEvent, OnFileChange);
            await _workspace.InitializeAsync();
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
        _transferCts?.Cancel();
        _transferCts = null;
        _currentOperationId = null;
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

        if (_searchMode
            && _searchRoot is not null
            && !string.Equals(_workspace.Pane(_searchPane).Path, _searchRoot, StringComparison.OrdinalIgnoreCase))
        {
            ClearSearchState();
        }

        ReplaceIfChanged(
            PrimaryFiles,
            (_searchMode && _searchPane == PaneId.Primary
                ? _activeSearchResults.Select(result => SearchRowFrom(result, PaneId.Primary))
                : _workspace.VisibleEntriesFor(PaneId.Primary).Select(entry => ToFileRow(entry, PaneId.Primary))).ToList(),
            SameFileRow);
        ReplaceIfChanged(
            SecondaryFiles,
            (_searchMode && _searchPane == PaneId.Secondary
                ? _activeSearchResults.Select(result => SearchRowFrom(result, PaneId.Secondary))
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

        var activeNavigationPane = _workspace.Pane(_workspace.Normalize(_workspace.ActivePane));
        PrimaryBackButton.IsEnabled = activeNavigationPane.CanGoBack;
        PrimaryForwardButton.IsEnabled = activeNavigationPane.CanGoForward;
        PrimaryUpButton.IsEnabled = activeNavigationPane.CanGoUp;

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
        UpdateDualPaneButton(_workspace.DualPaneEnabled);

        if (!_editingPrimaryPath)
        {
            PrimaryPathInput.Text = _workspace.Primary.Path;
        }

        if (!_editingSecondaryPath)
        {
            SecondaryPathInput.Text = _workspace.Secondary.Path;
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
            [ToolbarOverflowPlanner.DualPane] = 32,
            [ToolbarOverflowPlanner.ViewOptions] = 32,
            [ToolbarOverflowPlanner.NewFile] = 32,
            [ToolbarOverflowPlanner.NewFolder] = 32,
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
        var dualOverflowed = overflowed.Contains(ToolbarOverflowPlanner.DualPane);
        SetOverflowVisible(DualPaneButton, !dualOverflowed);
        SetOverflowVisible(ClosePrimaryPaneButton, false);
        SetOverflowVisible(PrimaryViewButton, !overflowed.Contains(ToolbarOverflowPlanner.ViewOptions));
        SetOverflowVisible(PrimaryNewFileButton, !overflowed.Contains(ToolbarOverflowPlanner.NewFile));
        SetOverflowVisible(PrimaryNewFolderButton, !overflowed.Contains(ToolbarOverflowPlanner.NewFolder));
        var actionsVisible = PrimaryNewFolderButton.Visibility == Visibility.Visible
            || PrimaryNewFileButton.Visibility == Visibility.Visible
            || DualPaneButton.Visibility == Visibility.Visible
            || ClosePrimaryPaneButton.Visibility == Visibility.Visible
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

    private void UpdateDualPaneButton(bool dualPaneEnabled)
    {
        var label = dualPaneEnabled ? "Close right pane" : "Open second pane";
        AutomationProperties.SetName(DualPaneButton, label);
        ToolTipService.SetToolTip(DualPaneButton, $"{label} (F6)");
    }

    private void RebuildBreadcrumbs(StackPanel host, IReadOnlyList<BreadcrumbSegment> crumbs, PaneId pane)
    {
        var key = string.Join('\u001f', crumbs.Select(crumb => crumb.Path + "=" + crumb.Label));
        if (Equals(host.Tag, key) && host.Children.Count > 0)
        {
            return;
        }

        host.Tag = key;
        host.Children.Clear();
        var lastIndex = crumbs.Count - 1;
        for (var index = 0; index < crumbs.Count; index++)
        {
            var segment = crumbs[index];
            var isLast = index == lastIndex;
            var button = new Button
            {
                Content = segment.Label,
                Tag = new PanePath(pane, segment.Path),
                Style = ChromeStyle("SfBreadcrumbButtonStyle"),
                FontWeight = isLast ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(isLast ? "SfTextPrimaryBrush" : "SfTextMutedBrush"),
            };
            button.Click += OnBreadcrumbClick;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Navigate to {segment.Label}");
            host.Children.Add(button);
            if (!isLast)
            {
                host.Children.Add(new FontIcon
                {
                    Glyph = "\uE76C",
                    FontSize = 8,
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    Foreground = Brush("SfTextMutedBrush"),
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.7,
                });
            }
        }
    }

    private void RebuildTabs(StackPanel host, ExplorerPane pane, PaneId paneId)
    {
        var key = string.Join(
            '\u001f',
            pane.Tabs.Select(tab => $"{tab.Id}:{tab.Path}:{tab.Id == pane.ActiveTabId}"));
        if (Equals(host.Tag, key) && host.Children.Count > 0)
        {
            return;
        }

        host.Tag = key;
        host.Children.Clear();
        foreach (var tab in pane.Tabs)
        {
            var isActive = tab.Id == pane.ActiveTabId;
            var tabId = new PaneTab(paneId, tab.Id);
            var select = new Button
            {
                Style = ChromeStyle("SfTabItemStyle"),
                Tag = tabId,
                Padding = new Thickness(8, 3, 6, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon
                        {
                            Glyph = "\uE8B7",
                            FontSize = 12,
                            FontFamily = new FontFamily("Segoe Fluent Icons"),
                            Foreground = Brush(isActive ? "SfTextPrimaryBrush" : "SfTextMutedBrush"),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = tab.Title,
                            MaxWidth = 140,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontSize = 12,
                            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                        },
                    },
                },
            };
            ToolTipService.SetToolTip(select, tab.Path);
            select.Click += OnTabClick;
            select.PointerPressed += OnTabPointerPressed;
            select.KeyDown += OnTabKeyDown;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(select, $"Tab {tab.Title}");

            var chrome = new Border
            {
                Tag = tabId,
                Background = isActive ? Brush("SfBgSelectedBrush") : Brush("SfTransparentBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2, 1, 2, 1),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Children =
                    {
                        select,
                        CreateTabCloseButton(tabId, tab.Title),
                    },
                },
            };
            chrome.PointerPressed += OnTabPointerPressed;
            host.Children.Add(chrome);
        }

        var add = new Button
        {
            Style = ChromeStyle("SfToolbarButtonStyle"),
            Content = new FontIcon
            {
                Glyph = "\uE710",
                FontSize = 11,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
            },
            Tag = paneId,
        };
        ToolTipService.SetToolTip(add, "New Tab");
        add.Click += OnNewTabClick;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, "New tab");
        host.Children.Add(add);
    }

    private Button CreateTabCloseButton(PaneTab tabId, string title)
    {
        var close = new Button
        {
            Style = ChromeStyle("SfSidebarIconButtonStyle"),
            Width = 20,
            Height = 20,
            MinWidth = 20,
            MinHeight = 20,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 9,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
            },
            Tag = tabId,
        };
        close.Click += OnTabCloseClick;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, $"Close tab {title}");
        return close;
    }

    private void UpdateSidebarEmptyStates()
    {
        if (_workspace is null)
        {
            return;
        }

        FolderTreeEmptyText.Visibility = _workspace.FolderTreeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BookmarksEmptyText.Visibility = _workspace.Bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentsEmptyText.Visibility = _workspace.RecentPaths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SmartFoldersEmptyText.Visibility = _workspace.SmartFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearRecentsButton.IsEnabled = _workspace.RecentPaths.Count > 0;
    }

    private void ApplySidebarSectionVisibility()
    {
        if (_workspace is null)
        {
            return;
        }

        QuickAccessSection.Visibility = _workspace.Settings.ShowQuickAccess ? Visibility.Visible : Visibility.Collapsed;
        FolderTreeSection.Visibility = _workspace.Settings.ShowFolderTree ? Visibility.Visible : Visibility.Collapsed;
        BookmarksSection.Visibility = _workspace.Settings.ShowBookmarks ? Visibility.Visible : Visibility.Collapsed;
        RecentSection.Visibility = _workspace.Settings.ShowRecentLocations ? Visibility.Visible : Visibility.Collapsed;
        SmartFoldersSection.Visibility = _workspace.Settings.ShowSmartFolders ? Visibility.Visible : Visibility.Collapsed;

        QuickAccessList.Visibility = _quickAccessCollapsed ? Visibility.Collapsed : Visibility.Visible;
        DriveList.Visibility = _myPcCollapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplySidebarLayout()
    {
        if (_workspace is null)
        {
            return;
        }

        var settings = _workspace.Settings;
        settings.SidebarWidth = UiSettings.NormalizeSidebarWidth(settings.SidebarWidth);
        if (settings.SidebarVisible)
        {
            SidebarColumn.MinWidth = UiSettings.SidebarMinWidth;
            SidebarColumn.MaxWidth = UiSettings.SidebarMaxWidth;
            SidebarColumn.Width = new GridLength(settings.SidebarWidth);
            SidebarDividerColumn.Width = new GridLength(UiSettings.DualPaneDividerWidth);
            SidebarRoot.Visibility = Visibility.Visible;
            SidebarDivider.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarColumn.MinWidth = 0;
            SidebarColumn.MaxWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarDividerColumn.Width = new GridLength(0);
            SidebarRoot.Visibility = Visibility.Collapsed;
            SidebarDivider.Visibility = Visibility.Collapsed;
        }

        UpdateSidebarToggleButton(PrimarySidebarToggleButton, settings.SidebarVisible);
    }

    private static void UpdateSidebarToggleButton(Button button, bool sidebarVisible)
    {
        ToolTipService.SetToolTip(button, sidebarVisible ? "Hide side menu" : "Show side menu");
    }

    private void HighlightSidebarTarget()
    {
        if (_workspace is null)
        {
            return;
        }

        var leftActive = !_workspace.DualPaneEnabled || _workspace.SidebarTarget == PaneId.Primary;
        SidebarLeftButton.Background = leftActive ? Brush("SfBgHoverBrush") : Brush("SfTransparentBrush");
        SidebarRightButton.Background = !leftActive ? Brush("SfBgHoverBrush") : Brush("SfTransparentBrush");
        SidebarLeftButton.Foreground = leftActive ? Brush("SfAccentBrush") : Brush("SfTextPrimaryBrush");
        SidebarRightButton.Foreground = !leftActive ? Brush("SfAccentBrush") : Brush("SfTextPrimaryBrush");
        SidebarLeftButton.FontWeight = leftActive ? FontWeights.SemiBold : FontWeights.Normal;
        SidebarRightButton.FontWeight = !leftActive ? FontWeights.SemiBold : FontWeights.Normal;
        AutomationProperties.SetName(SidebarLeftButton, leftActive ? "Side menu target: left pane" : "Navigate left pane");
        AutomationProperties.SetName(SidebarRightButton, !leftActive ? "Side menu target: right pane" : "Navigate right pane");
        ToolTipService.SetToolTip(SidebarLeftButton, leftActive ? "Side menu opens folders in the left pane" : "Target the left pane");
        ToolTipService.SetToolTip(SidebarRightButton, !leftActive ? "Side menu opens folders in the right pane" : "Target the right pane");
    }

    private void HighlightActivePane()
    {
        if (_workspace is null)
        {
            return;
        }

        var dual = _workspace.DualPaneEnabled;
        var primaryActive = !dual || _workspace.ActivePane == PaneId.Primary;
        var secondaryActive = dual && _workspace.ActivePane == PaneId.Secondary;

        PrimaryActivePaneRail.Visibility = dual && primaryActive ? Visibility.Visible : Visibility.Collapsed;
        SecondaryActivePaneRail.Visibility = secondaryActive ? Visibility.Visible : Visibility.Collapsed;
        PrimaryPaneCaption.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        SecondaryPaneCaption.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        PrimaryPaneHeader.Background = Brush(primaryActive ? "SfBgTertiaryBrush" : "SfBgSecondaryBrush");
        SecondaryPaneHeader.Background = Brush(secondaryActive ? "SfBgTertiaryBrush" : "SfBgSecondaryBrush");
        PrimaryPaneCaptionText.Foreground = Brush(primaryActive ? "SfAccentBrush" : "SfTextMutedBrush");
        SecondaryPaneCaptionText.Foreground = Brush(secondaryActive ? "SfAccentBrush" : "SfTextMutedBrush");
        PrimaryPaneCaptionRail.Visibility = primaryActive ? Visibility.Visible : Visibility.Collapsed;
        SecondaryPaneCaptionRail.Visibility = secondaryActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyCaptionButtonColors(ElementTheme theme)
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        var light = theme switch
        {
            ElementTheme.Light => true,
            ElementTheme.Dark => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Light,
        };
        if (light)
        {
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 118, 118, 118);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(26, 0, 0, 0);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(38, 0, 0, 0);
        }
        else
        {
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 158, 158, 158);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(26, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(38, 255, 255, 255);
        }
    }

    private static void SetExpandGlyph(Button button, bool collapsed)
    {
        if (button.Content is FontIcon icon)
        {
            icon.Glyph = collapsed ? "\uE76C" : "\uE70D";
        }
    }

    private void SyncSidebarCollapseStateFromSettings()
    {
        if (_workspace is null)
        {
            return;
        }

        _quickAccessCollapsed = _workspace.Settings.QuickAccessCollapsed;
        _myPcCollapsed = _workspace.Settings.MyPcCollapsed;
    }

    private void SyncQuickFilterFromWorkspace()
    {
        if (_workspace is null)
        {
            return;
        }

        SyncQuickFilterBox(QuickFilterBox, _workspace.FilterQueryFor(_workspace.Normalize(_workspace.ActivePane)));
    }

    private static void SyncQuickFilterBox(TextBox box, string filter)
    {
        if (!string.Equals(box.Text, filter, StringComparison.Ordinal))
        {
            box.Text = filter;
        }
    }

    private static T? ChromeResource<T>(string key) where T : class
    {
        return Application.Current.Resources.TryGetValue(key, out var value) && value is T resource
            ? resource
            : null;
    }

    private static Style? ChromeStyle(string key) => ChromeResource<Style>(key);

    private static Brush Brush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    private void ApplyFileListViewPresentation()
    {
        if (_workspace is null)
        {
            return;
        }

        var primaryView = _workspace.ViewFor(PaneId.Primary);
        var primaryIconSize = _workspace.IconSizeFor(PaneId.Primary);
        var secondaryView = _workspace.ViewFor(PaneId.Secondary);
        var secondaryIconSize = _workspace.IconSizeFor(PaneId.Secondary);
        FileListViewHost.Apply(PaneId.Primary, primaryView, primaryIconSize);
        FileListViewHost.Apply(PaneId.Secondary, secondaryView, secondaryIconSize);

        PrimaryColumnHeaderScroller.Visibility = primaryView == "details" ? Visibility.Visible : Visibility.Collapsed;
        SecondaryColumnHeaderScroller.Visibility = secondaryView == "details" ? Visibility.Visible : Visibility.Collapsed;

        ApplyFileListPresentation(PrimaryFileList, primaryView, primaryIconSize);
        ApplyFileListPresentation(SecondaryFileList, secondaryView, secondaryIconSize);
    }

    private void ApplyFileListPresentation(ListView list, string view, int iconSize)
    {
        var usesTiles = view == "tiles";
        var usesDetails = view == "details";
        var itemStyleKey = usesTiles
            ? "SfFileTileItemStyle"
            : usesDetails
                ? "SfFileDetailsItemStyle"
                : "SfFileListItemStyle";
        var itemsPanelKey = usesTiles ? "SfWrapItemsPanelTemplate" : "SfStackItemsPanelTemplate";

        var style = usesTiles ? TileItemStyleFor(iconSize) : ChromeStyle(itemStyleKey);
        if (style is not null && !ReferenceEquals(list.ItemContainerStyle, style))
        {
            list.ItemContainerStyle = style;
        }

        var itemsPanel = ChromeResource<ItemsPanelTemplate>(itemsPanelKey);
        if (itemsPanel is not null && !ReferenceEquals(list.ItemsPanel, itemsPanel))
        {
            list.ItemsPanel = itemsPanel;
        }

        list.Loaded -= OnTileFileListLoaded;
        if (usesTiles)
        {
            list.Loaded += OnTileFileListLoaded;
            ApplyTileItemsPanelMetrics(list, iconSize);
        }

        list.Padding = usesTiles
            ? new Thickness(6, 6, 2, 6)
            : usesDetails
                ? new Thickness(10, 4, 0, 6)
                : new Thickness(2, 4, 2, 6);
        ScrollViewer.SetHorizontalScrollBarVisibility(
            list,
            usesDetails ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(
            list,
            usesDetails ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        list.ContainerContentChanging -= OnFileListContainerContentChanging;
        if (usesDetails)
        {
            list.ContainerContentChanging += OnFileListContainerContentChanging;
            HookFileListColumnScroll(list);
        }
    }

    private Style? TileItemStyleFor(int iconSize)
    {
        var normalized = UiSettings.NormalizeIconSize(iconSize);
        if (_tileItemStyles.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var baseStyle = ChromeStyle("SfFileTileItemStyle");
        if (baseStyle is null)
        {
            return null;
        }

        var style = new Style(typeof(ListViewItem))
        {
            BasedOn = baseStyle,
        };
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, FileTileLayoutMetrics.ContainerWidthFor(normalized)));
        _tileItemStyles[normalized] = style;
        return style;
    }

    private void OnTileFileListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView list || _workspace is null)
        {
            return;
        }

        var pane = ReferenceEquals(list, SecondaryFileList) ? PaneId.Secondary : PaneId.Primary;
        if (_workspace.ViewFor(pane) == "tiles")
        {
            ApplyTileItemsPanelMetrics(list, _workspace.IconSizeFor(pane));
        }
    }

    private void ApplyTileItemsPanelMetrics(ListView list, int iconSize, bool deferIfMissing = true)
    {
        if (FindDescendant<ItemsWrapGrid>(list) is { } panel)
        {
            ApplyTileItemsPanelMetrics(panel, iconSize);
            return;
        }

        if (deferIfMissing)
        {
            _ = DispatcherQueue.TryEnqueue(() => ApplyTileItemsPanelMetrics(list, iconSize, deferIfMissing: false));
        }
    }

    private static void ApplyTileItemsPanelMetrics(ItemsWrapGrid panel, int iconSize)
    {
        SetPanelDimension(panel.ItemWidth, FileTileLayoutMetrics.ContainerWidthFor(iconSize), value => panel.ItemWidth = value);
        SetPanelDimension(panel.ItemHeight, FileTileLayoutMetrics.ContainerHeightFor(iconSize), value => panel.ItemHeight = value);
    }

    private static void SetPanelDimension(double current, double next, Action<double> assign)
    {
        if (double.IsNaN(current) || Math.Abs(current - next) > 0.1)
        {
            assign(next);
        }
    }

    private void OnFileListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem item)
        {
            item.MinWidth = ColumnLayoutHost.Shared.VisibleWidth;
            item.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private void ApplyFileListThumbnailPolicy()
    {
        if (_workspace is null)
        {
            FileListThumbnailHost.ApplyPolicy(PaneId.Primary, enabled: false);
            FileListThumbnailHost.ApplyPolicy(PaneId.Secondary, enabled: false);
            return;
        }

        FileListThumbnailHost.ApplyPolicy(
            PaneId.Primary,
            ShouldUseFileListThumbnails(PrimaryFiles));
        FileListThumbnailHost.ApplyPolicy(
            PaneId.Secondary,
            ShouldUseFileListThumbnails(SecondaryFiles));
    }

    private bool ShouldUseFileListThumbnails(IEnumerable<FileRow> rows)
    {
        if (_workspace is null)
        {
            return false;
        }

        var entries = rows.Select(row => new FileEntry
        {
            Name = row.Name,
            Path = row.Path,
            IsDir = row.IsDir,
            Extension = row.Extension,
        });
        return PhotoFolder.IsPhotoFolder(entries, _workspace.Settings.PhotoFolderImageThreshold);
    }

    private Task<string> LoadFileListImageThumbnailAsync(string path, uint size, CancellationToken cancellationToken)
    {
        var fileOps = _workspace?.FileOps
            ?? throw new InvalidOperationException("File operations are not available.");
        return fileOps.GenerateThumbnailAsync(path, size, cancellationToken);
    }

    private static void SelectRow(ListView list, ObservableCollection<FileRow> rows, string? path)
    {
        if (list.SelectionMode != ListViewSelectionMode.Single && list.SelectedItems.Count > 1)
        {
            return;
        }

        list.SelectedItem = path is null ? null : rows.FirstOrDefault(row => row.Path == path);
    }

    private FileRow ToFileRow(FileEntry entry) =>
        ToFileRow(entry, _workspace?.Normalize(_workspace.ActivePane) ?? PaneId.Primary);

    private FileRow ToFileRow(FileEntry entry, PaneId pane)
    {
        var cut = _workspace?.Clipboard is { Operation: ClipboardOperation.Cut, HasItems: true } clipboard
            && clipboard.SourcePaths.Any(path => PathRules.PathsEqual(path, entry.Path));
        Tag? tag = null;
        _workspace?.FileTags.TryGetValue(entry.Path, out tag);
        return FileRow.From(entry, cut, tag, pane);
    }

    private FileRow SearchRowFrom(SearchResult result, PaneId pane)
    {
        return ToFileRow(new FileEntry
        {
            Name = result.Name,
            Path = result.Path,
            IsDir = result.IsDir,
            Size = result.Size,
            Modified = result.Modified,
            Extension = result.Extension,
        }, pane);
    }

    private void UpdateEmptyStates()
    {
        if (_workspace is null)
        {
            return;
        }

        SetEmptyState(PrimaryEmptyText, PrimaryFiles.Count, _workspace.Primary, _searchMode && _searchPane == PaneId.Primary);
        SetEmptyState(SecondaryEmptyText, SecondaryFiles.Count, _workspace.Secondary, _searchMode && _searchPane == PaneId.Secondary);
    }

    private static void SetEmptyState(TextBlock target, int count, ExplorerPane pane, bool searching)
    {
        if (count > 0)
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }

        target.Text = pane.ListingInProgress
            ? "Loading…"
            : searching
                ? "No search results"
                : string.IsNullOrEmpty(pane.Path)
                    ? "Select a folder"
                    : "This folder is empty";
        target.Visibility = Visibility.Visible;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static bool ReplaceIfChanged<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, T, bool> same)
    {
        if (target.Count == source.Count)
        {
            var unchanged = true;
            for (var index = 0; index < source.Count; index++)
            {
                if (!same(target[index], source[index]))
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return false;
            }
        }

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }

        return true;
    }

    private static bool SameFileRow(FileRow left, FileRow right) =>
        left.Path == right.Path
        && left.Name == right.Name
        && left.IsDir == right.IsDir
        && left.IsCut == right.IsCut
        && left.Size == right.Size
        && left.ItemsText == right.ItemsText
        && left.ModifiedText == right.ModifiedText
        && left.SizeText == right.SizeText
        && left.TypeText == right.TypeText
        && left.ExtensionText == right.ExtensionText
        && left.GitText == right.GitText
        && left.SymlinkText == right.SymlinkText
        && left.PathText == right.PathText
        && left.ParentText == right.ParentText
        && left.TagColor == right.TagColor
        && left.Icon == right.Icon
        && left.Pane == right.Pane;

    private static bool SameDriveRow(DriveRow left, DriveRow right) =>
        left.Path == right.Path
        && left.Name == right.Name
        && left.IsActive == right.IsActive
        && left.Description == right.Description
        && left.Badge == right.Badge
        && left.UsageText == right.UsageText
        && left.ShowUsage == right.ShowUsage
        && Math.Abs(left.UsedPercent - right.UsedPercent) < 0.5
        && left.Icon == right.Icon;

    private static bool SameQuickAccessRow(QuickAccessRow left, QuickAccessRow right) =>
        left.Command == right.Command
        && left.Name == right.Name
        && left.Path == right.Path
        && left.Icon == right.Icon;

    private static void BindItemsSource(ListView list, object? items)
    {
        if (!ReferenceEquals(list.ItemsSource, items))
        {
            list.ItemsSource = items;
        }
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
        StatusText.Text = closing ? "Right pane closed" : "Second pane opened";
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
        StatusText.Text = closingLeft ? "Left pane closed" : "Right pane closed";
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
        StatusText.Text = _workspace.Settings.SidebarVisible ? "Side menu shown" : "Side menu hidden";
    }

    private void OnSidebarLeft(object sender, RoutedEventArgs e) => _workspace?.ActivatePane(PaneId.Primary);

    private void OnSidebarRight(object sender, RoutedEventArgs e) => _workspace?.ActivatePane(PaneId.Secondary);

    private void AttachPaneActivationHandlers()
    {
        PrimaryPaneRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPrimaryPanePressed), true);
        SecondaryPaneRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSecondaryPanePressed), true);
    }

    private void OnPrimaryPanePressed(object sender, PointerRoutedEventArgs e) => _workspace?.ActivatePane(PaneId.Primary);

    private void OnSecondaryPanePressed(object sender, PointerRoutedEventArgs e) => _workspace?.ActivatePane(PaneId.Secondary);

    private async void OnPrimaryBack(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Navigation", () => GoHistory(ActiveUiPane, -1));

    private async void OnPrimaryForward(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Navigation", () => GoHistory(ActiveUiPane, 1));

    private async void OnPrimaryUp(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null)
        {
            await RunUiActionAsync("Navigation", () => _workspace.GoUpAsync(ActiveUiPane));
        }
    }

    private async Task GoHistory(PaneId pane, int delta)
    {
        if (_workspace is null)
        {
            return;
        }

        if (delta < 0)
        {
            await _workspace.GoBackAsync(pane);
        }
        else
        {
            await _workspace.GoForwardAsync(pane);
        }
    }

    private async void OnRefreshDrives(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null)
        {
            await RunUiActionAsync("Refresh drives", () => _workspace.RefreshDrivesAsync());
        }
    }

    private void OnToggleQuickAccess(object sender, RoutedEventArgs e)
    {
        _quickAccessCollapsed = !_quickAccessCollapsed;
        if (_workspace is not null)
        {
            _workspace.Settings.QuickAccessCollapsed = _quickAccessCollapsed;
        }

        SetExpandGlyph(QuickAccessCollapseButton, _quickAccessCollapsed);
        ApplySidebarSectionVisibility();
    }

    private void OnToggleMyPc(object sender, RoutedEventArgs e)
    {
        _myPcCollapsed = !_myPcCollapsed;
        if (_workspace is not null)
        {
            _workspace.Settings.MyPcCollapsed = _myPcCollapsed;
        }

        SetExpandGlyph(MyPcCollapseButton, _myPcCollapsed);
        ApplySidebarSectionVisibility();
    }

    private async void OnTabKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_workspace is null || sender is not Button { Tag: PaneTab tab })
        {
            return;
        }

        if (e.Key is not VirtualKey.Left and not VirtualKey.Right)
        {
            return;
        }

        e.Handled = true;
        var pane = _workspace.Pane(tab.Pane);
        if (pane.Tabs.Count == 0)
        {
            return;
        }

        var index = pane.Tabs.FindIndex(candidate => candidate.Id == tab.TabId);
        if (index < 0)
        {
            return;
        }

        var delta = e.Key == VirtualKey.Right ? 1 : -1;
        var next = pane.Tabs[(index + delta + pane.Tabs.Count) % pane.Tabs.Count];
        await RunUiActionAsync("Tab", () => _workspace.SwitchToTabAsync(next.Id, tab.Pane));
    }

    private async void OnQuickAccessClick(object sender, ItemClickEventArgs e)
    {
        if (_workspace is not null && e.ClickedItem is QuickAccessRow row)
        {
            await RunUiActionAsync("Quick access", () => _workspace.NavigateSpecialAsync(row.Command, _workspace.SidebarTarget));
        }
    }

    private async void OnDriveClick(object sender, ItemClickEventArgs e)
    {
        if (_workspace is not null && e.ClickedItem is DriveRow row)
        {
            await RunUiActionAsync("Drive", () => _workspace.OpenPathAsync(row.Path, isDirectory: true, _workspace.SidebarTarget));
        }
    }

    private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && sender is Button { Tag: PanePath target })
        {
            await RunUiActionAsync("Breadcrumb", () => _workspace.NavigatePaneAsync(target.Pane, target.Path));
        }
    }

    private void OnEditPrimaryPath(object sender, RoutedEventArgs e) => BeginPathEdit(PaneId.Primary);

    private void OnEditSecondaryPath(object sender, RoutedEventArgs e) => BeginPathEdit(PaneId.Secondary);

    private void BeginPathEdit(PaneId pane)
    {
        if (_workspace is null)
        {
            return;
        }

        var input = pane == PaneId.Secondary ? SecondaryPathInput : PrimaryPathInput;
        var scroller = pane == PaneId.Secondary ? SecondaryBreadcrumbScroller : PrimaryBreadcrumbScroller;
        if (pane == PaneId.Secondary)
        {
            _editingSecondaryPath = true;
        }
        else
        {
            _editingPrimaryPath = true;
        }

        input.Text = _workspace.Pane(pane).Path;
        scroller.Visibility = Visibility.Collapsed;
        input.Visibility = Visibility.Visible;
        input.Focus(FocusState.Programmatic);
        input.SelectAll();
    }

    private void EndPathEdit(PaneId pane, bool reset)
    {
        var input = pane == PaneId.Secondary ? SecondaryPathInput : PrimaryPathInput;
        var scroller = pane == PaneId.Secondary ? SecondaryBreadcrumbScroller : PrimaryBreadcrumbScroller;
        if (pane == PaneId.Secondary)
        {
            _editingSecondaryPath = false;
        }
        else
        {
            _editingPrimaryPath = false;
        }

        if (reset && _workspace is not null)
        {
            input.Text = _workspace.Pane(pane).Path;
        }

        input.Visibility = Visibility.Collapsed;
        scroller.Visibility = Visibility.Visible;
    }

    private async void OnPrimaryPathKeyDown(object sender, KeyRoutedEventArgs e) =>
        await RunUiActionAsync("Navigation", () => HandlePathKey(e, PaneId.Primary));

    private async void OnSecondaryPathKeyDown(object sender, KeyRoutedEventArgs e) =>
        await RunUiActionAsync("Navigation", () => HandlePathKey(e, PaneId.Secondary));

    private void OnPrimaryPathLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingPrimaryPath)
        {
            EndPathEdit(PaneId.Primary, reset: true);
        }
    }

    private void OnSecondaryPathLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingSecondaryPath)
        {
            EndPathEdit(PaneId.Secondary, reset: true);
        }
    }

    private async Task HandlePathKey(KeyRoutedEventArgs e, PaneId pane)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndPathEdit(pane, reset: true);
            return;
        }

        if (e.Key != VirtualKey.Enter || _workspace is null)
        {
            return;
        }

        var input = pane == PaneId.Secondary ? SecondaryPathInput : PrimaryPathInput;
        var path = input.Text.Trim();
        if (path.Length == 0)
        {
            return;
        }

        e.Handled = true;
        EndPathEdit(pane, reset: false);
        await _workspace.NavigatePaneAsync(pane, path);
    }

    private void OnPrimaryFileRightTapped(object sender, RightTappedRoutedEventArgs e) =>
        SelectRightTapped(PrimaryFileList, PaneId.Primary, e);

    private void OnSecondaryFileRightTapped(object sender, RightTappedRoutedEventArgs e) =>
        SelectRightTapped(SecondaryFileList, PaneId.Secondary, e);

    private void SelectRightTapped(ListView list, PaneId pane, RightTappedRoutedEventArgs e)
    {
        if (_workspace is null || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var view = FindAncestor<FileRowView>(source);
        var row = view?.Row;
        if (row is null)
        {
            return;
        }

        if (list.SelectedItems.OfType<FileRow>().Any(item =>
                string.Equals(item.Path, row.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _applyingWorkspace = true;
        try
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(row);
        }
        finally
        {
            _applyingWorkspace = false;
        }

        _workspace.SelectPath(row.Path, pane);
        QueuePreview(row);
        UpdateSelectionStatus();
    }

    private void OnPrimarySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        HandleSelectionChanged(PrimaryFileList, PaneId.Primary, e);

    private void OnSecondarySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        HandleSelectionChanged(SecondaryFileList, PaneId.Secondary, e);

    private void HandleSelectionChanged(ListView list, PaneId pane, SelectionChangedEventArgs e)
    {
        if (_applyingWorkspace || _workspace is null)
        {
            return;
        }

        var row = e.AddedItems.OfType<FileRow>().LastOrDefault()
            ?? list.SelectedItems.OfType<FileRow>().LastOrDefault();
        if (row is null)
        {
            if (list == ActiveFileList)
            {
                _workspace.SelectPath(null, pane);
                ClearPreview();
                UpdateSelectionStatus();
            }

            return;
        }

        _workspace.SelectPath(row.Path, pane);
        QueuePreview(row);
        UpdateSelectionStatus();
    }

    private async void OnPrimaryFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        await RunUiActionAsync("Open", () => OpenSelectedFile(PrimaryFileList, PaneId.Primary));

    private async void OnSecondaryFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        await RunUiActionAsync("Open", () => OpenSelectedFile(SecondaryFileList, PaneId.Secondary));

    private async void OnPrimaryFileKeyDown(object sender, KeyRoutedEventArgs e) =>
        await RunUiActionAsync("File list", () => HandleFileKey(e, PrimaryFileList, PaneId.Primary));

    private async void OnSecondaryFileKeyDown(object sender, KeyRoutedEventArgs e) =>
        await RunUiActionAsync("File list", () => HandleFileKey(e, SecondaryFileList, PaneId.Secondary));

    private async Task HandleFileKey(KeyRoutedEventArgs e, ListView list, PaneId pane)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await OpenSelectedFile(list, pane);
            return;
        }

        var letter = e.Key.ToString();
        if (_workspace is not null && letter.Length == 1 && char.IsLetterOrDigit(letter[0]))
        {
            var match = _workspace.MatchTypeAhead(letter[0]);
            if (match is not null)
            {
                e.Handled = true;
                _workspace.SelectPath(match.Path, pane);
                var rows = pane == PaneId.Secondary ? SecondaryFiles : PrimaryFiles;
                SelectRow(list, rows, match.Path);
                var row = rows.FirstOrDefault(item =>
                    string.Equals(item.Path, match.Path, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    QueuePreview(row);
                }

                UpdateSelectionStatus();
            }
        }
    }

    private async Task OpenSelectedFile(ListView list, PaneId pane)
    {
        if (_workspace is not null && list.SelectedItem is FileRow row)
        {
            await SaveViewIconSizeNowAsync();
            await _workspace.OpenEntryAsync(
                new FileEntry { Name = row.Name, Path = row.Path, IsDir = row.IsDir },
                pane);
        }
    }

    private FileRow? ActiveSelectedRow =>
        ActiveFileList.SelectedItem as FileRow
        ?? ActiveFileList.SelectedItems.OfType<FileRow>().LastOrDefault();

    private IReadOnlyList<FileRow> ActiveSelectedRows =>
        ActiveFileList.SelectedItems.OfType<FileRow>().ToArray();

    private void UpdateSelectionStatus()
    {
        if (_workspace is null)
        {
            return;
        }

        var active = _workspace.Active;
        var searchCount = _searchMode && _searchPane == _workspace.ActivePane
            ? _activeSearchResults.Count
            : (int?)null;
        var visible = _workspace.VisibleEntriesFor(_workspace.ActivePane);
        var count = searchCount ?? visible.Count;
        var selectedEntries = ActiveSelectedRows
            .Select(row => new FileEntry { Name = row.Name, Path = row.Path, IsDir = row.IsDir, Size = row.Size })
            .ToList();
        var snapshot = StatusBarFormatter.Format(
            count,
            selectedEntries,
            active.Path,
            _workspace.ActivePaneLabel,
            listingInProgress: active.ListingInProgress,
            isEmpty: count == 0 && !active.ListingInProgress && searchCount is null);
        CountText.Text = searchCount is null
            ? snapshot.Combined
            : (count == 1 ? "1 search result" : $"{count} search results");
        if (searchCount is not null && !string.IsNullOrEmpty(_workspace.ActivePaneLabel))
        {
            CountText.Text = $"{_workspace.ActivePaneLabel} · {CountText.Text}";
        }

        if (active.ListingInProgress && count == 0)
        {
            StatusText.Text = "Loading…";
        }
        else if (!string.IsNullOrEmpty(_workspace.ErrorMessage))
        {
            StatusText.Text = _workspace.ErrorMessage;
        }
        else if (_searchMode && _searchPane == _workspace.ActivePane)
        {
            var searchText = SearchTextBoxFor(_searchPane).Text;
            StatusText.Text = string.IsNullOrWhiteSpace(searchText)
                ? "Search results"
                : $"Search results for \"{searchText.Trim()}\"";
        }
        else if (!string.IsNullOrEmpty(_workspace.StatusMessage))
        {
            StatusText.Text = _workspace.StatusMessage;
        }
        else
        {
            StatusText.Text = active.Path;
        }
    }

    private void QueuePreviewFromSelection()
    {
        var row = ActiveSelectedRow;
        if (row is null)
        {
            ClearPreview();
            return;
        }

        QueuePreview(row);
    }

    private void QueuePreview(FileRow row)
    {
        UpdatePreviewButtons(row);
        if (string.Equals(_previewPath, row.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _previewPath = row.Path;
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = LoadPreviewAsync(row, cts);
    }

    private void ClearPreview()
    {
        _previewPath = null;
        _previewCts?.Cancel();
        _previewCts = null;
        _ = Interlocked.Increment(ref _previewToken);
        PreviewTitle.Text = "Preview";
        PreviewSubtitle.Text = "Select a file";
        ClearPreviewIcon();
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewTextBox.Text = "";
        PreviewTextBox.Visibility = Visibility.Collapsed;
        PreviewEmptyText.Text = "No preview loaded.";
        PreviewEmptyText.Visibility = Visibility.Visible;
        PreviewMetadataRows.Children.Clear();
        PreviewChecksumText.Text = "";
        UpdatePreviewButtons(null);
    }

    private void UpdatePreviewButtons(FileRow? row)
    {
        var selected = ActiveSelectedRows;
        var canActOnSelection = row is not null;
        var canInspectFile = row is not null && !row.IsDir;
        PreviewOpenButton.IsEnabled = canActOnSelection;
        PreviewRevealButton.IsEnabled = canActOnSelection;
        PreviewOpenWithButton.IsEnabled = canInspectFile;
        PreviewChecksumButton.IsEnabled = canInspectFile;
        PreviewCompareButton.IsEnabled = selected.Count == 2 && selected.All(item => !item.IsDir);
    }

    private bool IsPreviewCurrent(string path, int token)
    {
        return token == _previewToken
            && string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPreviewCurrent(string path, int token, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested && IsPreviewCurrent(path, token);
    }

    private async Task LoadPreviewAsync(FileRow row, CancellationTokenSource cts)
    {
        var token = Interlocked.Increment(ref _previewToken);
        var cancellationToken = cts.Token;
        try
        {
            PreviewTitle.Text = row.Name;
            PreviewSubtitle.Text = row.Path;
            ShowPreviewIcon(row);
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewTextBox.Text = "";
            PreviewTextBox.Visibility = Visibility.Collapsed;
            PreviewEmptyText.Text = row.IsDir ? "Folder selected." : "Loading preview...";
            PreviewEmptyText.Visibility = Visibility.Visible;
            PreviewMetadataRows.Children.Clear();
            PreviewChecksumText.Text = "";
            AddMetadataRow("Type", row.TypeText);
            AddMetadataRow("Size", row.SizeText);
            AddMetadataRow("Modified", row.ModifiedText);

            if (row.IsDir || _workspace?.FileOps is null)
            {
                return;
            }

            FilePreview? preview = null;
            try
            {
                preview = await _workspace.FileOps.ReadFilePreviewAsync(row.Path, 2_000_000, cancellationToken);
                if (!IsPreviewCurrent(row.Path, token, cancellationToken))
                {
                    return;
                }

                AddMetadataRow("Preview type", preview.FileType);
                AddMetadataRow("MIME", preview.MimeType);
                AddMetadataRow("Preview size", EntryPresentation.FormatFileSize(preview.Size, isDirectory: false));
                await RenderPreviewContentAsync(row, preview, token, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!IsPreviewCurrent(row.Path, token, cancellationToken))
                {
                    return;
                }

                PreviewEmptyText.Text = exception.Message;
            }

            await LoadMetadataAsync(row.Path, preview?.FileType, token, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task RenderPreviewContentAsync(FileRow row, FilePreview preview, int token, CancellationToken cancellationToken)
    {
        var path = row.Path;
        if (preview.FileType == "text" && preview.Content is not null)
        {
            if (!IsPreviewCurrent(path, token, cancellationToken))
            {
                return;
            }

            ClearPreviewIcon();
            PreviewTextBox.Text = preview.Content;
            PreviewTextBox.Visibility = Visibility.Visible;
            PreviewEmptyText.Visibility = Visibility.Collapsed;
            return;
        }

        if (preview.FileType == "image")
        {
            if (preview.Content is not null && await TrySetPreviewImageAsync(preview.Content, path, token, cancellationToken))
            {
                ClearPreviewIcon();
                PreviewEmptyText.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var thumbnail = await _workspace!.FileOps!.GenerateThumbnailAsync(path, 256, cancellationToken);
                if (await TrySetPreviewImageAsync(thumbnail, path, token, cancellationToken))
                {
                    ClearPreviewIcon();
                    PreviewEmptyText.Text = "Thumbnail preview";
                    return;
                }
            }
            catch
            {
                // Unsupported image codecs still keep metadata and actions visible.
            }
        }

        if (!IsPreviewCurrent(path, token, cancellationToken))
        {
            return;
        }

        ShowPreviewIcon(row, FileTypePreviewLabel(row, preview));
        PreviewEmptyText.Text = IconPreviewMessage(preview);
        PreviewEmptyText.Visibility = Visibility.Visible;
    }

    private void ShowPreviewIcon(FileRow row, string? label = null)
    {
        PreviewIconImage.Source = ShellIconLoader.ForEntry(row.Path, row.IsDir, 96);
        PreviewIconLabel.Text = string.IsNullOrWhiteSpace(label) ? row.TypeText : label;
        PreviewIconPanel.Visibility = Visibility.Visible;
    }

    private void ClearPreviewIcon()
    {
        PreviewIconImage.Source = null;
        PreviewIconLabel.Text = "";
        PreviewIconPanel.Visibility = Visibility.Collapsed;
    }

    private static Image CreateFileTypePreviewIcon(FileRow row, int iconSize)
    {
        return new Image
        {
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Uniform,
            Source = ShellIconLoader.ForEntry(row.Path, row.IsDir, iconSize),
        };
    }

    private static string FileTypePreviewLabel(FileRow row, FilePreview preview)
    {
        if (!string.IsNullOrWhiteSpace(row.TypeText))
        {
            return row.TypeText;
        }

        return preview.FileType switch
        {
            "audio" => "Audio file",
            "video" => "Video file",
            "image" => "Image file",
            "pdf" => "PDF file",
            "document" => "Document",
            "spreadsheet" => "Spreadsheet",
            "presentation" => "Presentation",
            "archive" => "Archive",
            "package" => "Package",
            "executable" => "Application",
            "font" => "Font file",
            "database" => "Database file",
            "disk-image" => "Disk image",
            "ebook" => "Ebook",
            "email" => "Email",
            "calendar" => "Calendar file",
            "contact" => "Contact file",
            "certificate" => "Certificate",
            "design" => "Design file",
            "model" => "3D model",
            "cad" => "CAD file",
            "torrent" => "Torrent file",
            "binary" => "Binary file",
            _ => "File",
        };
    }

    private static string IconPreviewMessage(FilePreview preview)
    {
        return preview.FileType switch
        {
            "pdf" => "Showing the file-type icon for this PDF.",
            "image" => "Showing the file-type icon for this image.",
            "audio" => "Showing the file-type icon for this audio file.",
            "video" => "Showing the file-type icon for this video file.",
            "document" => "Showing the file-type icon for this document.",
            "spreadsheet" => "Showing the file-type icon for this spreadsheet.",
            "presentation" => "Showing the file-type icon for this presentation.",
            "archive" => "Showing the file-type icon for this archive.",
            "package" => "Showing the file-type icon for this package.",
            "executable" => "Showing the file-type icon for this application or script.",
            "font" => "Showing the file-type icon for this font.",
            "database" => "Showing the file-type icon for this database file.",
            "disk-image" => "Showing the file-type icon for this disk image.",
            "ebook" => "Showing the file-type icon for this ebook.",
            "email" => "Showing the file-type icon for this email file.",
            "calendar" => "Showing the file-type icon for this calendar file.",
            "contact" => "Showing the file-type icon for this contact file.",
            "certificate" => "Showing the file-type icon for this certificate or key.",
            "design" => "Showing the file-type icon for this design file.",
            "model" => "Showing the file-type icon for this 3D model.",
            "cad" => "Showing the file-type icon for this CAD file.",
            "torrent" => "Showing the file-type icon for this torrent file.",
            "binary" => "Showing the file-type icon for this binary file.",
            _ => "Showing the file-type icon for this file.",
        };
    }

    private async Task LoadMetadataAsync(string path, string? previewType, int token, CancellationToken cancellationToken)
    {
        if (_workspace?.FileOps is null)
        {
            return;
        }

        try
        {
            var metadata = await _workspace.FileOps.GetFileMetadataAsync(path, cancellationToken);
            if (!IsPreviewCurrent(path, token, cancellationToken))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Summary))
            {
                AddMetadataRow("Summary", metadata.Summary!);
            }

            if (!string.Equals(metadata.Kind, "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                AddMetadataRow("Metadata kind", metadata.Kind);
            }

            AddMetadataRows(metadata.Fields);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsPreviewCurrent(path, token, cancellationToken))
            {
                AddMetadataRow("Metadata", exception.Message);
            }
        }

        if (!string.Equals(previewType, "image", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var image = await _workspace.FileOps.GetImageMetadataAsync(path, cancellationToken);
            if (!IsPreviewCurrent(path, token, cancellationToken))
            {
                return;
            }

            AddMetadataRow("Dimensions", $"{image.Width} x {image.Height}");
            AddMetadataRows(image.Exif.Take(12));
        }
        catch
        {
            // get_file_metadata already covers the non-EXIF image summary.
        }
    }

    private async Task<bool> TrySetPreviewImageAsync(string base64, string path, int token, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsPreviewCurrent(path, token, cancellationToken))
            {
                return false;
            }

            var source = await CreatePreviewImageSourceAsync(base64, path);

            if (!IsPreviewCurrent(path, token, cancellationToken))
            {
                return false;
            }

            PreviewImage.Source = source;
            PreviewImage.Visibility = Visibility.Visible;
            return true;
        }
        catch
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            return false;
        }
    }

    private static Task<ImageSource> CreatePreviewImageSourceAsync(string base64, string path) =>
        PreviewImageSourceFactory.FromBase64Async(base64, path);

    private void AddMetadataRows(IEnumerable<string[]> rows)
    {
        foreach (var row in rows)
        {
            if (row.Length >= 2)
            {
                AddMetadataRow(row[0], row[1]);
            }
        }
    }

    private void AddMetadataRow(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var row = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(0, 0, 0, 1),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brush("SfTextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = Brush("SfTextPrimaryBrush"),
            Opacity = 0.88,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(valueText, 1);
        row.Children.Add(labelText);
        row.Children.Add(valueText);
        PreviewMetadataRows.Children.Add(row);
    }

    private async void OnPreviewOpenClick(object sender, RoutedEventArgs e)
    {
        var workspace = _workspace;
        if (workspace is null || ActiveSelectedRow is not { } row)
        {
            return;
        }

        var pane = workspace.ActivePane;
        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.OpenPathAsync(row.Path, row.IsDir, pane, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Open", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async void OnPreviewRevealClick(object sender, RoutedEventArgs e)
    {
        var workspace = _workspace;
        if (workspace is null || ActiveSelectedRow is not { } row)
        {
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.RevealInFolderAsync(row.Path, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Reveal in folder", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async void OnPreviewOpenWithClick(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Open With", OpenSelectedWithAsync);

    private async Task OpenSelectedWithAsync()
    {
        await ShowOpenWithChooserAsync();
    }

    private async void OnPreviewChecksumClick(object sender, RoutedEventArgs e)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || ActiveSelectedRow is not { IsDir: false } row)
        {
            return;
        }

        PreviewChecksumButton.IsEnabled = false;
        PreviewChecksumText.Text = "Computing...";
        var token = _previewToken;
        var path = row.Path;
        var utilityCts = BeginUtilityOperation();
        try
        {
            var checksums = await fileOps.ComputeChecksumAsync(path, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace)
                || utilityCts.IsCancellationRequested
                || !IsPreviewCurrent(path, token))
            {
                return;
            }

            PreviewChecksumText.Text =
                $"MD5    {checksums.Md5}{Environment.NewLine}" +
                $"SHA1   {checksums.Sha1}{Environment.NewLine}" +
                $"SHA256 {checksums.Sha256}";
        }
        catch (OperationCanceledException)
        {
            if (IsPreviewCurrent(path, token))
            {
                PreviewChecksumText.Text = "";
            }
        }
        catch (Exception exception)
        {
            if (IsPreviewCurrent(path, token))
            {
                PreviewChecksumText.Text = exception.Message;
            }
        }
        finally
        {
            if (IsPreviewCurrent(path, token))
            {
                PreviewChecksumButton.IsEnabled = ActiveSelectedRow is { IsDir: false };
            }

            FinishUtilityOperation(utilityCts);
        }
    }

    private async void OnPreviewCompareClick(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Compare files", CompareSelectedFilesAsync);

    private async Task CompareSelectedFilesAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var selected = ActiveSelectedRows;
        if (selected.Count != 2 || selected.Any(row => row.IsDir))
        {
            return;
        }

        var pathA = selected[0].Path;
        var pathB = selected[1].Path;
        var utilityCts = BeginUtilityOperation();
        try
        {
            var comparison = await fileOps.CompareFilesAsync(pathA, pathB, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            await ShowComparisonAsync(comparison);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Compare files", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task ShowComparisonAsync(FileComparison comparison)
    {
        var summary = comparison.Identical
            ? "Files are identical."
            : $"{comparison.Added} added, {comparison.Removed} removed, {comparison.Changed} changed";
        var rows = comparison.Rows
            .Take(80)
            .Select(row =>
            {
                var left = row.LeftLine?.ToString() ?? "";
                var right = row.RightLine?.ToString() ?? "";
                var text = row.LeftText ?? row.RightText ?? "";
                return $"{row.Kind,-8} {left,4} {right,4}  {text}";
            });

        var diffBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, rows),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 360,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(diffBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(diffBox, ScrollBarVisibility.Auto);

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"{comparison.LeftName} -> {comparison.RightName}" },
                new TextBlock { Text = summary },
                diffBox,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "File Compare",
            Content = body,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private async void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && sender is FrameworkElement { Tag: PaneTab tab })
        {
            await RunUiActionAsync("Tab", () => _workspace.SwitchToTabAsync(tab.TabId, tab.Pane));
        }
    }

    private async void OnTabPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        if (_workspace is not null && sender is FrameworkElement { Tag: PaneTab tab })
        {
            e.Handled = true;
            await RunUiActionAsync("Tab", () => _workspace.CloseTabAsync(tab.TabId, tab.Pane));
        }
    }

    private async void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && sender is Button { Tag: PaneTab tab })
        {
            await RunUiActionAsync("Tab", () => _workspace.CloseTabAsync(tab.TabId, tab.Pane));
        }
    }

    private async void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && sender is Button { Tag: PaneId pane })
        {
            await RunUiActionAsync("Tab", () => _workspace.OpenNewTabAsync(pane));
        }
    }

    private async void OnSortColumn(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && sender is FrameworkElement { Tag: PaneSort sort })
        {
            await RunUiActionAsync(
                "Sort",
                async () =>
                {
                    _workspace.SetSort(sort.Pane, sort.Sort);
                    await _workspace.SaveWorkspaceLayoutAsync();
                });
        }
    }

    private void ApplyColumnHeader(Grid header, ColumnLayout columns, PaneId pane, ref string? renderedKey)
    {
        var visible = columns.VisibleColumns;
        var sortBy = _workspace?.SortByFor(pane) ?? "name";
        var sortAscending = _workspace?.SortAscendingFor(pane) ?? true;
        var key = string.Join('\u001f', visible.Select(column => column.Id))
            + $"|{sortBy}:{sortAscending}";
        if (!string.Equals(renderedKey, key, StringComparison.Ordinal))
        {
            renderedKey = key;
            header.ColumnDefinitions.Clear();
            header.Children.Clear();

            for (var index = 0; index < visible.Count; index++)
            {
                var column = visible[index];
                header.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(column.Width),
                });

                var cell = new Grid();
                var button = new Button
                {
                    Style = ChromeStyle("SfColumnHeaderButtonStyle"),
                    Padding = column.Id == "name" ? new Thickness(38, 5, 8, 5) : new Thickness(10, 5, 8, 5),
                    Content = HeaderLabel(column, pane),
                    Tag = new PaneSort(pane, column.Sort),
                    ContextFlyout = CreateColumnHeaderFlyout(column.Id, pane),
                };
                button.Click += OnSortColumn;
                ToolTipService.SetToolTip(button, $"Sort by {column.Label}");
                AutomationProperties.SetName(button, $"Sort by {column.Label}");
                cell.Children.Add(button);

                var thumb = new PaneResizeGrip
                {
                    Width = 8,
                    MinWidth = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 0, -4, 0),
                    Tag = new ColumnResizeTarget(column.Id, pane),
                };
                thumb.Children.Add(new Rectangle
                {
                    Width = 1,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Fill = Brush("SfBorderBrush"),
                });
                thumb.PointerPressed += OnColumnThumbPressed;
                thumb.PointerMoved += OnColumnThumbMoved;
                thumb.PointerReleased += OnColumnThumbReleased;
                thumb.PointerCanceled += OnColumnThumbReleased;
                thumb.PointerCaptureLost += OnColumnThumbReleased;
                thumb.DoubleTapped += OnColumnThumbDoubleTapped;
                Canvas.SetZIndex(thumb, 1);
                cell.Children.Add(thumb);

                Grid.SetColumn(cell, index);
                header.Children.Add(cell);
            }
        }

        UpdateHeaderColumnWidths(header, columns);
    }

    private MenuFlyout CreateColumnHeaderFlyout(string columnId, PaneId pane)
    {
        var flyout = new MenuFlyout();
        var sizeColumn = new MenuFlyoutItem { Text = "Size Column to Fit" };
        sizeColumn.Click += (_, _) => SizeColumnToFit(columnId, pane, save: true);
        var sizeAll = new MenuFlyoutItem { Text = "Size All Columns to Fit" };
        sizeAll.Click += (_, _) => SizeAllColumnsToFit(pane);
        flyout.Items.Add(sizeColumn);
        flyout.Items.Add(sizeAll);
        return flyout;
    }

    private static void UpdateHeaderColumnWidths(Grid header, ColumnLayout columns)
    {
        var visible = columns.VisibleColumns;
        for (var index = 0; index < visible.Count && index < header.ColumnDefinitions.Count; index++)
        {
            header.ColumnDefinitions[index].Width = new GridLength(visible[index].Width);
        }

        header.Width = Math.Max(1, columns.VisibleWidth + header.Padding.Left + header.Padding.Right);
    }

    private string HeaderLabel(FileListColumn column, PaneId pane)
    {
        var label = column.Id == "date" ? "Date modified" : column.Label;
        if (_workspace is null || !string.Equals(_workspace.SortByFor(pane), column.Sort, StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        return _workspace.SortAscendingFor(pane) ? $"{label} ↑" : $"{label} ↓";
    }

    private void SizeColumnToFit(string columnId, PaneId pane, bool save)
    {
        var columns = _workspace?.Columns ?? ColumnLayoutHost.Shared;
        var column = columns.Find(columnId);
        if (column is null)
        {
            return;
        }

        var rows = pane == PaneId.Secondary ? SecondaryFiles : PrimaryFiles;
        var extra = columnId == "name" ? FileListViewHost.IconSizeFor(pane) + 38 : 24;
        var headerText = column.Id == "date" ? "Date modified" : column.Label;
        var width = MeasureTextWidth(headerText, 11, semiBold: true) + extra;
        var fontSize = columnId == "name" ? 13d : 12d;
        foreach (var row in rows)
        {
            width = Math.Max(width, MeasureTextWidth(row.ColumnText(columnId), fontSize, semiBold: false) + extra);
        }

        columns.Resize(columnId, width + 12);
        ApplyColumnWidths();
        if (save && _workspace is not null)
        {
            _ = RunUiActionAsync("Resize columns", () => _workspace.SaveUiSettingsAsync());
        }
    }

    private void SizeAllColumnsToFit(PaneId pane)
    {
        var columns = _workspace?.Columns ?? ColumnLayoutHost.Shared;
        foreach (var column in columns.VisibleColumns)
        {
            SizeColumnToFit(column.Id, pane, save: false);
        }

        if (_workspace is not null)
        {
            _ = RunUiActionAsync("Resize columns", () => _workspace.SaveUiSettingsAsync());
        }
    }

    private static double MeasureTextWidth(string text, double fontSize, bool semiBold)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.NoWrap,
        };
        block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    private void HookFileListColumnScroll(ListView list)
    {
        list.Loaded -= OnFileListLoadedForColumnScroll;
        list.Loaded += OnFileListLoadedForColumnScroll;
        AttachFileListColumnScroll(list);
    }

    private void OnFileListLoadedForColumnScroll(object sender, RoutedEventArgs e)
    {
        if (sender is ListView list)
        {
            AttachFileListColumnScroll(list);
        }
    }

    private void AttachFileListColumnScroll(ListView list)
    {
        var scroller = FindDescendantScrollViewer(list);
        if (scroller is null)
        {
            return;
        }

        if (ReferenceEquals(list, PrimaryFileList))
        {
            if (!ReferenceEquals(_primaryFileListScroller, scroller))
            {
                if (_primaryFileListScroller is not null)
                {
                    _primaryFileListScroller.ViewChanged -= OnPrimaryFileListViewChanged;
                }

                _primaryFileListScroller = scroller;
                scroller.ViewChanged += OnPrimaryFileListViewChanged;
            }

            SyncHeaderScroll(PrimaryColumnHeaderScroller, scroller);
        }
        else if (ReferenceEquals(list, SecondaryFileList))
        {
            if (!ReferenceEquals(_secondaryFileListScroller, scroller))
            {
                if (_secondaryFileListScroller is not null)
                {
                    _secondaryFileListScroller.ViewChanged -= OnSecondaryFileListViewChanged;
                }

                _secondaryFileListScroller = scroller;
                scroller.ViewChanged += OnSecondaryFileListViewChanged;
            }

            SyncHeaderScroll(SecondaryColumnHeaderScroller, scroller);
        }
    }

    private void OnPrimaryFileListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => SyncHeaderScroll(PrimaryColumnHeaderScroller, sender as ScrollViewer);

    private void OnSecondaryFileListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => SyncHeaderScroll(SecondaryColumnHeaderScroller, sender as ScrollViewer);

    private static void SyncHeaderScroll(ScrollViewer header, ScrollViewer? list)
    {
        if (list is null)
        {
            return;
        }

        if (Math.Abs(header.HorizontalOffset - list.HorizontalOffset) > 0.5)
        {
            header.ChangeView(list.HorizontalOffset, null, null, disableAnimation: true);
        }
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root) =>
        FindDescendant<ScrollViewer>(root);

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        if (root is T match)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, index));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

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

    private async void OnRefreshAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is not null && !IsEditingPath)
        {
            await RunUiActionAsync("Refresh", () => _workspace.RefreshAsync());
        }
    }

    private async void OnDualPaneAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Dual pane", ToggleDualPaneFromUiAsync);
    }

    private async void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is not null && !IsEditingPath)
        {
            await RunUiActionAsync("Navigation", () => _workspace.GoBackAsync());
        }
    }

    private async void OnForwardAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is not null && !IsEditingPath)
        {
            await RunUiActionAsync("Navigation", () => _workspace.GoForwardAsync());
        }
    }

    private async void OnUpAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is not null && !IsEditingPath)
        {
            await RunUiActionAsync("Navigation", () => _workspace.GoUpAsync());
        }
    }

    private void OnFocusPrimaryAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _workspace?.ActivatePane(PaneId.Primary);
    }

    private async void OnFocusSecondaryAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is not null)
        {
            await RunUiActionAsync("Focus pane", () => _workspace.FocusSecondaryAsync());
        }
    }

    private async void OnNewTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is not null && !IsEditingPath)
        {
            await RunUiActionAsync("Tab", () => _workspace.OpenNewTabAsync());
        }
    }

    private async void OnCloseTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is null || IsEditingPath)
        {
            return;
        }

        var id = _workspace.Active.ActiveTabId;
        if (id is not null)
        {
            await RunUiActionAsync("Tab", () => _workspace.CloseTabAsync(id, _workspace.ActivePane));
        }
    }

    private async void OnNextTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is not null && !IsEditingPath)
        {
            await RunUiActionAsync("Tab", () => _workspace.SwitchTabByAsync(1));
        }
    }

    private async void OnPreviousTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is not null && !IsEditingPath)
        {
            await RunUiActionAsync("Tab", () => _workspace.SwitchTabByAsync(-1));
        }
    }

    private bool IsEditingPath => _editingPrimaryPath || _editingSecondaryPath;

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
        StatusText.Text = message;
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

    private void QueueColumnEnrichment()
    {
        if (_workspace?.FileOps is null)
        {
            return;
        }

        var needsGit = _workspace.Columns.IsVisible("git") && _workspace.Settings.EnableGitIntegration;
        var needsItems = _workspace.Columns.IsVisible("items");
        var needsSizes = _workspace.Settings.ShowFolderSizes;

        // Suppress expensive enrichment on network paths. Git operations and
        // recursive folder size/item count walks are catastrophically slow over SMB.
        if (_workspace.Active.PathIsNetwork)
        {
            needsGit = false;
            needsSizes = false;
            needsItems = false;
        }
        if (!needsGit && !needsItems && !needsSizes)
        {
            return;
        }

        var panes = _workspace.DualPaneEnabled
            ? new[] { PaneId.Primary, PaneId.Secondary }
            : new[] { PaneId.Primary };
        var signatureParts = new List<string>
        {
            string.Join(',', _workspace.Columns.VisibleIds),
            $"git={needsGit}",
            $"items={needsItems}",
            $"sizes={needsSizes}",
        };
        signatureParts.AddRange(panes.Select(ColumnEnrichmentSignatureFor));
        var signature = string.Join('|', signatureParts);
        if (string.Equals(signature, _columnEnrichmentSignature, StringComparison.Ordinal))
        {
            return;
        }

        _columnEnrichmentSignature = signature;
        _columnEnrichmentCts?.Cancel();
        var cts = new CancellationTokenSource();
        _columnEnrichmentCts = cts;
        var token = Interlocked.Increment(ref _columnEnrichmentToken);
        _ = EnrichColumnsAsync(panes, needsGit, needsSizes, needsItems, token, cts);
    }

    private string ColumnEnrichmentSignatureFor(PaneId pane)
    {
        if (_workspace is null)
        {
            return "";
        }

        var state = _workspace.Pane(pane);
        var entries = string.Join(
            '\u001e',
            state.Entries.Take(64).Select(entry => $"{entry.Path}:{entry.Modified}:{entry.IsDir}"));
        return $"{pane}:{state.Path}:{state.NavigationToken}:{state.ListingInProgress}:{state.Entries.Count}:{entries}";
    }

    private async Task EnrichColumnsAsync(
        IReadOnlyList<PaneId> panes,
        bool needsGit,
        bool needsSizes,
        bool needsItems,
        int token,
        CancellationTokenSource cts)
    {
        try
        {
            var workspace = _workspace;
            var cancellationToken = cts.Token;
            if (workspace is null)
            {
                return;
            }

            foreach (var pane in panes)
            {
                if (cancellationToken.IsCancellationRequested
                    || token != _columnEnrichmentToken
                    || workspace.Pane(pane).ListingInProgress)
                {
                    return;
                }

                if (needsGit)
                {
                    await workspace.ApplyGitStatusesAsync(pane, cancellationToken).ConfigureAwait(true);
                }

                if (cancellationToken.IsCancellationRequested || token != _columnEnrichmentToken)
                {
                    return;
                }

                if (needsSizes || needsItems)
                {
                    await workspace.FillFolderMetricsAsync(pane, needsSizes, needsItems, cancellationToken).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_columnEnrichmentCts, cts))
            {
                _columnEnrichmentCts = null;
            }

            cts.Dispose();
        }
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
                StatusText.Text = exception.Message;
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

            if (!string.IsNullOrEmpty(_previewPath)
                && string.Equals(change.Path, _previewPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_searchMode)
            {
                return;
            }

            if (!PathRules.PathContains(_workspace.Active.Path, change.Path)
                && !string.Equals(_workspace.Active.Path, _watchedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var name = System.IO.Path.GetFileName(change.Path);
            StatusText.Text = string.IsNullOrEmpty(name)
                ? $"{change.Kind}: {change.Path}"
                : $"{change.Kind}: {name}";
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
            StatusText.Text = exception.Message;
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
        Interlocked.Increment(ref _previewToken);
        _previewCts?.Cancel();
        _previewCts = null;
        CancelNetworkReconnectPrompt();
        CancelUtilityOperation();
        CancelArchiveOperation();
        _transferCts?.Cancel();
        _transferCts = null;
        _currentOperationId = null;
        CloseTransferProgressWindow();
        _searchCts?.Cancel();
        _searchCts = null;
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
            ColumnLayoutHost.Detach(workspace.Columns);
            FileListThumbnailHost.Configure(null);
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

    // ========================================================================
    // File operation helpers
    // ========================================================================

    private ListView ActiveFileList
        => _workspace?.ActivePane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;

    private string[]? SelectedPaths
    {
        get
        {
            var list = ActiveFileList;
            var items = list.SelectedItems;
            if (items == null || items.Count == 0) return null;
            return items.OfType<FileRow>().Select(r => r.Path).ToArray();
        }
    }

    private async Task PromptAndCreateFolder(PaneId pane)
    {
        var workspace = _workspace;
        if (workspace is null) return;

        var dialog = new ContentDialog
        {
            Title = "New Folder",
            Content = new TextBox { PlaceholderText = "Folder name" },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.Content is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            if (!ReferenceEquals(_workspace, workspace))
            {
                return;
            }

            var utilityCts = BeginUtilityOperation();
            try
            {
                workspace.ActivatePane(pane);
                await workspace.CreateFolderInCurrentPaneAsync(tb.Text.Trim(), utilityCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowMessage("New Folder", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }
    }

    private async Task PromptAndCreateFile(PaneId pane)
    {
        var workspace = _workspace;
        if (workspace is null) return;

        var dialog = new ContentDialog
        {
            Title = "New File",
            Content = new TextBox { PlaceholderText = "File name" },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.Content is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            if (!ReferenceEquals(_workspace, workspace))
            {
                return;
            }

            var utilityCts = BeginUtilityOperation();
            try
            {
                workspace.ActivatePane(pane);
                await workspace.CreateFileInCurrentPaneAsync(tb.Text.Trim(), utilityCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowMessage("New File", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }
    }

    private async Task PromptAndRename()
    {
        var workspace = _workspace;
        if (workspace is null) return;

        var list = ActiveFileList;
        if (list.SelectedItem is not FileRow row) return;

        var tb = new TextBox { Text = row.Name };
        tb.SelectAll();

        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = tb,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(tb.Text) && tb.Text.Trim() != row.Name)
        {
            if (!ReferenceEquals(_workspace, workspace))
            {
                return;
            }

            var utilityCts = BeginUtilityOperation();
            try
            {
                await workspace.RenameSelectedAsync(row.Path, tb.Text.Trim(), utilityCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowMessage("Rename", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }
    }

    private async Task TrashSelected()
    {
        var workspace = _workspace;
        if (workspace is null) return;
        var paths = SelectedPaths;
        if (paths is null || paths.Length == 0) return;
        var itemText = FormatItemCount(paths.Length);

        if (workspace.Settings.ConfirmDelete)
        {
            var dialog = new ContentDialog
            {
                Title = "Move to Recycle Bin",
                Content = $"Move {itemText} to the Recycle Bin?",
                PrimaryButtonText = "Move to Recycle Bin",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.TrashSelectedAsync(paths, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ex is IpcException ipcException && FileOperationService.IsTrashUnavailable(ipcException))
            {
                await PromptPermanentDeleteAfterTrashUnavailableAsync(paths, workspace, ipcException, utilityCts.Token);
                return;
            }

            ShowMessage("Trash", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task PromptPermanentDeleteAfterTrashUnavailableAsync(
        string[] paths,
        ExplorerWorkspace workspace,
        IpcException exception,
        CancellationToken cancellationToken)
    {
        var itemText = paths.Length == 1 ? "this item" : $"{paths.Length} items";
        var dialog = new ContentDialog
        {
            Title = "Recycle Bin unavailable",
            Content = $"{FileOperationService.TrashUnavailableMessage(exception)}{Environment.NewLine}{Environment.NewLine}Permanently delete {itemText} instead? This cannot be undone.",
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            ShowMessage("Recycle Bin unavailable", "Nothing was permanently deleted.", InfoBarSeverity.Warning);
            return;
        }

        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        try
        {
            foreach (var path in paths)
            {
                await workspace.DeleteSelectedAsync(path, cancellationToken);
            }

            ShowMessage("Deleted permanently", $"Deleted {itemText}.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception deleteException)
        {
            ShowMessage("Delete", deleteException.Message, InfoBarSeverity.Error);
        }
    }

    private async Task DeleteSelected()
    {
        var workspace = _workspace;
        if (workspace is null) return;
        var paths = SelectedPaths;
        if (paths is null || paths.Length == 0) return;
        var itemText = FormatItemCount(paths.Length);
        var content = paths.Length == 1
            ? $"Permanently delete this item? This cannot be undone.{Environment.NewLine}{paths[0]}"
            : $"Permanently delete {itemText}? This cannot be undone.";

        var dialog = new ContentDialog
        {
            Title = "Delete Permanently",
            Content = content,
            PrimaryButtonText = "Delete Permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(_workspace, workspace))
            {
                return;
            }

            var utilityCts = BeginUtilityOperation();
            try
            {
                foreach (var path in paths)
                {
                    await workspace.DeleteSelectedAsync(path, utilityCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowMessage("Delete", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }
    }

    private static string FormatItemCount(int count) =>
        count == 1 ? "this item" : $"{count} items";

    private void CopyToClipboard()
    {
        var paths = SelectedPaths;
        if (paths is not null && paths.Length > 0)
        {
            _workspace?.Clipboard.SetCopy(paths);
            _workspace?.RememberClipboard();
            StatusText.Text = $"Copied {paths.Length} item(s)";
        }
    }

    private void CutToClipboard()
    {
        var paths = SelectedPaths;
        if (paths is not null && paths.Length > 0)
        {
            _workspace?.Clipboard.SetCut(paths);
            _workspace?.RememberClipboard();
            StatusText.Text = $"Cut {paths.Length} item(s)";
        }
    }

    private async Task PasteFromClipboard()
    {
        if (_workspace is null || !_workspace.Clipboard.HasItems) return;

        var clipboard = _workspace.Clipboard;
        var destination = _workspace.Active.Path;
        await TransferWithConflictAsync(clipboard.SourcePaths, destination, clipboard.Operation == ClipboardOperation.Cut);
        if (clipboard.Operation == ClipboardOperation.Cut)
        {
            clipboard.Clear();
        }
    }

    private void StartTransferProgress(string operationId, bool move, IReadOnlyList<string> sources, string destination)
    {
        _currentOperationId = operationId;
        var window = EnsureTransferProgressWindow();
        window.Start(new TransferProgressContext(
            move,
            sources.Count,
            DescribeTransferSource(sources),
            destination));
    }

    private static string DescribeTransferSource(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return "";
        }

        var parents = sources
            .Select(SourceParent)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parents.Length == 1 ? parents[0] : "Multiple locations";
    }

    private static string SourceParent(string source)
    {
        var trimmed = source.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return source;
        }

        var parent = System.IO.Path.GetDirectoryName(trimmed);
        return string.IsNullOrWhiteSpace(parent) ? source : parent;
    }

    private void OnTransferProgress(ProgressUpdate update)
    {
        if (_currentOperationId is not null
            && !string.Equals(update.OperationId, _currentOperationId, StringComparison.Ordinal))
        {
            return;
        }

        _transferProgressWindow?.UpdateProgress(update);
        if (update.Status is "completed" or "cancelled" or "error")
        {
            _currentOperationId = null;
        }
    }

    private async void OnFileProgressCancelRequested(object? sender, EventArgs e)
    {
        var operationId = _currentOperationId;
        _transferCts?.Cancel();
        if (string.IsNullOrEmpty(operationId) || _workspace?.FileOps is null)
        {
            return;
        }

        _transferProgressWindow?.SetCancelling();
        try
        {
            await _workspace.FileOps.CancelOperationAsync(operationId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowMessage("Cancel operation", ex.Message, InfoBarSeverity.Error);
        }
    }

    private TransferProgressWindow EnsureTransferProgressWindow()
    {
        if (_transferProgressWindow is { IsClosed: false } existing)
        {
            return existing;
        }

        var window = new TransferProgressWindow();
        window.CancelRequested += OnFileProgressCancelRequested;
        window.Closed += OnTransferProgressWindowClosed;
        _transferProgressWindow = window;
        return window;
    }

    private void OnTransferProgressWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is TransferProgressWindow window)
        {
            window.CancelRequested -= OnFileProgressCancelRequested;
            window.Closed -= OnTransferProgressWindowClosed;
        }

        if (ReferenceEquals(_transferProgressWindow, sender))
        {
            _transferProgressWindow = null;
        }

        if (_currentOperationId is not null || _transferCts is not null)
        {
            OnFileProgressCancelRequested(sender, EventArgs.Empty);
        }
    }

    private void CloseTransferProgressWindow()
    {
        var window = _transferProgressWindow;
        if (window is null)
        {
            return;
        }

        window.CancelRequested -= OnFileProgressCancelRequested;
        window.Closed -= OnTransferProgressWindowClosed;
        _transferProgressWindow = null;
        if (!window.IsClosed)
        {
            window.Close();
        }
    }

    private async Task StartSearchAsync(PaneId? requestedPane = null)
    {
        if (_workspace?.FileOps is null)
        {
            return;
        }

        var pane = _workspace.Normalize(requestedPane ?? _workspace.ActivePane);
        _workspace.ActivatePane(pane);
        var searchBox = SearchTextBoxFor(pane);
        var query = searchBox.Text.Trim();
        if (query.Length == 0)
        {
            await CancelActiveSearchAsync();
            ClearSearchState();
            SyncFromWorkspace();
            return;
        }

        await CancelActiveSearchAsync();

        var root = _workspace.Pane(pane).Path;
        var searchId = $"search_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _searchCounter)}";
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _activeSearchId = searchId;
        _searchMode = true;
        _searchPane = pane;
        _searchRoot = root;
        _activeSearchResults.Clear();
        SetSearchCancelEnabled(pane, true);
        ApplySearchRows();
        StatusText.Text = $"Searching {root}...";

        var options = new SearchOptions
        {
            Query = query,
            SearchPath = root,
            CaseSensitive = false,
            IncludeHidden = false,
            MaxResults = 1000,
            MaxDepth = 10,
            SearchId = searchId,
            ContentSearch = false,
        };

        try
        {
            var results = await _workspace.FileOps.SearchAsync(
                options,
                batch => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _activeSearchResults.AddRange(batch);
                    ApplySearchRows();
                    StatusText.Text = $"Searching... {_activeSearchResults.Count} result(s)";
                }),
                count => DispatcherQueue.TryEnqueue(() =>
                {
                    if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
                    {
                        StatusText.Text = $"Search complete: {count} result(s)";
                    }
                }),
                cts.Token);

            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                _activeSearchResults.Clear();
                _activeSearchResults.AddRange(results);
                ApplySearchRows();
                StatusText.Text = $"Search complete: {results.Length} result(s)";
            }
        }
        catch (OperationCanceledException)
        {
            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                StatusText.Text = "Search cancelled";
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                ShowMessage("Search", ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
            }

            cts.Dispose();
            FinishSearchRun(searchId);
        }
    }

    private void ApplySearchRows()
    {
        if (_searchPane == PaneId.Secondary)
        {
            Replace(SecondaryFiles, _activeSearchResults.Select(result => SearchRowFrom(result, PaneId.Secondary)));
        }
        else
        {
            Replace(PrimaryFiles, _activeSearchResults.Select(result => SearchRowFrom(result, PaneId.Primary)));
        }

        CountText.Text = _activeSearchResults.Count == 1
            ? "1 search result"
            : $"{_activeSearchResults.Count} search results";
    }

    private async Task CancelActiveSearchAsync()
    {
        var searchId = _activeSearchId;
        _searchCts?.Cancel();
        if (string.IsNullOrEmpty(searchId) || _workspace?.FileOps is null)
        {
            UpdateSearchCancelButtons();
            return;
        }

        try
        {
            await _workspace.FileOps.CancelSearchAsync(searchId);
            StatusText.Text = "Search cancelled";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowMessage("Cancel search", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _activeSearchId = null;
            UpdateSearchCancelButtons();
        }
    }

    private void ClearSearchState()
    {
        _searchMode = false;
        _searchRoot = null;
        _searchCts?.Cancel();
        _searchCts = null;
        _activeSearchId = null;
        _activeSearchResults.Clear();
        UpdateSearchCancelButtons();
    }

    private void FinishSearchRun(string searchId)
    {
        if (!string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
        {
            return;
        }

        _activeSearchId = null;
        UpdateSearchCancelButtons();
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Search", () => StartSearchAsync(ActiveUiPane));

    private async void OnCancelSearchClick(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Cancel search", CancelActiveSearchAsync);
    }

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await RunUiActionAsync("Search", () => StartSearchAsync(ActiveUiPane));
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            await RunUiActionAsync("Cancel search", CancelActiveSearchAsync);
            ClearSearchState();
            SyncFromWorkspace();
        }
    }

    // ========================================================================
    // Per-pane button Click handlers
    // ========================================================================

    private async void OnPrimaryNewFolder(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("New Folder", () => PromptAndCreateFolder(ActiveUiPane));

    private async void OnPrimaryNewFile(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("New File", () => PromptAndCreateFile(ActiveUiPane));

    private async void OnPrimaryRename(object sender, RoutedEventArgs e)
    {
        _workspace?.ActivatePane(PaneId.Primary);
        await RunUiActionAsync("Rename", PromptAndRename);
    }

    private async void OnPrimaryDelete(object sender, RoutedEventArgs e)
    {
        _workspace?.ActivatePane(PaneId.Primary);
        await RunUiActionAsync("Trash", TrashSelected);
    }

    private async void OnSecondaryNewFolder(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("New Folder", () => PromptAndCreateFolder(PaneId.Secondary));

    private async void OnSecondaryNewFile(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("New File", () => PromptAndCreateFile(PaneId.Secondary));

    private async void OnSecondaryRename(object sender, RoutedEventArgs e)
    {
        _workspace?.ActivatePane(PaneId.Secondary);
        await RunUiActionAsync("Rename", PromptAndRename);
    }

    private async void OnSecondaryDelete(object sender, RoutedEventArgs e)
    {
        _workspace?.ActivatePane(PaneId.Secondary);
        await RunUiActionAsync("Trash", TrashSelected);
    }

    // ========================================================================
    // Keyboard accelerator handlers
    // ========================================================================

    private async void OnNewFolderAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("New Folder", () => PromptAndCreateFolder(_workspace?.ActivePane ?? PaneId.Primary));
    }

    private async void OnNewFileAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("New File", () => PromptAndCreateFile(_workspace?.ActivePane ?? PaneId.Primary));
    }

    private async void OnRenameAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Rename", PromptAndRename);
    }

    private async void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Delete", DeleteSelected);
    }

    private async void OnTrashAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Trash", TrashSelected);
    }

    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        CopyToClipboard();
    }

    private void OnCutAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        CutToClipboard();
    }

    private async void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Paste", PasteFromClipboard);
    }

    private FileRow[] GetSelectedEntries() => ActiveSelectedRows.ToArray();
    private void RefreshView() => SyncFromWorkspace();

    private static bool IsCancellationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("cancel", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnOpenTerminalAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunUiActionAsync("Terminal", OpenTerminalInActivePathAsync);
    }

    private async void OnSettingsAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunUiActionAsync("Settings", ShowSettingsAsync);
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Settings", ShowSettingsAsync);

    private async Task ShowSettingsAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null) return;
        var dialog = new SettingsDialog
        {
            XamlRoot = Content.XamlRoot,
            OwnerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this),
        };

        var utilityCts = BeginUtilityOperation();
        dialog.ClearRecentHistoryAction = () => ClearRecentHistoryAsync(utilityCts.Token);
        try
        {
            try
            {
                await dialog.LoadSettingsAsync(fileOps, utilityCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                ShowMessage("Settings", exception.Message, InfoBarSeverity.Error);
                return;
            }

            if (!ReferenceEquals(_workspace, workspace)
                || utilityCts.IsCancellationRequested
                || await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                dialog.ApplyTo(workspace.Settings);
                workspace.ApplyUiSettings(workspace.Settings, applyViewDefaultsToPanes: false);
                ApplyTheme(workspace.Settings.Theme);
                await workspace.SaveUiSettingsAsync(utilityCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ShowMessage("Settings", $"Settings were applied but could not be saved: {exception.Message}", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async void OnViewArchiveClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("View archive", ViewSelectedArchiveAsync);

    private async Task ViewSelectedArchiveAsync()
    {
        if (_workspace?.FileOps == null) return;
        var selected = GetSelectedEntries();
        if (selected.Length != 1) return;
        var entry = selected[0];
        try
        {
            var info = await _workspace.FileOps.ListArchiveAsync(entry.Path);
            var dialog = new ArchiveViewerDialog { XamlRoot = Content.XamlRoot };
            dialog.ArchiveData = info;
            var result = await dialog.ShowAsync();
            if (dialog.ExtractRequested)
            {
                await ShowExtractDialogAsync(info);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowMessage("View archive", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task ShowExtractDialogAsync(SimpleFile.Ipc.ArchiveInfo info)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null) return;
        var dialog = new ExtractArchiveDialog
        {
            XamlRoot = Content.XamlRoot,
            BrowseFolderAsync = PickFolderAsync,
        };
        dialog.ArchiveData = info;
        dialog.SetBaseDirectory(workspace.Active.Path);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(_workspace, workspace))
            {
                return;
            }

            var archiveCts = BeginArchiveOperation();
            try
            {
                await fileOps.ExtractArchiveAsync(info.Path, dialog.Destination, archiveCts.Token);
                if (ReferenceEquals(_workspace, workspace) && !archiveCts.IsCancellationRequested)
                {
                    await workspace.RefreshAsync(archiveCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowMessage("Extract archive", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                FinishArchiveOperation(archiveCts);
            }
        }
    }

    private async void OnExtractArchiveClicked(object sender, RoutedEventArgs e)
    {
        if (_workspace?.FileOps == null) return;
        var selected = GetSelectedEntries();
        if (selected.Length != 1) return;
        try
        {
            var info = await _workspace.FileOps.ListArchiveAsync(selected[0].Path);
            await ShowExtractDialogAsync(info);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowMessage("Extract archive", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnCreateArchiveClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Create archive", CreateArchiveAsync);

    private async Task CreateArchiveAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null) return;
        var selected = GetSelectedEntries();
        if (selected.Length == 0) return;
        var dialog = new CreateArchiveDialog { XamlRoot = Content.XamlRoot };
        dialog.SelectedPaths = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(selected, e => e.Path));
        dialog.SelectedNames = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(selected, e => e.Name));
        dialog.TargetDirectory = workspace.Active.Path;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(_workspace, workspace))
            {
                return;
            }

            var archiveCts = BeginArchiveOperation();
            try
            {
                await fileOps.CreateArchiveAsync(
                    dialog.SelectedPaths,
                    System.IO.Path.Combine(dialog.TargetDirectory, dialog.ArchiveName),
                    dialog.ArchiveFormat,
                    archiveCts.Token);
                if (ReferenceEquals(_workspace, workspace) && !archiveCts.IsCancellationRequested)
                {
                    await workspace.RefreshAsync(archiveCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowMessage("Create archive", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                FinishArchiveOperation(archiveCts);
            }
        }
    }

    private async void OnDuplicateCheckerClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Duplicate checker", ShowDuplicateCheckerAsync);

    private async Task ShowDuplicateCheckerAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null) return;
        var path = workspace.Active.Path;
        if (string.IsNullOrWhiteSpace(path)) return;

        var dialog = new DuplicateCheckerDialog { XamlRoot = Content.XamlRoot, Directory = path };
        dialog.ShowConfiguration();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!ReferenceEquals(_workspace, workspace)) return;

        var utilityCts = BeginUtilityOperation();
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(utilityCts.Token);
        var scanToken = scanCts.Token;
        var progress = new Progress<Ipc.ProgressUpdate>(update =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_workspace, workspace) && !scanToken.IsCancellationRequested)
                {
                    dialog.UpdateProgress(update);
                }
            });
        });

        dialog.ScanCancelled += async (_, _) =>
        {
            scanCts.Cancel();
            await RunUiActionAsync(
                "Duplicate checker",
                () => fileOps.CancelDuplicateCheckAsync());
        };
        dialog.PreviewRequested += (_, filePath) =>
        {
            if (!ReferenceEquals(_workspace, workspace))
            {
                return;
            }

            QueuePreview(ToFileRow(new FileEntry
            {
                Name = System.IO.Path.GetFileName(filePath),
                Path = filePath,
            }));
        };
        dialog.OpenRequested += async (_, filePath) =>
        {
            await RunUiActionAsync(
                "Open",
                () => ReferenceEquals(_workspace, workspace)
                    ? fileOps.OpenFileAsync(filePath)
                    : Task.CompletedTask);
        };
        dialog.RevealRequested += async (_, filePath) =>
        {
            await RunUiActionAsync(
                "Reveal in folder",
                () => ReferenceEquals(_workspace, workspace)
                    ? fileOps.RevealInFolderAsync(filePath)
                    : Task.CompletedTask);
        };

        try
        {
            dialog.ShowScanning();
            var scanUi = dialog.ShowAsync();
            var result = await fileOps.DuplicateCheckAsync(
                path, dialog.MinSizeBytes, null, progress, scanCts.Token);
            if (dialog.ScanWasCancelled
                || !ReferenceEquals(_workspace, workspace)
                || scanCts.IsCancellationRequested)
            {
                return;
            }

            dialog.ShowResults(result);
            await scanUi;
            if (dialog.DeleteRequested && ReferenceEquals(_workspace, workspace))
            {
                var trash = dialog.PathsToDelete;
                if (trash.Length > 0)
                {
                    await fileOps.TrashAsync(trash, scanCts.Token);
                    if (ReferenceEquals(_workspace, workspace) && !scanCts.IsCancellationRequested)
                    {
                        await workspace.RefreshAsync(scanCts.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            dialog.Hide();
        }
        catch (Exception ex)
        {
            dialog.Hide();
            if (!IsCancellationMessage(ex.Message))
            {
                if (ex is IpcException ipcException && FileOperationService.IsTrashUnavailable(ipcException))
                {
                    ShowMessage(
                        "Recycle Bin unavailable",
                        FileOperationService.TrashUnavailableMessage(ipcException),
                        InfoBarSeverity.Warning);
                }
                else
                {
                    ShowMessage("Duplicate checker", ex.Message, InfoBarSeverity.Error);
                }
            }
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async void OnDiskCleanupClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Disk cleanup", ShowDiskCleanupAsync);

    private async Task ShowDiskCleanupAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null) return;
        var path = workspace.Active.Path;
        if (string.IsNullOrWhiteSpace(path)) return;

        var dialog = new DiskCleanupDialog { XamlRoot = Content.XamlRoot, Directory = path };
        dialog.ShowConfiguration();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!ReferenceEquals(_workspace, workspace)) return;

        var utilityCts = BeginUtilityOperation();
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(utilityCts.Token);
        var scanToken = scanCts.Token;
        var progress = new Progress<Ipc.ProgressUpdate>(update =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_workspace, workspace) && !scanToken.IsCancellationRequested)
                {
                    dialog.UpdateProgress(update);
                }
            });
        });

        dialog.ScanCancelled += async (_, _) =>
        {
            scanCts.Cancel();
            await RunUiActionAsync(
                "Disk cleanup",
                () => fileOps.CancelDiskCleanupAsync());
        };

        try
        {
            dialog.ShowScanning();
            var scanUi = dialog.ShowAsync();
            var result = await fileOps.DiskCleanupAsync(path, dialog.ThresholdBytes, progress, scanCts.Token);
            if (dialog.ScanWasCancelled
                || !ReferenceEquals(_workspace, workspace)
                || scanCts.IsCancellationRequested)
            {
                return;
            }

            dialog.ShowResults(result);
            await scanUi;
        }
        catch (OperationCanceledException)
        {
            dialog.Hide();
        }
        catch (Exception ex)
        {
            dialog.Hide();
            if (!IsCancellationMessage(ex.Message))
            {
                ShowMessage("Disk cleanup", ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async void OnSetColorLabelClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Color label", SetColorLabelAsync);

    private async Task SetColorLabelAsync()
    {
        var workspace = _workspace;
        if (workspace == null) return;
        var selected = GetSelectedEntries();
        if (selected.Length == 0) return;
        var dialog = new TagPickerDialog { XamlRoot = Content.XamlRoot };
        dialog.SetTags(System.Linq.Enumerable.ToArray(workspace.AllTags));
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ReferenceEquals(_workspace, workspace))
            {
                return;
            }

            var paths = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(selected, e => e.Path));
            var utilityCts = BeginUtilityOperation();
            try
            {
                if (dialog.SelectedTagId.HasValue)
                {
                    await workspace.SetColorLabelAsync(paths, dialog.SelectedTagId.Value, utilityCts.Token);
                }
                else
                {
                    await workspace.RemoveColorLabelAsync(paths, utilityCts.Token);
                }

                if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
                {
                    RefreshView();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ShowMessage("Color label", exception.Message, InfoBarSeverity.Error);
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }
    }

    private async void OnOpenTerminalClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Terminal", OpenTerminalInActivePathAsync);

    private async Task OpenTerminalInActivePathAsync()
    {
        if (_workspace?.FileOps == null) return;
        try
        {
            await _workspace.FileOps.OpenTerminalAsync(_workspace.Active.Path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Terminal", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void RefreshSmartFolders()
    {
        if (_workspace == null) return;
        BindItemsSource(SmartFoldersList, _workspace.SmartFolders);
    }

    private async void OnSmartFolderClicked(object sender, ItemClickEventArgs e)
    {
        if (_workspace == null || e.ClickedItem is not SimpleFile.Ipc.SmartFolder folder) return;

        await CancelActiveSearchAsync();

        var pane = _workspace.ActivePane;
        var template = folder.SearchOptions;
        var root = template?.SearchPath;
        if (string.IsNullOrWhiteSpace(root)) root = _workspace.Active.Path;

        var searchId = $"search_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _searchCounter)}";
        var options = SearchOptionsFactory.ForRun(template, searchId, root);
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        _activeSearchId = searchId;
        _searchMode = true;
        _searchPane = pane;
        _searchRoot = options.SearchPath;
        _activeSearchResults.Clear();
        SetSearchCancelEnabled(pane, true);
        ApplySearchRows();
        StatusText.Text = "Searching smart folder...";

        try
        {
            var results = await _workspace.FileOps!.SearchAsync(
                options,
                batch => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.Equals(_activeSearchId, searchId, StringComparison.Ordinal)) return;
                    _activeSearchResults.AddRange(batch);
                    ApplySearchRows();
                    StatusText.Text = $"Searching... {_activeSearchResults.Count} result(s)";
                }),
                count => DispatcherQueue.TryEnqueue(() =>
                {
                    if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
                        StatusText.Text = $"Search complete: {count} result(s)";
                }),
                cts.Token);

            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                _activeSearchResults.Clear();
                _activeSearchResults.AddRange(results);
                ApplySearchRows();
                StatusText.Text = $"Search complete: {results.Length} result(s)";
            }
        }
        catch (OperationCanceledException)
        {
            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                StatusText.Text = "Search cancelled";
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(_activeSearchId, searchId, StringComparison.Ordinal))
            {
                ShowMessage("Smart folder", ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
            }

            cts.Dispose();
            FinishSearchRun(searchId);
        }
    }

    private async void OnRefreshFolderTree(object sender, RoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        var root = _workspace.Active.Path;
        if (string.IsNullOrEmpty(root))
        {
            root = _workspace.HomePath;
        }

        await RunUiActionAsync("Folder tree", () => _workspace.LoadTreeChildrenAsync(root));
    }

    private async void OnFolderTreeClick(object sender, ItemClickEventArgs e)
    {
        if (_workspace is not null && e.ClickedItem is FolderTreeItem item)
        {
            await RunUiActionAsync("Folder tree", () => _workspace.NavigateToAsync(item.Path));
        }
    }

    private async void OnFolderTreeToggle(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || sender is not FrameworkElement { Tag: string path })
        {
            return;
        }

        _workspace.ToggleTreeExpanded(path);
        await RunUiActionAsync("Folder tree", () => _workspace.LoadTreeChildrenAsync(path));
    }

    private void OnAddBookmark(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && !string.IsNullOrEmpty(_workspace.Active.Path))
        {
            _workspace.AddBookmark(_workspace.Active.Path);
        }
    }

    private void OnRemoveBookmark(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && sender is FrameworkElement { Tag: string path })
        {
            _workspace.RemoveBookmark(path);
        }
    }

    private async void OnBookmarkClick(object sender, ItemClickEventArgs e)
    {
        if (_workspace is not null && e.ClickedItem is BookmarkItem item)
        {
            await RunUiActionAsync("Bookmark", () => _workspace.NavigateToAsync(item.Path));
        }
    }

    private async void OnRecentClick(object sender, ItemClickEventArgs e)
    {
        if (_workspace is not null && e.ClickedItem is string path)
        {
            await RunUiActionAsync("Recent", () => _workspace.NavigateToAsync(path));
        }
    }

    private async void OnClearRecentHistory(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Recent history", () => ClearRecentHistoryAsync());

    private async Task ClearRecentHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_workspace is null)
        {
            return;
        }

        _workspace.ClearRecentHistory();
        await _workspace.SaveUiSettingsAsync(cancellationToken);
        StatusText.Text = "Recent history cleared";
        UpdateSidebarEmptyStates();
        ApplySidebarSectionVisibility();
    }

    private async void OnSaveSmartFolder(object sender, RoutedEventArgs e)
    {
        var workspace = _workspace;
        if (workspace is null)
        {
            return;
        }

        var queryPane = _searchMode ? _searchPane : workspace.ActivePane;
        var query = SearchTextBoxFor(queryPane).Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowMessage("Smart folder", "Run a search before saving it as a smart folder.", InfoBarSeverity.Informational);
            return;
        }

        var nameBox = new TextBox { PlaceholderText = "Smart folder name", Text = query };
        var dialog = new ContentDialog
        {
            Title = "Save Smart Folder",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return;
        }

        var options = new SearchOptions
        {
            Query = query,
            SearchPath = _searchMode && !string.IsNullOrWhiteSpace(_searchRoot) ? _searchRoot : workspace.Active.Path,
            IncludeHidden = workspace.Settings.ShowHidden,
            SearchId = $"smart_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        };
        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.SaveCurrentSearchAsSmartFolderAsync(nameBox.Text.Trim(), options, utilityCts.Token);
            if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
            {
                RefreshSmartFolders();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Smart folder", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async void OnDeleteSmartFolderClicked(object sender, RoutedEventArgs e)
    {
        var workspace = _workspace;
        if (workspace == null || sender is not FrameworkElement fe || fe.Tag is not string folderId) return;
        var utilityCts = BeginUtilityOperation();
        try
        {
            await workspace.DeleteSmartFolderAsync(folderId, utilityCts.Token);
            if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
            {
                RefreshSmartFolders();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Smart folder", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    // ── Marquee (rubber-band) selection ──────────────────────────────────

    private bool _isMarqueeDragging;
    private Windows.Foundation.Point _marqueeStartPoint;
    private PaneId _marqueePane;
    private readonly HashSet<object> _marqueeInitialSelection = [];

    private void OnPrimaryMarqueePressed(object sender, PointerRoutedEventArgs e)
        => BeginMarquee(sender, e, PaneId.Primary, PrimaryFileList, PrimaryMarqueeCanvas);

    private void OnSecondaryMarqueePressed(object sender, PointerRoutedEventArgs e)
        => BeginMarquee(sender, e, PaneId.Secondary, SecondaryFileList, SecondaryMarqueeCanvas);

    private void BeginMarquee(object sender, PointerRoutedEventArgs e, PaneId pane, ListView list, Canvas canvas)
    {
        var props = e.GetCurrentPoint((UIElement)sender).Properties;
        if (!props.IsLeftButtonPressed) return;

        // Only start marquee if the pointer is on empty space, not on an existing item.
        if (IsInsideListViewItem(e.OriginalSource as DependencyObject, list)) return;

        _marqueePane = pane;
        _marqueeStartPoint = e.GetCurrentPoint(canvas).Position;
        _isMarqueeDragging = true;

        if (sender is UIElement container)
        {
            container.CapturePointer(e.Pointer);
        }

        // Record initial selection for Ctrl modifier support.
        _marqueeInitialSelection.Clear();
        var modifiers = e.KeyModifiers;
        if ((modifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
        {
            foreach (var item in list.SelectedItems)
            {
                _marqueeInitialSelection.Add(item);
            }
        }
        else
        {
            list.SelectedItems.Clear();
        }

        // Show the marquee rectangle at zero size.
        var rect = pane == PaneId.Secondary ? SecondaryMarqueeRect : PrimaryMarqueeRect;
        Canvas.SetLeft(rect, _marqueeStartPoint.X);
        Canvas.SetTop(rect, _marqueeStartPoint.Y);
        rect.Width = 0;
        rect.Height = 0;
        rect.Visibility = Visibility.Visible;

        e.Handled = true;
    }

    private void OnMarqueePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMarqueeDragging) return;

        var canvas = _marqueePane == PaneId.Secondary ? SecondaryMarqueeCanvas : PrimaryMarqueeCanvas;
        var rect = _marqueePane == PaneId.Secondary ? SecondaryMarqueeRect : PrimaryMarqueeRect;
        var list = _marqueePane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;

        var currentPoint = e.GetCurrentPoint(canvas).Position;

        double x = Math.Min(_marqueeStartPoint.X, currentPoint.X);
        double y = Math.Min(_marqueeStartPoint.Y, currentPoint.Y);
        double width = Math.Abs(currentPoint.X - _marqueeStartPoint.X);
        double height = Math.Abs(currentPoint.Y - _marqueeStartPoint.Y);

        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = width;
        rect.Height = height;

        var marqueeBounds = new Windows.Foundation.Rect(x, y, width, height);
        UpdateMarqueeSelection(list, canvas, marqueeBounds, e.KeyModifiers);

        e.Handled = true;
    }

    private void OnMarqueePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMarqueeDragging) return;
        FinishMarquee(sender as UIElement, e.Pointer);
        e.Handled = true;
    }

    private void OnMarqueePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMarqueeDragging) return;
        FinishMarquee(sender as UIElement, e.Pointer);
    }

    private void FinishMarquee(UIElement? container, Pointer pointer)
    {
        _isMarqueeDragging = false;

        var rect = _marqueePane == PaneId.Secondary ? SecondaryMarqueeRect : PrimaryMarqueeRect;
        rect.Visibility = Visibility.Collapsed;
        rect.Width = 0;
        rect.Height = 0;

        if (container is not null)
        {
            container.ReleasePointerCapture(pointer);
        }

        _marqueeInitialSelection.Clear();
    }

    private void UpdateMarqueeSelection(ListView list, Canvas canvas, Windows.Foundation.Rect marqueeBounds, Windows.System.VirtualKeyModifiers modifiers)
    {
        bool ctrlPressed = (modifiers & Windows.System.VirtualKeyModifiers.Control) != 0;

        for (int i = 0; i < list.Items.Count; i++)
        {
            var rawItem = list.Items[i];
            if (list.ContainerFromIndex(i) is not ListViewItem container) continue;

            var transform = container.TransformToVisual(canvas);
            var itemBounds = transform.TransformBounds(
                new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));

            bool intersects = RectIntersects(marqueeBounds, itemBounds);

            if (ctrlPressed)
            {
                bool wasOriginallySelected = _marqueeInitialSelection.Contains(rawItem);
                bool shouldBeSelected = intersects ? !wasOriginallySelected : wasOriginallySelected;
                SetItemSelected(list, rawItem, shouldBeSelected);
            }
            else
            {
                SetItemSelected(list, rawItem, intersects);
            }
        }
    }

    private static bool RectIntersects(Windows.Foundation.Rect a, Windows.Foundation.Rect b)
    {
        return !(a.Right < b.Left || a.Left > b.Right || a.Bottom < b.Top || a.Top > b.Bottom);
    }

    private static void SetItemSelected(ListView list, object item, bool selected)
    {
        if (selected)
        {
            if (!list.SelectedItems.Contains(item))
            {
                list.SelectedItems.Add(item);
            }
        }
        else
        {
            list.SelectedItems.Remove(item);
        }
    }

    private static bool IsInsideListViewItem(DependencyObject? source, ListView list)
    {
        while (source is not null)
        {
            if (source is ListViewItem) return true;
            if (source == list) return false;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
