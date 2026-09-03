using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;
using Windows.System;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private void OnSearchResultsChanged(object? sender, SearchResultsChangedEventArgs e)
    {
        ApplySearchRows();
    }

    private void OnSearchCleared(object? sender, EventArgs e)
    {
        SyncFromWorkspace();
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_search is null)
        {
            return;
        }

        if (e.PropertyName == nameof(SearchViewModel.StatusText))
        {
            SetStatusText(_search.StatusText);
        }
        else if (e.PropertyName is nameof(SearchViewModel.CanCancel) or nameof(SearchViewModel.Pane))
        {
            UpdateSearchCancelButtons();
        }
    }

    private void OnViewModelMessageRequested(object? sender, ViewModelMessageEventArgs e)
    {
        ShowMessage(e.Title, e.Message, InfoBarSeverity.Error);
    }

    private void OnContentSearchToggle(object sender, RoutedEventArgs e)
    {
        if (_search is null)
        {
            return;
        }

        _search.ContentSearch = ContentSearchButton.IsChecked == true;
        SetStatusText(_search.ContentSearch
            ? "Find in folder will include file contents"
            : "Find in folder matches filenames only");
    }

    private Task StartSearchAsync(PaneId? requestedPane = null)
    {
        if (_search is null)
        {
            return Task.CompletedTask;
        }

        _search.Query = ActiveToolbarSearchTextBox().Text;
        return _search.StartAsync(requestedPane, DispatchToUi);
    }

    private void ApplySearchRows()
    {
        if (_search is null)
        {
            return;
        }

        if (_search.Pane == PaneId.Secondary)
        {
            Replace(SecondaryFiles, _search.Results.Select(result => SearchRowFrom(result, PaneId.Secondary)));
        }
        else
        {
            Replace(PrimaryFiles, _search.Results.Select(result => SearchRowFrom(result, PaneId.Primary)));
        }

        SetCountText(_search.ResultCount == 1
            ? "1 search result"
            : $"{_search.ResultCount} search results");
    }

    private Task CancelActiveSearchAsync() =>
        _search?.CancelActiveAsync() ?? Task.CompletedTask;

    private void ClearSearchState() =>
        _search?.ClearState();

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
}
