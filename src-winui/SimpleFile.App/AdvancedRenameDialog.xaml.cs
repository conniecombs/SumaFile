using System.Collections.ObjectModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Graphics;

namespace SimpleFile.App;

public sealed partial class AdvancedRenameDialog : Window
{
    private const double DefaultOptionsPaneWidth = 470;
    private const double MinimumOptionsPaneWidth = 300;
    private const double MinimumPreviewPaneWidth = 320;
    private const double SplitterWidth = 12;

    private readonly IReadOnlyList<FileEntry> _selectedEntries;
    private readonly string _currentPath;
    private readonly Func<string, CancellationToken, Task<DirectoryListing>> _listDirectoryAsync;
    private readonly ObservableCollection<AdvancedRenamePreviewRowViewModel> _previewRows = [];
    private readonly TaskCompletionSource<ContentDialogResult> _resultSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _previewCts;
    private ContentDialogResult _result = ContentDialogResult.None;
    private bool _paneDragging;
    private double _paneDragStartWidth;
    private double _paneDragStartX;
    private int _previewVersion;
    private bool _loaded;

    public AdvancedRenameDialog(
        IReadOnlyList<FileEntry> selectedEntries,
        string currentPath,
        Func<string, CancellationToken, Task<DirectoryListing>> listDirectoryAsync)
    {
        _selectedEntries = selectedEntries;
        _currentPath = currentPath;
        _listDirectoryAsync = listDirectoryAsync;
        InitializeComponent();
        Title = "Advanced Rename";
        AppIcon.ApplyTo(this);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1120, 740));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        PreviewList.ItemsSource = _previewRows;
        RenameButton.IsEnabled = false;
        Closed += OnClosed;
    }

    public IReadOnlyList<AdvancedRenamePreviewRow> AllRows { get; private set; } = [];
    public IReadOnlyList<AdvancedRenamePreviewRow> ChangedRows { get; private set; } = [];
    public RenameRequest[] RenameRequests { get; private set; } = [];

    public Task<ContentDialogResult> ShowAsync()
    {
        Activate();
        return _resultSource.Task;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        SetOptionsPaneWidth(DefaultOptionsPaneWidth);
        ApplyDefaults();
        UpdateSectionVisibility();
        QueuePreviewRefresh();
    }

    private void ApplyDefaults()
    {
        SelectCombo(ApplyPartCombo, "full");
        SelectCombo(TrimModeCombo, "both");
        SelectCombo(AddPositionCombo, "prefix");
        SelectCombo(CapitalizeModeCombo, "first");
        SelectCombo(SeparatorModeCombo, "spaces-to-dashes");
        SelectCombo(NumberPositionCombo, "suffix");
        SelectCombo(ExtensionModeCombo, "lower");
        TemplatePatternBox.Text = string.IsNullOrWhiteSpace(TemplatePatternBox.Text) ? "{base}_{n}" : TemplatePatternBox.Text;
        NumberSeparatorBox.Text = string.IsNullOrEmpty(NumberSeparatorBox.Text) ? "_" : NumberSeparatorBox.Text;
        SanitizeReplacementBox.Text = string.IsNullOrEmpty(SanitizeReplacementBox.Text) ? "_" : SanitizeReplacementBox.Text;
    }

    private static void SelectCombo(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (RenameRequests.Length == 0)
        {
            SummaryText.Text = "No names would change.";
            RenameButton.IsEnabled = false;
            return;
        }

        if (AllRows.Any(row => row.Error is not null))
        {
            SummaryText.Text = "Resolve invalid rename targets before applying.";
            RenameButton.IsEnabled = false;
            return;
        }

        CloseWithResult(ContentDialogResult.Primary);
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        CloseWithResult(ContentDialogResult.None);
    }

    private void OnPlanChanged(object sender, RoutedEventArgs e)
    {
        UpdateSectionVisibility();
        QueuePreviewRefresh();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSectionVisibility();
        QueuePreviewRefresh();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        QueuePreviewRefresh();
    }

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        QueuePreviewRefresh();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_paneDragging)
        {
            ClampOptionsPaneWidth();
        }
    }

    private void OnPaneDividerPressed(object sender, PointerRoutedEventArgs e)
    {
        _paneDragging = true;
        _paneDragStartX = e.GetCurrentPoint(DialogRoot).Position.X;
        _paneDragStartWidth = OptionsPaneColumn.ActualWidth > 0
            ? OptionsPaneColumn.ActualWidth
            : DefaultOptionsPaneWidth;
        RenamePaneDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPaneDividerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_paneDragging)
        {
            return;
        }

        var delta = e.GetCurrentPoint(DialogRoot).Position.X - _paneDragStartX;
        SetOptionsPaneWidth(_paneDragStartWidth + delta);
        e.Handled = true;
    }

    private void OnPaneDividerReleased(object sender, PointerRoutedEventArgs e)
    {
        _paneDragging = false;
        RenamePaneDivider.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPaneDividerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetOptionsPaneWidth(DefaultOptionsPaneWidth);
        e.Handled = true;
    }

    private void UpdateSectionVisibility()
    {
        if (!_loaded)
        {
            return;
        }

        FilterBody.Visibility = VisibleWhen(FilterEnabledCheck.IsChecked == true);
        TemplateBody.Visibility = VisibleWhen(TemplateEnabledCheck.IsChecked == true);
        RemoveBody.Visibility = VisibleWhen(RemoveEnabledCheck.IsChecked == true);
        ReplaceBody.Visibility = VisibleWhen(ReplaceEnabledCheck.IsChecked == true);
        TrimBody.Visibility = VisibleWhen(TrimEnabledCheck.IsChecked == true);
        AddBody.Visibility = VisibleWhen(AddEnabledCheck.IsChecked == true);
        CapitalizeBody.Visibility = VisibleWhen(CapitalizeEnabledCheck.IsChecked == true);
        SeparatorBody.Visibility = VisibleWhen(SeparatorEnabledCheck.IsChecked == true);
        NumberBody.Visibility = VisibleWhen(NumberEnabledCheck.IsChecked == true);
        ExtensionBody.Visibility = VisibleWhen(ExtensionEnabledCheck.IsChecked == true);
        SanitizeBody.Visibility = VisibleWhen(SanitizeEnabledCheck.IsChecked == true);
    }

    private static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void QueuePreviewRefresh()
    {
        if (!_loaded)
        {
            return;
        }

        var version = ++_previewVersion;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        SummaryText.Text = "Building preview...";
        PreviewCountText.Text = "";
        RenameButton.IsEnabled = false;
        RenameRequests = [];
        ChangedRows = [];

        try
        {
            await Task.Delay(120, token).ConfigureAwait(true);
            var plan = ReadPlan();
            var targets = await AdvancedRename.CollectTargetsAsync(
                    _selectedEntries,
                    _currentPath,
                    plan,
                    _listDirectoryAsync,
                    token)
                .ConfigureAwait(true);
            var preview = AdvancedRename.BuildPreview(targets.ToList(), plan);
            if (version != _previewVersion || token.IsCancellationRequested)
            {
                return;
            }

            ApplyPreview(preview);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version == _previewVersion)
            {
                ApplyPreview(new AdvancedRenamePreview
                {
                    Message = exception.Message,
                    Mode = "error",
                });
            }
        }
    }

    private AdvancedRenamePlan ReadPlan()
    {
        return new AdvancedRenamePlan
        {
            AddEnabled = AddEnabledCheck.IsChecked == true,
            AddIndex = NumberValue(AddIndexBox, 1),
            AddPosition = ComboTag(AddPositionCombo, "prefix"),
            AddString = AddStringBox.Text ?? "",
            ApplyPart = ComboTag(ApplyPartCombo, "full"),
            CapitalizeEnabled = CapitalizeEnabledCheck.IsChecked == true,
            CapitalizeMode = ComboTag(CapitalizeModeCombo, "first"),
            ExtensionCustom = ExtensionCustomBox.Text ?? "",
            ExtensionEnabled = ExtensionEnabledCheck.IsChecked == true,
            ExtensionMode = ComboTag(ExtensionModeCombo, "lower"),
            FilterCase = FilterCaseCheck.IsChecked == true,
            FilterEnabled = FilterEnabledCheck.IsChecked == true,
            FilterExtensions = FilterExtensionsBox.Text ?? "",
            FilterInvert = FilterInvertCheck.IsChecked == true,
            FilterRegex = FilterRegexCheck.IsChecked == true,
            FilterText = FilterTextBox.Text ?? "",
            NumberEnabled = NumberEnabledCheck.IsChecked == true,
            NumberPad = Math.Max(1, NumberValue(NumberPadBox, 3)),
            NumberPosition = ComboTag(NumberPositionCombo, "suffix"),
            NumberSeparator = NumberSeparatorBox.Text ?? "",
            NumberStart = NumberValue(NumberStartBox, 1),
            NumberStep = Math.Max(1, NumberValue(NumberStepBox, 1)),
            RemoveCase = RemoveCaseCheck.IsChecked == true,
            RemoveEnabled = RemoveEnabledCheck.IsChecked == true,
            RemoveRegex = RemoveRegexCheck.IsChecked == true,
            RemoveString = RemoveStringBox.Text ?? "",
            ReplaceCase = ReplaceCaseCheck.IsChecked == true,
            ReplaceEnabled = ReplaceEnabledCheck.IsChecked == true,
            ReplaceFind = ReplaceFindBox.Text ?? "",
            ReplaceRegex = ReplaceRegexCheck.IsChecked == true,
            ReplaceWith = ReplaceWithBox.Text ?? "",
            SanitizeEnabled = SanitizeEnabledCheck.IsChecked == true,
            SanitizeReplacement = SanitizeReplacementBox.Text ?? "_",
            ScopeHidden = ScopeHiddenCheck.IsChecked == true,
            ScopeRecursive = ScopeRecursiveCheck.IsChecked == true,
            SeparatorCollapse = SeparatorCollapseCheck.IsChecked == true,
            SeparatorEnabled = SeparatorEnabledCheck.IsChecked == true,
            SeparatorMode = ComboTag(SeparatorModeCombo, "spaces-to-dashes"),
            TemplateEnabled = TemplateEnabledCheck.IsChecked == true,
            TemplateKeepExt = TemplateKeepExtCheck.IsChecked == true,
            TemplatePattern = TemplatePatternBox.Text ?? "{base}_{n}",
            TrimCollapse = TrimCollapseCheck.IsChecked == true,
            TrimEnabled = TrimEnabledCheck.IsChecked == true,
            TrimMode = ComboTag(TrimModeCombo, "both"),
        };
    }

    private static string ComboTag(ComboBox combo, string fallback)
    {
        return combo.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;
    }

    private static int NumberValue(NumberBox numberBox, int fallback)
    {
        return double.IsNaN(numberBox.Value)
            ? fallback
            : (int)Math.Round(numberBox.Value);
    }

    private void ApplyPreview(AdvancedRenamePreview preview)
    {
        _previewRows.Clear();
        AllRows = preview.AllRows;
        ChangedRows = preview.AllRows.Where(row => row.Changed && row.Error is null).ToArray();
        RenameRequests = AdvancedRename.BuildRequests(preview.AllRows);

        if (preview.Mode == "error")
        {
            SummaryText.Text = $"Preview failed. {preview.Message}";
            PreviewCountText.Text = "";
            RenameButton.IsEnabled = false;
            return;
        }

        foreach (var row in preview.Rows)
        {
            _previewRows.Add(new AdvancedRenamePreviewRowViewModel(row));
        }

        if (preview.Mode == "empty")
        {
            SummaryText.Text = preview.Message;
            PreviewCountText.Text = "";
        }
        else
        {
            var targetText = preview.TotalRows == 1 ? "1 target" : $"{preview.TotalRows} targets";
            var changedText = preview.ChangedCount == 1 ? "1 rename" : $"{preview.ChangedCount} renames";
            var invalidText = preview.InvalidCount > 0
                ? $" · {preview.InvalidCount} invalid"
                : "";
            SummaryText.Text = $"{targetText} ready · {changedText}{invalidText}.";
            PreviewCountText.Text = preview.ExtraCount > 0
                ? $"Showing {preview.Rows.Count} of {preview.TotalRows}"
                : $"{preview.TotalRows}";
        }

        RenameButton.IsEnabled = preview.Mode == "rows"
            && preview.InvalidCount == 0
            && RenameRequests.Length > 0;
    }

    private void ClampOptionsPaneWidth()
    {
        var currentWidth = OptionsPaneColumn.ActualWidth > 0
            ? OptionsPaneColumn.ActualWidth
            : DefaultOptionsPaneWidth;
        SetOptionsPaneWidth(currentWidth);
    }

    private void SetOptionsPaneWidth(double width)
    {
        var hostWidth = PaneGrid.ActualWidth;
        if (hostWidth <= 0)
        {
            OptionsPaneColumn.Width = new GridLength(width);
            return;
        }

        var maxOptionsWidth = Math.Max(
            MinimumOptionsPaneWidth,
            hostWidth - MinimumPreviewPaneWidth - SplitterWidth);
        OptionsPaneColumn.Width = new GridLength(Math.Clamp(width, MinimumOptionsPaneWidth, maxOptionsWidth));
    }

    private void CloseWithResult(ContentDialogResult result)
    {
        _result = result;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        _resultSource.TrySetResult(_result);
    }

    private sealed class AdvancedRenamePreviewRowViewModel
    {
        public AdvancedRenamePreviewRowViewModel(AdvancedRenamePreviewRow row)
        {
            OldName = row.OldName;
            NewName = row.NewName;
            DetailText = row.Error ?? row.Detail;
        }

        public string DetailText { get; }
        public string NewName { get; }
        public string OldName { get; }
    }
}
