using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleFile.PluginSdk;

namespace SimpleFile.Tests.Fixtures.HelloPlugin;

/// <summary>
/// Hello's panel bodies. Like <see cref="HelloUi"/>, only <see cref="HelloPlugin.CreatePanel"/> reaches it, and no
/// WinUI type appears in a signature, so the entry type loads without WinUI.
/// </summary>
internal static class HelloPanels
{
    /// <summary>
    /// A code-built body: a text (<c>Hello.PanelBody</c>), a <see cref="HelloGrip"/> (<c>Hello.PanelGrip</c>) and a
    /// button (<c>Hello.PanelDialog</c>) that shows "Hello panel dialog" on the panel's own XamlRoot, docked or in the
    /// panel's window. <c>dialog.txt</c> gets <c>panel:&lt;result&gt;</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object Create(IPluginHost host, PluginPanelRequest request)
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(12) };
        var text = new TextBlock
        {
            Text = request.Detached ? "Hello panel body (window)" : "Hello panel body",
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(text, "Hello.PanelBody");
        panel.Children.Add(text);

        var grip = new HelloGrip { Height = 8 };
        AutomationProperties.SetAutomationId(grip, "Hello.PanelGrip");
        panel.Children.Add(grip);

        var button = new Button { Content = "Show dialog" };
        AutomationProperties.SetAutomationId(button, "Hello.PanelDialog");
        button.Click += async (_, _) =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Hello panel dialog",
                    CloseButtonText = "Close",
                    XamlRoot = button.XamlRoot,
                };
                var result = await host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None);
                HelloMarkers.Append("dialog.txt", "panel:" + result);
            }
            catch (Exception exception)
            {
                HelloMarkers.Append("dialog.txt", "panel-error:" + exception.Message);
            }
        };
        panel.Children.Add(button);
        return panel;
    }
}

/// <summary>
/// A <see cref="Grid"/> subclass from the plugin's load context that sets the protected
/// <see cref="UIElement.ProtectedCursor"/>, as a plugin's own splitter would: the panel slot's proof that a plugin can
/// subclass WinUI types in its body.
/// </summary>
internal sealed class HelloGrip : Grid
{
    public HelloGrip()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
