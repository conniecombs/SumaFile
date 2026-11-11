using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using SimpleFile.PluginSdk;

namespace SimpleFile.Tests.Fixtures.HelloPlugin;

/// <summary>
/// Hello's WinUI code. Only the provider methods and the window and dialog commands of <see cref="HelloPlugin"/> reach
/// it, so the entry type loads and activates in a host without WinUI (the plugin host tests). Every member keeps WinUI
/// types out of its signature, and none is inlined into <see cref="HelloPlugin"/>, so compiling the entry type's
/// methods never loads WinUI.
/// </summary>
internal static class HelloUi
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object CreateSidebarSection()
    {
        var text = new TextBlock { Text = "Hello from the fixture", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(text, "Hello.SidebarSection");
        return text;
    }

    /// <summary>Hello's custom settings page: a code-built StackPanel with a labelled switch that settings search finds.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object CreateSettingsPage()
    {
        var title = new TextBlock { Text = "Hello page", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(title, "Hello.SettingsPage");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(title);
        panel.Children.Add(new ToggleSwitch { Header = "Hello page switch" });
        return panel;
    }

    /// <summary>Opens a <see cref="HelloWindow"/> through the host, which gives it the host's chrome and tracks it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OpenWindow(IPluginHost host)
    {
        var window = new HelloWindow(host);
        host.Windows.Attach(window, new PluginWindowOptions("Hello window", 480, 320));
        window.Activate();
        HelloMarkers.Write("window.txt", "Hello window");
    }

    /// <summary>A dialog with no XamlRoot: the host shows it on the main window.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<PluginDialogResult> ShowDialogAsync(IPluginHost host, CancellationToken ct) =>
        host.Dialogs.ShowDialogAsync(new ContentDialog { Title = "Hello dialog", CloseButtonText = "Close" }, ct);
}

/// <summary>
/// A plugin window: a <see cref="Window"/> subclass built in code (no <c>InitializeComponent</c>, spec 5.3) whose content
/// is a theme-aware <see cref="XamlReader"/> fragment, and whose button shows a dialog on the window's own XamlRoot.
/// </summary>
internal sealed class HelloWindow : Window
{
    private const string Fragment = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Spacing="12" Padding="24">
            <TextBlock AutomationProperties.AutomationId="Hello.WindowBody"
                       Text="Hello window body"
                       Foreground="{ThemeResource SfTextPrimaryBrush}" />
            <Button x:Name="WindowDialogButton"
                    AutomationProperties.AutomationId="Hello.WindowDialog"
                    Content="Show dialog" />
        </StackPanel>
        """;

    private readonly IPluginHost _host;

    public HelloWindow(IPluginHost host)
    {
        _host = host;
        var root = (FrameworkElement)XamlReader.Load(Fragment);
        Content = root;
        var button = (Button)root.FindName("WindowDialogButton");
        button.Click += OnDialogButtonClick;
    }

    private async void OnDialogButtonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Hello window dialog",
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot,
            };
            var result = await _host.Dialogs.ShowDialogAsync(dialog, CancellationToken.None);
            HelloMarkers.Append("dialog.txt", "window:" + result);
        }
        catch (Exception exception)
        {
            HelloMarkers.Append("dialog.txt", "window-error:" + exception.Message);
        }
    }
}
