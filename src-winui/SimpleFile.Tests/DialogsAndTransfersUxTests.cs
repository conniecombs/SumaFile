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
        Assert.Contains("Title = row.Name", quickLook);
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
}
