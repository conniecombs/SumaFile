using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace SimpleFile.App;

public sealed class ShortcutEditorRow : INotifyPropertyChanged
{
    private string _issueText = "";
    private bool _updatingShortcuts;

    public ShortcutEditorRow(KeyboardShortcut definition)
    {
        Definition = definition;
        Shortcuts.CollectionChanged += (_, _) =>
        {
            if (!_updatingShortcuts)
            {
                NotifyShortcutStateChanged();
            }
        };
        SetShortcuts(definition.DefaultShortcuts);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public KeyboardShortcut Definition { get; }
    public ObservableCollection<string> Shortcuts { get; } = [];
    public string Id => Definition.Id;
    public string Label => Definition.Label;
    public string Group => Definition.Group;
    public bool IsEditable => Definition.IsEditable;
    public string ShortcutText => KeyboardShortcutMap.FormatShortcuts(Shortcuts);
    public string DefaultText => KeyboardShortcutMap.FormatShortcuts(Definition.DefaultShortcuts);
    public bool IsModified => !KeyboardShortcutMap.ShortcutListsEqual(Shortcuts, Definition.DefaultShortcuts);

    public string IssueText
    {
        get => _issueText;
        set
        {
            if (string.Equals(_issueText, value, StringComparison.Ordinal))
            {
                return;
            }

            _issueText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IssueText)));
        }
    }

    public bool Matches(string query)
    {
        return query.Length == 0
            || Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Group.Contains(query, StringComparison.OrdinalIgnoreCase)
            || ShortcutText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void SetShortcuts(IEnumerable<string>? shortcuts)
    {
        _updatingShortcuts = true;
        Shortcuts.Clear();
        var next = IsEditable
            ? KeyboardShortcutMap.NormalizeShortcutList(shortcuts)
            : (shortcuts ?? [])
                .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut))
                .Select(shortcut => shortcut.Trim());
        foreach (var shortcut in next)
        {
            Shortcuts.Add(shortcut);
        }

        _updatingShortcuts = false;
        NotifyShortcutStateChanged();
    }

    private void NotifyShortcutStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortcutText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModified)));
    }
}

public sealed partial class SettingsWindow : Window
{
    private readonly string _initialCategory;
    private readonly TaskCompletionSource<ContentDialogResult> _resultSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ShortcutEditorRow> _shortcutRows;
    private ContentDialogResult _result = ContentDialogResult.None;

    public SettingsWindow(string? initialCategory = null)
    {
        _initialCategory = NormalizeCategoryName(initialCategory);
        InitializeComponent();
        Title = "Settings";
        AppIcon.ApplyTo(this);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1120, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        Closed += OnClosed;
        SelectInitialCategory();
        UpdateDefaultIconSizeValueText(DefaultIconSize);
        _shortcutRows = KeyboardShortcutMap.Defaults
            .Select(definition => new ShortcutEditorRow(definition))
            .ToList();
        InitializeToolbarEditor();
        RefreshShortcutList(selectFirst: true);
        RefreshShortcutValidation();
    }

    public Task<ContentDialogResult> ShowAsync()
    {
        Activate();
        OwnerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return _resultSource.Task;
    }

    private void SelectInitialCategory()
    {
        var selected = SettingsNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(CategoryName(item), _initialCategory, StringComparison.Ordinal))
            ?? SettingsNavigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
        SettingsNavigation.SelectedItem = selected;
        ShowCategory(CategoryName(selected));
    }

    private void OnCategorySelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        ShowCategory(CategoryName(args.SelectedItemContainer as NavigationViewItem));
    }

    private static string CategoryName(NavigationViewItem? item)
    {
        return item?.Tag?.ToString()
            ?? item?.Content?.ToString()
            ?? "";
    }

    private static string NormalizeCategoryName(string? category)
    {
        var value = (category ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Appearance";
        }

        return value;
    }

    private void ShowCategory(string category)
    {
        HideCategoryPanels();

        switch (category)
        {
            case "Appearance": AppearancePanel.Visibility = Visibility.Visible; break;
            case "Navigation": NavigationPanel.Visibility = Visibility.Visible; break;
            case "Behavior": BehaviorPanel.Visibility = Visibility.Visible; break;
            case "Storage & Cache": StoragePanel.Visibility = Visibility.Visible; break;
            case "Shortcuts": ShortcutsPanel.Visibility = Visibility.Visible; break;
            case "Toolbar": ToolbarPanel.Visibility = Visibility.Visible; break;
            case "Tools": ToolsPanel.Visibility = Visibility.Visible; break;
            case "Updates": UpdatesPanel.Visibility = Visibility.Visible; break;
            case "About": AboutPanel.Visibility = Visibility.Visible; break;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        NavigationViewItem? firstVisible = null;
        foreach (var item in SettingsNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            var label = item.Content?.ToString() ?? "";
            var visible = query.Length == 0 || label.Contains(query, StringComparison.OrdinalIgnoreCase);
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && firstVisible is null)
            {
                firstVisible = item;
            }
        }

        if (SettingsNavigation.SelectedItem is not NavigationViewItem selected
            || selected.Visibility != Visibility.Visible)
        {
            SettingsNavigation.SelectedItem = firstVisible;
            if (firstVisible is null)
            {
                HideCategoryPanels();
            }
            else
            {
                ShowCategory(CategoryName(firstVisible));
            }
        }
    }

    private void HideCategoryPanels()
    {
        AppearancePanel.Visibility = Visibility.Collapsed;
        NavigationPanel.Visibility = Visibility.Collapsed;
        BehaviorPanel.Visibility = Visibility.Collapsed;
        StoragePanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        ToolbarPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.Visibility = Visibility.Collapsed;
        UpdatesPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
    }

    private void OnShortcutSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshShortcutList();
    }

    private void OnShortcutSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateShortcutEditor(SelectedShortcutRow);
    }

    private void RefreshShortcutList(bool selectFirst = false)
    {
        var query = ShortcutSearchBox?.Text?.Trim() ?? "";
        var selectedId = SelectedShortcutRow?.Id;
        var visibleRows = _shortcutRows
            .Where(row => row.Matches(query))
            .ToList();

        ShortcutsList.ItemsSource = visibleRows;
        var selected = visibleRows.FirstOrDefault(row => string.Equals(row.Id, selectedId, StringComparison.Ordinal))
            ?? (selectFirst ? visibleRows.FirstOrDefault() : null);
        ShortcutsList.SelectedItem = selected;
        if (selected is null)
        {
            UpdateShortcutEditor(null);
        }
    }

    private ShortcutEditorRow? SelectedShortcutRow => ShortcutsList.SelectedItem as ShortcutEditorRow;

    private void UpdateShortcutEditor(ShortcutEditorRow? row)
    {
        SelectedShortcutTitle.Text = row?.Label ?? "Select a command";
        SelectedShortcutDefaultText.Text = row is null
            ? ""
            : row.IsEditable
                ? $"Default: {row.DefaultText}"
                : $"Default: {row.DefaultText} (fixed)";
        SelectedShortcutList.ItemsSource = row?.Shortcuts;
        ShortcutIssueText.Text = row?.IssueText ?? "";
        var canEdit = row?.IsEditable == true;
        ShortcutRecorderBox.IsEnabled = canEdit;
        ShortcutAddButton.IsEnabled = canEdit;
        ShortcutResetButton.IsEnabled = canEdit;
        ShortcutClearButton.IsEnabled = canEdit;
        if (!canEdit)
        {
            ShortcutRecorderBox.Text = "";
            ShortcutRecorderStatusText.Text = row is null ? "" : "Fixed shortcut.";
        }
        else
        {
            ValidateShortcutRecorderText();
        }
    }

    private Dictionary<string, List<string>> CurrentShortcutOverrides()
    {
        var raw = _shortcutRows
            .Where(row => row.IsEditable)
            .ToDictionary(
                row => row.Id,
                row => row.Shortcuts.ToList(),
                StringComparer.Ordinal);
        return KeyboardShortcutMap.NormalizeOverrides(raw);
    }

    private void ApplyShortcutOverrides(IDictionary<string, List<string>>? overrides)
    {
        var effective = KeyboardShortcutMap.EffectiveShortcuts(
                overrides is null ? null : new Dictionary<string, List<string>>(overrides, StringComparer.Ordinal))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var row in _shortcutRows)
        {
            if (effective.TryGetValue(row.Id, out var assignment))
            {
                row.SetShortcuts(assignment.Shortcuts);
            }
        }

        RefreshShortcutList();
        RefreshShortcutValidation();
    }

    private void RefreshShortcutValidation()
    {
        var issues = KeyboardShortcutMap.ValidateOverrides(CurrentShortcutOverrides());
        var issueLookup = issues
            .GroupBy(issue => issue.CommandId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => string.Join(" ", group.Select(issue => issue.Message).Distinct(StringComparer.Ordinal).Take(2)),
                StringComparer.Ordinal);

        foreach (var row in _shortcutRows)
        {
            row.IssueText = issueLookup.TryGetValue(row.Id, out var issueText) ? issueText : "";
        }

        var errors = issues.Count(issue => issue.Severity == KeyboardShortcutIssueSeverity.Error);
        var warnings = issues.Count(issue => issue.Severity == KeyboardShortcutIssueSeverity.Warning);
        var modified = CurrentShortcutOverrides().Count;
        ShortcutSummaryText.Text = errors > 0 || warnings > 0
            ? $"{modified} modified, {errors} conflicts, {warnings} warnings"
            : $"{modified} modified";
        UpdateShortcutEditor(SelectedShortcutRow);
    }

    private void OnShortcutRecorderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!TryFormatRecordedShortcut(e.Key, out var shortcut))
        {
            return;
        }

        ShortcutRecorderBox.Text = shortcut;
        ShortcutRecorderBox.SelectionStart = ShortcutRecorderBox.Text.Length;
        e.Handled = true;
        ValidateShortcutRecorderText();
    }

    private void OnShortcutRecorderTextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateShortcutRecorderText();
    }

    private void ValidateShortcutRecorderText()
    {
        var row = SelectedShortcutRow;
        if (row?.IsEditable != true)
        {
            ShortcutAddButton.IsEnabled = false;
            return;
        }

        var text = ShortcutRecorderBox.Text.Trim();
        if (text.Length == 0)
        {
            ShortcutRecorderStatusText.Text = "";
            ShortcutAddButton.IsEnabled = false;
            return;
        }

        if (!KeyboardShortcutMap.TryParseShortcut(text, out var gesture, out var error) || gesture is null)
        {
            ShortcutRecorderStatusText.Text = error ?? "Shortcut is not valid.";
            ShortcutAddButton.IsEnabled = false;
            return;
        }

        ShortcutRecorderStatusText.Text = KeyboardShortcutMap.TryGetReservedWindowsWarning(gesture.DisplayText, out var warning)
            ? warning
            : "";
        ShortcutAddButton.IsEnabled = true;
    }

    private void OnAddShortcutClicked(object sender, RoutedEventArgs e)
    {
        var row = SelectedShortcutRow;
        if (row?.IsEditable != true)
        {
            return;
        }

        if (!KeyboardShortcutMap.TryParseShortcut(ShortcutRecorderBox.Text, out var gesture, out var error)
            || gesture is null)
        {
            ShortcutRecorderStatusText.Text = error ?? "Shortcut is not valid.";
            return;
        }

        if (!row.Shortcuts.Contains(gesture.DisplayText, StringComparer.Ordinal))
        {
            row.Shortcuts.Add(gesture.DisplayText);
        }

        ShortcutRecorderBox.Text = "";
        RefreshShortcutList();
        RefreshShortcutValidation();
    }

    private void OnRemoveShortcutClicked(object sender, RoutedEventArgs e)
    {
        var row = SelectedShortcutRow;
        if (row?.IsEditable != true || sender is not Button { Tag: string shortcut })
        {
            return;
        }

        row.Shortcuts.Remove(shortcut);
        RefreshShortcutList();
        RefreshShortcutValidation();
    }

    private void OnResetShortcutClicked(object sender, RoutedEventArgs e)
    {
        var row = SelectedShortcutRow;
        if (row?.IsEditable != true)
        {
            return;
        }

        row.SetShortcuts(row.Definition.DefaultShortcuts);
        RefreshShortcutList();
        RefreshShortcutValidation();
    }

    private void OnClearShortcutClicked(object sender, RoutedEventArgs e)
    {
        var row = SelectedShortcutRow;
        if (row?.IsEditable != true)
        {
            return;
        }

        row.SetShortcuts([]);
        RefreshShortcutList();
        RefreshShortcutValidation();
    }

    private void OnResetAllShortcutsClicked(object sender, RoutedEventArgs e)
    {
        foreach (var row in _shortcutRows.Where(row => row.IsEditable))
        {
            row.SetShortcuts(row.Definition.DefaultShortcuts);
        }

        RefreshShortcutList();
        RefreshShortcutValidation();
    }

    private async void OnImportShortcutsClicked(object sender, RoutedEventArgs e)
    {
        var button = sender as Control;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            if (OwnerHwnd != 0)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHwnd);
            }

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var json = await FileIO.ReadTextAsync(file);
            ApplyShortcutOverrides(KeyboardShortcutExportDocument.FromJson(json));
            ShortcutSummaryText.Text = $"Imported {file.Name}";
        }
        catch (Exception exception)
        {
            ShortcutSummaryText.Text = exception.Message;
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private async void OnExportShortcutsClicked(object sender, RoutedEventArgs e)
    {
        var button = sender as Control;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "sumafile-shortcuts",
            };
            picker.FileTypeChoices.Add("JSON", [".json"]);
            if (OwnerHwnd != 0)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHwnd);
            }

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await FileIO.WriteTextAsync(file, KeyboardShortcutExportDocument.ToJson(CurrentShortcutOverrides()));
            ShortcutSummaryText.Text = $"Exported {file.Name}";
        }
        catch (Exception exception)
        {
            ShortcutSummaryText.Text = exception.Message;
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private static bool TryFormatRecordedShortcut(VirtualKey key, out string shortcut)
    {
        shortcut = "";
        if (IsModifierKey(key))
        {
            return false;
        }

        var keyText = KeyText(key);
        if (string.IsNullOrWhiteSpace(keyText))
        {
            return false;
        }

        var parts = new List<string>();
        if (IsKeyDown(VirtualKey.Control))
        {
            parts.Add("Ctrl");
        }

        if (IsKeyDown(VirtualKey.Menu))
        {
            parts.Add("Alt");
        }

        if (IsKeyDown(VirtualKey.Shift))
        {
            parts.Add("Shift");
        }

        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            parts.Add("Win");
        }

        parts.Add(keyText);
        shortcut = string.Join("+", parts);
        return KeyboardShortcutMap.TryParseShortcut(shortcut, out var gesture, out _)
            && gesture is not null
            && (shortcut = gesture.DisplayText).Length > 0;
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static bool IsModifierKey(VirtualKey key)
    {
        return key == VirtualKey.Control
            || key == VirtualKey.Menu
            || key == VirtualKey.Shift
            || key == VirtualKey.LeftWindows
            || key == VirtualKey.RightWindows;
    }

    private static string KeyText(VirtualKey key)
    {
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            return ((int)key - (int)VirtualKey.Number0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            return key.ToString();
        }

        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            return ((int)key - (int)VirtualKey.NumberPad0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return key switch
        {
            VirtualKey.Back => "Backspace",
            VirtualKey.Escape => "Escape",
            VirtualKey.Enter => "Enter",
            VirtualKey.Space => "Space",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            VirtualKey.Add => "Plus",
            VirtualKey.Subtract => "Minus",
            VirtualKey.Separator => "Comma",
            VirtualKey.Decimal => "Period",
            VirtualKey.Divide => "Divide",
            VirtualKey.Multiply => "Multiply",
            _ => key.ToString(),
        };
    }

    private async void OnClearRecentHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (ClearRecentHistoryAction is null)
        {
            return;
        }

        ClearRecentHistoryButton.IsEnabled = false;
        RecentHistoryStatusText.Visibility = Visibility.Collapsed;
        try
        {
            await ClearRecentHistoryAction().ConfigureAwait(true);
            RecentHistoryStatusText.Text = "Recent history cleared.";
            RecentHistoryStatusText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            RecentHistoryStatusText.Text = "Clear recent history cancelled.";
            RecentHistoryStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            RecentHistoryStatusText.Text = exception.Message;
            RecentHistoryStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            ClearRecentHistoryButton.IsEnabled = true;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        CloseWithResult(ContentDialogResult.Primary);
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        CloseWithResult(ContentDialogResult.None);
    }

    private void CloseWithResult(ContentDialogResult result)
    {
        _result = result;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _resultSource.TrySetResult(_result);
    }
}
