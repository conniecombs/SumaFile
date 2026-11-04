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

        // A real Windows sharing violation at finalization must wait visibly,
        // retain the original, then recover without user retry or lost bytes.
        var pressureSource = Path.Combine(root, "storage-source");
        var pressureDestination = Path.Combine(root, "storage-destination");
        Directory.CreateDirectory(pressureSource); Directory.CreateDirectory(pressureDestination);
        var pressureInput = Path.Combine(pressureSource, "photo.bin");
        var pressureOutput = Path.Combine(pressureDestination, "photo.bin");
        await File.WriteAllBytesAsync(pressureInput, block[..65539]);
        await File.WriteAllTextAsync(pressureOutput, "original destination");
        TransferOperationViewModel pressureOperation;
        using (var held = new FileStream(pressureOutput, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            pressureOperation = _transfer.Enqueue([pressureInput], pressureDestination, false, async (op, ct) =>
            {
                await backend.CopyAsync([pressureInput], pressureDestination, "replace",
                    new Progress<SimpleFile.Ipc.ProgressUpdate>(op.ApplyProgress), op.SetOperationId, ct,
                    new Progress<SimpleFile.Ipc.TuningUpdate>(op.ApplyTuning));
                return TransferOperationStatus.Completed;
            });
            await SmokeWaitAsync(() => pressureOperation.TuningText.Contains("Retrying failed I/O", StringComparison.Ordinal), "storage pressure wait");
            SmokeAssert(SmokeDescendants<TextBlock>(view).Any(t => t.Text.Contains("Retrying failed I/O", StringComparison.Ordinal)),
                "Storage wait did not render in the transfer window");
            SmokeAssert(pressureOperation.TuningText.Contains("up to 4 files", StringComparison.Ordinal), "A file retry changed Custom concurrency");
            SmokeAssert(!pressureOperation.TuningText.Contains("safety limit", StringComparison.Ordinal), "A file retry introduced a hidden bandwidth cap");
            using var reader = new StreamReader(held, leaveOpen: true);
            SmokeAssert(await reader.ReadToEndAsync() == "original destination", "Replacement changed the original before storage recovered");
            var pressureBitmap = new RenderTargetBitmap();
            await pressureBitmap.RenderAsync(view);
            var pressurePixels = (await pressureBitmap.GetPixelsAsync()).ToArray();
            using var screenshot = File.Create(Path.Combine(root, "storage-pressure.png"));
            var pressureEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, screenshot.AsRandomAccessStream());
            pressureEncoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)pressureBitmap.PixelWidth, (uint)pressureBitmap.PixelHeight, 96, 96, pressurePixels);
            await pressureEncoder.FlushAsync();
        }
        await SmokeWaitAsync(() => pressureOperation.IsTerminal, "storage pressure recovery");
        SmokeAssert(pressureOperation.Status == TransferOperationStatus.Completed, pressureOperation.ErrorMessage);
        SmokeAssert(File.ReadAllBytes(pressureOutput).SequenceEqual(block[..65539]), "Recovered transfer changed bytes");
        SmokeAssert(pressureOperation.ActiveFiles.Count == 0, "Recovered file remained active");
        results.Add(new { scenario = "storage-pressure-recovery", waitingRendered = true, originalProtected = true,
            automaticRecovery = true, customConcurrencyRespected = true, noHiddenBandwidthCap = true, contentsMatch = true, completedRowsRemoved = true });

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
