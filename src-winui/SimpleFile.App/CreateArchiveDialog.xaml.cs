using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class CreateArchiveDialog : ContentDialog
{
    private string[] _selectedPaths = Array.Empty<string>();
    private string[] _selectedNames = Array.Empty<string>();
    private bool _isInitialized = false;

    public string ArchiveName
    {
        get => NameInput.Text;
        set => NameInput.Text = value;
    }

    public string ArchiveFormat
    {
        get
        {
            if (FormatCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
                return item.Tag.ToString()!;
            return "zip";
        }
        set
        {
            var item = FormatCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == value);
            if (item != null)
                FormatCombo.SelectedItem = item;
        }
    }

    public string[] SelectedPaths
    {
        get => _selectedPaths;
        set => _selectedPaths = value;
    }

    public string[] SelectedNames
    {
        get => _selectedNames;
        set
        {
            _selectedNames = value;
            FilesList.ItemsSource = _selectedNames;

            if (!_isInitialized && _selectedNames.Length > 0)
            {
                string baseName = _selectedNames.Length == 1 ? _selectedNames[0] : "Archive";
                NameInput.Text = ArchivePaths.WithArchiveExtension(ArchivePaths.ExtractFolderName(baseName), ArchiveFormat);
                _isInitialized = true;
            }
        }
    }

    public string TargetDirectory { get; set; } = string.Empty;

    public CreateArchiveDialog()
    {
        InitializeComponent();
        FormatCombo.SelectedIndex = 0;
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;

        NameInput.Text = ArchivePaths.WithArchiveExtension(NameInput.Text, ArchiveFormat);
    }
}
