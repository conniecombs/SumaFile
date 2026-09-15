using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;
using Windows.System;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private bool IsEditingPath => _editingPrimaryPath || _editingSecondaryPath;

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
}
