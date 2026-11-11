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

        // The saved "light" must reach the window: a dark-mode Windows user who picks Light gets Light.
        var apply = Slice(Read("MainWindow.Commands.cs"), "private void ApplyTheme(string? theme)", "_appliedTheme = next;");
        Assert.Contains("\"light\" => ElementTheme.Light", apply);
        Assert.Contains("\"dark\" => ElementTheme.Dark", apply);
    }

    [Fact]
    public void Settings_OpensTransferOptionsAndLeavesGitToThePlugin()
    {
        var xaml = Read("SettingsWindow.xaml");
        var tools = Read("SettingsWindow.Tools.cs");
        var behavior = Slice(xaml, "x:Name=\"BehaviorPanel\"", "x:Name=\"StoragePanel\"");

        // Git's on/off switch is the Git plugin's "enabled" setting (Settings > Plugins > Git) since phase 4.
        Assert.DoesNotContain("DeveloperSettingsGroup", xaml);
        Assert.DoesNotContain("EnableGitSwitch", xaml);

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
        // Clearing a query that matched nothing reselects the first category instead of a blank page.
        var cleared = Slice(search, "if (query.Length == 0)", "var matches = ");
        Assert.Contains("SettingsNavigation.SelectedItem = navItems[0];", cleared);
        var text = Slice(code, "private static string SearchText(", "public bool IsClosed");
        foreach (var source in new[] { "ToggleSwitch toggle", "ComboBox combo", "TextBlock textBlock", "ContentControl { Content: string text }" })
        {
            Assert.Contains(source, text);
        }
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

    // The manager window, its consent and host-key dialogs, the insecure-profile rule and the host-key prefixes moved to
    // the remote plugin with core FTP/SFTP (RemotePluginSourceShapeTests, RemoteProfileRowTests). The plain wording of the
    // backend's plain-FTP refusal is pinned here, where the old core text was.
    [Fact]
    public void RemoteBackendMessages_UsePlainWordingForPlainFtpConsent()
    {
        var backend = Path.Combine(UiAuditSprint3Tests.FindRepoRoot(), "..", "plugins", "remote", "backend", "src");
        var profiles = File.ReadAllText(Path.Combine(backend, "profiles.rs"));
        var ftp = File.ReadAllText(Path.Combine(backend, "ftp.rs"));

        Assert.Contains("\"Confirm the unencrypted connection to use plain FTP\"", profiles);
        Assert.Contains("\"Confirm the unencrypted connection to use plain FTP\"", ftp);
        Assert.DoesNotContain("insecure_plain_ftp acknowledgement", profiles + ftp);
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
