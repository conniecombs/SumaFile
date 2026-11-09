namespace SimpleFile.PluginSdk;

/// <summary>
/// Supplies the body of a <c>contributions.sidebar</c> section. The plugin's entry type implements it; the host draws
/// the section's header, collapse button and optional command button from the manifest before any plugin code loads.
/// </summary>
public interface ISidebarSectionProvider
{
    /// <summary>
    /// Creates the section body. The SDK is WinUI-free, so the element crosses as <see cref="object"/>: return a
    /// <c>Microsoft.UI.Xaml.FrameworkElement</c>. The host checks the type, and marks the plugin LoadFailed for
    /// anything else, for null and for an exception.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread. The host may ask again for the same section after it dropped the previous element
    /// (the sidebar re-rendered, or the plugin was disabled and enabled again); return a new element then, or one
    /// that has no parent.
    /// </remarks>
    object CreateSidebarSection(PluginSidebarSectionRequest request);
}
