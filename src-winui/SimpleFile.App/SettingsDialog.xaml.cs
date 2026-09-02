using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using SimpleFile.Core;
using SimpleFile.Ipc;
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

public sealed partial class SettingsDialog : ContentDialog
{
    private const string RepositoryUrl = "https://github.com/conniecombs/SumaFile";
    private FileOperationService? _fileOps;
    private bool _checkedUpdateIsInstallable;
    private readonly List<ShortcutEditorRow> _shortcutRows;

    public SettingsDialog()
    {
        InitializeComponent();
        CategoryList.SelectedIndex = 0;
        UpdateDefaultIconSizeValueText(DefaultIconSize);
        _shortcutRows = KeyboardShortcutMap.Defaults
            .Select(definition => new ShortcutEditorRow(definition))
            .ToList();
        RefreshShortcutList(selectFirst: true);
        RefreshShortcutValidation();
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HideCategoryPanels();

        var selected = CategoryList.SelectedItem as ListViewItem;
        if (selected == null) return;

        switch (selected.Content.ToString())
        {
            case "Appearance": AppearancePanel.Visibility = Visibility.Visible; break;
            case "Navigation": NavigationPanel.Visibility = Visibility.Visible; break;
            case "Behavior": BehaviorPanel.Visibility = Visibility.Visible; break;
            case "Shortcuts": ShortcutsPanel.Visibility = Visibility.Visible; break;
            case "Tools": ToolsPanel.Visibility = Visibility.Visible; break;
            case "Updates": UpdatesPanel.Visibility = Visibility.Visible; break;
            case "About": AboutPanel.Visibility = Visibility.Visible; break;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        ListViewItem? firstVisible = null;
        foreach (var item in CategoryList.Items.OfType<ListViewItem>())
        {
            var label = item.Content?.ToString() ?? "";
            var visible = query.Length == 0 || label.Contains(query, StringComparison.OrdinalIgnoreCase);
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && firstVisible is null)
            {
                firstVisible = item;
            }
        }

        if (CategoryList.SelectedItem is not ListViewItem selected
            || selected.Visibility != Visibility.Visible)
        {
            CategoryList.SelectedItem = firstVisible;
            if (firstVisible is null)
            {
                HideCategoryPanels();
            }
        }
    }

    private void HideCategoryPanels()
    {
        AppearancePanel.Visibility = Visibility.Collapsed;
        NavigationPanel.Visibility = Visibility.Collapsed;
        BehaviorPanel.Visibility = Visibility.Collapsed;
        ShortcutsPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.Visibility = Visibility.Collapsed;
        UpdatesPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
    }

    public string Theme => ((ComboBoxItem?)ThemeComboBox.SelectedItem)?.Tag?.ToString() ?? "System";
    public string DefaultView => ((ComboBoxItem?)DefaultViewComboBox.SelectedItem)?.Tag?.ToString() ?? "details";
    public int DefaultIconSize => UiSettings.NormalizeIconSize((int)Math.Round(DefaultIconSizeSlider.Value));
    public string ColumnPreset => ((ComboBoxItem?)ColumnPresetComboBox.SelectedItem)?.Tag?.ToString() ?? "default";
    public bool ShowHidden => ShowHiddenSwitch.IsOn;
    public bool SidebarVisible => ShowSideMenuSwitch.IsOn;
    public bool ShowQuickAccess => ShowQuickAccessSwitch.IsOn;
    public bool ShowFolderTree => ShowFolderTreeSwitch.IsOn;
    public bool ShowBookmarks => ShowBookmarksSwitch.IsOn;
    public bool ShowRecentLocations => ShowRecentSwitch.IsOn;
    public bool ShowSmartFolders => ShowSmartFoldersSwitch.IsOn;
    public nint OwnerHwnd { get; set; }
    public Func<Task>? ClearRecentHistoryAction { get; set; }

    public void ApplyTo(UiSettings settings)
    {
        settings.Theme = UiSettings.NormalizeTheme(Theme);
        settings.DefaultView = UiSettings.NormalizeDefaultView(DefaultView);
        settings.DefaultIconSize = UiSettings.NormalizeIconSize(DefaultIconSize);
        settings.ColumnPreset = UiSettings.NormalizeColumnPreset(ColumnPreset);
        settings.ShowHidden = ShowHiddenSwitch.IsOn;
        settings.ConfirmDelete = ConfirmDeleteSwitch.IsOn;
        settings.KeepFoldersOnTop = KeepFoldersOnTopSwitch.IsOn;
        settings.StartLocation = UiSettings.NormalizeStartLocation(
            ((ComboBoxItem?)StartLocationComboBox.SelectedItem)?.Tag?.ToString());
        settings.CustomPath = CustomPathBox.Text.Trim();
        settings.OpenInNewTab = OpenInNewTabSwitch.IsOn;
        settings.SidebarVisible = ShowSideMenuSwitch.IsOn;
        settings.ShowQuickAccess = ShowQuickAccessSwitch.IsOn;
        settings.ShowFolderTree = ShowFolderTreeSwitch.IsOn;
        settings.ShowBookmarks = ShowBookmarksSwitch.IsOn;
        settings.ShowRecentLocations = ShowRecentSwitch.IsOn;
        settings.ShowSmartFolders = ShowSmartFoldersSwitch.IsOn;
        settings.EnableGitIntegration = EnableGitSwitch.IsOn;
        settings.ShowFolderSizes = ShowFolderSizesSwitch.IsOn;
        settings.ShortcutOverrides = CurrentShortcutOverrides();
    }

    public async Task LoadSettingsAsync(FileOperationService fileOps, CancellationToken cancellationToken = default)
    {
        _fileOps = fileOps;
        var defaults = UiSettings.CreateDefault();

        SelectTheme(await GetSettingOrDefaultAsync(fileOps, "theme", defaults.Theme, cancellationToken).ConfigureAwait(true));

        SelectDefaultView(await GetSettingOrDefaultAsync(fileOps, "defaultView", defaults.DefaultView, cancellationToken).ConfigureAwait(true));

        SelectDefaultIconSize(await GetSettingOrDefaultAsync(fileOps, "defaultIconSize", defaults.DefaultIconSize.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(true));

        SelectColumnPreset(await GetSettingOrDefaultAsync(fileOps, "columnPreset", defaults.ColumnPreset, cancellationToken).ConfigureAwait(true) ?? defaults.ColumnPreset);

        ShowHiddenSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "showHidden", defaults.ShowHidden, cancellationToken).ConfigureAwait(true);

        ConfirmDeleteSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "confirmDelete", defaults.ConfirmDelete, cancellationToken).ConfigureAwait(true);
        KeepFoldersOnTopSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "keepFoldersOnTop", defaults.KeepFoldersOnTop, cancellationToken).ConfigureAwait(true);

        var startLoc = UiSettings.NormalizeStartLocation(
            await GetSettingOrDefaultAsync(fileOps, "startLocation", defaults.StartLocation, cancellationToken).ConfigureAwait(true));
        StartLocationComboBox.SelectedIndex = startLoc == "custom" ? 2 : (startLoc == "last" ? 1 : 0);

        CustomPathBox.Text = await GetSettingOrDefaultAsync(fileOps, "customPath", defaults.CustomPath, cancellationToken).ConfigureAwait(true) ?? "";

        OpenInNewTabSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "openInNewTab", defaults.OpenInNewTab, cancellationToken).ConfigureAwait(true);
        ShowSideMenuSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.visible", defaults.SidebarVisible, cancellationToken).ConfigureAwait(true);
        ShowQuickAccessSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showQuickAccess", defaults.ShowQuickAccess, cancellationToken).ConfigureAwait(true);
        ShowFolderTreeSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showFolders", defaults.ShowFolderTree, cancellationToken).ConfigureAwait(true);
        ShowBookmarksSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showBookmarks", defaults.ShowBookmarks, cancellationToken).ConfigureAwait(true);
        ShowRecentSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showRecent", defaults.ShowRecentLocations, cancellationToken).ConfigureAwait(true);
        ShowSmartFoldersSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "sidebar.showSmartFolders", defaults.ShowSmartFolders, cancellationToken).ConfigureAwait(true);

        EnableGitSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "enableGitIntegration", defaults.EnableGitIntegration, cancellationToken).ConfigureAwait(true);
        ShowFolderSizesSwitch.IsOn = await ReadBoolSettingAsync(fileOps, "showFolderSizes", defaults.ShowFolderSizes, cancellationToken).ConfigureAwait(true);
        ApplyShortcutOverrides(KeyboardShortcutMap.ReadOverridesJson(
            await GetSettingOrDefaultAsync(fileOps, KeyboardShortcutMap.SettingsKey, "", cancellationToken).ConfigureAwait(true)));

        await CheckRarInstalledAsync(cancellationToken).ConfigureAwait(true);

        await LoadVersionAsync(fileOps, cancellationToken).ConfigureAwait(true);
    }

    private void SelectColumnPreset(string preset)
    {
        var normalized = UiSettings.NormalizeColumnPreset(preset);
        var selected = ColumnPresetComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), normalized, StringComparison.Ordinal));
        ColumnPresetComboBox.SelectedItem = selected ?? ColumnPresetComboBox.Items[0];
    }

    private void SelectDefaultView(string? view)
    {
        var normalized = UiSettings.NormalizeDefaultView(view);
        var selected = DefaultViewComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), normalized, StringComparison.Ordinal));
        DefaultViewComboBox.SelectedItem = selected ?? DefaultViewComboBox.Items[0];
    }

    private void SelectDefaultIconSize(string? iconSize)
    {
        var normalized = UiSettings.NormalizeIconSize(iconSize);
        DefaultIconSizeSlider.Value = normalized;
        UpdateDefaultIconSizeValueText(normalized);
    }

    private void OnDefaultIconSizeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var normalized = UiSettings.NormalizeIconSize((int)Math.Round(e.NewValue));
        if (sender is Slider slider && Math.Abs(slider.Value - normalized) > 0.01)
        {
            slider.Value = normalized;
            return;
        }

        UpdateDefaultIconSizeValueText(normalized);
    }

    private void UpdateDefaultIconSizeValueText(int iconSize)
    {
        DefaultIconSizeValueText.Text = $"{UiSettings.NormalizeIconSize(iconSize)} px";
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

    private void SelectTheme(string? theme)
    {
        ThemeComboBox.SelectedIndex = UiSettings.NormalizeTheme(theme) == "dark" ? 1 : 0;
    }

    private static async Task<string?> GetSettingOrDefaultAsync(
        FileOperationService fileOps,
        string key,
        string? fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return await fileOps.GetSettingAsync(key, cancellationToken).ConfigureAwait(true) ?? fallback;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return fallback;
        }
    }

    private static async Task<bool> ReadBoolSettingAsync(
        FileOperationService fileOps,
        string key,
        bool fallback,
        CancellationToken cancellationToken)
    {
        var value = await GetSettingOrDefaultAsync(fileOps, key, fallback ? "true" : "false", cancellationToken).ConfigureAwait(true);
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
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

    private async Task LoadVersionAsync(FileOperationService fileOps, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await fileOps.GetAppVersionAsync(cancellationToken).ConfigureAwait(true);
            CurrentVersionText.Text = $"Current Version: {version}";
            AboutVersionText.Text = $"Version {version}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CurrentVersionText.Text = "Current Version: unavailable";
            AboutVersionText.Text = "Version unavailable";
            UpdateStatusText.Text = $"Unable to load version: {exception.Message}";
        }
    }

    private async Task CheckRarInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (_fileOps == null) return;
        try
        {
            var installed = await _fileOps.CheckRarInstalledAsync(cancellationToken).ConfigureAwait(true);
            RarStatusText.Text = installed ? "Installed" : "Not installed";
            InstallRarButton.IsEnabled = !installed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RarStatusText.Text = $"Unable to check RAR support: {exception.Message}";
            InstallRarButton.IsEnabled = true;
        }
    }

    private async void OnInstallRarClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        InstallRarButton.IsEnabled = false;
        RarStatusText.Text = "Preparing install...";

        try
        {
            var prepResult = await _fileOps.PrepareRarInstallAsync().ConfigureAwait(true);
            if (prepResult != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Install RAR Support",
                    Content = "This will download and install third-party components to support RAR extraction. Do you agree to their terms?",
                    PrimaryButtonText = "Install",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    RarStatusText.Text = "Installing...";
                    await _fileOps.InstallRarAsync(prepResult.ConfirmationToken).ConfigureAwait(true);
                    await CheckRarInstalledAsync().ConfigureAwait(true);
                }
                else
                {
                    await _fileOps.DiscardRarInstallAsync(prepResult.ConfirmationToken).ConfigureAwait(true);
                    await CheckRarInstalledAsync().ConfigureAwait(true);
                }
            }
            else
            {
                RarStatusText.Text = "Failed to prepare installation.";
                InstallRarButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            RarStatusText.Text = "Installation cancelled.";
            InstallRarButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            RarStatusText.Text = exception.Message;
            InstallRarButton.IsEnabled = true;
        }
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _checkedUpdateIsInstallable = false;
        UpdateStatusText.Text = "Checking...";
        try
        {
            var update = await _fileOps.CheckForUpdateAsync().ConfigureAwait(true);
            if (update != null)
            {
                InstallUpdateButton.Visibility = Visibility.Visible;
                InstallUpdateButton.IsEnabled = true;
                _checkedUpdateIsInstallable = update.Installable;
                InstallUpdateButton.Content = update.Installable ? "Download & Install" : "Open GitHub Releases";
                var version = string.IsNullOrWhiteSpace(update.Version)
                    ? "an update"
                    : update.Version;
                UpdateStatusText.Text = update.Installable
                    ? $"Update available: {version}. The installer metadata is signed and ready to verify before launch."
                    : $"Update available: {version}. This build cannot verify the installer metadata, so download it from GitHub Releases.";
            }
            else
            {
                UpdateStatusText.Text = "No updates available.";
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "Update check cancelled.";
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = exception.Message;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps == null) return;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = _checkedUpdateIsInstallable
            ? "Downloading and verifying installer..."
            : "Opening GitHub Releases...";
        try
        {
            if (_checkedUpdateIsInstallable)
            {
                var progress = new Progress<long[]>(values =>
                {
                    if (values.Length >= 2)
                    {
                        var downloaded = values[0];
                        var total = values[1];
                        UpdateStatusText.Text = total > 0
                            ? $"Downloading update: {downloaded:N0} of {total:N0} bytes"
                            : $"Downloading update: {downloaded:N0} bytes";
                    }
                });
                await _fileOps.InstallUpdateAsync(progress).ConfigureAwait(true);
                UpdateStatusText.Text = "Verified installer launched. SumaFile may close while the update finishes.";
            }
            else
            {
                await _fileOps.OpenExternalUrlAsync(RepositoryUrl + "/releases").ConfigureAwait(true);
                UpdateStatusText.Text = "Download the latest release from the GitHub page.";
            }
        }
        catch (Exception exception)
        {
            InstallUpdateButton.Content = "Open GitHub Releases";
            _checkedUpdateIsInstallable = false;
            UpdateStatusText.Text = "Could not install in-app. Please visit " + RepositoryUrl + "/releases to download the update. " + exception.Message;
        }
        finally
        {
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private async void OnBrowseCustomPath(object sender, RoutedEventArgs e)
    {
        var browseButton = sender as Button;
        if (browseButton is not null)
        {
            browseButton.IsEnabled = false;
        }

        CustomPathStatusText.Visibility = Visibility.Collapsed;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            if (OwnerHwnd != 0)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                CustomPathBox.Text = folder.Path;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CustomPathStatusText.Text = exception.Message;
            CustomPathStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (browseButton is not null)
            {
                browseButton.IsEnabled = true;
            }
        }
    }

    private async void OnGitHubClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOps is null)
        {
            return;
        }

        var link = sender as Control;
        if (link is not null)
        {
            link.IsEnabled = false;
        }

        AboutStatusText.Visibility = Visibility.Collapsed;
        try
        {
            await _fileOps.OpenExternalUrlAsync(RepositoryUrl).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AboutStatusText.Text = exception.Message;
            AboutStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (link is not null)
            {
                link.IsEnabled = true;
            }
        }
    }
}
