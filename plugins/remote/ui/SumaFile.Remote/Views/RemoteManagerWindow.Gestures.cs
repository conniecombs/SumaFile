using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using SumaFile.Remote.Model;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace SumaFile.Remote.Views;

/// <summary>
/// What the local and the remote file list do beyond their toolbar buttons, as a file pane of the app does: a click
/// selects and a double-click or Enter opens a folder; Backspace, F5, F2 and Delete; a context menu; and drops, which
/// upload into the remote folder or download into the local one. The rules are <see cref="RemotePaneKeys"/>,
/// <see cref="RemotePaneMenu"/>, <see cref="RemoteDrop"/> and <see cref="RemotePaneLayout"/>; every command ends in
/// the method its toolbar button calls, so prompts, confirmations and error dialogs are the same. Also the two
/// splitters between the panels.
/// </summary>
internal sealed partial class RemoteManagerWindow
{
    // The rows being dragged out of one of the two lists, between DragItemsStarting and DragItemsCompleted.
    private RemotePane? _dragPane;
    private IReadOnlyList<RemoteFileEntry> _draggedEntries = [];

    private void AttachGestures()
    {
        AttachPaneGestures(_localList, RemotePane.Local);
        AttachPaneGestures(_remoteList, RemotePane.Remote);
    }

    private void AttachPaneGestures(ListView list, RemotePane pane)
    {
        DoubleTappedEventHandler doubleTapped = (_, e) =>
        {
            if (ListRows.RowOf<RemoteFileEntry>(list, e.OriginalSource) is { IsDir: true } folder)
            {
                e.Handled = true;
                _ = OpenEntryAsync(pane, folder);
            }
        };
        KeyEventHandler keyDown = (_, e) => OnPaneKeyDown(list, pane, e);
        TypedEventHandler<UIElement, ContextRequestedEventArgs> contextRequested = (_, e) => OnPaneContextRequested(list, pane, e);
        DragItemsStartingEventHandler dragStarting = (_, e) =>
        {
            var entries = e.Items.Select(item => ListRows.Clicked<RemoteFileEntry>(list, item)).OfType<RemoteFileEntry>().ToArray();
            if (entries.Length == 0)
            {
                e.Cancel = true;
                return;
            }

            _dragPane = pane;
            _draggedEntries = entries;
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        };
        TypedEventHandler<ListViewBase, DragItemsCompletedEventArgs> dragCompleted = (_, _) =>
        {
            _dragPane = null;
            _draggedEntries = [];
        };
        DragEventHandler dragOver = (_, e) => OnPaneDragOver(pane, e);
        DragEventHandler drop = (_, e) => OnPaneDrop(pane, e);

        list.CanDragItems = true;
        list.AllowDrop = true;
        list.DoubleTapped += doubleTapped;
        // Preview, so a key reaches the list's commands before the list or a row uses it.
        list.PreviewKeyDown += keyDown;
        list.ContextRequested += contextRequested;
        list.DragItemsStarting += dragStarting;
        list.DragItemsCompleted += dragCompleted;
        list.DragOver += dragOver;
        list.Drop += drop;
        _listDetachers.Add(() =>
        {
            list.DoubleTapped -= doubleTapped;
            list.PreviewKeyDown -= keyDown;
            list.ContextRequested -= contextRequested;
            list.DragItemsStarting -= dragStarting;
            list.DragItemsCompleted -= dragCompleted;
            list.DragOver -= dragOver;
            list.Drop -= drop;
        });
    }

    private RemotePaneState PaneState(ListView list, RemotePane pane)
    {
        var selected = SelectedModels<RemoteFileEntry>(list);
        return new RemotePaneState(
            pane,
            selected.Count,
            selected.Count == 1 && selected[0].IsDir,
            _viewModel.CanMutateRemoteEntries,
            _viewModel.CanUseWorkspaceTransfers);
    }

    private void OnPaneKeyDown(ListView list, RemotePane pane, KeyRoutedEventArgs e)
    {
        var command = RemotePaneKeys.CommandFor(
            pane,
            (int)e.Key,
            IsKeyDown(VirtualKey.Control),
            e.KeyStatus.IsMenuKeyDown,
            IsKeyDown(VirtualKey.Shift));
        if (command == RemotePaneCommand.None || IsClosed)
        {
            return;
        }

        if (command == RemotePaneCommand.Open)
        {
            // Enter opens the row that has the focus, which a Ctrl+arrow can move off the selection.
            if (ListRows.RowOf<RemoteFileEntry>(list, e.OriginalSource) is { IsDir: true } folder)
            {
                e.Handled = true;
                _ = OpenEntryAsync(pane, folder);
            }

            return;
        }

        if (RemotePaneCommands.IsAvailable(command, PaneState(list, pane)))
        {
            e.Handled = true;
            _ = RunPaneCommandAsync(list, pane, command);
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private Task OpenEntryAsync(RemotePane pane, RemoteFileEntry folder) => pane == RemotePane.Local
        ? RunRemoteActionAsync(() => _viewModel.NavigateLocalEntryAsync(folder))
        : RunRemoteActionAsync(() => _viewModel.NavigateRemoteEntryAsync(folder));

    /// <summary>Runs a list's command through the method its toolbar button calls.</summary>
    private Task RunPaneCommandAsync(ListView list, RemotePane pane, RemotePaneCommand command) => (pane, command) switch
    {
        (_, RemotePaneCommand.Open) when SelectedModels<RemoteFileEntry>(list) is [{ IsDir: true } folder] => OpenEntryAsync(pane, folder),
        (RemotePane.Local, RemotePaneCommand.Parent) => RunRemoteActionAsync(() => _viewModel.GoToParentLocalDirectoryAsync()),
        (RemotePane.Local, RemotePaneCommand.Refresh) => RunRemoteActionAsync(() => _viewModel.RefreshLocalDirectoryAsync()),
        (RemotePane.Local, RemotePaneCommand.Upload) => UploadSelectedLocalEntriesAsync(),
        (RemotePane.Remote, RemotePaneCommand.Parent) => RunRemoteActionAsync(() => _viewModel.GoToParentRemoteDirectoryAsync()),
        (RemotePane.Remote, RemotePaneCommand.Refresh) => RunRemoteActionAsync(() => _viewModel.RefreshRemoteDirectoryAsync()),
        (RemotePane.Remote, RemotePaneCommand.Download) => DownloadSelectedRemoteEntriesAsync(),
        (RemotePane.Remote, RemotePaneCommand.Rename) => RenameRemoteEntryAsync(),
        (RemotePane.Remote, RemotePaneCommand.Delete) => DeleteRemoteEntriesAsync(),
        (RemotePane.Remote, RemotePaneCommand.NewFolder) => CreateRemoteDirectoryAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// A right-click, or the menu key. As in the app's panes, a right-click on a row outside the selection selects
    /// that row alone, and one below the rows clears the selection; the menu is then built for what is selected.
    /// </summary>
    private void OnPaneContextRequested(ListView list, RemotePane pane, ContextRequestedEventArgs e)
    {
        if (IsClosed)
        {
            return;
        }

        var byPointer = e.TryGetPosition(list, out var point);
        var entry = ListRows.RowOf<RemoteFileEntry>(list, e.OriginalSource);
        if (entry is not null)
        {
            if (!list.SelectedItems.Contains(entry))
            {
                list.SelectedItem = entry;
            }
        }
        else if (byPointer && list.SelectedItems.Count > 0)
        {
            list.DeselectRange(new ItemIndexRange(0, (uint)list.Items.Count));
        }

        var flyout = new MenuFlyout();
        foreach (var item in RemotePaneMenu.Build(PaneState(list, pane)))
        {
            if (item.SeparatorBefore)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            var command = item.Command;
            var menuItem = new MenuFlyoutItem
            {
                Text = item.Text,
                IsEnabled = item.IsEnabled,
                KeyboardAcceleratorTextOverride = item.Shortcut,
            };
            AutomationProperties.SetAutomationId(menuItem, RemoteAutomationIds.MenuItemPrefix + command);
            menuItem.Click += (_, _) => _ = RunPaneCommandAsync(list, pane, command);
            flyout.Items.Add(menuItem);
        }

        e.Handled = true;
        if (byPointer)
        {
            flyout.ShowAt(list, new FlyoutShowOptions { Position = point });
        }
        else
        {
            // The menu key: beside the row that has the focus, or on the list when it is empty.
            flyout.ShowAt(entry is not null && list.ContainerFromItem(entry) is FrameworkElement row ? row : list);
        }
    }

    private RemoteDragSource DragSourceOf(DragEventArgs e) => _dragPane switch
    {
        RemotePane.Local => RemoteDragSource.LocalPane,
        RemotePane.Remote => RemoteDragSource.RemotePane,
        _ => e.DataView.Contains(StandardDataFormats.StorageItems) ? RemoteDragSource.Files : RemoteDragSource.Other,
    };

    private RemoteDropDecision DropDecision(RemotePane target, DragEventArgs e) => IsClosed
        ? RemoteDropDecision.Refused
        : RemoteDrop.Decide(target, DragSourceOf(e), _viewModel.CanMutateRemoteEntries, _viewModel.LocalPath, _viewModel.RemotePath);

    private void OnPaneDragOver(RemotePane target, DragEventArgs e)
    {
        var decision = DropDecision(target, e);
        e.AcceptedOperation = decision.Accepted ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (decision.Accepted && e.DragUIOverride is { } tip)
        {
            tip.Caption = decision.Caption;
            tip.IsCaptionVisible = true;
            tip.IsGlyphVisible = true;
        }

        e.Handled = true;
    }

    private async void OnPaneDrop(RemotePane target, DragEventArgs e)
    {
        var decision = DropDecision(target, e);
        if (!decision.Accepted)
        {
            return;
        }

        e.Handled = true;
        // DragItemsCompleted clears the fields once this handler first awaits.
        var dragged = _draggedEntries;
        if (_dragPane is not null)
        {
            await RunRemoteActionAsync(() => decision.Command == RemotePaneCommand.Upload
                ? _viewModel.UploadLocalEntriesToRemoteAsync(dragged)
                : _viewModel.DownloadRemoteEntriesToLocalAsync(dragged));
            return;
        }

        // Files from Explorer or from a file pane of the app.
        string[] paths;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("The dropped files could not be read: " + exception.Message);
            return;
        }
        finally
        {
            deferral.Complete();
        }

        if (!IsClosed)
        {
            await RunRemoteActionAsync(() => _viewModel.UploadLocalFilesAsync(paths));
        }
    }

    /// <summary>
    /// Puts a splitter in each gap between the three panels. The first sets the profiles list's width; the second
    /// shares the rest between the local and the remote list, as star weights, so they keep their proportion when the
    /// window is resized.
    /// </summary>
    private void AttachSplitters()
    {
        var profiles = new RemotePaneSplitter(RemoteAutomationIds.ProfilesSplitter, "Resize the profiles list", _panesGrid)
        {
            Dragged = x => SetProfilesWidth(x - RemotePaneLayout.SplitterWidth / 2),
            Nudged = distance => SetProfilesWidth(_profilesColumn.ActualWidth + distance),
            Reset = () => SetProfilesWidth(RemotePaneLayout.ProfilesDefaultWidth),
        };
        var panes = new RemotePaneSplitter(RemoteAutomationIds.PanesSplitter, "Resize the local and remote file lists", _panesGrid)
        {
            Dragged = x => SplitFilePanes(x - _profilesColumn.ActualWidth - RemotePaneLayout.SplitterWidth * 1.5),
            Nudged = distance => SplitFilePanes(_localColumn.ActualWidth + distance),
            Reset = () =>
            {
                _localColumn.Width = new GridLength(1, GridUnitType.Star);
                _remoteColumn.Width = new GridLength(1, GridUnitType.Star);
            },
        };
        Grid.SetColumn(profiles, 1);
        Grid.SetColumn(panes, 3);
        _panesGrid.Children.Add(profiles);
        _panesGrid.Children.Add(panes);
    }

    private void SetProfilesWidth(double requested) =>
        _profilesColumn.Width = new GridLength(RemotePaneLayout.ClampProfiles(requested, _panesGrid.ActualWidth));

    private void SplitFilePanes(double requestedLocal)
    {
        var (local, remote) = RemotePaneLayout.SplitPanes(requestedLocal, _localColumn.ActualWidth + _remoteColumn.ActualWidth);
        _localColumn.Width = new GridLength(local, GridUnitType.Star);
        _remoteColumn.Width = new GridLength(remote, GridUnitType.Star);
    }
}
