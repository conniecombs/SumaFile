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
            var policy = operation?.PolicyOverride ?? await TransferPolicyStore.LoadAsync(backend);
            string[] modes = ["automatic", "conservative", "maximum", "manual"];
            var mode = new ComboBox { Header = "Mode", HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[] { "Automatic", "Conservative", "Maximum throughput", "Custom" },
                SelectedIndex = Array.IndexOf(modes, policy.Mode) };
            var parallel = Number("Maximum simultaneous files", policy.Mode == "manual" ? policy.ManualConcurrency : policy.MaxConcurrency, 1, 32);
            var memory = Number("Copy buffer budget (MiB)", policy.MemoryBudgetMb, 16, 512);
            var bandwidth = Number("Bandwidth limit (MiB/s; 0 = unlimited)", policy.BandwidthMib, 0, 1_048_576);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Text = "Automatic measures speed and retries. Conservative caps parallel copies at 4. Maximum starts more workers. Custom uses your file limit. Backend and shared resource limits still apply." };
            var content = new StackPanel { Spacing = 12, MinWidth = 390, MaxWidth = 500 };
            content.Children.Add(mode); content.Children.Add(parallel); content.Children.Add(memory); content.Children.Add(bandwidth);
            content.Children.Add(status);
            content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Text = operation is null ? "Defaults apply to new tasks and running tasks without an override. Changes take effect within 5 seconds; existing files finish before a lower limit is reached."
                    : "Applies to this task only. Changes take effect within 5 seconds; existing files finish before a lower limit is reached." });
            if (operation is null)
            {
                var forget = new Button { Content = "Forget learned settings" };
                forget.Click += async (_, _) =>
                {
                    forget.IsEnabled = false;
                    try { await TransferPolicyStore.ClearLearningAsync(backend); status.Text = "Remembered settings cleared. Future tasks will measure again."; }
                    catch (Exception error) { status.Text = error.Message; }
                    finally { forget.IsEnabled = true; }
                };
                content.Children.Add(forget);
            }
            var dialog = new ContentDialog { XamlRoot = root, Title = operation is null ? "Transfer defaults" : "Task transfer options",
                Content = content, PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
                SecondaryButtonText = operation is null ? "" : "Use defaults", DefaultButton = ContentDialogButton.Primary };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return;
            TransferPolicy? updated = result == ContentDialogResult.Secondary ? null : new TransferPolicy {
                Mode = modes[Math.Clamp(mode.SelectedIndex, 0, modes.Length - 1)],
                MaxConcurrency = Value(parallel, policy.MaxConcurrency), ManualConcurrency = Value(parallel, policy.ManualConcurrency),
                MemoryBudgetMb = Value(memory, policy.MemoryBudgetMb), BandwidthMib = Value(bandwidth, policy.BandwidthMib) };
            if (operation is null) await TransferPolicyStore.SaveAsync(backend, updated!);
            else
            {
                operation.PolicyOverride = updated;
                if (operation.OperationId is { } operationId && !operation.IsTerminal)
                    await TransferPolicyStore.SetOverrideAsync(backend, operationId, updated);
            }
        }
        catch (Exception error)
        {
            await new ContentDialog { XamlRoot = root, Title = "Transfer options could not be saved", Content = error.Message, CloseButtonText = "Close" }.ShowAsync();
        }
    }

    private static NumberBox Number(string header, int value, int minimum, int maximum) => new() {
        Header = header, Value = value, Minimum = minimum, Maximum = maximum,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private static int Value(NumberBox box, int fallback) => double.IsFinite(box.Value) ? (int)box.Value : fallback;
}
