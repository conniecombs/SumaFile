using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;
using SimpleFile.Ipc;
using Windows.Graphics;

namespace SimpleFile.App;

public sealed partial class DuplicateCheckerDialog : Window, IScanDialog<DuplicateCheckResult>
{
    private const double LocalMinSizeKb = 1;
    private const double LocalPartialHashKb = 1024;
    private const double NetworkMinSizeKb = 64;
    private const double NetworkPartialHashKb = 256;
    private const double MaxPartialHashKb = 16384;

    private readonly ObservableCollection<DuplicateGroupViewModel> _groups = new();
    private TaskCompletionSource<ContentDialogResult>? _resultSource;
    private ContentDialogResult _result = ContentDialogResult.None;
    private bool _closed;

    public string Directory { get; set; } = string.Empty;

    public ulong MinSizeBytes =>
        IncludeEmptyCheck.IsChecked == true
            ? 0
            : (ulong)Math.Max(1, NormalizeNumber(MinSizeInput.Value, LocalMinSizeKb)) * 1024;

    public ulong PartialHashBytes =>
        (ulong)Math.Clamp(
            NormalizeNumber(PartialHashInput.Value, LocalPartialHashKb),
            4,
            MaxPartialHashKb) * 1024;

    public int? MaxDepth =>
        LimitDepthCheck.IsChecked == true
            ? (int)Math.Max(0, NormalizeNumber(MaxDepthInput.Value, 4))
            : null;

    public string[] ExcludePatterns => ParseExcludePatterns(ExcludePatternsTextBox.Text);

    public bool? NetworkMode
    {
        get
        {
            var tag = (NetworkModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return tag switch
            {
                "On" => true,
                "Off" => false,
                _ => null,
            };
        }
    }

    public DuplicateScanOptions ScanOptions => new()
    {
        MinSize = MinSizeBytes,
        PartialHashBytes = PartialHashBytes,
        MaxDepth = MaxDepth,
        ExcludePatterns = ExcludePatterns,
        NetworkMode = NetworkMode,
    };

    public DuplicateCheckResult? Result { get; set; }
    public string[] PathsToDelete => _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).Select(f => f.Path).ToArray();
    public bool DeleteRequested { get; private set; }
    public bool IsScanning { get; private set; }
    public bool ScanWasCancelled { get; private set; }

    public event EventHandler? ScanCancelled;
    public event EventHandler<string>? PreviewRequested;
    public event EventHandler<string>? OpenRequested;
    public event EventHandler<string>? RevealRequested;

    public DuplicateCheckerDialog()
    {
        InitializeComponent();
        AppIcon.ApplyTo(this);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(900, 640));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        Closed += OnWindowClosed;
        GroupsList.ItemsSource = _groups;
        NetworkModeBox.SelectedIndex = 0;
        LimitDepthCheck_Changed(this, new RoutedEventArgs());
    }

    public void ConfigureForPath(string directory, bool isNetwork)
    {
        Directory = directory;
        MinSizeInput.Value = isNetwork ? NetworkMinSizeKb : LocalMinSizeKb;
        PartialHashInput.Value = isNetwork ? NetworkPartialHashKb : LocalPartialHashKb;
        ExcludePatternsTextBox.Text = isNetwork
            ? "@eaDir; #recycle; .@__thumb"
            : string.Empty;
        NetworkModeBox.SelectedIndex = 0;
        BindFolderPath();
    }

    public void ShowConfiguration()
    {
        IsScanning = false;
        ScanWasCancelled = false;
        DeleteRequested = false;
        Title = "Find Duplicates";
        WindowTitleText.Text = "Find Duplicates";
        PhaseConfig.Visibility = Visibility.Visible;
        PhaseScan.Visibility = Visibility.Collapsed;
        PhaseResults.Visibility = Visibility.Collapsed;
        StartScanButton.IsEnabled = true;
        BindFolderPath();
    }

    public void ShowScanning()
    {
        IsScanning = true;
        Title = "Finding Duplicates";
        WindowTitleText.Text = "Finding Duplicates";
        ScanProgress.IsIndeterminate = true;
        ScanProgress.Value = 0;
        ScanStatusText.Text = "Preparing scan";
        ScanCurrentItem.Text = Directory;
        CancelScanButton.IsEnabled = true;
        PhaseConfig.Visibility = Visibility.Collapsed;
        PhaseScan.Visibility = Visibility.Visible;
        PhaseResults.Visibility = Visibility.Collapsed;
    }

    public void ShowResults(DuplicateCheckResult result)
    {
        IsScanning = false;
        Result = result;
        Title = "Duplicate Results";
        WindowTitleText.Text = "Duplicate Results";
        PhaseConfig.Visibility = Visibility.Collapsed;
        PhaseScan.Visibility = Visibility.Collapsed;
        PhaseResults.Visibility = Visibility.Visible;
        LoadResult(result);
    }

    public void UpdateProgress(ProgressUpdate update)
    {
        if (update.Total > 0)
        {
            ScanProgress.IsIndeterminate = false;
            ScanProgress.Maximum = update.Total;
            ScanProgress.Value = Math.Min(update.Current, update.Total);
            ScanStatusText.Text = $"{update.Current:N0} of {update.Total:N0}";
        }
        else
        {
            ScanProgress.IsIndeterminate = true;
            ScanStatusText.Text = update.Current > 0
                ? $"{update.Current:N0} files discovered"
                : string.IsNullOrWhiteSpace(update.Status)
                    ? "Scanning files"
                    : update.Status;
        }

        if (!string.IsNullOrWhiteSpace(update.CurrentItem))
        {
            ScanCurrentItem.Text = update.CurrentItem;
        }
    }

    public Task<ContentDialogResult> ShowScanHostAsync()
    {
        if (_closed)
        {
            return Task.FromResult(_result);
        }

        _resultSource = new TaskCompletionSource<ContentDialogResult>();
        Activate();
        return _resultSource.Task;
    }

    public void CloseScanHost()
    {
        if (!_closed)
        {
            Close();
        }
    }

    public void RemovePaths(string[] deletedPaths)
    {
        var deletedSet = new HashSet<string>(deletedPaths);
        var toRemoveGroups = new List<DuplicateGroupViewModel>();

        foreach (var group in _groups)
        {
            var remainingFiles = group.Files.Where(f => !deletedSet.Contains(f.Path)).ToList();
            if (remainingFiles.Count <= 1)
            {
                toRemoveGroups.Add(group);
            }
            else
            {
                group.Files.Clear();
                foreach (var f in remainingFiles) group.Files.Add(f);
                group.UpdateCanSelect();
            }
        }

        foreach (var g in toRemoveGroups)
        {
            _groups.Remove(g);
        }

        UpdateSummary();
    }

    private void LoadResult(DuplicateCheckResult result)
    {
        _groups.Clear();
        foreach (var group in result.Groups)
        {
            var gvm = new DuplicateGroupViewModel(group, this);
            _groups.Add(gvm);
        }
        UpdateSummary();
    }

    internal void UpdateSummary()
    {
        int groupsCount = _groups.Count;
        int filesCount = _groups.Sum(g => g.Files.Count);
        long reclaimable = _groups.Sum(g => g.WastedBytes);
        long selectedSize = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).Sum(f => f.Size);

        SummaryGroups.Text = groupsCount.ToString();
        SummaryFiles.Text = filesCount.ToString();
        SummaryReclaimable.Text = EntryPresentation.FormatCompactFileSize(reclaimable);
        SummarySelected.Text = EntryPresentation.FormatCompactFileSize(selectedSize);

        TrashButton.IsEnabled = selectedSize > 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => BindFolderPath();

    private void BindFolderPath()
    {
        FolderPathText.Text = string.IsNullOrWhiteSpace(Directory) ? "Current folder" : Directory;
        ToolTipService.SetToolTip(FolderPathText, FolderPathText.Text);
    }

    private void StartScanButton_Click(object sender, RoutedEventArgs e)
    {
        NormalizeConfigurationInputs();
        _result = ContentDialogResult.Primary;
        StartScanButton.IsEnabled = false;
        _resultSource?.TrySetResult(_result);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseWithResult(ContentDialogResult.None);

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        RequestScanCancel();
        CloseWithResult(ContentDialogResult.None);
    }

    private void LimitDepthCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (MaxDepthInput is not null)
        {
            MaxDepthInput.IsEnabled = LimitDepthCheck?.IsChecked == true;
        }
    }

    private void TrashButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested = true;
        CloseWithResult(ContentDialogResult.Primary);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        if (IsScanning)
        {
            RequestScanCancel();
        }

        _resultSource?.TrySetResult(_result);
    }

    private void RequestScanCancel()
    {
        if (ScanWasCancelled)
        {
            return;
        }

        ScanWasCancelled = true;
        CancelScanButton.IsEnabled = false;
        ScanStatusText.Text = "Cancelling scan";
        ScanCancelled?.Invoke(this, EventArgs.Empty);
    }

    private void CloseWithResult(ContentDialogResult result)
    {
        _result = result;
        _resultSource?.TrySetResult(result);
        CloseScanHost();
    }

    private void NormalizeConfigurationInputs()
    {
        if (double.IsNaN(MinSizeInput.Value) || MinSizeInput.Value < 0)
        {
            MinSizeInput.Value = LocalMinSizeKb;
        }

        if (double.IsNaN(PartialHashInput.Value) || PartialHashInput.Value < 4)
        {
            PartialHashInput.Value = NetworkMode == true ? NetworkPartialHashKb : LocalPartialHashKb;
        }

        if (PartialHashInput.Value > MaxPartialHashKb)
        {
            PartialHashInput.Value = MaxPartialHashKb;
        }

        if (LimitDepthCheck.IsChecked == true
            && (double.IsNaN(MaxDepthInput.Value) || MaxDepthInput.Value < 0))
        {
            MaxDepthInput.Value = 4;
        }
    }

    private static double NormalizeNumber(double value, double fallback)
        => double.IsNaN(value) ? fallback : Math.Floor(value);

    private static string[] ParseExcludePatterns(string text)
        => text.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pattern => pattern.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void GlobalKeepNewest_Click(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups) g.KeepNewest();
        UpdateSummary();
    }

    private void GlobalKeepFirst_Click(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups) g.KeepFirst();
        UpdateSummary();
    }

    private void GlobalClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups) g.ClearSelection();
        UpdateSummary();
    }

    private void GroupKeepNewest_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DuplicateGroupViewModel g)
        {
            g.KeepNewest();
            UpdateSummary();
        }
    }

    private void GroupKeepFirst_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DuplicateGroupViewModel g)
        {
            g.KeepFirst();
            UpdateSummary();
        }
    }

    private void GroupClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DuplicateGroupViewModel g)
        {
            g.ClearSelection();
            UpdateSummary();
        }
    }

    private void FileCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DuplicateFileViewModel f)
        {
            f.Group.UpdateCanSelect();
            UpdateSummary();
        }
    }

    private void PreviewFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DuplicateFileViewModel file)
        {
            PreviewRequested?.Invoke(this, file.Path);
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DuplicateFileViewModel file)
        {
            OpenRequested?.Invoke(this, file.Path);
        }
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DuplicateFileViewModel file)
        {
            RevealRequested?.Invoke(this, file.Path);
        }
    }
}

public class DuplicateGroupViewModel
{
    public ObservableCollection<DuplicateFileViewModel> Files { get; set; } = new();
    private readonly DuplicateCheckerDialog _dialog;
    public long SizeEach { get; }

    public long WastedBytes => (Files.Count > 1) ? (Files.Count - 1) * SizeEach : 0;

    public string HeaderText => $"{Files.Count} matching files · {EntryPresentation.FormatCompactFileSize(SizeEach)} each · {EntryPresentation.FormatCompactFileSize(WastedBytes)} wasted";

    public DuplicateGroupViewModel(DuplicateCheckGroup group, DuplicateCheckerDialog dialog)
    {
        _dialog = dialog;
        SizeEach = group.Files.FirstOrDefault() != null ? (long)group.Files.First().Size : 0;
        foreach (var f in group.Files)
        {
            Files.Add(new DuplicateFileViewModel(f, this));
        }
        UpdateCanSelect();
    }

    public void UpdateCanSelect()
    {
        int unselectedCount = Files.Count(f => !f.IsSelected);
        foreach (var f in Files)
        {
            f.CanSelect = f.IsSelected || unselectedCount > 1;
        }
    }

    public void KeepNewest()
    {
        var newest = Files.OrderByDescending(f => f.Modified).FirstOrDefault();
        if (newest != null)
        {
            foreach (var f in Files) f.SetIsSelected(f != newest);
        }
        UpdateCanSelect();
    }

    public void KeepFirst()
    {
        var first = Files.FirstOrDefault();
        if (first != null)
        {
            foreach (var f in Files) f.SetIsSelected(f != first);
        }
        UpdateCanSelect();
    }

    public void ClearSelection()
    {
        foreach (var f in Files) f.SetIsSelected(false);
        UpdateCanSelect();
    }

}

public class DuplicateFileViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _canSelect = true;

    public DuplicateGroupViewModel Group { get; }
    public string Path { get; }
    public string FileName { get; }
    public long Size { get; }
    public DateTime Modified { get; }

    public string SizeAndDate => $"{EntryPresentation.FormatCompactFileSize(Size)} · {Modified:g}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanSelect
    {
        get => _canSelect;
        set
        {
            if (_canSelect != value)
            {
                _canSelect = value;
                OnPropertyChanged();
            }
        }
    }

    public DuplicateFileViewModel(DuplicateCheckFile file, DuplicateGroupViewModel group)
    {
        Group = group;
        Path = file.Path;
        FileName = file.Name;
        Size = (long)file.Size;
        if (DateTime.TryParse(file.Modified, out DateTime parsed))
            Modified = parsed;
        else
            Modified = DateTime.MinValue;
    }

    public void SetIsSelected(bool value)
    {
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
