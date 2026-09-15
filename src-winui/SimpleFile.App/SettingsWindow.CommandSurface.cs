using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed class ToolbarEditorRow
{
    public ToolbarEditorRow(CommandSurfaceItem item)
    {
        IsSeparator = item.IsSeparator;
        ActionId = item.IsSeparator ? "" : item.Id;
        if (IsSeparator)
        {
            Label = "Separator";
            Detail = "Divider";
            IconGlyph = "\uE76F";
            return;
        }

        var action = ToolbarActionCatalog.Find(ActionId);
        Label = action?.Label ?? ActionId;
        Detail = action is null ? "Custom" : action.Group;
        IconGlyph = action?.IconGlyph ?? ContextMenuIconCatalog.Document;
    }

    public string ActionId { get; }
    public string Label { get; }
    public string Detail { get; }
    public string IconGlyph { get; }
    public bool IsSeparator { get; }

    public CommandSurfaceItem ToItem() =>
        IsSeparator ? CommandSurfaceItem.Separator() : CommandSurfaceItem.Command(ActionId);
}

public sealed class ToolbarCommandChoice
{
    public ToolbarCommandChoice(ToolbarAction action)
    {
        Action = action;
    }

    public ToolbarAction Action { get; }
    public string DisplayText => $"{Action.Label} - {Action.Group}";
}

public sealed partial class SettingsWindow
{
    private readonly ObservableCollection<ToolbarEditorRow> _toolbarRows = [];

    private void InitializeToolbarEditor()
    {
        ToolbarLayoutList.ItemsSource = _toolbarRows;
        ApplyCommandSurfaceLayout(CommandSurfaceLayout.CreateDefault());
    }

    private CommandSurfaceLayout CurrentCommandSurfaceLayout()
    {
        var layout = new CommandSurfaceLayout
        {
            ToolbarDisplayMode = SelectedToolbarDisplayMode(),
            PrimaryToolbar = _toolbarRows.Select(row => row.ToItem()).ToList(),
        };
        layout.Normalize();
        return layout;
    }

    private void ApplyCommandSurfaceLayout(CommandSurfaceLayout layout)
    {
        layout.Normalize();
        SelectToolbarDisplayMode(layout.ToolbarDisplayMode);
        _toolbarRows.Clear();
        foreach (var item in layout.PrimaryToolbar)
        {
            _toolbarRows.Add(new ToolbarEditorRow(item));
        }

        if (_toolbarRows.Count > 0)
        {
            ToolbarLayoutList.SelectedIndex = 0;
        }

        RefreshToolbarAvailableCommands();
    }

    private string SelectedToolbarDisplayMode()
    {
        return ToolbarDisplayModeComboBox.SelectedItem is ComboBoxItem { Tag: string mode }
            ? ToolbarActionCatalog.NormalizeDisplayMode(mode)
            : ToolbarActionCatalog.IconOnlyDisplayMode;
    }

    private void SelectToolbarDisplayMode(string displayMode)
    {
        var normalized = ToolbarActionCatalog.NormalizeDisplayMode(displayMode);
        foreach (var item in ToolbarDisplayModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.Ordinal))
            {
                ToolbarDisplayModeComboBox.SelectedItem = item;
                return;
            }
        }

        ToolbarDisplayModeComboBox.SelectedIndex = 0;
    }

    private void RefreshToolbarAvailableCommands()
    {
        var used = _toolbarRows
            .Where(row => !row.IsSeparator)
            .Select(row => row.ActionId)
            .ToHashSet(StringComparer.Ordinal);
        var choices = ToolbarActionCatalog.All
            .Where(action => !used.Contains(action.Id))
            .OrderBy(action => action.Group, StringComparer.Ordinal)
            .ThenBy(action => action.Label, StringComparer.Ordinal)
            .Select(action => new ToolbarCommandChoice(action))
            .ToList();

        ToolbarAvailableCommandsComboBox.ItemsSource = choices;
        ToolbarAvailableCommandsComboBox.SelectedIndex = choices.Count > 0 ? 0 : -1;
        ToolbarAddCommandButton.IsEnabled = choices.Count > 0;
    }

    private int ToolbarInsertIndex()
    {
        return ToolbarLayoutList.SelectedIndex >= 0
            ? ToolbarLayoutList.SelectedIndex + 1
            : _toolbarRows.Count;
    }

    private void OnToolbarAddCommandClicked(object sender, RoutedEventArgs e)
    {
        if (ToolbarAvailableCommandsComboBox.SelectedItem is not ToolbarCommandChoice choice)
        {
            return;
        }

        var index = ToolbarInsertIndex();
        var row = new ToolbarEditorRow(CommandSurfaceItem.Command(choice.Action.Id));
        _toolbarRows.Insert(index, row);
        ToolbarLayoutList.SelectedItem = row;
        RefreshToolbarAvailableCommands();
    }

    private void OnToolbarAddSeparatorClicked(object sender, RoutedEventArgs e)
    {
        var index = ToolbarInsertIndex();
        var row = new ToolbarEditorRow(CommandSurfaceItem.Separator());
        _toolbarRows.Insert(index, row);
        ToolbarLayoutList.SelectedItem = row;
    }

    private void OnToolbarResetClicked(object sender, RoutedEventArgs e)
    {
        ApplyCommandSurfaceLayout(CommandSurfaceLayout.CreateDefault());
    }

    private void OnToolbarMoveUpClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ToolbarEditorRow row })
        {
            return;
        }

        var index = _toolbarRows.IndexOf(row);
        if (index <= 0)
        {
            return;
        }

        _toolbarRows.Move(index, index - 1);
        ToolbarLayoutList.SelectedItem = row;
    }

    private void OnToolbarMoveDownClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ToolbarEditorRow row })
        {
            return;
        }

        var index = _toolbarRows.IndexOf(row);
        if (index < 0 || index >= _toolbarRows.Count - 1)
        {
            return;
        }

        _toolbarRows.Move(index, index + 1);
        ToolbarLayoutList.SelectedItem = row;
    }

    private void OnToolbarRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ToolbarEditorRow row })
        {
            return;
        }

        _toolbarRows.Remove(row);
        RefreshToolbarAvailableCommands();
    }
}
