using System.Linq;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class ArchiveViewerDialog : ContentDialog
{
    private ArchiveInfo? _archiveData;

    public ArchiveInfo? ArchiveData
    {
        get => _archiveData;
        set
        {
            _archiveData = value;
            UpdateUi();
        }
    }

    public bool ExtractRequested { get; private set; }

    public ArchiveViewerDialog()
    {
        InitializeComponent();
        
        PrimaryButtonClick += (_, _) => ExtractRequested = true;
    }

    private void UpdateUi()
    {
        if (_archiveData == null) return;

        ArchiveNameText.Text = System.IO.Path.GetFileName(_archiveData.Path);
        FormatBadgeText.Text = _archiveData.Format.ToUpperInvariant();

        EntryCountText.Text = _archiveData.Entries.Count.ToString();
        TotalSizeText.Text = FormatSize((long)_archiveData.TotalSize);
        CompressedSizeText.Text = FormatSize((long)_archiveData.CompressedSize);

        if (_archiveData.UnsafeEntries.Count > 0)
        {
            WarningBar.IsOpen = true;
            WarningBar.Message = $"Warning: This archive contains {_archiveData.UnsafeEntries.Count} unsafe entries that may extract outside the target directory.";
        }
        else
        {
            WarningBar.IsOpen = false;
        }

        EntriesList.ItemsSource = _archiveData.Entries.Select(e => new ArchiveEntryViewModel(e)).ToList();
    }

    private string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }
}

public class ArchiveEntryViewModel
{
    public string Icon { get; }
    public string Name { get; }
    public string SizeFormatted { get; }

    public ArchiveEntryViewModel(ArchiveEntry entry)
    {
        Icon = entry.IsDir ? "📁" : "📄";
        Name = entry.Name;
        SizeFormatted = FormatSize((long)entry.Size);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }
}
