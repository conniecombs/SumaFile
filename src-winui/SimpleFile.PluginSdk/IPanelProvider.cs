namespace SimpleFile.PluginSdk;

/// <summary>
/// Supplies the body of a <c>contributions.panels</c> entry. The plugin's entry type implements it; the host draws the
/// panel's chrome (title, dock or window toggle, close button and resize grip) from the manifest, and shows one panel
/// at a time, docked next to the file list or in a host-owned window.
/// </summary>
public interface IPanelProvider
{
    /// <summary>
    /// Creates the panel body. The SDK is WinUI-free, so the element crosses as <see cref="object"/>: return a
    /// <c>Microsoft.UI.Xaml.FrameworkElement</c>. The host checks the type, and marks the plugin LoadFailed for
    /// anything else, for null and for an exception.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread. The host asks once per presentation: docked (<see cref="PluginPanelRequest.Detached"/>
    /// false), and again for its window when the user detaches it (true), because a WinUI element cannot move to
    /// another window. It may also ask again after it dropped the previous element. Return a new element each time,
    /// or one that has no parent, and keep the panel's state outside the element.
    /// </remarks>
    object CreatePanel(PluginPanelRequest request);
}
