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

public sealed partial class MainWindow
{

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

    private Brush Brush(string key)
    {
        return ThemeResourceLookup.Brush(RootGrid, key);
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
        return MediaFolder.IsMediaFolder(entries, _workspace.Settings.PhotoFolderImageThreshold);
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

}

