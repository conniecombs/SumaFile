namespace SimpleFile.PluginSdk;

/// <summary>Plugin-owned secondary windows, reached through <see cref="IPluginHost.Windows"/>.</summary>
public interface IPluginWindows
{
    /// <summary>
    /// Hands a window the plugin created to the host. The host gives it SumaFile's window chrome (icon, backdrop,
    /// theme, DPI-aware size) and tracks it until it closes. The SDK is WinUI-free, so the window crosses as
    /// <see cref="object"/>: pass a <c>Microsoft.UI.Xaml.Window</c>; anything else throws
    /// <see cref="ArgumentException"/>. Call it on the UI thread, before activating the window.
    /// </summary>
    /// <remarks>
    /// When the host disables, removes or shuts down the plugin, it closes every attached window first, before it
    /// stops the backend and before <see cref="IPlugin.DeactivateAsync"/> runs. After the host starts stopping the
    /// plugin, <c>Attach</c> closes the window at once instead of tracking it, so a window attached then never opens
    /// (or closes right away). A plugin enabled again gets a new <see cref="IPluginHost"/> whose windows work as usual.
    /// </remarks>
    void Attach(object window, PluginWindowOptions options);
}
