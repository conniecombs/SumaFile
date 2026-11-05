using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

internal static class TransferOptionsDialog
{
    public static async Task ShowAsync(XamlRoot root, ISettingsBackend backend, TransferOperationViewModel? operation = null)
    {
        try
        {
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
            var parallel = Number("Maximum simultaneous files", policy.Mode == "manual" ? policy.ManualConcurrency : policy.MaxConcurrency, 1, 32);
            var checkers = Number("Directory checkers (--checkers)", policy.Checkers, 1, 64);
            var maxBacklog = Number("Maximum queue backlog (--max-backlog)", policy.MaxBacklog, 128, 100_000);
            var orderBy = new ComboBox
            {
                Header = "Transfer order (--order-by)",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[]
                {
                    "Discovery order (default)",
                    "Smallest first (size,ascending)",
                    "Largest first (size,descending)",
                    "Alphabetical (name)",
                },
                SelectedIndex = Math.Max(0, Array.IndexOf(orders, policy.OrderBy)),
            };

            var bufferSize = Number("Stream buffer size (KiB, --buffer-size)", policy.BufferSizeKib, 64, 65_536);
            var memory = Number("Copy buffer budget (MiB)", policy.MemoryBudgetMb, 16, 512);
            var multiThreadCutoff = Number("Multi-thread cutoff (MiB, --multi-thread-cutoff)", policy.MultiThreadCutoffMib, 1, 16_384);
            var multiThreadStreams = Number("Multi-thread streams (--multi-thread-streams)", policy.MultiThreadStreams, 0, 16);

            var checkMode = new ComboBox
            {
                Header = "Integrity verification",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[]
                {
                    "Size and modification time",
                    "Size only (--size-only)",
                    "Checksum verification (--checksum)",
                    "Always transfer (--ignore-times)",
                },
                SelectedIndex = Math.Max(0, Array.IndexOf(checkModes, policy.CheckMode)),
            };
            var inplace = new CheckBox
            {
                Content = "Write directly to destination (--inplace)",
                IsChecked = policy.Inplace,
            };
            var preallocate = new CheckBox
            {
                Content = "Pre-allocate disk space (--preallocate)",
                IsChecked = policy.Preallocate,
            };
            var flushOnClose = new CheckBox
            {
                Content = "Flush file buffers before commit (--fsync)",
                IsChecked = policy.FlushOnClose,
            };

            var bandwidth = Number("Bandwidth limit (MiB/s; 0 = unlimited)", policy.BandwidthMib, 0, 1_048_576);
            var lowLevelRetries = Number("Low-level retries (--low-level-retries)", policy.LowLevelRetries, 1, 50);
            var highLevelRetries = Number("Batch retry passes (--retries)", policy.HighLevelRetries, 1, 20);
            var retrySleepMs = Number("Base retry backoff (ms, --retries-sleep)", policy.RetrySleepMs, 10, 60_000);

            var status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = "Automatic measures sustained transfer speed. Conservative caps parallel copies at 4. Maximum starts more workers. Custom keeps your file and bandwidth limits, subject to the buffer budget and provider limits.",
            };

            var content = new StackPanel { Spacing = 12, MinWidth = 420, MaxWidth = 520, Padding = new Thickness(0, 0, 12, 0) };
            content.Children.Add(SectionHeader("Concurrency & Queue"));
            content.Children.Add(mode);
            content.Children.Add(parallel);
            content.Children.Add(checkers);
            content.Children.Add(maxBacklog);
            content.Children.Add(orderBy);

            content.Children.Add(SectionHeader("Buffers & Multi-Stream"));
            content.Children.Add(bufferSize);
            content.Children.Add(memory);
            content.Children.Add(multiThreadCutoff);
            content.Children.Add(multiThreadStreams);

            content.Children.Add(SectionHeader("Integrity & Storage"));
            content.Children.Add(checkMode);
            content.Children.Add(inplace);
            content.Children.Add(preallocate);
            content.Children.Add(flushOnClose);

            content.Children.Add(SectionHeader("Retries & Pacing"));
            content.Children.Add(bandwidth);
            content.Children.Add(lowLevelRetries);
            content.Children.Add(highLevelRetries);
            content.Children.Add(retrySleepMs);

            content.Children.Add(status);
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = "Zero bandwidth means unlimited in every mode. Normal cloud or network delays do not impose a hidden speed limit. Failed files retry with exponential backoff and full-batch retry passes. The buffer budget controls pooled SumaFile memory.",
            });
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Text = operation is null
                    ? "Defaults apply to new tasks and running tasks without an override. Changes take effect within 5 seconds; existing files finish before a lower limit is reached."
                    : "Applies to this task only. Changes take effect within 5 seconds; existing files finish before a lower limit is reached.",
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
                        status.Text = "Remembered settings cleared. Future tasks will measure again.";
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
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return;
            TransferPolicy? updated = result == ContentDialogResult.Secondary
                ? null
                : new TransferPolicy
                {
                    Mode = modes[Math.Clamp(mode.SelectedIndex, 0, modes.Length - 1)],
                    MaxConcurrency = Value(parallel, policy.MaxConcurrency),
                    ManualConcurrency = Value(parallel, policy.ManualConcurrency),
                    Checkers = Value(checkers, policy.Checkers),
                    MaxBacklog = Value(maxBacklog, policy.MaxBacklog),
                    OrderBy = orders[Math.Clamp(orderBy.SelectedIndex, 0, orders.Length - 1)],
                    BufferSizeKib = Value(bufferSize, policy.BufferSizeKib),
                    MemoryBudgetMb = Value(memory, policy.MemoryBudgetMb),
                    MultiThreadCutoffMib = Value(multiThreadCutoff, policy.MultiThreadCutoffMib),
                    MultiThreadStreams = Value(multiThreadStreams, policy.MultiThreadStreams),
                    CheckMode = checkModes[Math.Clamp(checkMode.SelectedIndex, 0, checkModes.Length - 1)],
                    Inplace = inplace.IsChecked == true,
                    Preallocate = preallocate.IsChecked != false,
                    FlushOnClose = flushOnClose.IsChecked != false,
                    BandwidthMib = Value(bandwidth, policy.BandwidthMib),
                    LowLevelRetries = Value(lowLevelRetries, policy.LowLevelRetries),
                    HighLevelRetries = Value(highLevelRetries, policy.HighLevelRetries),
                    RetrySleepMs = Value(retrySleepMs, policy.RetrySleepMs),
                }.Normalize();
            if (operation is null)
            {
                await TransferPolicyStore.SaveAsync(backend, updated!);
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
            await new ContentDialog
            {
                XamlRoot = root,
                Title = "Transfer options could not be saved",
                Content = error.Message,
                CloseButtonText = "Close",
            }.ShowAsync();
        }
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static NumberBox Number(string header, int value, int minimum, int maximum) => new()
    {
        Header = header,
        Value = value,
        Minimum = minimum,
        Maximum = maximum,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };

    private static int Value(NumberBox box, int fallback) => double.IsFinite(box.Value) ? (int)box.Value : fallback;
}
