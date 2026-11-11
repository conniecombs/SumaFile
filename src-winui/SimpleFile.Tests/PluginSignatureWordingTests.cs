using System;
using System.IO;
using Xunit;

namespace SimpleFile.Tests;

/// <summary>
/// Final fix wave, item 3: the key a signature is checked against comes from the plugin's own plugin.json, so a good
/// signature says nothing about who the publisher is. The consent dialog and the Settings > Plugins card (both through
/// <c>PluginConsentDialog.SignatureText</c>, which the App alone compiles) say "Signed · key &lt;fingerprint&gt;".
/// </summary>
public sealed class PluginSignatureWordingTests
{
    private static string AppRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml")) && Directory.Exists(Path.Combine(dir.FullName, "src-winui")))
                return Path.Combine(dir.FullName, "src-winui", "SimpleFile.App");
        }

        throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds both Cargo.toml and src-winui.");
    }

    [Fact]
    public void TheSignatureLine_SaysSignedWithTheKey_NeverVerifiedPublisher()
    {
        var dialog = File.ReadAllText(Path.Combine(AppRoot(), "PluginConsentDialog.xaml.cs"));

        Assert.Contains("SignatureStatus.Verified => $\"Signed · key {signature.KeyFingerprint}\",", dialog, StringComparison.Ordinal);
        Assert.Contains("SignatureStatus.Invalid => $\"Invalid signature: {signature.Error}\",", dialog, StringComparison.Ordinal);
        Assert.Contains("_ => \"Unsigned plugin\",", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAppSource_ClaimsAVerifiedPublisher()
    {
        foreach (var file in Directory.EnumerateFiles(AppRoot(), "*.*", SearchOption.TopDirectoryOnly))
        {
            if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".xaml", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("Verified publisher", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheSettingsCard_UsesTheDialogsSignatureLine()
    {
        var card = File.ReadAllText(Path.Combine(AppRoot(), "PluginSettingsPanelBuilder.cs"));

        Assert.Contains("PluginConsentDialog.SignatureText(record.Signature)", card, StringComparison.Ordinal);
    }
}
