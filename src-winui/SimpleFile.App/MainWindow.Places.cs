using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
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

        var query = ActiveToolbarSearchTextBox().Text.Trim();
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
}
