using System.Text.RegularExpressions;
using Xunit;
using static SimpleFile.Tests.UiAuditSprint3Tests;

namespace SimpleFile.Tests;

/// <summary>1.0.4 UX recommendations: dialogs, file operations and transfers (source-shape guards).</summary>
public class DialogsAndTransfersUxTests
{
    [Fact]
    public void ConflictDialog_IsARadioChoiceWithContinueAsTheDefault()
    {
        var xaml = Read("ConflictDialog.xaml");
        Assert.Contains("<RadioButtons", xaml);
        Assert.Contains("DefaultButton=\"Primary\"", xaml);
        Assert.Contains("PrimaryButtonText=\"Continue\"", xaml);
        Assert.Contains("CloseButtonText=\"Stop transfer\"", xaml);
        Assert.DoesNotContain("SecondaryButtonText", xaml);
        Assert.Contains("Replace the existing file", xaml);
        Assert.Contains("Skip this file", xaml);
        Assert.Contains("Replaced files cannot be undone.", xaml);
        Assert.Contains("Do this for all remaining conflicts", xaml);

        var code = Read("ConflictDialog.xaml.cs");
        Assert.Contains("TransferConflictPromptFormatter.Badges(", code);
        Assert.Contains("TransferConflictPromptFormatter.KeepBothLabel(", code);
    }

    [Fact]
    public void TransferConflictPrompt_MapsTheDialogResultAndAsksPerConflict()
    {
        var transfer = Read("MainWindow.Transfer.cs");
        Assert.DoesNotContain("ContentDialogResult.Primary => \"replace\"", transfer);
        Assert.DoesNotContain("ContentDialogResult.Secondary => \"skip\"", transfer);
        Assert.Contains("ConflictResolution.Replace => \"replace\"", transfer);

        var each = Slice(transfer, "private async Task<string?> PromptEachConflictAsync(", "private static bool TryGetPreDecidedAction(");
        Assert.Contains("reuse: dialog", each);
        Assert.Contains("session.TryGetSticky(", each);
    }

    [Fact]
    public void AdvancedRename_NoLongerAsksASecondTime()
    {
        foreach (var file in Directory.EnumerateFiles(Sprint2ShellTests.AppRoot(), "*.cs"))
        {
            Assert.DoesNotContain("ConfirmAdvancedRenameAsync", File.ReadAllText(file));
        }

        Assert.Contains("Renamed {requests.Length} items", Read("MainWindow.Transfer.cs"));
        var xaml = Read("AdvancedRenameDialog.xaml");
        Assert.Contains("Content=\"Tokens\"", xaml);
        Assert.DoesNotContain("{}{base} {ext} {name}", xaml);
    }

    [Fact]
    public void RecycleBinPrompt_IsOneHelperThatDefaultsToMove()
    {
        var dialogs = Read("FileOperationDialogService.cs");
        var helper = Slice(dialogs, "private async Task<bool> ConfirmRecycleAsync(string[] paths)", "private static string FormatItemCount(");
        Assert.Contains("DefaultButton = ContentDialogButton.Primary", helper);
        Assert.Contains("to the Recycle Bin?", helper);
        Assert.Single(Regex.Matches(dialogs, "Title = \"Move to Recycle Bin\""));

        var permanent = Slice(dialogs, "public async Task DeleteSelectedAsync()", "public void CloseSettingsWindow()");
        Assert.Contains("DefaultButton = ContentDialogButton.Close", permanent);
    }

    [Fact]
    public void TransfersWindow_ActivatesOnlyOnRequestAndHidesDetails()
    {
        var ops = Read("MainWindow.FileOperations.cs");
        var show = Slice(ops, "private void ShowTransferProgressWindow(bool activate = true)", "private void StartTransferProgress(");
        Assert.Contains("if (activate)", show);
        Assert.DoesNotContain("created ||", show);
        Assert.Contains("TimeSpan.FromSeconds(3)", ops);
        Assert.Contains("ShowTransferProgressWindow(activate: true)", ops);
        Assert.Contains("if (TryBeginInlineRename())", ops);

        var xaml = Read("TransferProgressWindow.xaml");
        Assert.Matches(new Regex("<Expander[^>]*Header=\"Details\"[^>]*IsExpanded=\"False\"", RegexOptions.Singleline), xaml);
    }

    [Fact]
    public void TransferOptions_UsePlainLabelsAndAdvancedExpander()
    {
        var options = Read("TransferOptionsDialog.cs");
        Assert.DoesNotContain("(--", options);
        Assert.Contains("public static async Task ShowDefaultsAsync(XamlRoot root)", options);
        Assert.Contains("Header = \"Advanced\"", options);
        Assert.Contains("Bandwidth limit (MB/s, 0 = unlimited)", options);
        Assert.Contains("Verify copies with a checksum (slower)", options);
        Assert.Contains("Wait before retrying (seconds)", options);
    }

    [Fact]
    public void DuplicateChecker_UsesThemedButtonAndAdvancedExpander()
    {
        var xaml = Read("DuplicateCheckerDialog.xaml");
        Assert.DoesNotContain("#AA0000", xaml);
        Assert.DoesNotContain("Keep first", xaml);
        Assert.Contains("Keep the copy in the current folder", xaml);
        Assert.Contains("Move selected to Recycle Bin", xaml);
        Assert.Matches(new Regex("x:Name=\"TrashButton\"\\s+Style=\"\\{StaticResource AccentButtonStyle\\}\""), xaml);
        var advanced = Slice(xaml, "Header=\"Advanced\"", "</Expander>");
        Assert.Contains("PartialHashInput", advanced);
        Assert.Contains("NetworkModeBox", advanced);
    }

    [Fact]
    public void LargeFilesDialog_OffersRowActionsAndTheDuplicateFinder()
    {
        var xaml = Read("DiskCleanupDialog.xaml");
        Assert.Contains("Title=\"Large files and duplicates\"", xaml);
        Assert.Contains("Open Duplicate Finder for this folder", xaml);
        Assert.Contains("AutomationProperties.Name=\"Reveal in folder\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Move to Recycle Bin\"", xaml);

        var code = Read("DiskCleanupDialog.xaml.cs");
        Assert.Contains("Showing first {shown:N0} of {total:N0} {noun}", code);
        Assert.DoesNotContain("Analyze Cleanup", code);

        var dialogs = Read("FileOperationDialogService.cs");
        var cleanup = Slice(dialogs, "public async Task ShowDiskCleanupAsync()", "public async Task SetColorLabelAsync()");
        Assert.Contains("workspace.TrashSelectedAsync(trash", cleanup);
        Assert.Contains("ConfirmRecycleAsync(trash)", cleanup);
        Assert.Contains("ShowDuplicateCheckerAsync(path)", cleanup);
    }

    [Fact]
    public void SecondaryWindows_GetEscapeAndCtrlEnterAccelerators()
    {
        var chrome = Read("SecondaryWindowChrome.cs");
        Assert.Contains("Action? cancel = null", chrome);
        Assert.Contains("Action? primary = null", chrome);
        Assert.Contains("VirtualKey.Escape", chrome);
        Assert.Contains("VirtualKey.Enter, VirtualKeyModifiers.Control", chrome);

        // A popup open inside the window (ContentDialog, flyout, ComboBox dropdown) owns
        // Escape and Ctrl+Enter; the window must not close or submit underneath it.
        var invoked = Slice(chrome, "accelerator.Invoked += (_, args) =>", "root.KeyboardAccelerators.Add(accelerator);");
        Assert.Contains("if (HasOpenPopup(root))", invoked);
        Assert.True(invoked.IndexOf("if (HasOpenPopup(root))", StringComparison.Ordinal)
            < invoked.IndexOf("args.Handled = true;", StringComparison.Ordinal), "The popup guard must run before the key is claimed.");
        Assert.Contains("VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot)", chrome);

        foreach (var window in new[] { "AdvancedRenameDialog.xaml.cs", "DuplicateCheckerDialog.xaml.cs", "TransferProgressWindow.xaml.cs" })
        {
            Assert.Contains("cancel:", Read(window));
        }
    }

    [Fact]
    public void Undo_IsSurfacedAfterTransfers()
    {
        var transfer = Read("MainWindow.Transfer.cs");
        var undo = Slice(transfer, "private async Task UndoLastAsync()", "private async Task RedoLastAsync()");
        Assert.Contains("Nothing to undo (replaced files can't be restored)", undo);
        Assert.Contains("$\"Undid: {description}\"", undo);
        Assert.True(
            undo.IndexOf("NextUndoDescription", StringComparison.Ordinal) < undo.IndexOf("Undo.UndoAsync(", StringComparison.Ordinal),
            "the description must be captured before undoing");

        Assert.Contains("$\"Undo: {nextUndo}\"", Read("MainWindow.InspectionDialogs.cs"));
        var statusCenter = Read("MainWindow.StatusCenter.cs");
        Assert.Contains("MessageBar.ActionButton = undo;", statusCenter);
        Assert.Contains("<ProgressBar", statusCenter);
        Assert.Contains("Operation.ProgressPercent", statusCenter);
    }

    [Fact]
    public void ConflictDialog_ShowsAPictureOfEachSideOfAPictureConflict()
    {
        Assert.True(new SimpleFile.Core.ConflictFileSnapshot(@"C:\Photos\IMG_0001.JPG", "File", 10, null).HasThumbnail);
        Assert.True(new SimpleFile.Core.ConflictFileSnapshot(@"C:\Photos\scan.webp", "File", 10, null).HasThumbnail);
        Assert.False(new SimpleFile.Core.ConflictFileSnapshot(@"C:\Docs\report.txt", "File", 10, null).HasThumbnail);
        Assert.False(new SimpleFile.Core.ConflictFileSnapshot(@"C:\Docs\no-extension", "File", 10, null).HasThumbnail);
        // A folder named like a picture, and a link to one, are not read as pictures.
        Assert.False(new SimpleFile.Core.ConflictFileSnapshot(@"C:\Photos\album.png", "Folder", null, null).HasThumbnail);
        Assert.False(new SimpleFile.Core.ConflictFileSnapshot(@"C:\Photos\link.png", "Link", null, null).HasThumbnail);

        var dialog = Read("ConflictDialog.xaml.cs");
        var card = Slice(dialog, "private FrameworkElement FileCard(", "private const double ThumbnailSize");
        // No loader, or not a picture: the card is its text, as before.
        Assert.Contains("if (ThumbnailLoader is not { } load || !file.HasThumbnail)", card);
        Assert.Contains("Visibility = Visibility.Collapsed,", card);
        Assert.Contains("_ = ShowThumbnailAsync(frame, picture, file.Path, load, token);", card);
        var show = Slice(dialog, "private async Task ShowThumbnailAsync(", "private static StackPanel FileDetails(");
        Assert.Contains("catch (Exception)", show);
        Assert.Contains("if (source is null || token.IsCancellationRequested)", show);
        // The reads of one prompt end when the next is set and when the dialog closes.
        Assert.Contains("_thumbnails?.Cancel();", Slice(dialog, "public void SetComparisons(", "private FrameworkElement FileCard("));
        Assert.Contains("Closed += (_, _) => _thumbnails?.Cancel();", dialog);
        Assert.Contains("dialog.ThumbnailLoader ??= LoadConflictThumbnailAsync;", Read("MainWindow.Transfer.cs"));
    }

    [Fact]
    public void QuickLook_IsALightboxAndFoldersOpenProperties()
    {
        var inspection = Read("MainWindow.InspectionDialogs.cs");
        var quickLook = Slice(inspection, "private async Task ShowQuickLookAsync()", "private static async Task<bool> TryAddQuickLookImageAsync(");
        Assert.DoesNotContain("GetFolderMetricsAsync", quickLook);
        Assert.DoesNotContain("GetFileMetadataAsync", quickLook);
        Assert.Contains("await ShowPropertiesAsync();", quickLook);
        Assert.Contains("VirtualKey.Left", quickLook);
        Assert.Contains("VirtualKey.Right", quickLook);
        Assert.Contains("VirtualKey.Space", quickLook);
        // The header is the name, with the item's place among the pane's rows beside it.
        Assert.Contains("Title = QuickLookTitle(row.Name, PositionOf(row))", quickLook);
        Assert.Contains("dialog.Title = QuickLookTitle(current.Name, QuickLookText.Position(nextIndex, rows.Count));", quickLook);
        Assert.Contains("QuickLookText.Position(index, rows.Count)", quickLook);
        // An image zooms from the keyboard; the view that zooms is found through the body being shown.
        Assert.Contains("image.ChangeView(null, null, (float)QuickLookZoom.Step(image.ZoomFactor, delta));", quickLook);
        // The lightbox takes its keys before whatever has the focus inside it, so a text file's caret does not
        // keep Left and Right for itself; a key with Ctrl or Alt is not the lightbox's.
        Assert.Contains("dialog.PreviewKeyDown += (_, e) =>", quickLook);
        Assert.Contains("if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))", quickLook);
        Assert.Contains("VirtualKey.Add or (VirtualKey)187 => Zoom(1),", quickLook);
        Assert.Contains("VirtualKey.Subtract or (VirtualKey)189 => Zoom(-1),", quickLook);
        Assert.Contains("VirtualKey.Number0 or VirtualKey.NumberPad0 => Zoom(0),", quickLook);
        Assert.Contains("ZoomMode = ZoomMode.Enabled", inspection);
        Assert.Contains("body.Tag = view;", inspection);
        // A text file that can be rendered gets the pane's Source | Rendered choice, and opens in the pane's mode.
        Assert.Contains("if (PreviewCapabilities.CanRenderAsHtml(row.Path, preview))", quickLook);
        Assert.Contains("rendered: workspace?.Settings.PreviewRenderHtml == true", quickLook);
        Assert.Contains("PreviewPresenter.CreateQuickLookHtmlView(", quickLook);

        // The rendered view is locked down as the pane's is: no script, and no navigation away from the document.
        var html = Read("PreviewPresenter.Html.cs");
        var view = Slice(html, "internal static Microsoft.UI.Xaml.Controls.WebView2 CreateQuickLookHtmlView(", "private static bool IsAllowedPreviewNavigation(");
        Assert.Contains("LockDownPreviewWebView(core);", view);
        Assert.Contains("core.Settings.IsScriptEnabled = false;", view);
        Assert.Contains("if (!IsAllowedPreviewNavigation(args.Uri, null))", view);
        Assert.Contains("core.NewWindowRequested += OnPreviewWebViewNewWindowRequested;", view);
        Assert.True(view.IndexOf("LockDownPreviewWebView(core);", StringComparison.Ordinal) < view.IndexOf("view.NavigateToString(html);", StringComparison.Ordinal));
        Assert.Contains("view.Close();", view);
        Assert.Contains("QuickLookTextBudget = 2_000_000", inspection);
    }

    [Fact]
    public void LabelPicker_UsesLabelWordingAndPreselects()
    {
        var xaml = Read("TagPickerDialog.xaml");
        Assert.Contains("Title=\"Choose a label\"", xaml);
        Assert.Contains("None (remove label)", xaml);
        Assert.Contains("DefaultButton=\"Primary\"", xaml);

        var dialogs = Read("FileOperationDialogService.cs");
        Assert.Contains("dialog.SetTags(workspace.AllTags.ToArray(), currentLabels.Length == 1 ? currentLabels[0] : null);", dialogs);
    }

    // Review fix: a transfer's Undo buttons must never undo a later rename or delete.
    [Fact]
    public void TransferUndo_RunsOnlyWhileItsOwnEntryIsNewest()
    {
        var statusCenter = Read("MainWindow.StatusCenter.cs");
        Assert.Contains("ReferenceEquals(entries[^1], entry)", statusCenter);
        Assert.Contains("_undoableTransferEntry = undo.Entries is { Count: > 0 } entries ? entries[^1] : null;", statusCenter);

        var guarded = Slice(statusCenter, "private async Task UndoTransferIfCurrentAsync()", "/// <summary>Completion receipt");
        Assert.True(
            guarded.IndexOf("IsTransferUndoCurrent()", StringComparison.Ordinal) < guarded.IndexOf("UndoLastAsync", StringComparison.Ordinal),
            "the newest-entry check must run before undoing");
        var buttons = Slice(statusCenter, "private async void UndoFromStatusCenter()", "/// <summary>")
            + Slice(statusCenter, "private void ShowUndoReceipt(", "_undoReceiptMessage = receipt;");
        Assert.DoesNotContain("UndoLastAsync", buttons);
        Assert.Contains("UndoTransferIfCurrentAsync()", buttons);
        Assert.Contains("&& IsTransferUndoCurrent();", Slice(statusCenter, "private bool IsTransferUndoable(", "private bool IsTransferUndoCurrent()"));
        Assert.Contains("MarkTransferUndoable(operation, workspace.Undo);", Read("MainWindow.Transfer.cs"));
    }

    // The rule above relies on Entries running oldest to newest.
    [Fact]
    public void UndoStackEntries_EndWithTheNewestEntry()
    {
        var undo = new SimpleFile.Core.UndoStack();
        static SimpleFile.Core.UndoEntry Entry(string name) => new()
        {
            Description = name,
            Undo = _ => Task.CompletedTask,
            Redo = _ => Task.CompletedTask,
        };
        var transfer = Entry("Copy 2 item(s)");
        undo.Push(transfer);
        Assert.Same(transfer, undo.Entries[^1]);

        undo.Push(Entry("Rename 1 item"));
        Assert.NotSame(transfer, undo.Entries[^1]);
        Assert.Same(transfer, undo.Entries[0]);
    }

    [Fact]
    public void CrossPaneTransfer_WithoutDualPaneUsesTheStatusBar()
    {
        var transfer = Read("MainWindow.Transfer.cs");
        var method = Slice(transfer, "private async Task CopyOrMoveToOtherPaneAsync(bool move)", "private async Task PromptPackIntoFolderAsync()");
        Assert.DoesNotContain("ShowMessage(", method);
        Assert.Contains("SetStatusText(", method);
        Assert.DoesNotContain("side menu", transfer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossPaneTransfer_NamesItsDestinationPaneBeforeTheTransferRuns()
    {
        var method = Slice(Read("MainWindow.Transfer.cs"), "private async Task CopyOrMoveToOtherPaneAsync(bool move)", "private async Task PromptPackIntoFolderAsync()");
        var transfer = method.IndexOf("await TransferWithConflictAsync(", StringComparison.Ordinal);
        var named = method.IndexOf("var destinationPane = workspace.OtherPane().Id;", StringComparison.Ordinal);
        Assert.True(named >= 0 && named < transfer, "the destination pane must be captured before the transfer is awaited");
        // After the await, neither the window's workspace field nor "the other pane" is asked again.
        var after = method[transfer..];
        Assert.DoesNotContain("OtherPane()", after);
        Assert.DoesNotContain("_workspace.NavigatePaneAsync", after);
        // A selection in the folder both panes show is refused before anything is transferred.
        var refused = method.IndexOf("PathRules.AllLieDirectlyIn(paths, destination)", StringComparison.Ordinal);
        Assert.True(refused >= 0 && refused < transfer);
    }
}
