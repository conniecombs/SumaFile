using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimpleFile.Core;
using Windows.System;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private OmnibarMode _omnibarMode = OmnibarMode.Navigate;
    private bool _omnibarFocused;
    private bool _syncingOmnibar;

    private void OnOmnibarGotFocus(object sender, RoutedEventArgs e)
    {
        _omnibarFocused = true;
    }

    private void OnOmnibarLostFocus(object sender, RoutedEventArgs e)
    {
        _omnibarFocused = false;
        SyncOmnibarFromWorkspace(force: true);
    }

    private void OnOmnibarModeRequested(object? sender, OmnibarMode mode)
    {
        SetOmnibarMode(mode, focus: true);
    }

    private async void OnOmnibarExecuteClick(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync("Omnibar", ExecuteOmnibarAsync);
    }

    private async void OnOmnibarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await RunUiActionAsync("Omnibar", ExecuteOmnibarAsync);
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            SyncOmnibarFromWorkspace(force: true);
            OmnibarInput.SelectAll();
        }
    }

    private void SetOmnibarMode(OmnibarMode mode, bool focus)
    {
        var typed = PreserveTypedOmnibarText();
        _omnibarMode = mode;
        ApplyOmnibarModeButtons();
        SyncOmnibarFromWorkspace(force: true);
        if (typed is not null)
        {
            OmnibarInput.Text = typed;
        }

        if (focus)
        {
            OmnibarInput.Focus(FocusState.Programmatic);
            if (typed is null)
            {
                OmnibarInput.SelectAll();
            }
            else
            {
                OmnibarInput.SelectionStart = typed.Length;
            }
        }
    }

    /// <summary>
    /// Text the user has typed into the focused omnibar that differs from the current mode's
    /// synced value, so a mode switch carries it over instead of discarding it.
    /// </summary>
    private string? PreserveTypedOmnibarText()
    {
        if (_workspace is null || !_omnibarFocused)
        {
            return null;
        }

        var typed = OmnibarInput.Text;
        if (string.IsNullOrEmpty(typed) || string.Equals(typed, SyncedOmnibarTextFor(_omnibarMode), StringComparison.Ordinal))
        {
            return null;
        }

        return typed;
    }

    private string SyncedOmnibarTextFor(OmnibarMode mode) => mode switch
    {
        OmnibarMode.Search => _search?.Query ?? "",
        OmnibarMode.Filter => _workspace?.FilterQueryFor(ActiveUiPane) ?? "",
        OmnibarMode.Command => "",
        _ => _workspace?.Active.Path ?? "",
    };

    /// <summary>Filter mode applies as the user types, like the old filter box did.</summary>
    private void OnOmnibarTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingOmnibar || _applyingWorkspace || _omnibarMode != OmnibarMode.Filter || !_omnibarFocused)
        {
            return;
        }

        _workspace?.SetFilterQuery(ActiveUiPane, OmnibarInput.Text);
    }

    private void FocusOmnibar(OmnibarMode mode)
    {
        SetOmnibarMode(mode, focus: true);
    }

    private async Task ExecuteOmnibarAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        var intent = OmnibarIntentParser.Parse(OmnibarInput.Text, _omnibarMode, _workspace.Active.Path);
        switch (intent.Kind)
        {
            case OmnibarIntentKind.Navigate:
                if (!string.IsNullOrWhiteSpace(intent.Value))
                {
                    SetOmnibarMode(OmnibarMode.Navigate, focus: false);
                    await _workspace.NavigatePaneAsync(ActiveUiPane, intent.Value);
                }

                break;
            case OmnibarIntentKind.Search:
                SetOmnibarMode(OmnibarMode.Search, focus: false);
                if (_search is not null)
                {
                    _search.Query = intent.Value;
                }

                await StartSearchAsync(ActiveUiPane);
                break;
            case OmnibarIntentKind.Filter:
                SetOmnibarMode(OmnibarMode.Filter, focus: false);
                _workspace.SetFilterQuery(ActiveUiPane, intent.Value);
                SetStatusText(string.IsNullOrWhiteSpace(intent.Value)
                    ? "Filter cleared"
                    : $"Filtering current folder for \"{intent.Value}\"");
                break;
            case OmnibarIntentKind.Command:
                await ExecuteOmnibarCommandAsync(intent.Value);
                break;
        }
        SyncOmnibarFromWorkspace(force: true);
    }

    private async Task ExecuteOmnibarCommandAsync(string query)
    {
        var matches = OmnibarIntentParser.FilterCommands(query, IsGitIntegrationEnabled);
        var command = matches.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Label, query, StringComparison.OrdinalIgnoreCase))
            ?? (matches.Count == 1 ? matches[0] : null);

        if (command is null)
        {
            OpenCommandPalette(query);
            return;
        }

        SetOmnibarMode(OmnibarMode.Command, focus: false);
        await RunAppCommandAsync(command.Id);
    }

    private void SyncOmnibarFromWorkspace(bool force = false)
    {
        if (_workspace is null || _syncingOmnibar)
        {
            return;
        }

        if (!force && _omnibarFocused)
        {
            return;
        }

        _syncingOmnibar = true;
        try
        {
            ApplyOmnibarModeButtons();
            OmnibarInput.Text = SyncedOmnibarTextFor(_omnibarMode);
            OmnibarInput.PlaceholderText = _omnibarMode switch
            {
                OmnibarMode.Search => "Find in folder",
                OmnibarMode.Filter => "Filter current folder",
                OmnibarMode.Command => "Type a command",
                _ => "Path",
            };
        }
        finally
        {
            _syncingOmnibar = false;
        }
    }

    private void ApplyOmnibarModeButtons()
    {
        OmnibarPathModeButton.IsChecked = _omnibarMode == OmnibarMode.Navigate;
        OmnibarSearchModeButton.IsChecked = _omnibarMode == OmnibarMode.Search;
        OmnibarFilterModeButton.IsChecked = _omnibarMode == OmnibarMode.Filter;
        OmnibarCommandModeButton.IsChecked = _omnibarMode == OmnibarMode.Command;

        // Search mode exposes the content-search toggle and the cancel button beside Run.
        var searching = _omnibarMode == OmnibarMode.Search;
        ContentSearchButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        SearchCancelButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        OmnibarInput.Padding = new Thickness(116, 4, searching ? 92 : 34, 4);
    }
}
