using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class DuplicateCheckerDialog : ContentDialog
{
    public string Directory { get; set; } = string.Empty;
    public ulong MinSizeBytes =>
        IncludeEmptyCheck.IsChecked == true
            ? 0
            : Math.Max(1, (ulong)Math.Max(0, MinSizeInput.Value) * 1024);
    public DuplicateCheckResult? Result { get; set; }
    public string[] PathsToDelete => _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).Select(f => f.Path).ToArray();
    public bool DeleteRequested { get; private set; }
    public bool IsScanning { get; private set; }
    public bool ScanWasCancelled { get; private set; }

    public event EventHandler? ScanCancelled;
    public event EventHandler<string>? PreviewRequested;
    public event EventHandler<string>? OpenRequested;
    public event EventHandler<string>? RevealRequested;

    private ObservableCollection<DuplicateGroupViewModel> _groups = new();

    public DuplicateCheckerDialog()
    {
        InitializeComponent();
        GroupsList.ItemsSource = _groups;
    }

    public void ShowConfiguration()
    {
        IsScanning = false;
        ScanWasCancelled = false;
        DeleteRequested = false;
        Title = "Find Duplicates";
        PrimaryButtonText = "Start Scan";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        DialogRoot.Width = 440;
        DialogRoot.Height = double.NaN;
        PhaseConfig.Visibility = Visibility.Visible;
        PhaseScan.Visibility = Visibility.Collapsed;
        PhaseResults.Visibility = Visibility.Collapsed;
        BindFolderPath();
    }

    public void ShowScanning()
    {
        IsScanning = true;
        Title = "Finding Duplicates";
        PrimaryButtonText = string.Empty;
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        DialogRoot.Width = 440;
        DialogRoot.Height = double.NaN;
        ScanProgress.IsIndeterminate = true;
        ScanProgress.Value = 0;
        ScanStatusText.Text = "Preparing scan";
        ScanCurrentItem.Text = Directory;
        PhaseConfig.Visibility = Visibility.Collapsed;
        PhaseScan.Visibility = Visibility.Visible;
        PhaseResults.Visibility = Visibility.Collapsed;
    }

    public void ShowResults(DuplicateCheckResult result)
    {
        IsScanning = false;
        Result = result;
        Title = "Duplicate Results";
        PrimaryButtonText = string.Empty;
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;
        DialogRoot.Width = 680;
        DialogRoot.Height = 480;
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
            ScanStatusText.Text = string.IsNullOrWhiteSpace(update.Status)
                ? "Scanning files"
                : update.Status;
        }

        if (!string.IsNullOrWhiteSpace(update.CurrentItem))
        {
            ScanCurrentItem.Text = update.CurrentItem;
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
        SummaryReclaimable.Text = FormatSize(reclaimable);
        SummarySelected.Text = FormatSize(selectedSize);

        TrashButton.IsEnabled = selectedSize > 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => BindFolderPath();

    private void BindFolderPath()
    {
        FolderPathText.Text = string.IsNullOrWhiteSpace(Directory) ? "Current folder" : Directory;
        ToolTipService.SetToolTip(FolderPathText, FolderPathText.Text);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (MinSizeInput.Value is double.NaN || MinSizeInput.Value < 0)
        {
            args.Cancel = true;
            MinSizeInput.Value = 1;
        }
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (IsScanning)
        {
            ScanWasCancelled = true;
            ScanCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void TrashButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested = true;
        Hide();
    }

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

    public static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
public class DuplicateGroupViewModel
{
    public ObservableCollection<DuplicateFileViewModel> Files { get; set; } = new();
    private readonly DuplicateCheckerDialog _dialog;
    public long SizeEach { get; }
    
    public long WastedBytes => (Files.Count > 1) ? (Files.Count - 1) * SizeEach : 0;
    
    public string HeaderText => $"{Files.Count} matching files · {DuplicateCheckerDialog.FormatSize(SizeEach)} each · {DuplicateCheckerDialog.FormatSize(WastedBytes)} wasted";

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

    public string SizeAndDate => $"{DuplicateCheckerDialog.FormatSize(Size)} · {Modified:g}";

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
