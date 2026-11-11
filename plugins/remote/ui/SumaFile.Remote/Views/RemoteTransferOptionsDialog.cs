using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.PluginSdk;
using SumaFile.Remote.Model;

namespace SumaFile.Remote.Views;

/// <summary>
/// The manager's Transfer options dialog: the remote branch of the 1.0.4 transfer options, now over the plugin's own
/// eight settings (<see cref="RemoteTransferSettings"/>, the keys the backend reads at the start of each transfer and
/// every 5 s while it runs). Labels are the manifest's; the bounds are the engine's <c>Policy::normalized</c> clamps.
/// Only changed keys are written.
/// </summary>
internal static class RemoteTransferOptionsDialog
{
    internal const string Footnote = "Remote sessions copy one file at a time, and checksum verification downloads each file again.";

    private static readonly string[] CheckModeLabels = ["Size and modification time", "Size only", "Checksum", "Always copy"];

    public static async Task ShowAsync(IPluginHost host, XamlRoot root)
    {
        try
        {
            var settings = await RemoteTransferSettings.LoadAsync(host.Settings, CancellationToken.None);
            var checkModes = RemoteTransferSettings.CheckModes;

            var bandwidth = Number("Bandwidth limit (MB/s, 0 = unlimited)", settings.BandwidthMib, 0, 1_048_576);
            var compareBy = new ComboBox
            {
                Header = "Decide a file is already up to date by",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = CheckModeLabels,
                SelectedIndex = Math.Max(0, IndexOf(checkModes, settings.CheckMode)),
            };
            var inplace = new CheckBox
            {
                Content = "Write downloads directly to the destination instead of a temporary file",
                IsChecked = settings.Inplace,
            };
            var inplaceNote = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(28, -8, 0, 0),
                Foreground = Xaml.Brush("SfTextMutedBrush"),
                Text = "Downloads appear under their final names while they run, and replaced files are overwritten in place. A cancelled or failed download leaves a partial file behind.",
            };
            var preallocate = new CheckBox
            {
                Content = "Pre-allocate local download space",
                IsChecked = settings.Preallocate,
            };
            var flushOnClose = new CheckBox
            {
                Content = "Flush local downloads to disk before finishing each file",
                IsChecked = settings.FlushOnClose,
            };
            var lowLevelRetries = Number("Retry a failing file up to", settings.LowLevelRetries, 1, 50);
            var highLevelRetries = Number("Retry rounds per failed file", settings.HighLevelRetries, 1, 20);
            var retrySleep = Number("Wait before retrying (milliseconds)", settings.RetrySleepMs, 10, 60_000);
            retrySleep.SmallChange = 250;
            retrySleep.LargeChange = 1000;

            var content = new StackPanel { Spacing = 12, MinWidth = 420, MaxWidth = 520, Padding = new Thickness(0, 0, 12, 0) };
            content.Children.Add(bandwidth);
            content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = Footnote });
            content.Children.Add(compareBy);
            content.Children.Add(inplace);
            content.Children.Add(inplaceNote);
            content.Children.Add(preallocate);
            content.Children.Add(flushOnClose);
            content.Children.Add(lowLevelRetries);
            content.Children.Add(highLevelRetries);
            content.Children.Add(retrySleep);
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = "Speed limits also update running transfers within a few seconds. Only files that fail are retried, each time waiting a little longer.",
            });

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "FTP/SFTP transfer options",
                Content = new ScrollViewer
                {
                    Content = content,
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None) != PluginDialogResult.Primary)
            {
                return;
            }

            settings.BandwidthMib = Value(bandwidth, settings.BandwidthMib);
            settings.CheckMode = checkModes[Math.Clamp(compareBy.SelectedIndex, 0, checkModes.Count - 1)];
            settings.Inplace = inplace.IsChecked == true;
            settings.Preallocate = preallocate.IsChecked != false;
            settings.FlushOnClose = flushOnClose.IsChecked != false;
            settings.LowLevelRetries = Value(lowLevelRetries, settings.LowLevelRetries);
            settings.HighLevelRetries = Value(highLevelRetries, settings.HighLevelRetries);
            settings.RetrySleepMs = Value(retrySleep, settings.RetrySleepMs);
            await settings.SaveAsync(host.Settings, CancellationToken.None);
        }
        catch (Exception error)
        {
            await host.Dialogs.ShowDialogAsync(
                new ContentDialog
                {
                    XamlRoot = root,
                    Title = "Transfer options could not be saved",
                    Content = error.Message,
                    CloseButtonText = "Close",
                },
                CancellationToken.None);
        }
    }

    private static NumberBox Number(string header, int value, int minimum, int maximum) => new()
    {
        Header = header,
        Value = Math.Clamp(value, minimum, maximum),
        Minimum = minimum,
        Maximum = maximum,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };

    /// <summary>The box's value, clamped to its bounds; the previous value when the box is empty.</summary>
    private static int Value(NumberBox box, int previous) =>
        double.IsFinite(box.Value) ? (int)Math.Clamp(Math.Round(box.Value), box.Minimum, box.Maximum) : previous;

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
