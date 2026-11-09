namespace SimpleFile.PluginSdk;

/// <summary>
/// Supplies the custom page of a <c>contributions.settingsPage</c>. The host shows it in the plugin's card under
/// Settings &gt; Plugins, below the declarative settings. The plugin's entry type implements it.
/// </summary>
public interface ISettingsPageProvider
{
    /// <summary>
    /// Creates the page. The SDK is WinUI-free, so the element crosses as <see cref="object"/>: return a
    /// <c>Microsoft.UI.Xaml.FrameworkElement</c>. The host checks the type, and marks the plugin LoadFailed for
    /// anything else, for null and for an exception.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread. The host asks once per Settings window, and may ask again after it dropped the
    /// previous element; return a new element then, or one that has no parent.
    /// </remarks>
    object CreateSettingsPage(PluginSettingsPageRequest request);
}
