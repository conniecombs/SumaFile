#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private async Task SmokeUiPolishAsync(string root, string sample, List<object> results)
    {
        var workspace = _workspace!;
        if (!workspace.DualPaneEnabled) await workspace.ToggleDualPaneAsync();
        await workspace.NavigatePaneAsync(PaneId.Primary, sample);
        await workspace.NavigatePaneAsync(PaneId.Secondary, sample);
        workspace.SetFilterQuery(PaneId.Primary, "file-0000");
        workspace.SetFilterQuery(PaneId.Secondary, ".png");
        SyncFromWorkspace();
        SmokeAssert(PrimaryPane.ContextBar.Visibility == Visibility.Visible && SecondaryPane.ContextBar.Visibility == Visibility.Visible,
            "Both panes must retain independent filter indicators");
        SetOmnibarMode(OmnibarMode.Navigate, false);
        workspace.ActivatePane(PaneId.Secondary);
        SyncFromWorkspace();
        SmokeAssert(PrimaryPane.ContextBar.Visibility == Visibility.Visible, "Switching panes or omnibar mode hid a filter");
        SmokeInvoke(PrimaryPane.ContextBar.ClearButton);
        await SmokeWaitAsync(() => workspace.FilterQueryFor(PaneId.Primary) == "", "clear filter button");
        SmokeAssert(workspace.FilterQueryFor(PaneId.Secondary) == ".png", "Clearing one pane changed the other filter");
        SmokeInvoke(SecondaryPane.ContextBar.ClearButton);
        await SmokeWaitAsync(() => workspace.FilterQueryFor(PaneId.Secondary) == "", "clear second filter");
        workspace.ActivatePane(PaneId.Primary);
        _search!.IsPathIndexedCheck = _ => false;
        SearchBox.Text = "file-0000";
        await StartSearchAsync(PaneId.Primary);
        SmokeAssert(_search.IsActive && PrimaryPane.ContextBar.Visibility == Visibility.Visible, "Search has no persistent return banner");
        SmokeInvoke(PrimaryPane.ContextBar.ReturnButton);
        await SmokeWaitAsync(() => !_search.IsActive && PrimaryFiles.Count == workspace.Primary.Entries.Count, "return to folder button");
        results.Add(new { scenario = "pane-filter-search-controls", passed = true });

        workspace.Settings.PreviewVisible = true;
        workspace.Settings.PreviewPinned = false;
        workspace.Settings.PreviewAutoCollapse = true;
        _previewManuallyExpanded = false;
        PrimaryFileList.SelectedItem = null;
        PrimaryDetailsFileList.ClearSelection();
        workspace.SelectPath(null, PaneId.Primary);
        _previewEmptySettled = true;
        ApplyPreviewVisibility();
        SmokeAssert(PreviewRail.Visibility == Visibility.Visible, "Empty unpinned preview did not collapse");
        SmokeInvoke(PreviewRail);
        SmokeAssert(PreviewPanel.Visibility == Visibility.Visible, "Preview rail did not reopen the panel");
        var pin = SmokeDescendants<ToggleButton>(PreviewPanel).Single(button => button.Name == "PreviewPinButton");
        ((IToggleProvider)new ToggleButtonAutomationPeer(pin).GetPattern(PatternInterface.Toggle)).Toggle();
        await SmokeWaitAsync(() => workspace.Settings.PreviewPinned, "pin preview button");
        _previewManuallyExpanded = false;
        ApplyPreviewVisibility();
        SmokeAssert(PreviewPanel.Visibility == Visibility.Visible, "Pinned preview collapsed");
        workspace.SetFilterQuery(PaneId.Primary, "file-0000");
        workspace.SetFileListView(PaneId.Primary, "details");
        workspace.SetFileListView(PaneId.Secondary, "details");
        SyncFromWorkspace();
        await Task.Delay(200);
        await SmokeCheckListAsync(SmokeDescendants<ListView>(PrimaryDetailsFileList).Single(), false);
        await SmokeCheckListAsync(SmokeDescendants<ListView>(SecondaryDetailsFileList).Single(), false);
        await Task.Delay(350);
        SmokeAssert(OmnibarHost.ActualWidth >= OmnibarHost.MinWidth - 1, "Toolbar actions squeezed the omnibar below its minimum width");
        await SmokeCaptureAsync(RootGrid, Path.Combine(root, "workspace-polish.png"));
        workspace.SetFilterQuery(PaneId.Primary, "");
        workspace.Settings.PreviewVisible = false;
        ApplyPreviewVisibility();
        results.Add(new { scenario = "preview-collapse-expand-pin", passed = true });

        var incomingFolder = Path.Combine(root, "conflict-incoming");
        var existingFolder = Path.Combine(root, "conflict-existing");
        Directory.CreateDirectory(incomingFolder);
        Directory.CreateDirectory(existingFolder);
        var incoming = Path.Combine(incomingFolder, "report.txt");
        await File.WriteAllTextAsync(incoming, "incoming content");
        await File.WriteAllTextAsync(Path.Combine(existingFolder, "report.txt"), "existing");
        var comparisons = TransferConflictComparisonLoader.Load([incoming], existingFolder, ["report.txt"], CancellationToken.None);
        var conflict = new ConflictDialog { XamlRoot = RootGrid.XamlRoot };
        conflict.SetConflict(new(false, 1, existingFolder, null, ["report.txt"]));
        conflict.SetComparisons(comparisons);
        var dialogTask = conflict.ShowAsync().AsTask();
        try
        {
            await Task.Delay(300);
            SmokeAssert(comparisons[0].KeepBothName == "report (1).txt", "Unexpected Keep both preview");
            await SmokeCaptureAsync(conflict, Path.Combine(root, "conflict-polish.png"));
            SmokeInvoke(SmokeDescendants<Button>(conflict).Single(button => button.Content?.ToString() == "Keep both"));
        }
        finally { conflict.Hide(); }
        await dialogTask;
        SmokeAssert(conflict.Result == ConflictResolution.KeepBoth, "Keep both button did not select its action");
        await workspace.FileOps!.CopyAsync([incoming], existingFolder, "keep-both");
        SmokeAssert(await File.ReadAllTextAsync(Path.Combine(existingFolder, comparisons[0].KeepBothName)) == "incoming content"
            && await File.ReadAllTextAsync(Path.Combine(existingFolder, "report.txt")) == "existing",
            "Keep both preview differed from the backend result or replaced the original file");
        results.Add(new { scenario = "conflict-comparison-dialog", passed = true });

        var longName = "quarterly-financial-review-with-a-long-filename-that-must-wrap-and-keep-its-extension-visible.txt";
        var longPath = Path.Combine(incomingFolder, longName);
        await File.WriteAllTextAsync(longPath, "long name fixture");
        var rename = new AdvancedRenameDialog(
            [new SimpleFile.Ipc.FileEntry { Name = "report.txt", Path = incoming },
             new SimpleFile.Ipc.FileEntry { Name = longName, Path = longPath }], incomingFolder,
            (path, token) => _backend!.ListDirectoryAsync(path, cancellationToken: token));
        var renameTask = rename.ShowAsync();
        try
        {
            await Task.Delay(500);
            var renameRoot = (FrameworkElement)rename.Content;
            var template = (CheckBox)renameRoot.FindName("TemplateEnabledCheck");
            await SmokeWaitAsync(() => rename.AllRows.Count == 2 && rename.AllRows.All(row => !row.Changed), "unchanged rename preview");
            template.IsChecked = true;
            ((TextBox)renameRoot.FindName("TemplatePatternBox")).Text = "duplicate";
            await SmokeWaitAsync(() => rename.AllRows.Count == 2 && rename.AllRows.All(row => row.Error is not null), "conflicting rename preview");
            SmokeAssert(!((Button)renameRoot.FindName("RenameButton")).IsEnabled, "Conflicting renames were allowed");
            ((TextBox)renameRoot.FindName("TemplatePatternBox")).Text = "{base}_reviewed";
            await SmokeWaitAsync(() => rename.RenameRequests.Length == 2, "rename preview update");
            var previewList = (ListView)renameRoot.FindName("PreviewList");
            await SmokeWaitAsync(() => previewList.ContainerFromIndex(0) is ListViewItem { ActualHeight: > 0 }
                && SmokeDescendants<TextBlock>(previewList).Any(text => text.Text.StartsWith("Ready ·", StringComparison.Ordinal)
                    && text.ActualHeight > 0), "visible rename preview row");
            await Task.Delay(350);
            SmokeAssert(rename.ChangedRows[0].NewName == "report_reviewed.txt", "Rename preview lost the extension");
            SmokeAssert(SmokeDescendants<RenamePreviewName>(renameRoot).Count() == 4, "Rename Before/After columns were not rendered");
            var beforeName = SmokeDescendants<RenamePreviewName>(previewList).First(name => !name.ShowAfter);
            var afterName = SmokeDescendants<RenamePreviewName>(previewList).First(name => name.ShowAfter);
            var beforeHeader = (TextBlock)renameRoot.FindName("BeforeHeaderText");
            var afterHeader = (TextBlock)renameRoot.FindName("AfterHeaderText");
            double Left(FrameworkElement element) => element.TransformToVisual(renameRoot).TransformPoint(new Windows.Foundation.Point()).X;
            SmokeAssert(Math.Abs(Left(beforeHeader) - Left(beforeName)) <= 2 && Math.Abs(Left(afterHeader) - Left(afterName)) <= 2,
                "Rename column headers do not align with their names");
            await SmokeCaptureAsync(renameRoot, Path.Combine(root, "rename-polish.png"));
        }
        finally { rename.Close(); }
        await renameTask;
        results.Add(new { scenario = "rename-before-after-preview", passed = true });
    }

    private static void SmokeInvoke(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static async Task SmokeCaptureAsync(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(path, []);
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
}
#endif
