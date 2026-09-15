using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.Ipc;
using System.Globalization;

namespace SimpleFile.App;

internal sealed partial class PreviewPresenter
{
    private async Task ShowComparisonAsync(FileComparison comparison)
    {
        var isBinary = string.Equals(comparison.ComparisonType, "binary", StringComparison.OrdinalIgnoreCase);
        var summary = isBinary
            ? BinaryComparisonSummary(comparison)
            : TextComparisonSummary(comparison);
        var rows = isBinary
            ? BinaryComparisonRows(comparison)
            : TextComparisonRows(comparison);

        var diffBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, rows),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 360,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(diffBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(diffBox, ScrollBarVisibility.Auto);

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"{comparison.LeftName} -> {comparison.RightName}" },
                new TextBlock { Text = summary },
                diffBox,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "File Compare",
            Content = body,
            CloseButtonText = "Close",
            XamlRoot = _xamlRoot(),
        };

        await dialog.ShowAsync();
    }

    private static string TextComparisonSummary(FileComparison comparison)
    {
        return comparison.Identical
            ? "Files are identical."
            : $"{comparison.Added} added, {comparison.Removed} removed, {comparison.Changed} changed";
    }

    private static IEnumerable<string> TextComparisonRows(FileComparison comparison)
    {
        return comparison.Rows
            .Take(80)
            .Select(row =>
            {
                var left = row.LeftLine?.ToString(CultureInfo.CurrentCulture) ?? "";
                var right = row.RightLine?.ToString(CultureInfo.CurrentCulture) ?? "";
                var text = row.LeftText ?? row.RightText ?? "";
                return $"{row.Kind,-8} {left,4} {right,4}  {text}";
            });
    }

    private static string BinaryComparisonSummary(FileComparison comparison)
    {
        if (comparison.Identical)
        {
            return $"Binary files are identical ({FormatByteCount(comparison.ComparedBytes ?? comparison.LeftSize)} compared).";
        }

        var first = comparison.FirstDifference is { } offset
            ? $"first difference at 0x{offset:X}"
            : "first difference unavailable";
        var differences = comparison.DifferentBytes is { } differentBytes
            ? FormatByteCount(differentBytes)
            : "one or more bytes";
        var suffix = comparison.BinaryRowsTruncated
            ? $" Showing first {comparison.BinaryRows.Count.ToString(CultureInfo.CurrentCulture)} differing rows."
            : "";
        return $"Binary files differ: {differences} differ, {first}.{suffix}";
    }

    private static IEnumerable<string> BinaryComparisonRows(FileComparison comparison)
    {
        yield return "Offset(h)    Left hex                                           Right hex                                          Left ASCII        Right ASCII";
        foreach (var row in comparison.BinaryRows.Take(128))
        {
            yield return $"{row.Offset,10:X}  {row.LeftHex,-47}  {row.RightHex,-47}  {row.LeftAscii}  {row.RightAscii}";
        }
    }

    private static string FormatByteCount(ulong bytes)
    {
        return bytes == 1
            ? "1 byte"
            : $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes";
    }
}
