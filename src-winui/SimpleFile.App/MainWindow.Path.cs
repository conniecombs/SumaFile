using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;
using Windows.System;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private const int PathSuggestDebounceMilliseconds = 150;
    private int _pathSuggestToken;

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

        // Coalesce keystrokes and drop stale responses so a slower earlier lookup cannot
        // overwrite the suggestions for the text the user has typed since.
        var token = Interlocked.Increment(ref _pathSuggestToken);
        await Task.Delay(PathSuggestDebounceMilliseconds);
        if (token != Volatile.Read(ref _pathSuggestToken))
        {
            return;
        }

        if (!PathCompletion.TrySplit(input.Text, out var directory, out var prefix))
        {
            HidePathSuggestions();
            return;
        }

        var candidates = await ListFolderCandidatesAsync(pane, directory);
        if (token != Volatile.Read(ref _pathSuggestToken))
        {
            return;
        }

        var suggestions = candidates is null ? [] : OmnibarSuggestions.FolderSuggestions(candidates, prefix);
        if (suggestions.Count == 0)
        {
            HidePathSuggestions();
            return;
        }

        ShowSuggestions(input, suggestions);
    }

    /// <summary>
    /// The omnibar's suggestions for what is typed in it: folders for a path, commands after <c>&gt;</c> or in
    /// command mode, and none for a search or a filter. Debounced and token-checked like the path box's.
    /// </summary>
    private async Task UpdateOmnibarSuggestionsAsync()
    {
        if (_workspace is null || !_omnibarFocused)
        {
            HideSuggestions(OmnibarInput);
            return;
        }

        var token = Interlocked.Increment(ref _pathSuggestToken);
        await Task.Delay(PathSuggestDebounceMilliseconds);
        if (token != Volatile.Read(ref _pathSuggestToken))
        {
            return;
        }

        var pane = ActiveUiPane;
        var request = OmnibarSuggestions.Request(OmnibarInput.Text, _omnibarMode, _workspace.Pane(pane).Path);
        IReadOnlyList<OmnibarSuggestion> suggestions = [];
        if (request.Kind == OmnibarSuggestionKind.Command)
        {
            suggestions = OmnibarSuggestions.CommandSuggestions(request.Query);
        }
        else if (request.Kind == OmnibarSuggestionKind.Folder)
        {
            var candidates = await ListFolderCandidatesAsync(pane, request.Directory);
            if (token != Volatile.Read(ref _pathSuggestToken))
            {
                return;
            }

            suggestions = candidates is null ? [] : OmnibarSuggestions.FolderSuggestions(candidates, request.Prefix);
        }

        if (suggestions.Count == 0 || !_omnibarFocused)
        {
            HideSuggestions(OmnibarInput);
            return;
        }

        ShowSuggestions(OmnibarInput, suggestions);
    }

    /// <summary>
    /// The folders in <paramref name="directory"/>: from the pane's own listing when it shows that folder, else from
    /// the service. Null when they cannot be listed.
    /// </summary>
    private async Task<IEnumerable<string>?> ListFolderCandidatesAsync(PaneId pane, string directory)
    {
        if (_workspace is null)
        {
            return null;
        }

        var paneState = _workspace.Pane(pane);
        if (PathRules.PathsEqual(directory, paneState.Path))
        {
            return paneState.Entries.Where(entry => entry.IsDir).Select(entry => entry.Path).ToArray();
        }

        if (_workspace.FileOps is null)
        {
            return null;
        }

        try
        {
            var nodes = await _workspace.FileOps.ListSubdirectoriesAsync(directory);
            return nodes.Select(node => node.Path).ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Shows the list under <paramref name="box"/>, with no line chosen: Enter still runs what is typed.</summary>
    private void ShowSuggestions(TextBox box, IReadOnlyList<OmnibarSuggestion> suggestions)
    {
        EnsurePathSuggestUi();
        _suggestBox = box;
        _suggestions = suggestions;
        _suggestionsOpen = true;
        _pathSuggestList!.ItemsSource = suggestions.Select(suggestion => suggestion.Text).ToList();
        _pathSuggestFlyout!.ShowAt(box);
    }

    private void HidePathSuggestions()
    {
        // Closed at once for the keys: the flyout reports that it closed only later, and until then Enter would
        // take the line that was chosen in a list that is no longer there, instead of running what is typed.
        _suggestionsOpen = false;
        _suggestions = [];
        _pathSuggestFlyout?.Hide();
    }

    /// <summary>Hides the list when it is open under <paramref name="box"/>, and leaves another box's alone.</summary>
    private void HideSuggestions(TextBox box)
    {
        if (ReferenceEquals(_suggestBox, box))
        {
            HidePathSuggestions();
        }
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
            // Keep keyboard focus in the path box; the flyout must not end the edit.
            AllowFocusOnInteraction = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_pathSuggestList, "Suggestions");
        _pathSuggestList.ItemClick += OnPathSuggestClick;
        _pathSuggestFlyout = new Flyout
        {
            Content = _pathSuggestList,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Transient,
        };
        // A click elsewhere dismisses the flyout without going through HidePathSuggestions. A list shown again
        // right after it was hidden is open when the first one's Closed arrives.
        var flyout = _pathSuggestFlyout;
        flyout.Closed += (_, _) => _suggestionsOpen = flyout.IsOpen;
    }

    private void OnPathSuggestClick(object sender, ItemClickEventArgs e)
    {
        if (_pathSuggestList is not null)
        {
            AcceptSuggestion(_pathSuggestList.Items.IndexOf(e.ClickedItem));
        }
    }

    /// <summary>
    /// Takes a line of the list. A folder fills the box with its path and a separator and keeps the box in use, so
    /// the next list is that folder's; a command is run.
    /// </summary>
    private void AcceptSuggestion(int index)
    {
        if (_suggestBox is not { } box || index < 0 || index >= _suggestions.Count)
        {
            return;
        }

        var suggestion = _suggestions[index];
        if (suggestion.Kind == OmnibarSuggestionKind.Command)
        {
            HidePathSuggestions();
            _ = RunUiActionAsync("Omnibar", async () =>
            {
                SetOmnibarMode(OmnibarMode.Command, focus: false);
                await RunAppCommandAsync(suggestion.Value);
                SyncOmnibarFromWorkspace(force: true);
            });
            return;
        }

        _acceptingPathSuggestion = true;
        Interlocked.Increment(ref _pathLostFocusToken);
        var filled = OmnibarSuggestions.AcceptedFolderText(suggestion.Value);
        box.Text = filled;
        box.SelectionStart = filled.Length;
        HidePathSuggestions();
        box.Focus(FocusState.Programmatic);
        _acceptingPathSuggestion = false;
    }

    /// <summary>
    /// The keys of an open list, taken before the box sees them: Down and Up choose a line, Tab takes the chosen
    /// line or the first, Enter takes the chosen line (with none chosen it runs what is typed, as without a list),
    /// and Esc closes the list and leaves the text.
    /// </summary>
    private void OnSuggestBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_suggestionsOpen || !ReferenceEquals(sender, _suggestBox) || _pathSuggestList is not { } list || _suggestions.Count == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Down:
            case VirtualKey.Up:
                var index = OmnibarSuggestions.MoveSelection(list.SelectedIndex, _suggestions.Count, e.Key == VirtualKey.Down ? 1 : -1);
                list.SelectedIndex = index;
                if (index >= 0)
                {
                    list.ScrollIntoView(list.Items[index]);
                }

                e.Handled = true;
                break;
            case VirtualKey.Tab when !IsKeyDown(VirtualKey.Shift):
                e.Handled = true;
                AcceptSuggestion(Math.Max(0, list.SelectedIndex));
                break;
            case VirtualKey.Enter when list.SelectedIndex >= 0:
                e.Handled = true;
                AcceptSuggestion(list.SelectedIndex);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                HidePathSuggestions();
                break;
        }
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
