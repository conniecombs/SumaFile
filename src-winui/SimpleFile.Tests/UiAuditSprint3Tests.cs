using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>Sprint 3 guards for docs/UI_AUDIT_2026-11-05.md (progress/cancel, accessibility, labels, settings, dialogs).</summary>
public class UiAuditSprint3Tests
{
    // S14: any window resize re-collapsed a preview the user had opened from the rail.
    [Fact]
    public void PreviewPane_KeepsManualExpansionAcrossResizes()
    {
        var layout = Read("MainWindow.PreviewLayout.cs");
        var handler = Slice(layout, "private void OnExplorerContentSizeChanged(", "private void OnExpandPreview(");

        Assert.DoesNotContain("_previewManuallyExpanded = false", handler);
    }

    // C9 / T7 / P6: long operations ran with no progress and no cancel.
    [Fact]
    public void LongOperations_RunUnderACancellableProgressDialog()
    {
        var host = Read("DialogHost.cs");
        Assert.Contains("RunWithProgressAsync", host);
        Assert.Contains("ProgressRing", host);

        var inspection = Read("MainWindow.InspectionDialogs.cs");
        Assert.Contains("RunWithProgressAsync(", Slice(inspection, "private async Task ShowQuickLookAsync()", "private async Task<(UIElement Body, Action? Cleanup)> LoadQuickLookBodyAsync("));
        Assert.Contains("RunWithProgressAsync(", Slice(inspection, "private async Task ShowFolderMetricsAsync()", "private async Task ShowOperationHistoryAsync()"));

        var dialogs = Read("FileOperationDialogService.cs");
        Assert.DoesNotMatch(new Regex(@"ListArchiveAsync\([^,()]+\)"), dialogs);
        Assert.True(Regex.Matches(dialogs, @"RunWithProgressAsync\(").Count >= 4, "archive list/create/extract must show progress");

        var transfer = Read("MainWindow.Transfer.cs");
        Assert.Contains("RunWithProgressAsync(", Slice(transfer, "private async Task ExtractSelectedArchiveAsync(string mode)", "private async Task<string?> PickFolderAsync("));
    }

    // P3: git buttons stayed enabled while a command ran and cancellation was silent.
    [Fact]
    public void GitCommands_MarkTheWorkbenchBusyAndReportCancellation()
    {
        var model = Read("GitWorkbenchViewModel.cs");
        var git = Read("MainWindow.Git.cs");

        Assert.Contains("public void SetCommandRunning(bool running)", model);
        foreach (var runner in new[] { "RunGitRepositoryCommandAsync", "RunGitPathsCommandAsync" })
        {
            var body = Slice(git, $"private async Task {runner}(", "finally");
            Assert.Contains("SetCommandRunning(true)", body);
        }

        Assert.True(Regex.Matches(git, @"SetCommandRunning\(false\)").Count >= 2);
        Assert.Contains("was cancelled", git);
    }

    // T14 / S16 / P10: icon-only controls must carry an accessible name.
    [Fact]
    public void IconOnlyControls_HaveAccessibleNames()
    {
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppRoot(), "*.xaml"))
        {
            var document = XDocument.Load(file);
            foreach (var element in document.Descendants())
            {
                var tag = element.Name.LocalName;
                var hasName = element.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name");
                var isButton = tag is "Button" or "ToggleButton" or "AppBarButton" or "RepeatButton";
                if (isButton && !HasTextualContent(element) && !hasName)
                {
                    missing.Add($"{Path.GetFileName(file)}: {tag} {NameOf(element)}");
                }
                else if (tag == "CheckBox" && !HasTextualContent(element) && !hasName)
                {
                    missing.Add($"{Path.GetFileName(file)}: CheckBox {NameOf(element)}");
                }
                else if (tag is "ProgressBar" or "ProgressRing" && !hasName)
                {
                    missing.Add($"{Path.GetFileName(file)}: {tag} {NameOf(element)}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Controls without AutomationProperties.Name:\n" + string.Join("\n", missing));
    }

    // S15: status-bar Git/Preview buttons gave no state feedback.
    [Fact]
    public void StatusBarToggles_ExposeTheirState()
    {
        Assert.Contains("SetToggleChromeState(GitToggleButton", Read("MainWindow.Git.cs"));
        Assert.Contains("SetToggleChromeState(PreviewToggleButton", Read("MainWindow.Commands.cs"));
    }

    // F22: sort state and resize grips were invisible to assistive technology.
    [Fact]
    public void ColumnHeaders_ExposeSortStateAndGripNames()
    {
        var columns = Read("MainWindow.Columns.cs");
        Assert.Contains("SortAutomationName(", columns);
        Assert.Contains("AutomationProperties.SetName(thumb,", columns);
        Assert.Contains("AutomationProperties.SetName(args.ItemContainer", Read("DetailsFileListView.cs"));
    }

    // X2: the high-contrast palette collapsed muted/primary text and selected/hover backgrounds.
    [Fact]
    public void HighContrastPalette_KeepsMutedAndSelectedDistinct()
    {
        var app = XDocument.Load(Path.Combine(AppRoot(), "App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var highContrast = app.Descendants()
            .First(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == "HighContrast");
        string Color(string key) => (string)highContrast.Elements()
            .First(e => (string?)e.Attribute(x + "Key") == key).Attribute("Color")!;

        Assert.NotEqual(Color("SfTextPrimaryBrush"), Color("SfTextMutedBrush"));
        Assert.NotEqual(Color("SfBgHoverBrush"), Color("SfBgSelectedBrush"));
    }

    // F12: rejected drops gave no feedback.
    [Fact]
    public void RejectedDrops_ReportAStatusMessage()
    {
        Assert.Contains("SetStatusText(\"Cannot drop", Read("MainWindow.Transfer.cs"));
    }

    // S26: "~\Documents" was parsed as a filter, and the prefixes were undocumented.
    [Fact]
    public void Omnibar_TreatsHomePathsAsNavigationAndDocumentsPrefixes()
    {
        Assert.Equal(OmnibarIntentKind.Navigate, OmnibarIntentParser.Parse(@"~\Documents", OmnibarMode.Navigate).Kind);
        Assert.Equal(OmnibarIntentKind.Navigate, OmnibarIntentParser.Parse("~/Documents", OmnibarMode.Navigate).Kind);
        Assert.Equal(OmnibarIntentKind.Filter, OmnibarIntentParser.Parse("~report", OmnibarMode.Navigate).Kind);
        Assert.Contains(">command", Read("PrimaryToolbarView.xaml"));
    }

    // C16: closing Settings discarded edits silently and there was no way back to defaults.
    [Fact]
    public void Settings_PromptOnDiscardAndOfferRestoreDefaults()
    {
        var code = Read("SettingsWindow.xaml.cs");
        Assert.Contains("AppWindow.Closing +=", code);
        Assert.Contains("HasUnsavedChanges", code);
        Assert.Contains("RestoreDefaultsButton", Read("SettingsWindow.xaml"));
    }

    // T8 / T9: Enter cancelled the whole transfer; Enter in history re-ran a completed copy.
    // 1.0.4: Enter is Continue with the selected choice, and the choice starts on Skip.
    [Fact]
    public void ConflictAndHistoryDialogs_HaveSafeDefaults()
    {
        var conflict = Read("ConflictDialog.xaml");
        Assert.Contains("DefaultButton=\"Primary\"", conflict);
        Assert.Contains("SelectedIndex=\"1\"", conflict);
        var history = Slice(Read("MainWindow.InspectionDialogs.cs"), "private async Task ShowOperationHistoryAsync()", "await DialogHost.ShowAsync(dialog);");
        Assert.Contains("DefaultButton = ContentDialogButton.Close", history);
        Assert.Contains("IsPrimaryButtonEnabled", history);
    }

    // T6: the duplicate checker trashed without confirmation, undo or pane refresh.
    [Fact]
    public void DuplicateChecker_TrashesThroughTheWorkspaceWithConfirmation()
    {
        var dialogs = Read("FileOperationDialogService.cs");
        var duplicates = Slice(dialogs, "\"Duplicate checker\",", "PromptPermanentDeleteAfterTrashUnavailableAsync(trash");

        Assert.Contains("workspace.TrashSelectedAsync(trash", duplicates);
        Assert.Contains("ConfirmDelete", duplicates);
    }

    private static bool HasTextualContent(XElement element)
    {
        if (element.Attributes().Any(a => a.Name.LocalName == "Content" && !string.IsNullOrWhiteSpace(a.Value)))
        {
            return true;
        }

        return element.Descendants().Any(d => d.Name.LocalName is "TextBlock" or "Run" or "HyperlinkButton");
    }

    private static string NameOf(XElement element)
    {
        var name = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;
        var tip = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "ToolTipService.ToolTip")?.Value;
        return $"{name ?? "(unnamed)"} tooltip='{tip ?? ""}'";
    }

    private static IEnumerable<ContextMenuEntry> Flatten(IEnumerable<ContextMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Flatten(entry.Children))
            {
                yield return child;
            }
        }
    }

    internal static string Read(string fileName) => File.ReadAllText(Path.Combine(AppRoot(), fileName));

    internal static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Anchor '{start}' not found.");
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Anchor '{end}' not found after '{start}'.");
        return text[startIndex..endIndex];
    }

    internal static string AppRoot() => Path.Combine(FindRepoRoot(), "SimpleFile.App");

    internal static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SimpleFile.App", "MainWindow.xaml.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src-winui source root.");
    }
}
