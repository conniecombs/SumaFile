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
    private static async Task SmokeExpandTransferDetailsAsync(FrameworkElement view)
    {
        await SmokeWaitAsync(() => SmokeDescendants<Expander>(view).Any(e => Equals(e.Header, "Details")), "transfer details expander");
        foreach (var expander in SmokeDescendants<Expander>(view).Where(e => Equals(e.Header, "Details")))
        {
            expander.IsExpanded = true;
        }
        view.UpdateLayout();
        await Task.Delay(200);
    }

    private static async Task SmokeTransferDetailsVerticalResizeAsync(Window transferWindow, FrameworkElement view)
    {
        var expander = SmokeDescendants<Expander>(view).First(e => Equals(e.Header, "Details") && e.IsExpanded);
        var detailsScroll = ((Grid)expander.Content).Children.OfType<ScrollViewer>().Single();
        await SmokeWaitAsync(() => detailsScroll.ActualHeight >= TransferWindowLayout.MinDetailsHeight, "stretched Details pane height");

        // Both heights leave the one expanded pane above its minimum, so the pane has room to follow the window.
        SecondaryWindowChrome.Resize(transferWindow, 800, 600);
        await Task.Delay(150);
        view.UpdateLayout();
        var shorterViewHeight = view.ActualHeight;
        var shorterDetailsHeight = detailsScroll.ActualHeight;

        SecondaryWindowChrome.Resize(transferWindow, 800, 720);
        await Task.Delay(150);
        view.UpdateLayout();
        var tallerViewHeight = view.ActualHeight;
        var tallerDetailsHeight = detailsScroll.ActualHeight;

        var viewDelta = tallerViewHeight - shorterViewHeight;
        var detailsDelta = tallerDetailsHeight - shorterDetailsHeight;
        SmokeAssert(viewDelta > 40 && Math.Abs(detailsDelta - viewDelta) <= 6,
            $"Details pane did not resize vertically with the window (details delta {detailsDelta:0.#} vs window delta {viewDelta:0.#})");

        SecondaryWindowChrome.Resize(transferWindow, 800, 620);
        await Task.Delay(100);
        view.UpdateLayout();
    }

    // FittedText is where the Transfers window shortens text itself, and only a live layout pass shows that it
    // measures: hand it text far wider than its width and read back what it drew.
    private static async Task SmokeFittedTextAsync(Grid host)
    {
        FittedText Probe(FittedTextKind kind, string text, string suffix, double width, double fontSize) => new()
        {
            Kind = kind, Text = text, Suffix = suffix, Width = width, FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var folder = Probe(FittedTextKind.FolderPath,
            @"D:\Media\Library\2024\A very long folder name for the season\Another long folder name\", "", 260, 12);
        var file = Probe(FittedTextKind.FileName,
            "A very long recording name that cannot fit in the line - part 0042.mkv", " and 7 more files", 300, 13);
        var whole = Probe(FittedTextKind.FileName, "short.bin", "", 300, 12);
        FittedText[] probes = [folder, file, whole];
        foreach (var probe in probes) host.Children.Add(probe);
        try
        {
            host.UpdateLayout();
            await Task.Delay(100);
            host.UpdateLayout();
            static TextBlock Line(FittedText probe) => SmokeDescendants<TextBlock>(probe).Single();
            var folderText = Line(folder).Text;
            SmokeAssert(folderText.Contains(TextFit.Ellipsis, StringComparison.Ordinal)
                && folderText.StartsWith(@"D:\", StringComparison.Ordinal)
                && folderText.EndsWith(@"\Another long folder name\", StringComparison.Ordinal)
                && !folderText.Contains("Library", StringComparison.Ordinal),
                $"A long folder path was not shortened in the middle: '{folderText}'");
            var fileText = Line(file).Text;
            SmokeAssert(fileText.Contains(TextFit.Ellipsis, StringComparison.Ordinal)
                && fileText.StartsWith("A very", StringComparison.Ordinal)
                && fileText.EndsWith("0042.mkv and 7 more files", StringComparison.Ordinal),
                $"A long file name did not keep its start, its end and the count: '{fileText}'");
            SmokeAssert(Line(whole).Text == "short.bin", $"Text that fits was changed: '{Line(whole).Text}'");
            foreach (var probe in probes)
            {
                var line = Line(probe);
                SmokeAssert(line.ActualWidth <= probe.ActualWidth + 0.5 && !line.IsTextTrimmed,
                    $"Shortened text is still too wide: '{line.Text}' is {line.ActualWidth:0.#} of {probe.ActualWidth:0.#}");
            }
        }
        finally
        {
            foreach (var probe in probes) host.Children.Remove(probe);
        }
    }

    private async Task SmokeAdaptiveTransfersAsync(string root, string manyFiles, List<object> results,
        Func<string, Task> checkpoint)
    {
        await checkpoint("transfer-options");
        var backend = _workspace!.FileOps!;
        ShowTransferProgressWindow();
        var view = (FrameworkElement)_transferProgressWindow!.Content;
        var optionsTask = TransferOptionsDialog.ShowAsync(view.XamlRoot, backend);
        ContentDialog? dialog = null;
        // The popup is listed as open before the dialog's first layout pass applies its template and adds
        // its content, so wait for the loaded dialog and the controls driven below, not only for the popup.
        await SmokeWaitAsync(() =>
        {
            dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault();
            return dialog is { IsLoaded: true }
                && SmokeDescendants<ComboBox>(dialog).Any(c => Equals(c.Header, "Mode"))
                && SmokeDescendants<Button>(dialog).Any(b => b.Name == "PrimaryButton");
        }, "transfer options dialog controls");
        var mode = SmokeDescendants<ComboBox>(dialog!).Single(c => Equals(c.Header, "Mode"));
        var numbers = SmokeDescendants<NumberBox>(dialog!).ToArray();
        mode.SelectedIndex = 3;
        numbers.Single(n => Equals(n.Header, "Files at once")).Value = 4;
        numbers.Single(n => Equals(n.Header, "Bandwidth limit (MB/s, 0 = unlimited)")).Value = 4;
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
        await checkpoint("adaptive-transfer-ui");
        // The cancelled row of the previous scenario would otherwise be expanded below as well: two Details
        // panes then share the viewport, both sit at their minimum height, and the resize check measures nothing.
        _transfer!.ClearCompleted();
        var clock = Stopwatch.StartNew();
        var transfer = TransferWithConflictAsync([source], destination, move: false);
        await SmokeWaitAsync(() => _transfer!.Operations.Any(o => o.Sources.Contains(source) && o.ActiveFiles.Count == 4), "four active file rows");
        var operation = _transfer!.Operations.First(o => o.Sources.Contains(source));
        await Task.Delay(200);
        // Active files and tuning text live in each task's collapsed "Details" expander.
        await SmokeExpandTransferDetailsAsync(view);
        SmokeAssert(SmokeDescendants<TextBlock>(view).Any(t => t.Text == "image-0.bin" || t.Text.StartsWith("image-", StringComparison.Ordinal)),
            "The active file model did not render in the transfer window");
        // Several files run at once: the row names one and counts the others, and Details lists them under
        // the one folder they come from.
        await SmokeWaitAsync(() => SmokeDescendants<TextBlock>(view).Any(t => t.Text.StartsWith("image-", StringComparison.Ordinal)
            && t.Text.Contains(" more file", StringComparison.Ordinal)), "the row's count of the other active files");
        SmokeAssert(operation.ActiveFolders.Count == 1, $"Files from one folder were listed under {operation.ActiveFolders.Count} headings");
        await SmokeWaitAsync(() => SmokeDescendants<TextBlock>(view).Any(t => t.Text.EndsWith("adaptive-source" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            "the source folder heading in Details");
        await SmokeTransferDetailsVerticalResizeAsync(_transferProgressWindow!, view);
        await SmokeFittedTextAsync((Grid)view);
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
            activeRowsRendered = true, otherFilesCounted = true, groupedByFolder = true, longTextShortened = true,
            completedRowsRemoved = true, hashesMatch = true });

        // A real Windows sharing violation at finalization must wait visibly,
        // retain the original, then recover without user retry or lost bytes.
        var pressureSource = Path.Combine(root, "storage-source");
        await checkpoint("storage-pressure-recovery");
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
            await SmokeExpandTransferDetailsAsync(view);
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
        await checkpoint($"adaptive-bulk-copy-{Path.GetFileName(manyFiles)}");
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
