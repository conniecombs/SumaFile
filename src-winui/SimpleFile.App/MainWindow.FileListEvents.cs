using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimpleFile.Core;
using Windows.UI.Core;
using SimpleFile.Ipc;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace SimpleFile.App;

public sealed partial class MainWindow
{

    private void OnPrimaryFileWheelChanged(object sender, PointerRoutedEventArgs e) =>
        HandleFileListWheel(e, PaneId.Primary);

    private void OnSecondaryFileWheelChanged(object sender, PointerRoutedEventArgs e) =>
        HandleFileListWheel(e, PaneId.Secondary);

    private void HandleFileListWheel(PointerRoutedEventArgs e, PaneId pane)
    {
        if (_workspace is null)
        {
            return;
        }

        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            _workspace.ForegroundScheduler.RecordActivity(ForegroundActivityKind.Scrolling);
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

    private void OnToggleTags(object sender, RoutedEventArgs e)
    {
        _tagsCollapsed = !_tagsCollapsed;
        if (_workspace is not null)
        {
            _workspace.Settings.TagsCollapsed = _tagsCollapsed;
        }

        SetExpandGlyph(TagsCollapseButton, _tagsCollapsed);
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
            await RunUiActionAsync("Drive", async () =>
            {
                if (DrivePresentation.Status(row.Source) == "unknown")
                {
                    await _workspace.RefreshDriveAsync(row.Path, quiet: true);
                }

                await _workspace.OpenPathAsync(row.Path, isDirectory: true, _workspace.SidebarTarget);
            });
        }
    }

    private void OnManageRemoteProfiles(object sender, RoutedEventArgs e) => ShowRemoteManagerWindow();

    private void OnRemoteProfileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RemoteProfileSidebarRow row)
        {
            ShowRemoteManagerWindow(row.ProfileId);
        }
    }

    private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null && sender is Button { Tag: PanePath target })
        {
            await RunUiActionAsync("Breadcrumb", () => _workspace.NavigatePaneAsync(target.Pane, target.Path));
        }
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
            // Blank space: drop the stale selection so the ContextFlyout that follows
            // builds the background menu instead of acting on hidden items.
            _workspace.ActivatePane(pane);
            if (list.SelectedItems.Count > 0)
            {
                list.SelectedItems.Clear();
            }

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

    private void OnPrimaryDetailsSelectionChanged(object? sender, DetailsFileSelectionChangedEventArgs e) =>
        HandleDetailsSelectionChanged(PrimaryDetailsFileList, PaneId.Primary, e);

    private void OnSecondaryDetailsSelectionChanged(object? sender, DetailsFileSelectionChangedEventArgs e) =>
        HandleDetailsSelectionChanged(SecondaryDetailsFileList, PaneId.Secondary, e);

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

    private void HandleDetailsSelectionChanged(DetailsFileListView list, PaneId pane, DetailsFileSelectionChangedEventArgs e)
    {
        if (_applyingWorkspace || _workspace is null)
        {
            return;
        }

        var row = e.AddedRows.LastOrDefault() ?? list.SelectedRow;
        if (row is null)
        {
            if (pane == _workspace.ActivePane)
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

    private async void OnPrimaryFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DoubleTapHitsRow(e.OriginalSource))
        {
            await RunUiActionAsync("Open", () => OpenSelectedFile(PaneId.Primary));
        }
    }

    private async void OnSecondaryFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DoubleTapHitsRow(e.OriginalSource))
        {
            await RunUiActionAsync("Open", () => OpenSelectedFile(PaneId.Secondary));
        }
    }

    /// <summary>Double-clicks on blank list space must not open the current selection.</summary>
    private static bool DoubleTapHitsRow(object? originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is FileRowView or ListViewItem)
            {
                return true;
            }

            if (current is ListView)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void OnPrimaryDetailsRowInvoked(object? sender, DetailsFileRowEventArgs e) =>
        await RunUiActionAsync("Open", () => OpenSelectedFile(PaneId.Primary));

    private async void OnSecondaryDetailsRowInvoked(object? sender, DetailsFileRowEventArgs e) =>
        await RunUiActionAsync("Open", () => OpenSelectedFile(PaneId.Secondary));

    private async void OnPrimaryFileKeyDown(object sender, KeyRoutedEventArgs e) =>
        await RunUiActionAsync("File list", () => HandleFileKey(e, PaneId.Primary));

    private async void OnSecondaryFileKeyDown(object sender, KeyRoutedEventArgs e) =>
        await RunUiActionAsync("File list", () => HandleFileKey(e, PaneId.Secondary));

    private async void OnPrimaryDetailsFileKeyDown(object sender, KeyRoutedEventArgs e) =>
        await RunUiActionAsync("File list", () => HandleFileKey(e, PaneId.Primary));

    private async void OnSecondaryDetailsFileKeyDown(object sender, KeyRoutedEventArgs e) =>
        await RunUiActionAsync("File list", () => HandleFileKey(e, PaneId.Secondary));

    private async Task HandleFileKey(KeyRoutedEventArgs e, PaneId pane)
    {
        // Keys typed into the inline rename box belong to the box, not to the list.
        if (e.OriginalSource is TextBox)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await OpenSelectedFile(pane);
            return;
        }

        if (_workspace is null)
        {
            return;
        }

        var chord = IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu)
            || IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);
        if (!TypeAhead.TryMapKey(e.Key.ToString(), chord, out var character))
        {
            return;
        }

        // Backspace only edits an active type-ahead buffer; otherwise it stays "go up".
        if (character == '\b' && _workspace.TypeAheadBuffer.Text.Length == 0)
        {
            return;
        }

        var match = _workspace.MatchTypeAhead(character);
        if (match is null)
        {
            e.Handled = character == '\b';
            return;
        }

        e.Handled = true;
        _workspace.SelectPath(match.Path, pane);
        SelectRow(pane, match.Path);
        var rows = pane == PaneId.Secondary ? SecondaryFiles : PrimaryFiles;
        var row = rows.FirstOrDefault(item => PathRules.PathsEqual(item.Path, match.Path));
        if (row is not null)
        {
            QueuePreview(row);
        }

        UpdateSelectionStatus();
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async Task OpenSelectedFile(PaneId pane)
    {
        if (_workspace is not null && SelectedRowForPane(pane) is FileRow row)
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

    private void SelectAllActiveFileList()
    {
        var pane = _workspace?.ActivePane ?? PaneId.Primary;
        var details = pane == PaneId.Secondary ? SecondaryDetailsFileList : PrimaryDetailsFileList;
        if (details.Visibility == Visibility.Visible)
        {
            details.SelectAll();
            return;
        }

        ActiveFileList.SelectAll();
    }

    private void ClearSelectionForPane(PaneId pane)
    {
        var details = pane == PaneId.Secondary ? SecondaryDetailsFileList : PrimaryDetailsFileList;
        if (details.Visibility == Visibility.Visible)
        {
            details.ClearSelection();
            return;
        }

        var list = pane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;
        list.SelectedItems.Clear();
    }

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
            _search is null ? null : ActiveSearchQuery);
        ApplyStatusBarState();
    }

    private void QueuePreviewFromSelection()
    {
        ApplyPreviewVisibility();
        _previewPresenter.QueueFromSelection();
    }

    private void RequeueCurrentPreview()
    {
        if (ActiveSelectedRow is { } row)
        {
            QueuePreview(row);
        }
    }

    private void QueuePreview(FileRow row)
    {
        ApplyPreviewVisibility();
        _previewPresenter.Queue(row);
    }

    private void ClearPreview()
    {
        ApplyPreviewVisibility();
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

    private FileRowView? _inlineRenameView;

    private bool IsInlineRenameActive => _inlineRenameView?.IsRenaming == true;

    /// <summary>
    /// F2 in the Details view with exactly one item selected renames it in place. Returns
    /// false (so the caller falls back to the rename dialog) for other views, multiple or no
    /// selection, or when the row cannot be realized.
    /// </summary>
    private bool TryBeginInlineRename()
    {
        var workspace = _workspace;
        if (workspace is null)
        {
            return false;
        }

        if (IsInlineRenameActive)
        {
            return true;
        }

        var pane = ActiveUiPane;
        if (!string.Equals(workspace.ViewFor(pane), "details", StringComparison.Ordinal)
            || PathRules.IsRecycleBinPath(workspace.Pane(pane).Path))
        {
            return false;
        }

        var list = pane == PaneId.Secondary ? SecondaryDetailsFileList : PrimaryDetailsFileList;
        if (list.Visibility != Visibility.Visible)
        {
            return false;
        }

        var selected = list.SelectedRows;
        if (selected.Count != 1)
        {
            return false;
        }

        var view = list.RowViewFor(selected[0]);
        if (view is null || !view.BeginRename(CommitInlineRenameAsync))
        {
            return false;
        }

        _inlineRenameView = view;
        return true;
    }

    private void CancelInlineRename()
    {
        var view = _inlineRenameView;
        _inlineRenameView = null;
        view?.CancelRename();
    }

    private async Task CommitInlineRenameAsync(FileRow row, string newName)
    {
        _inlineRenameView = null;
        var workspace = _workspace;
        if (workspace is null)
        {
            return;
        }

        // Same rules as the Rename dialog and Advanced Rename.
        if (!AdvancedRename.IsValidFileName(newName))
        {
            ShowMessage(
                "Rename",
                $"\"{newName}\" can't be used as a name. Names can't contain < > : \" / \\ | ? * or end with a space or period.",
                InfoBarSeverity.Warning);
            return;
        }

        await RunUiActionAsync("Rename", async () =>
        {
            var utilityCts = BeginUtilityOperation("CommitInlineRenameAsync");
            try
            {
                await workspace.RenameSelectedAsync(row.Path, newName, utilityCts.Token);
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        });
    }
}
