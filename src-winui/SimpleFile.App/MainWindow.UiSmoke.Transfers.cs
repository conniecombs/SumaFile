#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;
using Windows.Graphics.Imaging;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private async Task SmokeAdaptiveTransfersAsync(string root, string manyFiles, List<object> results)
    {
        var backend = _workspace!.FileOps!;
        ShowTransferProgressWindow();
        var view = (FrameworkElement)_transferProgressWindow!.Content;
        var optionsTask = TransferOptionsDialog.ShowAsync(view.XamlRoot, backend);
        ContentDialog? dialog = null;
        await SmokeWaitAsync(() =>
        {
            dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault();
            return dialog is not null;
        }, "transfer options dialog");
        var mode = SmokeDescendants<ComboBox>(dialog!).Single();
        var numbers = SmokeDescendants<NumberBox>(dialog!).ToArray();
        mode.SelectedIndex = 3;
        numbers.Single(n => Equals(n.Header, "Maximum simultaneous files")).Value = 4;
        numbers.Single(n => Equals(n.Header, "Bandwidth limit (MiB/s; 0 = unlimited)")).Value = 4;
        var apply = SmokeDescendants<Button>(dialog!).Single(b => b.Name == "PrimaryButton");
        ((IInvokeProvider)new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke)).Invoke();
        await optionsTask;
        var saved = await TransferPolicyStore.LoadAsync(backend);
        SmokeAssert(saved.Mode == "manual" && saved.ManualConcurrency == 4 && saved.BandwidthMib == 4,
            "Transfer options did not persist the selected values");

        var source = Path.Combine(root, "adaptive-source");
        var destination = Path.Combine(root, "adaptive-destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        var block = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(block);
        for (var i = 0; i < 8; i++) await File.WriteAllBytesAsync(Path.Combine(source, $"image-{i}.bin"), block);
        var expectedHash = SHA256.HashData(block);
        var clock = Stopwatch.StartNew();
        var transfer = TransferWithConflictAsync([source], destination, move: false);
        await SmokeWaitAsync(() => _transfer!.Operations.Any(o => o.Sources.Contains(source) && o.ActiveFiles.Count == 4), "four active file rows");
        var operation = _transfer!.Operations.First(o => o.Sources.Contains(source));
        await Task.Delay(200);
        SmokeAssert(SmokeDescendants<TextBlock>(view).Any(t => t.Text == "image-0.bin" || t.Text.StartsWith("image-", StringComparison.Ordinal)),
            "The active file model did not render in the transfer window");
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(view);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using (var output = File.Create(Path.Combine(root, "adaptive-transfers.png")))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        operation.PolicyOverride = saved with { ManualConcurrency = 1, MaxConcurrency = 1 };
        await TransferPolicyStore.SetOverrideAsync(backend, operation.OperationId!, operation.PolicyOverride);
        await SmokeWaitAsync(() => operation.TuningText.Contains("up to 1 file", StringComparison.Ordinal), "live concurrency override");
        await transfer.WaitAsync(TimeSpan.FromSeconds(60));
        SmokeAssert(operation.Status == TransferOperationStatus.Completed && operation.ActiveFiles.Count == 0,
            "Completed files did not disappear from the active list");
        foreach (var file in Directory.GetFiles(Path.Combine(destination, "adaptive-source")))
        {
            using var input = File.OpenRead(file);
            SmokeAssert(SHA256.HashData(input).SequenceEqual(expectedHash), "Concurrent copy changed file bytes");
        }
        results.Add(new { scenario = "adaptive-transfer-ui", files = 8, bytes = 64 * 1024 * 1024,
            elapsedMs = clock.Elapsed.TotalMilliseconds, optionsPersisted = true, liveOverride = true,
            activeRowsRendered = true, completedRowsRemoved = true, hashesMatch = true });

        await TransferPolicyStore.SaveAsync(backend, new TransferPolicy());
        var bulkDestination = Path.Combine(root, "adaptive-bulk");
        Directory.CreateDirectory(bulkDestination);
        clock.Restart();
        var copies = await backend.CopyAsync([manyFiles], bulkDestination, "error");
        var copiedFolder = Path.Combine(bulkDestination, Path.GetFileName(manyFiles));
        var inputs = Directory.GetFiles(manyFiles);
        SmokeAssert(copies.Length == 1 && Directory.GetFiles(copiedFolder).Length == inputs.Length, "Bulk transfer count differs");
        foreach (var file in inputs)
            SmokeAssert(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(copiedFolder, Path.GetFileName(file)))), "Bulk file content differs");
        results.Add(new { scenario = "adaptive-bulk-copy", files = inputs.Length, elapsedMs = clock.Elapsed.TotalMilliseconds, contentsMatch = true });
    }
}
#endif
