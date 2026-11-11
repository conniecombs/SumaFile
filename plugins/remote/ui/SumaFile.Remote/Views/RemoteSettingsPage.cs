using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace SumaFile.Remote.Views;

/// <summary>
/// The plugin's custom settings page under Settings &gt; Plugins (it replaces the 1.0.4 Settings &gt; Tools card):
/// a description and the button that opens the one manager window. The host renders it below the declarative
/// transfer settings.
/// </summary>
internal sealed class RemoteSettingsPage
{
    internal const string Description =
        "Save FTP, FTPS and SFTP connection profiles, browse remote folders, and upload or download files. "
        + "Remote transfers also appear in the Transfers window.";

    public RemoteSettingsPage(RemotePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var description = new TextBlock
        {
            Text = Description,
            TextWrapping = TextWrapping.Wrap,
        };
        var open = new Button
        {
            Content = "Open FTP/SFTP manager",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAutomationId(open, RemoteAutomationIds.SettingsOpenManager);
        open.Click += (_, _) => plugin.OpenManager(null);

        Root = new StackPanel { Spacing = 8 };
        Root.Children.Add(description);
        Root.Children.Add(open);
    }

    /// <summary>The element the host places in the plugin's settings card.</summary>
    public StackPanel Root { get; }
}
