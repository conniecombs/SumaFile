using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class DiskCleanupDialog : ContentDialog
{
    private const int LargeFileLimit = 50;
    private const int DuplicateGroupLimit = 25;

    public string Directory { get; set; } = string.Empty;
    public ulong ThresholdBytes => ToThresholdBytes(ThresholdInput.Value);
    public bool AnalyzeRequested { get; private set; }
    public bool IsScanning { get; private set; }
    public bool ScanWasCancelled { get; private set; }

    public event EventHandler? ScanCancelled;

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
        Title = "Analyze Cleanup";
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
        Title = "Analyzing Cleanup";
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
        Title = "Cleanup Results";
        PrimaryButtonText = string.Empty;
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;
        DialogRoot.Width = 620;
        DialogRoot.Height = 460;

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

    private static ulong ToThresholdBytes(double megabytes)
    {
        if (double.IsNaN(megabytes) || megabytes < 1)
        {
            megabytes = 100;
        }

        return (ulong)(megabytes * 1024 * 1024);
    }

    public static string FormatSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = Math.Max(0, bytes);
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}

public sealed class LargeFileViewModel
{
    public string Path { get; }
    public string Name { get; }
    public string Directory { get; }
    public ulong Size { get; }
    public string FormattedSize => DiskCleanupDialog.FormatSize((long)Size);

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
