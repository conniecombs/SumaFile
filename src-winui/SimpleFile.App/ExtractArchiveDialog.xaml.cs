using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class ExtractArchiveDialog : ContentDialog
{
    private ArchiveInfo? _archiveData;
    private string _destination = "";
    private string _baseDirectory = "";

    public ArchiveInfo? ArchiveData
    {
        get => _archiveData;
        set
        {
            _archiveData = value;
            UpdateUi();
        }
    }

    public string Destination
    {
        get => _destination;
        set
        {
            _destination = value;
            DestinationText.Text = _destination;
        }
    }

    public string SelectedExtractMode { get; private set; } = "here";

    public Func<string?, Task<string?>>? BrowseFolderAsync { get; set; }
    
    public void SetBaseDirectory(string dir)
    {
        _baseDirectory = dir;
        UpdateDestinationPreview();
    }

    public ExtractArchiveDialog()
    {
        InitializeComponent();
    }

    private void UpdateUi()
    {
        if (_archiveData == null) return;

        ArchiveNameText.Text = System.IO.Path.GetFileName(_archiveData.Path);
        
        long safeCount = _archiveData.Entries.Count - _archiveData.UnsafeEntries.Count;
        ContentsText.Text = $"{safeCount} safe entries, {FormatSize((long)_archiveData.TotalSize)} total size";

        if (_archiveData.UnsafeEntries.Count > 0)
        {
            WarningBar.IsOpen = true;
            WarningBar.Message = $"Warning: {_archiveData.UnsafeEntries.Count} entries may extract outside target directory.";
        }
        else
        {
            WarningBar.IsOpen = false;
        }

        RadioSubfolder.Content = $"Extract to {ArchivePaths.ExtractFolderName(_archiveData.Path)}/";
        UpdateDestinationPreview();
    }

    private void OnRadioChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag != null)
        {
            SelectedExtractMode = rb.Tag.ToString()!;
            if (BrowseButton != null)
                BrowseButton.IsEnabled = SelectedExtractMode == "custom";
            
            UpdateDestinationPreview();
        }
    }

    private void UpdateDestinationPreview()
    {
        if (string.IsNullOrEmpty(_baseDirectory) && SelectedExtractMode != "custom") return;

        if (SelectedExtractMode == "here")
        {
            Destination = _baseDirectory;
        }
        else if (SelectedExtractMode == "subfolder" && _archiveData != null)
        {
            Destination = System.IO.Path.Combine(_baseDirectory, ArchivePaths.ExtractFolderName(_archiveData.Path));
        }
        // Custom is handled by browse button
    }

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        if (BrowseFolderAsync is null)
        {
            return;
        }

        BrowseButton.IsEnabled = false;
        try
        {
            var picked = await BrowseFolderAsync(string.IsNullOrWhiteSpace(Destination) ? _baseDirectory : Destination);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                Destination = picked;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            WarningBar.Severity = InfoBarSeverity.Error;
            WarningBar.Message = exception.Message;
            WarningBar.IsOpen = true;
        }
        finally
        {
            BrowseButton.IsEnabled = SelectedExtractMode == "custom";
        }
    }

    private string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }
}
