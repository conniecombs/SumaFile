using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

internal static class TransferOptionsDialog
{
    private const string ChecksumMode = "checksum";
    private const string DefaultCheckMode = "size_and_mod_time";

    /// <summary>
    /// Transfer defaults from Settings: resolves the settings backend the same way the Transfers
    /// window gets it (the workspace's file service) and opens the defaults dialog.
    /// </summary>
    public static async Task ShowDefaultsAsync(XamlRoot root)
    {
        ISettingsBackend? backend;
        try
        {
            backend = AppServices.GetRequired<ExplorerWorkspace>().FileOps;
        }
        catch (InvalidOperationException)
        {
            backend = null;
        }

        if (backend is null)
        {
            await DialogHost.ShowAsync(new ContentDialog
            {
                XamlRoot = root,
                Title = "Transfer defaults",
                Content = "SumaFile's file service is not available yet. Try again in a moment.",
                CloseButtonText = "Close",
            });
            return;
        }

        await ShowAsync(root, backend);
    }

    public static async Task ShowAsync(XamlRoot root, ISettingsBackend backend, TransferOperationViewModel? operation = null, bool remote = false)
    {
        try
        {
            // Seed from the real current values; only fields the user changes are written back.
            var policy = (operation?.PolicyOverride ?? await TransferPolicyStore.LoadAsync(backend)).Normalize();
            string[] modes = ["automatic", "conservative", "maximum", "manual"];
            string[] orders = ["default", "size_asc", "size_desc", "name"];
            string[] checkModes = ["size_and_mod_time", "size_only", "checksum", "ignore_times"];

            var mode = new ComboBox
            {
                Header = "Mode",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[] { "Automatic", "Conservative", "Maximum throughput", "Custom" },
                SelectedIndex = Math.Max(0, Array.IndexOf(modes, policy.Mode)),
            };
            var filesAtOnce = Number("Files at once", policy.ManualConcurrency, 1, 32);
            var bandwidth = Number("Bandwidth limit (MB/s, 0 = unlimited)", policy.BandwidthMib, 0, 1_048_576);
            var verify = new CheckBox
            {
                Content = "Verify copies with a checksum (slower)",
                IsChecked = policy.CheckMode == ChecksumMode,
            };

            void UpdateCustomVisibility() =>
                filesAtOnce.Visibility = modes[Math.Clamp(mode.SelectedIndex, 0, modes.Length - 1)] == "manual"
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            mode.SelectionChanged += (_, _) => UpdateCustomVisibility();
            UpdateCustomVisibility();

            // Advanced settings.
            var maxConcurrency = Number("Most files at once in automatic modes", policy.MaxConcurrency, 1, 32);
            var checkers = Number("Folder scanners", policy.Checkers, 1, 64);
            var maxBacklog = Number("Maximum queued items", policy.MaxBacklog, 128, 100_000);
            var orderBy = new ComboBox
            {
                Header = "Transfer order",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[]
                {
                    "As found",
                    "Smallest first",
                    "Largest first",
                    "Alphabetical",
                },
                SelectedIndex = Math.Max(0, Array.IndexOf(orders, policy.OrderBy)),
            };
            var bufferSize = Number("Stream buffer size (KiB)", policy.BufferSizeKib, 64, 65_536);
            var memory = Number("Copy buffer budget (MiB)", policy.MemoryBudgetMb, 16, 512);
            var multiThreadCutoff = Number("Multi-thread cutoff (MiB)", policy.MultiThreadCutoffMib, 1, 16_384);
            var multiThreadStreams = Number("Streams per large file", policy.MultiThreadStreams, 0, 16);
            var compareBy = new ComboBox
            {
                Header = "Decide a file is already up to date by",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[]
                {
                    "Size and modification time",
                    "Size only",
                    "Checksum",
                    "Always copy",
                },
                SelectedIndex = Math.Max(0, Array.IndexOf(checkModes, policy.CheckMode)),
            };
            var inplace = new CheckBox
            {
                Content = "Write directly to the destination instead of a temporary file",
                IsChecked = policy.Inplace,
            };
            var inplaceNote = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(28, -8, 0, 0),
                Text = "Files and folders appear under their final names while they copy, and replaced files are overwritten in place. A cancelled or failed transfer leaves partial files behind.",
            };
            var preallocate = new CheckBox
            {
                Content = "Pre-allocate disk space",
                IsChecked = policy.Preallocate,
            };
            var flushOnClose = new CheckBox
            {
                Content = "Flush to disk before finishing each file",
                IsChecked = policy.FlushOnClose,
            };
            var lowLevelRetries = Number("Retry a failing file up to", policy.LowLevelRetries, 1, 50);
            var highLevelRetries = Number("Retry rounds for the whole task", policy.HighLevelRetries, 1, 20);
            var retryWait = new NumberBox
            {
                Header = "Wait before retrying (seconds)",
                Value = policy.RetrySleepMs / 1000.0,
                Minimum = 0.01,
                Maximum = 60,
                SmallChange = 0.5,
                LargeChange = 5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };

            // The checkbox and the advanced picker describe the same setting; keep them in step.
            verify.Checked += (_, _) => compareBy.SelectedIndex = Array.IndexOf(checkModes, ChecksumMode);
            verify.Unchecked += (_, _) =>
            {
                if (compareBy.SelectedIndex == Array.IndexOf(checkModes, ChecksumMode))
                {
                    compareBy.SelectedIndex = 0;
                }
            };
            compareBy.SelectionChanged += (_, _) =>
                verify.IsChecked = compareBy.SelectedIndex == Array.IndexOf(checkModes, ChecksumMode);

            if (remote)
            {
                foreach (var control in new Control[] { mode, filesAtOnce, maxConcurrency, checkers, maxBacklog, bufferSize, memory, multiThreadCutoff, multiThreadStreams })
                    control.IsEnabled = false;
                inplace.Content = "Write downloads directly to the destination instead of a temporary file";
                inplaceNote.Text = "Downloads appear under their final names while they run, and replaced files are overwritten in place. A cancelled or failed download leaves a partial file behind.";
                preallocate.Content = "Pre-allocate local download space";
                flushOnClose.Content = "Flush local downloads to disk before finishing each file";
                highLevelRetries.Header = "Retry rounds per failed file";
            }

            var status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = remote
                    ? "Remote sessions copy one file at a time, and checksum verification downloads each file again."
                    : "Automatic adjusts to measured speed, Conservative copies up to 4 files at once, Maximum copies more, and Custom uses your own limit.",
            };

            var advanced = new StackPanel { Spacing = 12 };
            advanced.Children.Add(maxConcurrency);
            advanced.Children.Add(checkers);
            advanced.Children.Add(maxBacklog);
            advanced.Children.Add(orderBy);
            advanced.Children.Add(bufferSize);
            advanced.Children.Add(memory);
            advanced.Children.Add(multiThreadCutoff);
            advanced.Children.Add(multiThreadStreams);
            advanced.Children.Add(compareBy);
            advanced.Children.Add(inplace);
            advanced.Children.Add(inplaceNote);
            advanced.Children.Add(preallocate);
            advanced.Children.Add(flushOnClose);
            advanced.Children.Add(lowLevelRetries);
            advanced.Children.Add(highLevelRetries);
            advanced.Children.Add(retryWait);
            advanced.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = "Only files that fail are retried, each time waiting a little longer.",
            });

            var content = new StackPanel { Spacing = 12, MinWidth = 420, MaxWidth = 520, Padding = new Thickness(0, 0, 12, 0) };
            content.Children.Add(mode);
            content.Children.Add(filesAtOnce);
            content.Children.Add(bandwidth);
            content.Children.Add(verify);
            content.Children.Add(status);
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = operation is null
                    ? "Defaults apply to new transfers, and speed limits also update running ones within a few seconds."
                    : "These settings apply to this transfer only, and files already in progress finish with their current settings.",
            });
            content.Children.Add(new Expander
            {
                Header = "Advanced",
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = advanced,
            });

            if (operation is null)
            {
                var forget = new Button { Content = "Forget learned settings" };
                forget.Click += async (_, _) =>
                {
                    forget.IsEnabled = false;
                    try
                    {
                        await TransferPolicyStore.ClearLearningAsync(backend);
                        status.Text = "Remembered speeds cleared. Future transfers will measure again.";
                    }
                    catch (Exception error)
                    {
                        status.Text = error.Message;
                    }
                    finally
                    {
                        forget.IsEnabled = true;
                    }
                };
                content.Children.Add(forget);
            }

            var scroll = new ScrollViewer
            {
                Content = content,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = operation is null ? "Transfer defaults" : "Task transfer options",
                Content = scroll,
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                SecondaryButtonText = operation is null ? "" : "Use defaults",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await DialogHost.ShowAsync(dialog);
            if (result == ContentDialogResult.None) return;

            TransferPolicy? updated = null;
            if (result != ContentDialogResult.Secondary)
            {
                var next = policy;
                var nextMode = modes[Math.Clamp(mode.SelectedIndex, 0, modes.Length - 1)];
                if (nextMode != policy.Mode) next = next with { Mode = nextMode };
                if (Changed(filesAtOnce, policy.ManualConcurrency, out var manual)) next = next with { ManualConcurrency = manual };
                if (Changed(bandwidth, policy.BandwidthMib, out var limit)) next = next with { BandwidthMib = limit };
                if (Changed(maxConcurrency, policy.MaxConcurrency, out var most)) next = next with { MaxConcurrency = most };
                if (Changed(checkers, policy.Checkers, out var scanners)) next = next with { Checkers = scanners };
                if (Changed(maxBacklog, policy.MaxBacklog, out var queued)) next = next with { MaxBacklog = queued };
                var nextOrder = orders[Math.Clamp(orderBy.SelectedIndex, 0, orders.Length - 1)];
                if (nextOrder != policy.OrderBy) next = next with { OrderBy = nextOrder };
                if (Changed(bufferSize, policy.BufferSizeKib, out var buffer)) next = next with { BufferSizeKib = buffer };
                if (Changed(memory, policy.MemoryBudgetMb, out var budget)) next = next with { MemoryBudgetMb = budget };
                if (Changed(multiThreadCutoff, policy.MultiThreadCutoffMib, out var cutoff)) next = next with { MultiThreadCutoffMib = cutoff };
                if (Changed(multiThreadStreams, policy.MultiThreadStreams, out var streams)) next = next with { MultiThreadStreams = streams };
                var nextCheckMode = compareBy.SelectedIndex >= 0
                    ? checkModes[Math.Clamp(compareBy.SelectedIndex, 0, checkModes.Length - 1)]
                    : verify.IsChecked == true ? ChecksumMode : DefaultCheckMode;
                if (nextCheckMode != policy.CheckMode) next = next with { CheckMode = nextCheckMode };
                if ((inplace.IsChecked == true) != policy.Inplace) next = next with { Inplace = inplace.IsChecked == true };
                if ((preallocate.IsChecked != false) != policy.Preallocate) next = next with { Preallocate = preallocate.IsChecked != false };
                if ((flushOnClose.IsChecked != false) != policy.FlushOnClose) next = next with { FlushOnClose = flushOnClose.IsChecked != false };
                if (Changed(lowLevelRetries, policy.LowLevelRetries, out var retries)) next = next with { LowLevelRetries = retries };
                if (Changed(highLevelRetries, policy.HighLevelRetries, out var rounds)) next = next with { HighLevelRetries = rounds };
                var waitMs = double.IsFinite(retryWait.Value) ? (int)Math.Round(retryWait.Value * 1000) : policy.RetrySleepMs;
                if (waitMs != policy.RetrySleepMs) next = next with { RetrySleepMs = waitMs };
                updated = next.Normalize();
            }

            if (operation is null)
            {
                if (updated is not null && updated != policy)
                {
                    await TransferPolicyStore.SaveAsync(backend, updated);
                }
            }
            else
            {
                operation.PolicyOverride = updated;
                if (operation.OperationId is { } operationId && !operation.IsTerminal)
                {
                    await TransferPolicyStore.SetOverrideAsync(backend, operationId, updated);
                }
            }
        }
        catch (Exception error)
        {
            await DialogHost.ShowAsync(new ContentDialog
            {
                XamlRoot = root,
                Title = "Transfer options could not be saved",
                Content = error.Message,
                CloseButtonText = "Close",
            });
        }
    }

    private static NumberBox Number(string header, int value, int minimum, int maximum) => new()
    {
        Header = header,
        Value = value,
        Minimum = minimum,
        Maximum = maximum,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };

    /// <summary>True when the box holds a value different from the seeded one.</summary>
    private static bool Changed(NumberBox box, int seeded, out int value)
    {
        value = double.IsFinite(box.Value) ? (int)box.Value : seeded;
        return value != seeded;
    }
}
