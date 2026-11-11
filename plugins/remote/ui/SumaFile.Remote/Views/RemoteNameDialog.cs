using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.PluginSdk;

namespace SumaFile.Remote.Views;

/// <summary>The manager's name prompt (new remote folder, rename), shown through the host's dialog gate.</summary>
internal static class RemoteNameDialog
{
    /// <summary>The typed name, or null when the user cancelled.</summary>
    public static async Task<string?> ShowAsync(
        IPluginHost host,
        XamlRoot root,
        string title,
        string primaryButtonText,
        string? initialValue = null)
    {
        var input = new TextBox
        {
            Text = initialValue ?? "",
            PlaceholderText = "Name",
            MinWidth = 320,
        };
        AutomationProperties.SetAutomationId(input, RemoteAutomationIds.NamePrompt);
        AutomationProperties.SetName(input, "Name");
        input.Loaded += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = input,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None);
        return result == PluginDialogResult.Primary ? input.Text : null;
    }
}
