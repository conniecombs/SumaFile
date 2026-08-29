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
            RunUiActionAsync,
            ShowMessage,
            QueuePreview,
            ToFileRow,
            RefreshView,
            ApplyTheme,
            ClearRecentHistoryAsync,
            action => DispatcherQueue.TryEnqueue(() => action()));

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
            _transfer = AppServices.GetRequired<TransferViewModel>();
            _toolbar = AppServices.GetRequired<ToolbarViewModel>();
            AttachViewModels();
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

    private void AttachViewModels()
    {
        if (_search is not null)
        {
            _search.ResultsChanged += OnSearchResultsChanged;
            _search.Cleared += OnSearchCleared;
            _search.MessageRequested += OnViewModelMessageRequested;
            _search.PropertyChanged += OnSearchPropertyChanged;
        }

        if (_transfer is not null)
        {
            _transfer.ProgressReceived += OnTransferProgressReceived;
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

        if (_transfer is not null)
        {
            _transfer.ProgressReceived -= OnTransferProgressReceived;
        }
    }

    private void OnTransferProgressReceived(object? sender, ProgressUpdate update)
    {
        _transferProgressWindow?.UpdateProgress(update);
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

        SetEmptyState(
            PrimaryEmptyState,
            PrimaryEmptyTitle,
            PrimaryEmptyHint,
            PrimaryFiles.Count,
            _workspace.Primary,
            _search?.IsActiveForPane(PaneId.Primary) == true);
        SetEmptyState(
            SecondaryEmptyState,
            SecondaryEmptyTitle,
            SecondaryEmptyHint,
            SecondaryFiles.Count,
            _workspace.Secondary,
            _search?.IsActiveForPane(PaneId.Secondary) == true);
    }

    private void SetEmptyState(FrameworkElement host, TextBlock title, TextBlock hint, int count, ExplorerPane pane, bool searching)
    {
        var error = _workspace is not null && pane.Id == _workspace.ActivePane
            ? _workspace.ErrorMessage
            : null;
        var state = EmptyPaneState.Resolve(
            count,
            pane.Entries.Count,
            pane.ListingInProgress,
            searching,
            error,
            _workspace?.FilterQueryFor(pane.Id),
            _workspace?.ShowHiddenFiles ?? false,
            pane.Path);
        host.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (!state.Visible)
        {
            return;
        }

        title.Text = state.Title;
        hint.Text = state.Hint;
        hint.Visibility = string.IsNullOrEmpty(state.Hint) ? Visibility.Collapsed : Visibility.Visible;
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
        return ListReplace.Apply(target, source, same);
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
        && left.IsHidden == right.IsHidden
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

    private void OnPrimaryFileWheelChanged(object sender, PointerRoutedEventArgs e) =>
        HandleFileListWheel(e, PaneId.Primary);

    private void OnSecondaryFileWheelChanged(object sender, PointerRoutedEventArgs e) =>
        HandleFileListWheel(e, PaneId.Secondary);

    private void HandleFileListWheel(PointerRoutedEventArgs e, PaneId pane)
    {
        if (_workspace is null || !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            return;
        }

        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        e.Handled = true;
        var size = _workspace.NudgeFileListIconSize(pane, delta > 0 ? 1 : -1);
        ApplyFileListViewPresentation();
        QueueViewIconSizeSave();
        SetStatusText($"Icon size {size} px");
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

    private void OnPrimaryPathLostFocus(object sender, RoutedEventArgs e) =>
        QueueEndPathEdit(PaneId.Primary);

    private void OnSecondaryPathLostFocus(object sender, RoutedEventArgs e) =>
        QueueEndPathEdit(PaneId.Secondary);

    private void OnPrimaryPathTextChanged(object sender, TextChangedEventArgs e) =>
        _ = UpdatePathSuggestionsAsync(PaneId.Primary);

    private void OnSecondaryPathTextChanged(object sender, TextChangedEventArgs e) =>
        _ = UpdatePathSuggestionsAsync(PaneId.Secondary);

    private void QueueEndPathEdit(PaneId pane)
    {
        var token = Interlocked.Increment(ref _pathLostFocusToken);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token != Volatile.Read(ref _pathLostFocusToken) || _acceptingPathSuggestion)
            {
                return;
            }

            var editing = pane == PaneId.Secondary ? _editingSecondaryPath : _editingPrimaryPath;
            if (editing)
            {
                HidePathSuggestions();
                EndPathEdit(pane, reset: true);
            }
        });
    }

    private async Task UpdatePathSuggestionsAsync(PaneId pane)
    {
        if (_workspace is null)
        {
            return;
        }

        var editing = pane == PaneId.Secondary ? _editingSecondaryPath : _editingPrimaryPath;
        var input = pane == PaneId.Secondary ? SecondaryPathInput : PrimaryPathInput;
        if (!editing || input.Visibility != Visibility.Visible)
        {
            HidePathSuggestions();
            return;
        }

        if (!PathCompletion.TrySplit(input.Text, out var directory, out var prefix))
        {
            HidePathSuggestions();
            return;
        }

        IEnumerable<string> candidates;
        var paneState = _workspace.Pane(pane);
        if (PathRules.PathsEqual(directory, paneState.Path))
        {
            candidates = paneState.Entries
                .Where(entry => entry.IsDir)
                .Select(entry => entry.Path);
        }
        else if (_workspace.FileOps is not null)
        {
            try
            {
                var nodes = await _workspace.FileOps.ListSubdirectoriesAsync(directory);
                candidates = nodes.Select(node => node.Path);
            }
            catch
            {
                HidePathSuggestions();
                return;
            }
        }
        else
        {
            HidePathSuggestions();
            return;
        }

        var suggestions = PathCompletion.Suggest(candidates, prefix);
        if (suggestions.Count == 0)
        {
            HidePathSuggestions();
            return;
        }

        ShowPathSuggestions(pane, input, suggestions);
    }

    private void ShowPathSuggestions(PaneId pane, FrameworkElement anchor, IReadOnlyList<string> suggestions)
    {
        EnsurePathSuggestUi();
        _pathSuggestPane = pane;
        _pathSuggestList!.ItemsSource = suggestions;
        _pathSuggestFlyout!.ShowAt(anchor);
    }

    private void HidePathSuggestions()
    {
        _pathSuggestFlyout?.Hide();
    }

    private void EnsurePathSuggestUi()
    {
        if (_pathSuggestFlyout is not null)
        {
            return;
        }

        _pathSuggestList = new ListView
        {
            MinWidth = 360,
            MaxHeight = 240,
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.Single,
        };
        _pathSuggestList.ItemClick += OnPathSuggestClick;
        _pathSuggestFlyout = new Flyout
        {
            Content = _pathSuggestList,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        };
    }

    private void OnPathSuggestClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string path || _workspace is null)
        {
            return;
        }

        _acceptingPathSuggestion = true;
        Interlocked.Increment(ref _pathLostFocusToken);
        var pane = _pathSuggestPane;
        var input = pane == PaneId.Secondary ? SecondaryPathInput : PrimaryPathInput;
        var filled = path.TrimEnd('\\', '/') + PathRules.PathSeparator(path);
        input.Text = filled;
        input.SelectionStart = filled.Length;
        HidePathSuggestions();
        input.Focus(FocusState.Programmatic);
        _acceptingPathSuggestion = false;
    }

    private async Task HandlePathKey(KeyRoutedEventArgs e, PaneId pane)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HidePathSuggestions();
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
        HidePathSuggestions();
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
            if (PathRules.IsRecycleBinPath(_workspace.Pane(pane).Path) && row.IsDir)
            {
                await RestoreSelectedAsync();
                return;
            }

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

        var searchCount = _search?.IsActiveForPane(_workspace.ActivePane) == true
            ? _search.ResultCount
            : (int?)null;
        var visible = _workspace.VisibleEntriesFor(_workspace.ActivePane);
        var selectedEntries = ActiveSelectedRows
            .Select(row => new FileEntry { Name = row.Name, Path = row.Path, IsDir = row.IsDir, Size = row.Size })
            .ToList();
        _toolbar?.UpdateStatusBar(
            visible.Count,
            selectedEntries,
            searchCount,
            _search is null ? null : SearchTextBoxFor(_search.Pane).Text);
        ApplyStatusBarState();
    }

    private void QueuePreviewFromSelection()
    {
        _previewPresenter.QueueFromSelection();
    }

    private void QueuePreview(FileRow row)
    {
        _previewPresenter.Queue(row);
    }

    private void ClearPreview()
    {
        _previewPresenter.Clear();
    }

    private void UpdatePreviewButtons(FileRow? row)
    {
        _previewPresenter.UpdateButtons(row);
    }

    private async void OnPreviewOpenClick(object sender, RoutedEventArgs e)
    {
        await _previewPresenter.OpenSelectedAsync();
    }

    private async void OnPreviewRevealClick(object sender, RoutedEventArgs e)
    {
        await _previewPresenter.RevealSelectedAsync();
    }

    private async void OnPreviewOpenWithClick(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Open With", OpenSelectedWithAsync);

    private async Task OpenSelectedWithAsync()
    {
        await _previewPresenter.OpenWithSelectedAsync();
    }

    private async void OnPreviewChecksumClick(object sender, RoutedEventArgs e)
    {
        await _previewPresenter.ComputeChecksumAsync();
    }

    private async void OnPreviewCompareClick(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Compare files", CompareSelectedFilesAsync);

    private async Task CompareSelectedFilesAsync()
    {
        await _previewPresenter.CompareSelectedFilesAsync();
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
                var state = workspace.Pane(pane);
                if (cancellationToken.IsCancellationRequested
                    || token != _columnEnrichmentToken
                    || state.ListingInProgress)
                {
                    return;
                }

                if (state.PathIsNetwork)
                {
                    continue;
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
            ColumnLayoutHost.Detach(workspace.Columns);
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
        await _fileOperationDialogs.PromptAndCreateFolderAsync(pane);
    }

    private async Task PromptAndCreateFile(PaneId pane)
    {
        await _fileOperationDialogs.PromptAndCreateFileAsync(pane);
    }

    private async Task PromptAndRename()
    {
        await _fileOperationDialogs.PromptAndRenameAsync();
    }

    private async Task TrashSelected()
    {
        await _fileOperationDialogs.TrashSelectedAsync();
    }

    private async Task DeleteSelected()
    {
        await _fileOperationDialogs.DeleteSelectedAsync();
    }

    private async Task CopyToClipboard()
    {
        var paths = SelectedPaths;
        if (paths is not null && paths.Length > 0)
        {
            _workspace?.Clipboard.SetCopy(paths);
            _workspace?.RememberClipboard();
            var exported = await TrySetWindowsFileClipboardAsync(paths, ClipboardOperation.Copy);
            SetStatusText(exported
                ? $"Copied {paths.Length} item(s)"
                : $"Copied {paths.Length} item(s) in SumaFile");
        }
    }

    private async Task CutToClipboard()
    {
        var paths = SelectedPaths;
        if (paths is not null && paths.Length > 0)
        {
            _workspace?.Clipboard.SetCut(paths);
            _workspace?.RememberClipboard();
            var exported = await TrySetWindowsFileClipboardAsync(paths, ClipboardOperation.Cut);
            SetStatusText(exported
                ? $"Cut {paths.Length} item(s)"
                : $"Cut {paths.Length} item(s) in SumaFile");
        }
    }

    private async Task PasteFromClipboard(string? destinationOverride = null)
    {
        if (_workspace is null) return;

        var clipboard = _workspace.Clipboard;
        var payload = clipboard.HasItems
            ? new ClipboardTransferPayload(clipboard.Operation, clipboard.SourcePaths, IsInternal: true)
            : await TryReadWindowsFileClipboardAsync();
        if (payload is null || payload.SourcePaths.Length == 0)
        {
            SetStatusText("Clipboard does not contain files");
            return;
        }

        var destination = destinationOverride ?? _workspace.Active.Path;
        if (!DropDestination.IsValidDrop(payload.SourcePaths, destination))
        {
            SetStatusText("Cannot paste into that location");
            return;
        }

        var outcome = await TransferWithConflictAsync(
            payload.SourcePaths,
            destination,
            payload.Operation == ClipboardOperation.Cut);
        if (payload.IsInternal
            && payload.Operation == ClipboardOperation.Cut
            && outcome == TransferRunStatus.Completed)
        {
            clipboard.Clear();
        }
    }

    private void StartTransferProgress(string operationId, bool move, IReadOnlyList<string> sources, string destination)
    {
        _transfer?.SetOperationId(operationId);
        var window = EnsureTransferProgressWindow();
        window.Start(new TransferProgressContext(
            move,
            sources.Count,
            TransferViewModel.DescribeSource(sources),
            destination));
    }

    private void OnTransferProgress(ProgressUpdate update)
    {
        _transfer?.OnProgress(update);
    }

    private async void OnFileProgressCancelRequested(object? sender, EventArgs e)
    {
        if (_transfer is null)
        {
            return;
        }

        _transferProgressWindow?.SetCancelling();
        try
        {
            await _transfer.CancelAsync();
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

        if (_transfer?.HasActiveTransfer == true)
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

    private async void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (IsEditingPath || IsTextInputFocused())
        {
            return;
        }

        e.Handled = true;
        await RunUiActionAsync("Copy", CopyToClipboard);
    }

    private async void OnCutAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (IsEditingPath || IsTextInputFocused())
        {
            return;
        }

        e.Handled = true;
        await RunUiActionAsync("Cut", CutToClipboard);
    }

    private async void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (IsEditingPath || IsTextInputFocused())
        {
            return;
        }

        e.Handled = true;
        await RunUiActionAsync("Paste", () => PasteFromClipboard());
    }

    private FileRow[] GetSelectedEntries() => ActiveSelectedRows.ToArray();
    private void RefreshView() => SyncFromWorkspace();

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
        await _fileOperationDialogs.ShowSettingsAsync();
    }

    private async void OnViewArchiveClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("View archive", ViewSelectedArchiveAsync);

    private async Task ViewSelectedArchiveAsync()
    {
        await _fileOperationDialogs.ViewSelectedArchiveAsync();
    }

    private async void OnExtractArchiveClicked(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Extract archive", () => _fileOperationDialogs.ExtractSelectedArchiveAsync());
    }

    private async void OnCreateArchiveClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Create archive", CreateArchiveAsync);

    private async Task CreateArchiveAsync()
    {
        await _fileOperationDialogs.CreateArchiveAsync();
    }

    private async void OnDuplicateCheckerClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Duplicate checker", ShowDuplicateCheckerAsync);

    private async Task ShowDuplicateCheckerAsync()
    {
        await _fileOperationDialogs.ShowDuplicateCheckerAsync();
    }

    private async void OnDiskCleanupClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Disk cleanup", ShowDiskCleanupAsync);

    private async Task ShowDiskCleanupAsync()
    {
        await _fileOperationDialogs.ShowDiskCleanupAsync();
    }

    private async void OnSetColorLabelClicked(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync("Color label", SetColorLabelAsync);

    private async Task SetColorLabelAsync()
    {
        await _fileOperationDialogs.SetColorLabelAsync();
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

        await RunUiActionAsync(
            "Smart folder",
            () => _search?.StartSmartFolderAsync(folder, DispatchToUi) ?? Task.CompletedTask);
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
        SetStatusText("Recent history cleared");
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

        var queryPane = _search?.IsActive == true ? _search.Pane : workspace.ActivePane;
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
            SearchPath = _search?.IsActive == true && !string.IsNullOrWhiteSpace(_search.Root)
                ? _search.Root
                : workspace.Active.Path,
            IncludeHidden = workspace.Settings.ShowHidden,
            ContentSearch = _search?.ContentSearch == true,
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
