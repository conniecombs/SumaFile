using Microsoft.UI.Xaml.Controls;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Trust;

namespace SimpleFile.App;

/// <summary>Asks whether to enable a newly found (or changed) plugin. Primary = Enable; Close = Keep disabled.</summary>
public sealed partial class PluginConsentDialog : ContentDialog
{
    public PluginConsentDialog(PluginRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        InitializeComponent();

        var manifest = record.Discovered.Manifest;
        PluginName.Text = manifest?.Name ?? record.Id;
        var publisher = manifest?.Publisher.Name;
        PublisherAndVersion.Text = string.IsNullOrWhiteSpace(publisher)
            ? $"Version {manifest?.Version ?? "unknown"} · {record.Id}"
            : $"{publisher} · Version {manifest?.Version ?? "unknown"} · {record.Id}";
        SignatureLine.Text = SignatureText(record.Signature);
        PermissionsList.Text = manifest?.Permissions is { Count: > 0 } permissions
            ? string.Join(Environment.NewLine, permissions.Select(permission => $"• {permission}"))
            : "None declared";
        BackendLine.Text = manifest?.Backend is { } backend
            ? $"Runs a background process: {Path.GetFileName(backend.Executable)}"
            : "No background process";
    }

    /// <summary>
    /// The signature line of the consent dialog and the Settings card. A good signature only proves the files match the
    /// key in the plugin's own plugin.json, not who the publisher is, so it reads "Signed · key", never "Verified".
    /// </summary>
    internal static string SignatureText(SignatureState signature) => signature.Status switch
    {
        SignatureStatus.Verified => $"Signed · key {signature.KeyFingerprint}",
        SignatureStatus.Invalid => $"Invalid signature: {signature.Error}",
        _ => "Unsigned plugin",
    };
}
