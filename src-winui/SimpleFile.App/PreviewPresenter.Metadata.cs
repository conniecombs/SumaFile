using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Core;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
    private async Task LoadMetadataAsync(string path, string? previewType, int token, CancellationToken cancellationToken)
    {
        if (_workspace()?.FileOps is null)
        {
            return;
        }

        try
        {
            var metadata = await _workspace()!.FileOps!.GetFileMetadataAsync(path, cancellationToken);
            if (!IsCurrent(path, token, cancellationToken))
            {
                return;
            }

            AddMetadataSections(InspectionDetails.MetadataSections(
                metadata,
                includeSummary: true,
                includeKind: true,
                maxFields: 24));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsCurrent(path, token, cancellationToken))
            {
                AddMetadataSection("Metadata");
                AddMetadataRow("Error", exception.Message);
            }
        }

        if (!string.Equals(previewType, "image", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var image = await _workspace()!.FileOps!.GetImageMetadataAsync(path, cancellationToken);
            if (!IsCurrent(path, token, cancellationToken))
            {
                return;
            }

            AddMetadataSection("Image");
            AddMetadataRow("Dimensions", $"{image.Width} x {image.Height}");
            AddMetadataRows(InspectionDetails.RawRows(image.Exif.Take(12)));
        }
        catch
        {
            // get_file_metadata already covers the non-EXIF image summary.
        }
    }

    private void ClearMetadataRows()
    {
        _metadataRows.Children.Clear();
        _metadataKeys.Clear();
    }

    private void AddMetadataSections(IEnumerable<InspectionDetailSection> sections)
    {
        foreach (var section in sections)
        {
            AddMetadataSection(section.Title);
            AddMetadataRows(section.Rows);
        }
    }

    private void AddMetadataSection(string title)
    {
        if (string.IsNullOrWhiteSpace(title)
            || _metadataRows.Children.OfType<TextBlock>().Any(block => string.Equals(block.Text, title, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _metadataRows.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("SfTextPrimaryBrush"),
            Margin = new Thickness(0, _metadataRows.Children.Count == 0 ? 0 : 6, 0, 1),
        });
    }

    private void AddMetadataRows(IEnumerable<InspectionDetailRow> rows)
    {
        foreach (var row in rows)
        {
            AddMetadataRow(row.Label, row.Value);
        }
    }

    private void AddMetadataRow(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var key = $"{label.Trim()}\u001f{value.Trim()}";
        if (!_metadataKeys.Add(key))
        {
            return;
        }

        var row = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(0, 0, 0, 1),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brush("SfTextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = Brush("SfTextPrimaryBrush"),
            Opacity = 0.88,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(valueText, 1);
        row.Children.Add(labelText);
        row.Children.Add(valueText);
        _metadataRows.Children.Add(row);
    }

    public void RefreshThemeResources()
    {
        foreach (var row in _metadataRows.Children.OfType<Grid>())
        {
            foreach (var text in row.Children.OfType<TextBlock>())
            {
                text.Foreground = Brush(Grid.GetColumn(text) == 0 ? "SfTextMutedBrush" : "SfTextPrimaryBrush");
            }
        }

        foreach (var section in _metadataRows.Children.OfType<TextBlock>())
        {
            section.Foreground = Brush("SfTextPrimaryBrush");
        }
    }

    private Brush Brush(string key)
    {
        return ThemeResourceLookup.Brush(_metadataRows, key);
    }
}
