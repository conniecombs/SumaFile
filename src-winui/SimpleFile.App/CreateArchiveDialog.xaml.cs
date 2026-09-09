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
        SetArchiveFormats(DefaultCreateFormats());
    }

    public void SetArchiveFormats(System.Collections.Generic.IEnumerable<ArchiveFormatCapability> capabilities)
    {
        var selectedFormat = ArchiveFormat;
        FormatCombo.Items.Clear();

        foreach (var capability in capabilities.Where(format => format.CanCreate))
        {
            var tag = NormalizeArchiveFormat(capability.Format);
            FormatCombo.Items.Add(new ComboBoxItem
            {
                Content = ArchiveFormatLabel(tag, capability.Extension),
                Tag = tag,
            });
        }

        if (FormatCombo.Items.Count == 0)
        {
            foreach (var capability in DefaultCreateFormats())
            {
                FormatCombo.Items.Add(new ComboBoxItem
                {
                    Content = ArchiveFormatLabel(capability.Format, capability.Extension),
                    Tag = capability.Format,
                });
            }
        }

        FormatCombo.SelectedIndex = 0;
        ArchiveFormat = selectedFormat;
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;

        NameInput.Text = ArchivePaths.WithArchiveExtension(NameInput.Text, ArchiveFormat);
    }

    private static ArchiveFormatCapability[] DefaultCreateFormats() =>
    [
        new() { Format = "zip", Extension = ".zip", CanCreate = true },
        new() { Format = "7z", Extension = ".7z", CanCreate = true },
        new() { Format = "tar", Extension = ".tar", CanCreate = true },
        new() { Format = "tar.gz", Extension = ".tar.gz", CanCreate = true },
    ];

    private static string NormalizeArchiveFormat(string value) =>
        value.Trim().TrimStart('.').ToLowerInvariant();

    private static string ArchiveFormatLabel(string format, string extension) =>
        format switch
        {
            "zip" => "ZIP (.zip)",
            "7z" => "7-Zip (.7z)",
            "tar" => "TAR (.tar)",
            "tar.gz" or "tgz" => "TAR.GZ (.tar.gz)",
            _ => string.IsNullOrWhiteSpace(extension)
                ? format.ToUpperInvariant()
                : $"{format.ToUpperInvariant()} ({extension})",
        };
}
