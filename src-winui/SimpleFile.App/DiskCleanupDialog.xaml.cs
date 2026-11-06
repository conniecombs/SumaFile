using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class DiskCleanupDialog : ContentDialog, IScanDialog<CleanupResult>
{
    private const int LargeFileLimit = 50;
    private const int DuplicateGroupLimit = 25;

    public string Directory { get; set; } = string.Empty;
    public ulong ThresholdBytes => ToThresholdBytes(ThresholdInput.Value);
    public bool AnalyzeRequested { get; private set; }

    /// <summary>Set when a row asked to move its file to the Recycle Bin; the host confirms and trashes after the dialog closes.</summary>
    public bool TrashRequested { get; private set; }
    public string[] PathsToTrash { get; private set; } = [];

    /// <summary>Set when the user chose to open the Duplicate Finder for this folder instead.</summary>
    public bool DuplicateFinderRequested { get; private set; }
    public bool IsScanning { get; private set; }
    public bool ScanWasCancelled { get; private set; }

    public event EventHandler? ScanCancelled;
    public event EventHandler<string>? RevealRequested;

    public ObservableCollection<LargeFileViewModel> LargeFiles { get; } = new();
    public ObservableCollection<CleanupDuplicateGroupViewModel> Duplicates { get; } = new();

    public DiskCleanupDialog()
    {
        InitializeComponent();
        LargeFilesList.ItemsSource = LargeFiles;
        DuplicatesList.ItemsSource = Duplicates;
    }

    public void ShowConfiguration()
    {
        IsScanning = false;
        ScanWasCancelled = false;
        AnalyzeRequested = false;
        TrashRequested = false;
        PathsToTrash = [];
        DuplicateFinderRequested = false;
        Title = "Large files and duplicates";
        PrimaryButtonText = "Analyze";
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
        Title = "Analyzing folder";
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

    public void ShowResults(CleanupResult result)
    {
        IsScanning = false;
        Title = "Results";
        PrimaryButtonText = string.Empty;
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;
        DialogRoot.Width = 620;
        DialogRoot.Height = 500;

        LargeFiles.Clear();
        foreach (var file in result.LargeFiles.Take(LargeFileLimit))
        {
            LargeFiles.Add(new LargeFileViewModel(file));
        }

        Duplicates.Clear();
        foreach (var group in result.Duplicates.Take(DuplicateGroupLimit))
        {
            Duplicates.Add(new CleanupDuplicateGroupViewModel(group));
        }

        SummaryLargeFiles.Text = result.LargeFiles.Count.ToString("N0");
        SummaryDuplicates.Text = result.Duplicates.Count.ToString("N0");
        SummaryScanned.Text = result.ScannedFiles.ToString("N0");

        SetFooter(LargeFilesFooter, LargeFiles.Count, result.LargeFiles.Count, "large files");
        SetFooter(DuplicatesFooter, Duplicates.Count, result.Duplicates.Count, "duplicate groups");

        LargeFilesEmpty.Visibility = LargeFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DuplicatesEmpty.Visibility = Duplicates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LargeFilesList.Visibility = LargeFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DuplicatesList.Visibility = Duplicates.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        PhaseConfig.Visibility = Visibility.Collapsed;
        PhaseScan.Visibility = Visibility.Collapsed;
        PhaseResults.Visibility = Visibility.Visible;
    }

    public void UpdateProgress(ProgressUpdate update)
    {
        if (update.Total > 0)
        {
            ScanProgress.IsIndeterminate = false;
            ScanProgress.Maximum = update.Total;
            ScanProgress.Value = Math.Min(update.Current, update.Total);
            ScanStatusText.Text = $"{update.Current:N0} of {update.Total:N0} files";
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

    public async Task<ContentDialogResult> ShowScanHostAsync() => await ShowAsync();

    public void CloseScanHost() => Hide();

    private void OnLoaded(object sender, RoutedEventArgs e) => BindFolderPath();

    private void BindFolderPath()
    {
        FolderPathText.Text = string.IsNullOrWhiteSpace(Directory) ? "Current folder" : Directory;
        ToolTipService.SetToolTip(FolderPathText, FolderPathText.Text);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ThresholdInput.Value is double.NaN || ThresholdInput.Value < 1)
        {
            args.Cancel = true;
            ThresholdInput.Value = 100;
            return;
        }

        AnalyzeRequested = true;
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (IsScanning)
        {
            ScanWasCancelled = true;
            ScanCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>"Showing first 50 of 312 large files" when the list is truncated.</summary>
    internal static string TruncationFooter(int shown, int total, string noun) =>
        total > shown ? $"Showing first {shown:N0} of {total:N0} {noun}" : string.Empty;

    private static void SetFooter(TextBlock footer, int shown, int total, string noun)
    {
        footer.Text = TruncationFooter(shown, total, noun);
        footer.Visibility = footer.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string? RowPath(object sender) => (sender as FrameworkElement)?.Tag switch
    {
        LargeFileViewModel file => file.Path,
        string path when path.Length > 0 => path,
        _ => null,
    };

    private void OnRevealClicked(object sender, RoutedEventArgs e)
    {
        if (RowPath(sender) is { } path)
        {
            RevealRequested?.Invoke(this, path);
        }
    }

    // Trashing needs a confirmation dialog, which cannot open over this one: close first,
    // and the host trashes through the workspace (undoable, panes refresh).
    private void OnRecycleClicked(object sender, RoutedEventArgs e)
    {
        if (RowPath(sender) is { } path)
        {
            PathsToTrash = [path];
            TrashRequested = true;
            Hide();
        }
    }

    private void OnOpenDuplicateFinderClicked(object sender, RoutedEventArgs e)
    {
        DuplicateFinderRequested = true;
        Hide();
    }

    private static ulong ToThresholdBytes(double megabytes)
    {
        if (double.IsNaN(megabytes) || megabytes < 1)
        {
            megabytes = 100;
        }

        return (ulong)(megabytes * 1024 * 1024);
    }

}

public sealed class LargeFileViewModel
{
    public string Path { get; }
    public string Name { get; }
    public string Directory { get; }
    public ulong Size { get; }
    public string FormattedSize => EntryPresentation.FormatCompactFileSize(Size);

    public LargeFileViewModel(CleanupFile file)
    {
        Path = file.Path;
        Name = System.IO.Path.GetFileName(file.Path);
        if (string.IsNullOrEmpty(Name))
        {
            Name = file.Path;
        }

        Directory = System.IO.Path.GetDirectoryName(file.Path) ?? string.Empty;
        Size = file.Size;
    }
}

public sealed class CleanupDuplicateGroupViewModel
{
    public string HashPrefix { get; }
    public int FileCount { get; }
    public string[] Paths { get; }

    public string HeaderText => FileCount == 1
        ? "1 matching file"
        : $"{FileCount} duplicate files";

    public string HashText => HashPrefix.Length == 0
        ? string.Empty
        : $"SHA-256 {HashPrefix}…";

    public CleanupDuplicateGroupViewModel(DuplicateGroup group)
    {
        HashPrefix = string.IsNullOrEmpty(group.Hash)
            ? string.Empty
            : group.Hash[..Math.Min(16, group.Hash.Length)];
        FileCount = group.Files.Count;
        Paths = [.. group.Files];
    }
}
