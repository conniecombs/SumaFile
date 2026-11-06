using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Guards for the 1.0.4 UX recommendations covering Settings, Git, the preview header,
/// the FTP/SFTP manager and Open with (docs/superpowers/specs/2026-11-06-ux-recommendations.md).
/// </summary>
public class UxRecommendationsSettingsGitRemoteTests
{
    private static string Read(string fileName) => UiAuditSprint3Tests.Read(fileName);

    private static string Slice(string text, string start, string end) => UiAuditSprint3Tests.Slice(text, start, end);

    [Fact]
    public void Settings_UsesSentenceCaseLabelsAndPlainWording()
    {
        var xaml = Read("SettingsWindow.xaml");

        foreach (var label in new[]
        {
            "Header=\"Show hidden files\"",
            "Header=\"Start location\"",
            "Header=\"Custom path\"",
            "Header=\"Cache location\"",
            "Header=\"Show labels\"",
            "Header=\"Show sidebar\"",
            "<TextBlock Text=\"Sidebar\" FontWeight=\"SemiBold\" />",
            "Content=\"Check for updates\"",
            "Header=\"Ask before moving to Recycle Bin\"",
            "<ComboBoxItem Content=\"Home folder\" Tag=\"Home\" />",
            "<ComboBoxItem Content=\"Restore last session (tabs and panes)\" Tag=\"Last\" />",
            "<ComboBoxItem Content=\"Custom folder…\" Tag=\"Custom\" />",
            "<ComboBoxItem Content=\"Basic\" Tag=\"default\" />",
            "<ComboBoxItem Content=\"Full details\" Tag=\"details\" />",
            "Header=\"Show rendered view for Markdown and data files\"",
            "Header=\"Collapse the preview when panes are cramped\"",
            "PlaceholderText=\"Search settings...\"",
            "SumaFile can download and install signed updates. Other releases open the download page.",
        })
        {
            Assert.Contains(label, xaml);
        }

        foreach (var stale in new[]
        {
            "Show Hidden Files",
            "Start Location",
            "Custom Path",
            "Cache Location",
            "Show Tags",
            "Show side menu",
            "Check for Updates",
            "Confirm before delete",
            "Render Markdown and data previews",
            "Collapse unpinned preview",
            "Current Version",
            "📁",
        })
        {
            Assert.DoesNotContain(stale, xaml);
        }
    }

    [Fact]
    public void Settings_OffersLightThemeAndMapsItBackByIndex()
    {
        var xaml = Read("SettingsWindow.xaml");
        var state = Read("SettingsWindow.SettingsState.cs");

        Assert.Contains("<ComboBoxItem Content=\"Light\" Tag=\"Light\" />", xaml);
        Assert.True(
            xaml.IndexOf("Tag=\"Dark\"", StringComparison.Ordinal) < xaml.IndexOf("Tag=\"Light\"", StringComparison.Ordinal),
            "Light follows Dark in the theme list.");
        var select = Slice(state, "private void SelectTheme(", "private async void OnBrowseCustomPath(");
        Assert.Contains("\"dark\" => 1", select);
        Assert.Contains("\"light\" => 2", select);
        Assert.Contains("_ => 0", select);
        Assert.Contains("ThemeComboBox.SelectedItem)?.Tag", state);
    }

    [Fact]
    public void Settings_GroupsGitUnderDeveloperAndOpensTransferOptions()
    {
        var xaml = Read("SettingsWindow.xaml");
        var tools = Read("SettingsWindow.Tools.cs");
        var behavior = Slice(xaml, "x:Name=\"BehaviorPanel\"", "x:Name=\"StoragePanel\"");

        var developer = Slice(behavior, "x:Name=\"DeveloperSettingsGroup\"", "</StackPanel>");
        Assert.Contains("<TextBlock Text=\"Developer\" FontWeight=\"SemiBold\" />", developer);
        Assert.Contains("<ToggleSwitch x:Name=\"EnableGitSwitch\" Header=\"Git integration\" />", developer);
        Assert.Contains("Shows Git status for files inside repositories and enables the Git workbench.", developer);

        var transfers = Slice(behavior, "x:Name=\"TransfersSettingsGroup\"", "</StackPanel>");
        Assert.Contains("Transfer options", transfers);
        Assert.Contains("Speed, bandwidth limit, verification and retries.", transfers);
        Assert.Contains("Content=\"Open transfer options...\"", transfers);
        Assert.Contains("TransferOptionsDialog.ShowDefaultsAsync(Content.XamlRoot)", tools);
        Assert.Contains("Last checked: ", tools);
        Assert.Contains("$\"Version {version}\"", tools);
    }

    [Fact]
    public void Settings_SearchIndexesControlsAndJumpsToTheOwningCategory()
    {
        var code = Read("SettingsWindow.xaml.cs");
        var search = Slice(code, "private void OnSearchTextChanged(", "private void OnSearchBoxKeyDown(");

        Assert.Contains("SearchableElements(panel)", search);
        Assert.Contains("SearchText(element)", search);
        Assert.Contains("item.Visibility = matches.ContainsKey(category)", search);
        Assert.Contains("StartBringIntoView(", search);
        var text = Slice(code, "private static string SearchText(", "public bool IsClosed");
        foreach (var source in new[] { "ToggleSwitch toggle", "ComboBox combo", "TextBlock textBlock", "ContentControl { Content: string text }" })
        {
            Assert.Contains(source, text);
        }
    }

    [Fact]
    public void GitDiscard_ListsPathsAndWarnsAboutUntrackedFiles()
    {
        var git = Read("MainWindow.Git.cs");
        var text = Slice(git, "internal static string GitDiscardConfirmationText(", "private async Task<bool> ConfirmGitDiscardAsync(");
        var confirm = Slice(git, "private async Task<bool> ConfirmGitDiscardAsync(IReadOnlyList<GitChangeRow> rows)", "return await DialogHost.ShowAsync(dialog)");

        Assert.Contains("const int listed = 5;", text);
        Assert.Contains("$\"and {rows.Count - listed} more\"", text);
        Assert.Contains("\"This cannot be undone.\"", text);
        Assert.Contains("\"1 untracked file will be deleted from disk, not moved to the Recycle Bin.\"", text);
        Assert.Contains("untracked files will be deleted from disk, not moved to the Recycle Bin.", text);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", confirm);
        Assert.Contains("ConfirmGitDiscardAsync(SelectedGitDiscardRows(selectedPaths))", git);

        var workbench = Read("GitWorkbenchView.xaml");
        var diff = workbench.IndexOf("Click=\"OnDiffClicked\"", StringComparison.Ordinal);
        var discard = workbench.IndexOf("Click=\"OnDiscardClicked\"", StringComparison.Ordinal);
        Assert.True(diff > 0 && discard > diff, "Discard sits after the other actions.");
        var discardButton = Slice(workbench, "x:Name=\"DiscardButton\"", "</Button>");
        Assert.Contains("Foreground=\"{ThemeResource SystemFillColorCriticalBrush}\"", discardButton);
        Assert.Contains("AutomationProperties.Name=", discardButton);
    }

    [Fact]
    public void GitStatusButton_AppearsOnlyInsideARepository()
    {
        var git = Read("MainWindow.Git.cs");
        var visibility = Slice(git, "private void RefreshGitUiVisibility()", "private bool? ActivePaneInGitRepository()");

        Assert.Contains("ActivePaneInGitRepository()", visibility);
        Assert.Contains("enabled && (inRepository == true || _gitPanelOpen)", visibility);
        Assert.Contains("pane.GitRepositoryRoot", git);
    }

    [Fact]
    public void PreviewHeader_UsesSegmentedSourceRenderedAndPosterPlayControls()
    {
        var pane = Read("PreviewPaneView.xaml");
        var code = Read("PreviewPaneView.xaml.cs");
        var document = XDocument.Load(Path.Combine(UiAuditSprint3Tests.AppRoot(), "PreviewPaneView.xaml"));
        var selectors = document.Descendants().Where(element => element.Name.LocalName == "SelectorBar").ToList();

        Assert.Equal(2, selectors.Count);
        Assert.Contains("<SelectorBarItem x:Name=\"PreviewSourceModeItem\" Text=\"Source\" />", pane);
        Assert.Contains("<SelectorBarItem x:Name=\"PreviewRenderedModeItem\" Text=\"Rendered\" />", pane);
        Assert.Contains("<SelectorBarItem x:Name=\"PreviewVideoPosterItem\" Text=\"Poster\" />", pane);
        Assert.Contains("<SelectorBarItem x:Name=\"PreviewVideoPlayItem\" Text=\"Play\" />", pane);
        Assert.Contains("x:Name=\"PreviewOptionStateHost\" Visibility=\"Collapsed\"", pane);
        Assert.Contains("PreviewRenderHtmlCheckBox.IsChecked = rendered;", code);
        Assert.Contains("PreviewVideoPlaybackCheckBox.IsChecked = play;", code);
        Assert.Contains("\"Unpin preview\" : \"Pin preview open\"", code);
    }

    [Fact]
    public void RemoteManager_AsksForPlainFtpConsentAndTrustsHostKeysExplicitly()
    {
        var code = Read("RemoteManagerWindow.xaml.cs");
        var xaml = Read("RemoteManagerWindow.xaml");

        Assert.Contains("\"Connect without encryption. Passwords and files are sent in plain text.\"", code);
        Assert.Contains("\"Confirm the unencrypted connection to save this profile\"", code);
        Assert.Contains("dialog.IsPrimaryButtonEnabled = !needsConsent;", code);
        Assert.DoesNotContain("Allow plain FTP", code);
        var connection = Slice(code, "\"Connection\",", "rootBox),");
        Assert.True(
            connection.IndexOf("protocolBox,", StringComparison.Ordinal) < connection.IndexOf("insecureBox,", StringComparison.Ordinal),
            "Plain-FTP consent sits directly under Protocol.");
        Assert.DoesNotContain("credentialBox,\n                    ftpOptionsPanel", code);
        Assert.Contains("DefaultPortFor(", code);

        Assert.Contains("Title = \"Trust this server?\"", code);
        Assert.Contains("PrimaryButtonText = \"Trust and connect\"", code);
        Assert.Contains("Title = \"Server key changed\"", code);
        Assert.Contains("PrimaryButtonText = \"Replace trusted key\"", code);
        Assert.Contains("TrustFingerprintAndConnectAsync(fingerprint, token)", code);
        var mismatch = Slice(code, "Title = \"Server key changed\"", "if (await DialogHost.ShowAsync(dialog)");
        Assert.Contains("DefaultButton = ContentDialogButton.Close", mismatch);

        Assert.Contains("Text=\"Insecure\"", xaml);
        Assert.Contains("InsecureBadgeVisibility(Protocol, InsecurePlainFtp)", xaml);
        Assert.Contains("Content=\"Transfer options...\"", xaml);
        Assert.Contains("Replace (keeps a backup until the transfer finishes)", xaml);
        Assert.DoesNotContain("Text=\"Reload\"", xaml);
    }

    [Fact]
    public void RustRemoteMessages_UsePlainWordingAndStableHostKeyPrefixes()
    {
        var crates = Path.Combine(UiAuditSprint3Tests.FindRepoRoot(), "..", "crates");
        var profiles = File.ReadAllText(Path.Combine(crates, "simplefile-core", "src", "remote", "profiles.rs"));
        var ftp = File.ReadAllText(Path.Combine(crates, "simplefile-service", "src", "remote", "ftp.rs"));
        var sftp = File.ReadAllText(Path.Combine(crates, "simplefile-service", "src", "remote", "sftp.rs"));

        Assert.Contains("\"Confirm the unencrypted connection to use plain FTP\"", profiles);
        Assert.Contains("\"Confirm the unencrypted connection to use plain FTP\"", ftp);
        Assert.DoesNotContain("insecure_plain_ftp acknowledgement", profiles + ftp);
        Assert.Contains("\"" + RemoteManagerViewModel.UntrustedFingerprintPrefix + ".", sftp);
        Assert.Contains("\"" + RemoteManagerViewModel.FingerprintMismatchPrefix + ".", sftp);
        Assert.Contains("Expected {expected}, got {fingerprint}", sftp);
    }

    [Theory]
    [InlineData("ftp", true, true)]
    [InlineData("FTP", true, true)]
    [InlineData("ftp", false, false)]
    [InlineData("ftps", true, false)]
    [InlineData("sftp", true, false)]
    public void RemoteProfiles_AreInsecureOnlyForConfirmedPlainFtp(string protocol, bool insecure, bool expected)
    {
        Assert.Equal(expected, RemoteManagerViewModel.IsInsecureProfile(protocol, insecure));
    }

    [Fact]
    public void OpenWith_UsesPlainLabelsAndPrevalidatesTypedPaths()
    {
        var openWith = Read("MainWindow.OpenWith.cs");

        Assert.Contains("\"Open with can't launch .exe, .bat or script files directly. Use Open instead.\"", openWith);
        Assert.Contains("\"No matching apps were found.\"", openWith);
        Assert.Contains("\"registered\" => \"Installed\"", openWith);
        Assert.Contains("\"suggested\" => \"Suggested\"", openWith);
        Assert.Contains("_ => \"Other\"", openWith);
        Assert.Contains("OpenWithPolicy.IsLaunchableApplication(", openWith);
        Assert.Contains("SumaFile only launches apps installed under Program Files, your user apps folder or Windows.", openWith);
        foreach (var stale in new[] { "payload", "found yet", "Registered app", "Suggested app", "Custom app", "\"Open With\"" })
        {
            Assert.DoesNotContain(stale, openWith);
        }
    }
}
