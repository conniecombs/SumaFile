using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SimpleFile.App;

internal sealed class ClipboardHistoryRow
{
    public ClipboardHistoryRow(ClipboardHistoryEntry entry)
    {
        Entry = entry;
    }

    public ClipboardHistoryEntry Entry { get; }

    public override string ToString()
    {
        var names = string.Join(", ", Entry.Paths.Select(PathRules.Basename));
        return $"{Entry.Operation} · {Entry.Paths.Length} item(s) · {names}";
    }
}

internal sealed class OperationHistoryRow
{
    public OperationHistoryRow(OperationRecord record)
    {
        Record = record;
    }

    public OperationRecord Record { get; }

    public override string ToString()
    {
        return $"{Record.Status} · {Record.Description}";
    }
}

public sealed partial class MainWindow
{
    private bool _commandPaletteOpen;
    private bool _syncingViewOptionsFlyout;
    private int _viewIconSizeSaveToken;
    private readonly SemaphoreSlim _viewIconSizeSaveGate = new(1, 1);
    private List<AppCommand> _paletteCommands = [];

    private void OnCommandPaletteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (!IsEditingPath && !IsTextInputFocused())
        {
            OpenCommandPalette();
        }
    }

    private void OnFocusPathAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        BeginPathEdit(_workspace?.ActivePane ?? PaneId.Primary);
    }

    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        FocusSearchUi();
    }

    private void FocusSearchUi()
    {
        var pane = ActiveUiPane;
        var host = SearchHostFor(pane);
        var box = SearchTextBoxFor(pane);
        if (host.Visibility == Visibility.Visible)
        {
            _workspace?.ActivatePane(pane);
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
            return;
        }

        ShowOverflowInputFlyout(
            MoreButtonFor(pane),
            "Search",
            box.Text,
            async text =>
            {
                box.Text = text;
                await StartSearchAsync(pane);
            },
            commitOnClose: false);
    }

    private void FocusFilterUi()
    {
        var pane = ActiveUiPane;
        var box = QuickFilterBoxFor(pane);
        if (box.Visibility == Visibility.Visible)
        {
            _workspace?.ActivatePane(pane);
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
            return;
        }

        ShowOverflowInputFlyout(
            MoreButtonFor(pane),
            "Filter",
            box.Text,
            text =>
            {
                if (!string.Equals(box.Text, text, StringComparison.Ordinal))
                {
                    box.Text = text;
                }

                _workspace?.SetFilterQuery(pane, text);
                return Task.CompletedTask;
            },
            commitOnClose: true);
    }

    private void ShowOverflowInputFlyout(
        FrameworkElement anchor,
        string placeholder,
        string? current,
        Func<string, Task> commit,
        bool commitOnClose)
    {
        var input = new TextBox
        {
            MinWidth = 240,
            PlaceholderText = placeholder,
            Text = current ?? "",
            Style = SearchBox.Style,
        };
        var flyout = new Flyout { Content = input };
        var committed = false;

        async Task CommitAsync()
        {
            if (committed)
            {
                return;
            }

            committed = true;
            await commit(input.Text);
        }

        input.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                flyout.Hide();
                await CommitAsync();
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                flyout.Hide();
            }
        };
        input.Loaded += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        if (commitOnClose)
        {
            flyout.Closed += async (_, _) => await CommitAsync();
        }

        flyout.ShowAt(anchor);
    }

    private PaneId ActiveUiPane => _workspace?.Normalize(_workspace.ActivePane) ?? PaneId.Primary;

    private TextBox SearchTextBoxFor(PaneId pane) =>
        SearchBox;

    private Button SearchCancelButtonFor(PaneId pane) =>
        SearchCancelButton;

    private FrameworkElement SearchHostFor(PaneId pane) =>
        PrimarySearchHost;

    private TextBox QuickFilterBoxFor(PaneId pane) =>
        QuickFilterBox;

    private Button MoreButtonFor(PaneId pane) =>
        PrimaryMoreButton;

    private void SetSearchCancelEnabled(PaneId pane, bool enabled)
    {
        SearchCancelButtonFor(pane).IsEnabled = enabled;
    }

    private void UpdateSearchCancelButtons()
    {
        SetSearchCancelEnabled(_search?.Pane ?? PaneId.Primary, _search?.CanCancel == true);
    }

    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (IsEditingPath || IsTextInputFocused())
        {
            return;
        }

        e.Handled = true;
        ActiveFileList.SelectAll();
    }

    private async void OnCopyPathAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Copy path", () => RunAppCommandAsync("copy-path"));
    }

    private async void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Undo", UndoLastAsync);
    }

    private async void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Redo", RedoLastAsync);
    }

    private async void OnKeyboardHelpAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Keyboard shortcuts", ShowKeyboardHelpAsync);
    }

    private async void OnCopyToOtherPaneAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Copy to other pane", () => RunAppCommandAsync("copy-to-pane"));
    }

    private async void OnMoveToOtherPaneAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Move to other pane", () => RunAppCommandAsync("move-to-pane"));
    }

    private async void OnOpenInNewTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Open in new tab", () => RunAppCommandAsync("open-selected-tab"));
    }

    private async void OnToggleHiddenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Hidden files", () => RunAppCommandAsync("toggle-hidden"));
    }

    private async void OnBookmarkAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Bookmark", () => RunAppCommandAsync("bookmark-folder"));
    }

    private async void OnPropertiesAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await RunUiActionAsync("Properties", () => RunAppCommandAsync("properties"));
    }

    private async void OnTab1Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 1);

    private async void OnTab2Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 2);

    private async void OnTab3Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 3);

    private async void OnTab4Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 4);

    private async void OnTab5Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 5);

    private async void OnTab6Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 6);

    private async void OnTab7Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 7);

    private async void OnTab8Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 8);

    private async void OnTab9Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) =>
        await SwitchToTabAtFromAcceleratorAsync(e, 9);

    private async Task SwitchToTabAtFromAcceleratorAsync(KeyboardAcceleratorInvokedEventArgs e, int index)
    {
        e.Handled = true;
        if (_workspace is not null && !IsEditingPath)
        {
            await RunUiActionAsync("Tab", () => _workspace.SwitchToTabAtAsync(index));
        }
    }

    private void OnQuickFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingWorkspace)
        {
            return;
        }

        var pane = ActiveUiPane;
        _workspace?.SetFilterQuery(pane, QuickFilterBox.Text);
    }

    private async void OnTogglePreview(object sender, RoutedEventArgs e)
    {
        var workspace = _workspace;
        if (workspace is null)
        {
            return;
        }

        workspace.Settings.PreviewVisible = !workspace.Settings.PreviewVisible;
        ApplyPreviewVisibility();
        await RunUiActionAsync("Preview pane", () => workspace.SaveUiSettingsAsync());
    }

    private void ApplyPreviewVisibility()
    {
        var visible = _workspace?.Settings.PreviewVisible != false;
        var width = UiSettings.NormalizePreviewWidth(
            _workspace?.Settings.PreviewWidth ?? UiSettings.PreviewDefaultWidth);
        if (_workspace is not null)
        {
            _workspace.Settings.PreviewWidth = width;
        }

        PreviewPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            PreviewColumn.MinWidth = UiSettings.PreviewMinWidth;
            PreviewColumn.MaxWidth = UiSettings.PreviewMaxWidth;
            PreviewColumn.Width = new GridLength(width);
            PreviewDividerColumn.Width = new GridLength(UiSettings.DualPaneDividerWidth);
        }
        else
        {
            PreviewColumn.MinWidth = 0;
            PreviewColumn.MaxWidth = 0;
            PreviewColumn.Width = new GridLength(0);
            PreviewDividerColumn.Width = new GridLength(0);
        }

        ToolTipService.SetToolTip(PreviewToggleButton, visible ? "Hide preview pane" : "Show preview pane");
    }

    private void ApplyTheme(string? theme)
    {
        var next = UiSettings.NormalizeTheme(theme) switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (RootGrid.RequestedTheme != next)
        {
            RootGrid.RequestedTheme = next;
        }

        ApplyCaptionButtonColors(next);
    }

    private void OpenCommandPalette()
    {
        _commandPaletteOpen = true;
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        CommandPaletteInput.Text = "";
        RefreshCommandPalette("");
        CommandPaletteInput.Focus(FocusState.Programmatic);
    }

    private void CloseCommandPalette()
    {
        _commandPaletteOpen = false;
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
    }

    private void RefreshCommandPalette(string query)
    {
        _paletteCommands = [.. AppCommandCatalog.Filter(query)];
        CommandPaletteList.ItemsSource = _paletteCommands;
        if (_paletteCommands.Count > 0)
        {
            CommandPaletteList.SelectedIndex = 0;
        }
    }

    private void OnCommandPaletteTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshCommandPalette(CommandPaletteInput.Text);
    }

    private async void OnCommandPaletteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseCommandPalette();
            return;
        }

        if (e.Key == VirtualKey.Down)
        {
            e.Handled = true;
            if (_paletteCommands.Count == 0)
            {
                return;
            }

            CommandPaletteList.SelectedIndex = (CommandPaletteList.SelectedIndex + 1) % _paletteCommands.Count;
            CommandPaletteList.ScrollIntoView(CommandPaletteList.SelectedItem);
            return;
        }

        if (e.Key == VirtualKey.Up)
        {
            e.Handled = true;
            if (_paletteCommands.Count == 0)
            {
                return;
            }

            var next = CommandPaletteList.SelectedIndex - 1;
            CommandPaletteList.SelectedIndex = next < 0 ? _paletteCommands.Count - 1 : next;
            CommandPaletteList.ScrollIntoView(CommandPaletteList.SelectedItem);
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (CommandPaletteList.SelectedItem is AppCommand command)
            {
                CloseCommandPalette();
                await RunUiActionAsync("Command palette", () => RunAppCommandAsync(command.Id));
            }
        }
    }

    private async void OnCommandPaletteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppCommand command)
        {
            CloseCommandPalette();
            await RunUiActionAsync("Command palette", () => RunAppCommandAsync(command.Id));
        }
    }

    private void OnCommandPaletteOverlayPressed(object sender, PointerRoutedEventArgs e)
    {
        CloseCommandPalette();
    }

    private void OnCommandPaletteInnerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void CopySelectedPathsToClipboard()
    {
        var rows = ActiveSelectedRows;
        if (rows.Count == 0)
        {
            return;
        }

        var inBin = PathRules.IsRecycleBinPath(_workspace?.Active.Path);
        var paths = rows
            .Select(row => inBin && !string.IsNullOrEmpty(row.SymlinkText) ? row.SymlinkText : row.Path)
            .ToArray();
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, paths));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SetStatusText(paths.Length == 1 ? "Path copied" : $"{paths.Length} paths copied");
    }

    private async Task RestoreSelectedAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var paths = SelectedPaths;
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        var restored = await _workspace.RestoreRecycleBinAsync(paths);
        SetStatusText(restored.Length == 1
            ? $"Restored to {restored[0]}"
            : $"Restored {restored.Length} item(s)");
    }

    private async Task EmptyRecycleBinAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Empty Recycle Bin",
            Content = "Permanently delete all items in the Recycle Bin?",
            PrimaryButtonText = "Empty Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _workspace.EmptyRecycleBinAsync();
        SetStatusText("Recycle Bin emptied");
    }

    private async Task ToggleHiddenFilesAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var shown = _workspace.ToggleShowHidden();
        SetStatusText(shown ? "Hidden files shown" : "Hidden files hidden");
        await _workspace.SaveUiSettingsAsync();
    }

    private async Task BookmarkCurrentFolderAsync()
    {
        if (_workspace is null || string.IsNullOrEmpty(_workspace.Active.Path))
        {
            return;
        }

        _workspace.AddBookmark(_workspace.Active.Path);
        await _workspace.SaveUiSettingsAsync();
        SetStatusText("Current folder bookmarked");
    }

    private async Task BookmarkSelectedFolderAsync()
    {
        if (_workspace is null || ActiveSelectedRow is not { IsDir: true } row)
        {
            return;
        }

        _workspace.AddBookmark(row.Path);
        await _workspace.SaveUiSettingsAsync();
        SetStatusText($"Bookmarked {row.Name}");
    }

    private async Task OpenSelectedInNewTabAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        await SaveViewIconSizeNowAsync();
        var row = ActiveSelectedRow;
        var path = row is { IsDir: true } ? row.Path : _workspace.Active.Path;
        await _workspace.OpenNewTabAsync(_workspace.ActivePane, path);
    }

    private async Task OpenSelectedInOtherPaneAsync()
    {
        if (_workspace is null || ActiveSelectedRow is not { } row)
        {
            return;
        }

        await _workspace.OpenInOtherPaneAsync(row.Path, row.IsDir);
    }

    private async Task RunAppCommandAsync(string id)
    {
        if (_workspace is null)
        {
            return;
        }

        switch (CommandAliasCatalog.Normalize(id))
        {
            case "go-home":
                await _workspace.NavigateSpecialAsync("navigateHome");
                break;
            case "go-recycle-bin":
                await _workspace.NavigateSpecialAsync("navigateRecycleBin");
                break;
            case "restore-selected":
                await RestoreSelectedAsync();
                break;
            case "empty-recycle-bin":
                await EmptyRecycleBinAsync();
                break;
            case "go-back":
                if (!IsEditingPath)
                {
                    await _workspace.GoBackAsync();
                }
                break;
            case "go-forward":
                if (!IsEditingPath)
                {
                    await _workspace.GoForwardAsync();
                }
                break;
            case "go-up":
                if (!IsEditingPath)
                {
                    await _workspace.GoUpAsync();
                }
                break;
            case "refresh":
                await _workspace.RefreshAsync();
                break;
            case "copy":
                await CopyToClipboard();
                break;
            case "cut":
                await CutToClipboard();
                break;
            case "paste":
                await PasteFromClipboard();
                break;
            case "copy-path":
                CopySelectedPathsToClipboard();
                break;
            case "clipboard-history":
                await ShowClipboardHistoryAsync();
                break;
            case "operation-history":
                await ShowOperationHistoryAsync();
                break;
            case "clear-recent-history":
                await ClearRecentHistoryAsync();
                break;
            case "undo":
                await UndoLastAsync();
                break;
            case "redo":
                await RedoLastAsync();
                break;
            case "delete":
                await TrashSelected();
                break;
            case "delete-permanent":
                await DeleteSelected();
                break;
            case "rename":
                await PromptAndRename();
                break;
            case "advanced-rename":
                await PromptAdvancedRenameAsync();
                break;
            case "new-folder":
                await PromptAndCreateFolder(_workspace.ActivePane);
                break;
            case "new-file":
                await PromptAndCreateFile(_workspace.ActivePane);
                break;
            case "create-archive":
                await CreateArchiveAsync();
                break;
            case "terminal":
                await OpenTerminalInActivePathAsync();
                break;
            case "powershell-admin":
                await OpenPowershellAdminAsync();
                break;
            case "preview":
                OnTogglePreview(this, new RoutedEventArgs());
                break;
            case "toggle-hidden":
                await ToggleHiddenFilesAsync();
                break;
            case "toggle-side-menu":
                await ToggleSidebarAsync();
                break;
            case "dual-pane":
                await ToggleDualPaneFromUiAsync();
                break;
            case "close-left-pane":
                await CloseFilePaneFromUiAsync(PaneId.Primary);
                break;
            case "close-right-pane":
                await CloseFilePaneFromUiAsync(PaneId.Secondary);
                break;
            case "copy-to-pane":
                await CopyOrMoveToOtherPaneAsync(move: false);
                break;
            case "move-to-pane":
                await CopyOrMoveToOtherPaneAsync(move: true);
                break;
            case "open-selected-tab":
                await OpenSelectedInNewTabAsync();
                break;
            case "open-other-pane":
                await OpenSelectedInOtherPaneAsync();
                break;
            case "view-details":
                await ApplyViewOptionAsync("view:details");
                break;
            case "view-list":
                await ApplyViewOptionAsync("view:list");
                break;
            case "view-tiles":
                await ApplyViewOptionAsync("view:tiles");
                break;
            case "view-content":
                await ApplyViewOptionAsync("view:content");
                break;
            case "icon-size-small":
                await ApplyViewOptionAsync("icon:16");
                break;
            case "icon-size-medium":
                await ApplyViewOptionAsync("icon:32");
                break;
            case "icon-size-large":
                await ApplyViewOptionAsync("icon:48");
                break;
            case "icon-size-extra-large":
                await ApplyViewOptionAsync("icon:96");
                break;
            case "icon-size-jumbo":
                await ApplyViewOptionAsync("icon:128");
                break;
            case "icon-size-huge":
                await ApplyViewOptionAsync("icon:192");
                break;
            case "icon-size-maximum":
                await ApplyViewOptionAsync("icon:256");
                break;
            case "search":
                FocusSearchUi();
                break;
            case "filter":
                FocusFilterUi();
                break;
            case "quick-look":
                await ShowQuickLookAsync();
                break;
            case "properties":
                await ShowPropertiesAsync();
                break;
            case "color-label":
                await SetColorLabelAsync();
                break;
            case "bookmark-folder":
                await BookmarkCurrentFolderAsync();
                break;
            case "bookmark-selected-folder":
                await BookmarkSelectedFolderAsync();
                break;
            case "folder-metrics":
                await ShowFolderMetricsAsync();
                break;
            case "disk-cleanup":
                await ShowDiskCleanupAsync();
                break;
            case "duplicate-checker":
                await ShowDuplicateCheckerAsync();
                break;
            case "settings":
                await ShowSettingsAsync();
                break;
            case "keyboard-help":
                await ShowKeyboardHelpAsync();
                break;
            case "git-pull":
                await RunGitAsync(pull: true);
                break;
            case "git-push":
                await RunGitAsync(pull: false);
                break;
        }
    }

    private void OnFileRowContextRequested(object sender, ContextRequestedEventArgs e)
    {
        if (sender is not FileRowView view || view.Row is null || _workspace is null)
        {
            return;
        }

        var list = FindAncestor<ListView>(view);
        if (list is null)
        {
            return;
        }

        var pane = ReferenceEquals(list, SecondaryFileList) ? PaneId.Secondary : PaneId.Primary;
        _workspace.ActivatePane(pane);

        var row = view.Row;
        if (!list.SelectedItems.OfType<FileRow>().Any(selected =>
                string.Equals(selected.Path, row.Path, StringComparison.OrdinalIgnoreCase)))
        {
            list.SelectedItems.Clear();
            list.SelectedItem = row;
        }

        if (list.ContextFlyout is not MenuFlyout flyout)
        {
            return;
        }

        PopulateFileListContextFlyout(flyout, pane);
        if (e.TryGetPosition(list, out var point))
        {
            flyout.ShowAt(list, new FlyoutShowOptions { Position = point });
        }
        else
        {
            flyout.ShowAt(view);
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : class
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnPrimaryFileListContextOpening(object sender, object e) =>
        PopulateFileListContextFlyout(sender as MenuFlyout, PaneId.Primary);

    private void OnSecondaryFileListContextOpening(object sender, object e) =>
        PopulateFileListContextFlyout(sender as MenuFlyout, PaneId.Secondary);

    private void OnPrimaryMoreMenuOpening(object sender, object e) =>
        PopulatePaneMoreMenu(sender as MenuFlyout, ActiveUiPane);

    private void OnViewOptionsFlyoutOpening(object sender, object e)
    {
        if (_workspace is null)
        {
            return;
        }

        var pane = ActiveUiPane;
        var currentView = _workspace.ViewFor(pane);
        var currentIconSize = _workspace.IconSizeFor(pane);

        _syncingViewOptionsFlyout = true;
        try
        {
            ViewDualPaneText.Text = _workspace.DualPaneEnabled ? "Close right pane" : "Open second pane";
            ViewDualPaneIcon.Glyph = _workspace.DualPaneEnabled
                ? ContextMenuIconCatalog.ClosePane
                : ContextMenuIconCatalog.OpenPane;
            ViewApplyBothButton.Visibility = _workspace.DualPaneEnabled ? Visibility.Visible : Visibility.Collapsed;
            SelectViewStyle(currentView);

            ViewIconSizeSlider.Minimum = UiSettings.IconSizeMin;
            ViewIconSizeSlider.Maximum = UiSettings.IconSizeMax;
            ViewIconSizeSlider.SmallChange = UiSettings.IconSizeStep;
            ViewIconSizeSlider.LargeChange = UiSettings.IconSizeStep * 2;
            ViewIconSizeSlider.StepFrequency = UiSettings.IconSizeStep;
            ViewIconSizeSlider.Value = currentIconSize;
            UpdateViewIconSizeValueText(currentIconSize);
        }
        finally
        {
            _syncingViewOptionsFlyout = false;
        }
    }

    private void SelectViewStyle(string currentView)
    {
        for (var index = 0; index < ViewStyleRadioButtons.Items.Count; index++)
        {
            if (ViewStyleRadioButtons.Items[index] is RadioButton item
                && string.Equals(item.Tag?.ToString(), currentView, StringComparison.Ordinal))
            {
                ViewStyleRadioButtons.SelectedIndex = index;
                return;
            }
        }

        ViewStyleRadioButtons.SelectedIndex = 0;
    }

    private async void OnViewStyleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingViewOptionsFlyout
            || _workspace is null
            || ViewStyleRadioButtons.SelectedItem is not RadioButton item
            || item.Tag is not string view)
        {
            return;
        }

        await RunUiActionAsync("View options", () => ApplyViewOptionAsync($"view:{view}"));
    }

    private async void OnViewDualPaneClicked(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("View options", () => ApplyViewOptionAsync("pane:dual"));
    }

    private async void OnViewApplyBothClicked(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("View options", () => ApplyViewOptionAsync("pane:apply-view-to-both"));
    }

    private void OnViewIconSizeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var iconSize = UiSettings.NormalizeIconSize((int)Math.Round(e.NewValue));
        if (sender is Slider slider && Math.Abs(slider.Value - iconSize) > 0.01)
        {
            var wasSyncing = _syncingViewOptionsFlyout;
            _syncingViewOptionsFlyout = true;
            slider.Value = iconSize;
            _syncingViewOptionsFlyout = wasSyncing;
        }

        UpdateViewIconSizeValueText(iconSize);
        if (_syncingViewOptionsFlyout || _workspace is null)
        {
            return;
        }

        _workspace.SetFileListIconSize(ActiveUiPane, iconSize);
        QueueViewIconSizeSave();
    }

    private void UpdateViewIconSizeValueText(int iconSize)
    {
        ViewIconSizeValueText.Text = $"{UiSettings.NormalizeIconSize(iconSize)} px";
    }

    private void QueueViewIconSizeSave()
    {
        var token = Interlocked.Increment(ref _viewIconSizeSaveToken);
        _ = SaveViewIconSizeAsync(token, delay: true);
    }

    private Task SaveViewIconSizeNowAsync()
    {
        var token = Interlocked.Increment(ref _viewIconSizeSaveToken);
        return SaveViewIconSizeAsync(token, delay: false);
    }

    private async Task SaveViewIconSizeAsync(int token, bool delay)
    {
        if (delay)
        {
            await Task.Delay(350);
        }

        if (token != Volatile.Read(ref _viewIconSizeSaveToken))
        {
            return;
        }

        await _viewIconSizeSaveGate.WaitAsync();
        try
        {
            if (token != Volatile.Read(ref _viewIconSizeSaveToken) || _workspace is null)
            {
                return;
            }

            await _workspace.SaveWorkspaceLayoutAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Icon size", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _viewIconSizeSaveGate.Release();
        }
    }

    private async Task ApplyViewOptionAsync(string tag)
    {
        if (_workspace is null)
        {
            return;
        }

        if (tag.StartsWith("view:", StringComparison.Ordinal))
        {
            _workspace.SetFileListView(ActiveUiPane, tag["view:".Length..]);
            await _workspace.SaveWorkspaceLayoutAsync();
        }
        else if (tag.StartsWith("icon:", StringComparison.Ordinal)
            && int.TryParse(tag["icon:".Length..], out var iconSize))
        {
            _workspace.SetFileListIconSize(ActiveUiPane, iconSize);
            await _workspace.SaveWorkspaceLayoutAsync();
        }
        else if (tag == "pane:apply-view-to-both")
        {
            _workspace.ApplyViewOptionsToBothPanes(ActiveUiPane);
            await _workspace.SaveWorkspaceLayoutAsync();
        }
        else if (tag == "pane:dual")
        {
            await ToggleDualPaneFromUiAsync();
            await _workspace.SaveWorkspaceLayoutAsync();
        }

        ApplyFileListViewPresentation();
    }

    private void PopulateFileListContextFlyout(MenuFlyout? flyout, PaneId pane)
    {
        if (flyout is null || _workspace is null)
        {
            return;
        }

        var targetPane = ActivatePaneForMenu(pane);
        var selected = SelectedRowsForPane(targetPane);

        PopulateMenuFlyout(flyout, ContextMenuBuilder.Build(BuildContextMenuRequest(selected)));
    }

    private void PopulatePaneMoreMenu(MenuFlyout? flyout, PaneId pane)
    {
        if (flyout is null || _workspace is null)
        {
            return;
        }

        var targetPane = ActivatePaneForMenu(pane);
        var selected = SelectedRowsForPane(targetPane);
        var overflow = _primaryToolbarOverflow;

        PopulateMenuFlyout(flyout, ContextMenuBuilder.BuildPaneMoreMenu(BuildContextMenuRequest(selected, overflow)));
    }

    private PaneId ActivatePaneForMenu(PaneId pane)
    {
        var targetPane = _workspace?.Normalize(pane) ?? PaneId.Primary;
        _workspace?.ActivatePane(targetPane);
        return targetPane;
    }

    private IReadOnlyList<FileRow> SelectedRowsForPane(PaneId pane)
    {
        var list = pane == PaneId.Secondary ? SecondaryFileList : PrimaryFileList;
        return list.SelectedItems.OfType<FileRow>().ToArray();
    }

    private ContextMenuRequest BuildContextMenuRequest(
        IReadOnlyList<FileRow> selected,
        IReadOnlyCollection<string>? overflowedToolbarIds = null)
    {
        var selectedFile = selected.Count == 1 && !selected[0].IsDir ? selected[0] : null;
        return new ContextMenuRequest
        {
            SelectionCount = selected.Count,
            HasClipboard = _workspace?.Clipboard.HasItems == true || HasWindowsFileClipboardContent(),
            DualPaneEnabled = _workspace?.DualPaneEnabled == true,
            MenuPane = _workspace?.ActivePane ?? PaneId.Primary,
            OtherPaneHasPath = _workspace?.OtherPanePath() is not null,
            SelectedIsDirectory = selected.Count == 1 && selected[0].IsDir,
            SelectedDirectoryPath = selected.Count == 1 && selected[0].IsDir ? selected[0].Path : null,
            HasFolderSelection = selected.Any(row => row.IsDir),
            AllSelectedAreFiles = selected.Count > 0 && selected.All(row => !row.IsDir),
            SelectedIsArchive = selected.Count == 1 && !selected[0].IsDir && ArchivePaths.IsArchiveFile(selected[0].Path),
            ArchiveExtractFolderName = selected.Count == 1 ? ArchivePaths.ExtractFolderName(selected[0].Name) : null,
            SelectedExtension = selectedFile?.Extension,
            OpenWithApplications = selectedFile is null ? [] : OpenWithApplicationsForPath(selectedFile.Path),
            OverflowedToolbarIds = overflowedToolbarIds ?? [],
            InRecycleBin = PathRules.IsRecycleBinPath(_workspace?.Active.Path),
        };
    }

    private void PopulateMenuFlyout(MenuFlyout flyout, IReadOnlyList<ContextMenuEntry> entries)
    {
        flyout.Items.Clear();
        foreach (var entry in entries)
        {
            flyout.Items.Add(CreateMenuEntry(entry));
        }
    }

    private MenuFlyoutItemBase CreateMenuEntry(ContextMenuEntry entry)
    {
        if (entry.Kind == ContextMenuKind.Divider)
        {
            return new MenuFlyoutSeparator();
        }

        if (entry.Children.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = entry.Label, Tag = entry };
            if (!string.IsNullOrWhiteSpace(entry.IconGlyph))
            {
                sub.Icon = CreateMenuIcon(entry.IconGlyph);
            }

            foreach (var child in entry.Children)
            {
                sub.Items.Add(CreateMenuEntry(child));
            }

            return sub;
        }

        var item = new MenuFlyoutItem
        {
            Text = entry.Label,
            Tag = entry,
            Name = entry.Id,
            KeyboardAcceleratorTextOverride = entry.Shortcut ?? "",
        };
        if (!string.IsNullOrWhiteSpace(entry.IconGlyph))
        {
            item.Icon = CreateMenuIcon(entry.IconGlyph);
        }

        item.Click += OnContextMenuItemClick;
        return item;
    }

    private static FontIcon CreateMenuIcon(string glyph)
    {
        return new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            Glyph = glyph,
        };
    }

    private async void OnContextMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item)
        {
            return;
        }

        var entry = item.Tag as ContextMenuEntry;
        var id = entry?.Id ?? item.Tag as string;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        await RunUiActionAsync("Context menu", () => RunContextCommandAsync(id, entry?.CommandParameter));
    }

    private async Task RunContextCommandAsync(string id, string? commandParameter = null)
    {
        if (id == "ctx-paste" && !string.IsNullOrWhiteSpace(commandParameter))
        {
            await PasteFromClipboard(commandParameter);
            return;
        }

        if (id.StartsWith("view:", StringComparison.Ordinal)
            || id.StartsWith("icon:", StringComparison.Ordinal)
            || id == "pane:dual")
        {
            await ApplyViewOptionAsync(id);
            return;
        }

        if (id.StartsWith("ctx-open-with-app-", StringComparison.Ordinal))
        {
            await OpenSelectedWithApplicationAsync(commandParameter);
            return;
        }

        var commandId = CommandAliasCatalog.Normalize(id);
        if (!string.Equals(commandId, id, StringComparison.Ordinal))
        {
            await RunAppCommandAsync(commandId);
            return;
        }

        switch (id)
        {
            case "ctx-open":
                await OpenSelectedFile(ActiveFileList, _workspace?.ActivePane ?? PaneId.Primary);
                break;
            case "ctx-open-with":
            case "ctx-open-with-choose":
                await OpenSelectedWithAsync();
                break;
            case "ctx-compare":
                await CompareSelectedFilesAsync();
                break;
            case "ctx-view-archive":
                await ViewSelectedArchiveAsync();
                break;
            case "ctx-pack":
                await PromptPackIntoFolderAsync();
                break;
            case "ctx-unpack":
                await UnpackSelectedFolderAsync();
                break;
            case "ctx-extract":
            case "ctx-extract-folder":
            case "ctx-extract-to":
                await ExtractSelectedArchiveAsync(id);
                break;
        }
    }

    private async void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (_commandPaletteOpen)
            {
                e.Handled = true;
                CloseCommandPalette();
                return;
            }

            if (IsEditingPath)
            {
                e.Handled = true;
                EndPathEdit(_editingSecondaryPath ? PaneId.Secondary : PaneId.Primary, reset: true);
                return;
            }

            if (_search?.IsActive == true)
            {
                e.Handled = true;
                await CancelActiveSearchAsync();
                ClearSearchState();
                SyncFromWorkspace();
                return;
            }

            var filterBox = QuickFilterBoxFor(_workspace.ActivePane);
            if (!string.IsNullOrEmpty(filterBox.Text))
            {
                e.Handled = true;
                filterBox.Text = "";
                return;
            }

            if (ActiveFileList.SelectedItems.Count > 0)
            {
                e.Handled = true;
                ActiveFileList.SelectedItems.Clear();
                _workspace.SelectPath(null);
            }

            return;
        }

        if (IsEditingPath || IsTextInputFocused())
        {
            return;
        }

        if (e.Key == VirtualKey.Tab && _workspace.DualPaneEnabled)
        {
            e.Handled = true;
            _workspace.SwitchActivePane();
            return;
        }

        if (e.Key == VirtualKey.Back)
        {
            e.Handled = true;
            await RunUiActionAsync("Navigation", () => _workspace.GoUpAsync());
            return;
        }

        if (e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            await RunUiActionAsync("Quick Look", ShowQuickLookAsync);
        }
    }

    private bool IsTextInputFocused()
    {
        return FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox;
    }

    private async Task ShowKeyboardHelpAsync()
    {
        var lines = KeyboardShortcutMap.Defaults.Select(item => $"{item.Keys,-22}  {item.Label}");
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, lines),
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinWidth = 420,
            MaxHeight = 360,
        };
        var dialog = new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = box,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async Task ShowQuickLookAsync()
    {
        if (ActiveSelectedRow is not { } row)
        {
            return;
        }

        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        var body = new StackPanel { Spacing = 8, Width = 560 };
        body.Children.Add(new TextBlock { Text = row.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = row.Path, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        body.Children.Add(new TextBlock { Text = $"{row.TypeText}  {row.SizeText}  {row.ModifiedText}" });
        var hasVisualPreview = false;

        if (fileOps is not null && row.IsDir)
        {
            // Folder summary stats: show item count and total size.
            var utilityCts = BeginUtilityOperation();
            try
            {
                var sizeTask = fileOps.CalculateFolderSizeAsync(row.Path, utilityCts.Token);
                var countTask = fileOps.CountFolderItemsAsync(row.Path, utilityCts.Token);
                var subdirsTask = fileOps.ListSubdirectoriesAsync(row.Path, utilityCts.Token);
                await Task.WhenAll(sizeTask, countTask, subdirsTask).ConfigureAwait(true);

                if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
                {
                    return;
                }

                var totalSize = sizeTask.Result;
                var totalItems = countTask.Result;
                var subdirs = subdirsTask.Result;

                var statsPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
                statsPanel.Children.Add(new TextBlock
                {
                    Text = "Folder Contents",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.7,
                    FontSize = 12,
                });
                statsPanel.Children.Add(QuickLookMetadataRow(
                    "Subfolders",
                    $"{subdirs.Length:N0} {(subdirs.Length == 1 ? "folder" : "folders")}"));
                statsPanel.Children.Add(QuickLookMetadataRow(
                    "Total Items",
                    $"{totalItems:N0} {(totalItems == 1 ? "item" : "items")}"));
                statsPanel.Children.Add(QuickLookMetadataRow(
                    "Total Size",
                    EntryPresentation.FormatFileSize(totalSize)));
                body.Children.Add(statsPanel);
                hasVisualPreview = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                body.Children.Add(new TextBlock { Text = exception.Message });
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }
        else if (fileOps is not null && !row.IsDir)
        {
            var utilityCts = BeginUtilityOperation();
            try
            {
                var preview = await fileOps.ReadFilePreviewAsync(row.Path, 80_000, utilityCts.Token);
                if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
                {
                    return;
                }

                if (preview.FileType == "text" && preview.Content is not null)
                {
                    body.Children.Add(new TextBox
                    {
                        Text = preview.Content,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 12,
                        MaxHeight = 280,
                    });
                    hasVisualPreview = true;
                }
                else if (preview.FileType == "image" && await TryAddQuickLookImageAsync(body, row, preview, fileOps, utilityCts.Token))
                {
                    hasVisualPreview = true;
                }
                else
                {
                    body.Children.Add(PreviewPresenter.CreateFileTypePreviewIcon(row, 96));
                    body.Children.Add(new TextBlock { Text = PreviewPresenter.IconPreviewMessage(preview), TextWrapping = TextWrapping.Wrap });
                    hasVisualPreview = true;
                }

                // Rich file metadata: show structured properties when available.
                try
                {
                    var metadata = await fileOps.GetFileMetadataAsync(row.Path, utilityCts.Token);
                    if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
                    {
                        return;
                    }

                    if (metadata.Fields.Count > 0)
                    {
                        var metaPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
                        var heading = metadata.Summary ?? metadata.Kind switch
                        {
                            "image" => "Image Details",
                            "audio" => "Audio Details",
                            "video" => "Video Details",
                            "pdf" => "PDF Details",
                            "office" => "Document Details",
                            _ => "Details",
                        };
                        metaPanel.Children.Add(new TextBlock
                        {
                            Text = heading,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Opacity = 0.7,
                            FontSize = 12,
                        });

                        var maxFields = Math.Min(metadata.Fields.Count, 12);
                        for (var i = 0; i < maxFields; i++)
                        {
                            var field = metadata.Fields[i];
                            if (field.Length >= 2)
                            {
                                metaPanel.Children.Add(QuickLookMetadataRow(field[0], field[1]));
                            }
                        }

                        if (metadata.Fields.Count > maxFields)
                        {
                            metaPanel.Children.Add(new TextBlock
                            {
                                Text = $"+ {metadata.Fields.Count - maxFields} more fields…",
                                Opacity = 0.6,
                                FontSize = 12,
                            });
                        }

                        body.Children.Add(metaPanel);
                    }
                }
                catch
                {
                    // Best-effort: metadata extraction may fail for some file types.
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                body.Children.Add(new TextBlock { Text = exception.Message });
            }
            finally
            {
                FinishUtilityOperation(utilityCts);
            }
        }

        if (!hasVisualPreview)
        {
            body.Children.Add(PreviewPresenter.CreateFileTypePreviewIcon(row, 96));
        }

        if (workspace is not null && !ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Quick Look",
            Content = body,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private static Grid QuickLookMetadataRow(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            Opacity = 0.7,
            FontSize = 13,
        };
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);

        return grid;
    }

    private static async Task<bool> TryAddQuickLookImageAsync(
        StackPanel body,
        FileRow row,
        FilePreview preview,
        FileOperationService fileOps,
        CancellationToken cancellationToken)
    {
        var imageData = preview.Content;
        if (string.IsNullOrWhiteSpace(imageData))
        {
            try
            {
                imageData = await fileOps.GenerateThumbnailAsync(row.Path, 512, cancellationToken);
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var source = await PreviewImageSourceFactory.FromBase64Async(imageData, row.Path);
            body.Children.Add(new Image
            {
                Source = source,
                MaxHeight = 420,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Stretch = Stretch.Uniform,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ShowPropertiesAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null || ActiveSelectedRow is not { } row)
        {
            return;
        }

        var rows = new StackPanel { Spacing = 8, Width = 460 };
        void AddRow(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            rows.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Opacity = 0.7,
            });
            rows.Children.Add(new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        AddRow("Name", row.Name);
        AddRow("Type", row.TypeText);
        AddRow("Location", PathRules.GetParentPath(row.Path) ?? row.Path);
        AddRow("Path", row.Path);
        if (!string.IsNullOrEmpty(row.SymlinkText))
        {
            AddRow(PathRules.IsRecycleBinPath(workspace.Active.Path) ? "Original location" : "Link target", row.SymlinkText);
        }

        AddRow("Size", row.SizeText);
        AddRow("Modified", row.ModifiedText);

        var checksumText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 };
        var checksumButton = new Button { Content = "Compute checksums", HorizontalAlignment = HorizontalAlignment.Left };

        var utilityCts = BeginUtilityOperation();
        try
        {
            var info = await fileOps.GetEntryInfoAsync(row.Path, utilityCts.Token);
            if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
            {
                return;
            }

            var attributes = new List<string>();
            if (info.IsHidden)
            {
                attributes.Add("Hidden");
            }

            if (info.IsSystem)
            {
                attributes.Add("System");
            }

            if (info.IsSymlink)
            {
                attributes.Add("Shortcut");
            }

            AddRow("Attributes", attributes.Count == 0 ? "Normal" : string.Join(", ", attributes));
            if (!string.IsNullOrEmpty(info.Permissions))
            {
                AddRow("Permissions", info.Permissions);
            }

            try
            {
                var metadata = await fileOps.GetFileMetadataAsync(row.Path, utilityCts.Token);
                if (!string.IsNullOrEmpty(metadata.Summary))
                {
                    AddRow("Summary", metadata.Summary);
                }

                foreach (var field in metadata.Fields)
                {
                    if (field.Length >= 2)
                    {
                        AddRow(field[0], field[1]);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Metadata is optional; core properties still show.
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddRow("Error", exception.Message);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }

        checksumButton.Click += async (_, _) =>
        {
            checksumButton.IsEnabled = false;
            checksumText.Text = "Computing…";
            var hashCts = BeginUtilityOperation();
            try
            {
                var checksums = await fileOps.ComputeChecksumAsync(row.Path, hashCts.Token);
                checksumText.Text = $"MD5    {checksums.Md5}{Environment.NewLine}SHA-1  {checksums.Sha1}{Environment.NewLine}SHA-256 {checksums.Sha256}";
            }
            catch (Exception exception)
            {
                checksumText.Text = exception.Message;
            }
            finally
            {
                FinishUtilityOperation(hashCts);
                checksumButton.IsEnabled = true;
            }
        };

        rows.Children.Add(checksumButton);
        rows.Children.Add(checksumText);

        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Properties",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = rows,
            },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async Task ShowClipboardHistoryAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var entries = _workspace.ClipboardHistory.Items;
        if (entries.Count == 0)
        {
            SetStatusText("Clipboard history is empty");
            return;
        }

        var list = new ListView
        {
            MinWidth = 420,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Single,
        };
        foreach (var entry in entries)
        {
            list.Items.Add(new ClipboardHistoryRow(entry));
        }

        list.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            Title = "Clipboard history",
            Content = list,
            PrimaryButtonText = "Paste this",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not ClipboardHistoryRow row)
        {
            return;
        }

        if (row.Entry.Operation == ClipboardOperation.Cut)
        {
            _workspace.Clipboard.SetCut(row.Entry.Paths);
        }
        else
        {
            _workspace.Clipboard.SetCopy(row.Entry.Paths);
        }

        await PasteFromClipboard();
    }

    private async Task ShowFolderMetricsAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var folders = ActiveSelectedRows.Where(row => row.IsDir).ToArray();
        var paths = folders.Length > 0
            ? folders.Select(f => f.Path).ToArray()
            : [workspace.Active.Path];

        var utilityCts = BeginUtilityOperation();
        try
        {
            var lines = new List<string>();
            ulong totalSize = 0;
            ulong totalCount = 0;

            foreach (var path in paths)
            {
                SetStatusText($"Calculating metrics for {path}...");
                var size = await fileOps.CalculateFolderSizeAsync(path, utilityCts.Token);
                var count = await fileOps.CountFolderItemsAsync(path, utilityCts.Token);
                if (!ReferenceEquals(_workspace, workspace) || utilityCts.IsCancellationRequested)
                {
                    return;
                }

                lines.Add($"{path}{Environment.NewLine}{EntryPresentation.FormatFileSize(size)} · {count} item(s)");
                totalSize += size;
                totalCount += count;
            }

            if (paths.Length > 1)
            {
                lines.Add($"Total: {EntryPresentation.FormatFileSize(totalSize)} · {totalCount} item(s) across {paths.Length} folders");
            }
            else
            {
                lines.Add($"Total: {EntryPresentation.FormatFileSize(totalSize)} · {totalCount} item(s)");
            }

            SetStatusText("");
            var dialog = new ContentDialog
            {
                Title = "Folder metrics",
                Content = new ScrollViewer
                {
                    MaxHeight = 400,
                    Content = new TextBlock
                    {
                        Text = string.Join(Environment.NewLine + Environment.NewLine, lines),
                        TextWrapping = TextWrapping.Wrap,
                        Width = 420,
                    },
                },
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("Folder metrics", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task ShowOperationHistoryAsync()
    {
        var workspace = _workspace;
        var records = workspace?.OperationLog ?? [];
        if (workspace is null || records.Count == 0)
        {
            SetStatusText("No operations in this session.");
            return;
        }

        var list = new ListView
        {
            MinWidth = 420,
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.Single,
        };
        foreach (var record in records)
        {
            list.Items.Add(new OperationHistoryRow(record));
        }

        list.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "Operation history",
            Content = list,
            PrimaryButtonText = "Retry",
            SecondaryButtonText = workspace.Undo.CanUndo ? "Undo last" : "",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (!ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        if (result == ContentDialogResult.Primary && list.SelectedItem is OperationHistoryRow row)
        {
            await TransferWithConflictAsync(
                row.Record.Sources,
                row.Record.Destination,
                row.Record.Move);
        }
        else if (result == ContentDialogResult.Secondary && workspace.Undo.CanUndo)
        {
            await UndoLastAsync();
        }
    }

    private async Task RunGitAsync(bool pull)
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var path = workspace.Active.Path;
        var utilityCts = BeginUtilityOperation();
        try
        {
            if (pull)
            {
                SetStatusText($"Pulling Git changes in {path}...");
                await fileOps.GitPullAsync(path, utilityCts.Token);
                if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
                {
                    ShowMessage("Git", "Pull completed.", InfoBarSeverity.Success);
                }
            }
            else
            {
                SetStatusText($"Pushing Git changes from {path}...");
                await fileOps.GitPushAsync(path, utilityCts.Token);
                if (ReferenceEquals(_workspace, workspace) && !utilityCts.IsCancellationRequested)
                {
                    ShowMessage("Git", "Push completed.", InfoBarSeverity.Success);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage(pull ? "Git pull" : "Git push", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }

    private async Task OpenPowershellAdminAsync()
    {
        var workspace = _workspace;
        var fileOps = workspace?.FileOps;
        if (workspace is null || fileOps is null)
        {
            return;
        }

        var utilityCts = BeginUtilityOperation();
        try
        {
            await fileOps.OpenPowershellAdminAsync(workspace.Active.Path, utilityCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowMessage("PowerShell", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            FinishUtilityOperation(utilityCts);
        }
    }
}
