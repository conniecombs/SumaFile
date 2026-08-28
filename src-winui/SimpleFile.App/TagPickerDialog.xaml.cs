using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.App;

public sealed partial class TagPickerDialog : ContentDialog
{
    public Tag[] AvailableTags { get; set; } = Array.Empty<Tag>();
    public long? SelectedTagId { get; private set; }
    public bool ApplyRequested { get; private set; }

    public ObservableCollection<TagViewModel> TagViewModels { get; } = new();

    public TagPickerDialog()
    {
        InitializeComponent();
        TagsList.ItemsSource = TagViewModels;
    }

    public void SetTags(Tag[] tags, long? currentTagId = null)
    {
        AvailableTags = tags;
        TagViewModels.Clear();
        
        bool foundCurrent = false;
        foreach (var tag in tags)
        {
            var tvm = new TagViewModel(tag);
            if (currentTagId.HasValue && tag.Id == currentTagId.Value)
            {
                tvm.IsSelected = true;
                foundCurrent = true;
            }
            TagViewModels.Add(tvm);
        }

        if (!foundCurrent)
        {
            NoTagRadio.IsChecked = true;
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ApplyRequested = true;
        
        if (NoTagRadio.IsChecked == true)
        {
            SelectedTagId = null;
        }
        else
        {
            var selected = TagViewModels.FirstOrDefault(t => t.IsSelected);
            SelectedTagId = selected?.Id;
        }
    }

    private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ApplyRequested = false;
    }
}

public class TagViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    public long Id { get; }
    public string Name { get; }
    public string ColorHex { get; }

    public SolidColorBrush ColorBrush
    {
        get
        {
            try
            {
                var hex = ColorHex.StartsWith("#") ? ColorHex : "#" + ColorHex;
                if (hex.Length == 7) // #RRGGBB
                {
                    byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                    return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
                }
                return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            catch
            {
                return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
    }

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

    public TagViewModel(Tag tag)
    {
        Id = tag.Id;
        Name = tag.Name;
        ColorHex = tag.Color;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Dummy types
// public class Tag { public long Id { get; set; } public string Name { get; set; } = ""; public string Color { get; set; } = ""; }
