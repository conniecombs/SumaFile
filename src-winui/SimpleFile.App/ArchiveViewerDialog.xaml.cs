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
        TotalSizeText.Text = EntryPresentation.FormatFileSize(_archiveData.TotalSize);
        CompressedSizeText.Text = EntryPresentation.FormatFileSize(_archiveData.CompressedSize);

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
        SizeFormatted = EntryPresentation.FormatFileSize(entry.Size);
    }
}
