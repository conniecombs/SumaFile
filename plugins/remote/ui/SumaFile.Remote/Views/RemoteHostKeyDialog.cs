using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.PluginSdk;
using SumaFile.Remote.Model;

namespace SumaFile.Remote.Views;

/// <summary>
/// Asks the user about an untrusted or changed SFTP host key. The fingerprint to trust is shown on its own, selectable,
/// as <see cref="RemoteAutomationIds.HostKeyFingerprint"/>. A changed key defaults to Cancel, so it never connects
/// silently.
/// </summary>
internal static class RemoteHostKeyDialog
{
    /// <summary>The fingerprint the user chose to trust, or null when they cancelled or there is nothing to ask.</summary>
    public static async Task<string?> AskAsync(IPluginHost host, XamlRoot root, RemoteProfile profile, ConnectOutcome outcome)
    {
        ContentDialog dialog;
        string fingerprint;
        if (outcome.UntrustedFingerprint is { } untrusted)
        {
            fingerprint = untrusted;
            dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Trust this server?",
                Content = Body($"{profile.Host}:{profile.Port}\nSHA256 fingerprint:", untrusted, null),
                PrimaryButtonText = "Trust and connect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
        }
        else if (outcome.MismatchActual is { } actual)
        {
            fingerprint = actual;
            dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Server key changed",
                Content = Body(
                    $"The key for {profile.Host} does not match the trusted fingerprint.\n"
                    + $"Expected {outcome.MismatchExpected}\n"
                    + "Received",
                    actual,
                    "Only continue if you know why the key changed."),
                PrimaryButtonText = "Replace trusted key",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
        }
        else
        {
            return null;
        }

        var result = await host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None);
        return result == PluginDialogResult.Primary ? fingerprint : null;
    }

    private static StackPanel Body(string lead, string fingerprint, string? trailer)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 460 };
        panel.Children.Add(Text(lead));
        var key = Text(fingerprint);
        AutomationProperties.SetAutomationId(key, RemoteAutomationIds.HostKeyFingerprint);
        AutomationProperties.SetName(key, "Server key fingerprint");
        panel.Children.Add(key);
        if (trailer is not null)
        {
            panel.Children.Add(Text(trailer));
        }

        return panel;
    }

    private static TextBlock Text(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };
}
