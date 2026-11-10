namespace SimpleFile.PluginSdk;

/// <summary>
/// The SDK contract version. A plugin's <c>plugin.json</c> names the version it was built against as
/// <c>sdkVersion</c>, and the host loads it only when that major version is <see cref="Major"/> and its minor version is
/// no higher than <see cref="Version"/>'s: the SDK assembly's version is major.minor.0.0, so a plugin built against a
/// newer minor could not bind to an older host's SDK.
/// </summary>
/// <remarks>
/// <para>
/// The SDK follows semantic versioning: a breaking change to any contract bumps <see cref="Major"/>, and an addition
/// bumps the minor version.
/// </para>
/// <para>
/// The <c>object</c> hand-off rule: the SDK is WinUI-free, so every UI object crosses it typed as <see cref="object"/>.
/// <see cref="ISidebarSectionProvider.CreateSidebarSection"/>, <see cref="ISettingsPageProvider.CreateSettingsPage"/> and
/// <see cref="IPanelProvider.CreatePanel"/> return a <c>Microsoft.UI.Xaml.FrameworkElement</c>,
/// <see cref="IPluginWindows.Attach"/> takes a <c>Microsoft.UI.Xaml.Window</c> and
/// <see cref="IPluginDialogs.ShowDialogAsync"/> takes a <c>Microsoft.UI.Xaml.Controls.ContentDialog</c>. The host
/// checks the type at run time: a provider that returns anything else marks the plugin LoadFailed, and
/// <c>Attach</c> and <c>ShowDialogAsync</c> throw <see cref="ArgumentException"/>. A plugin references the Windows
/// App SDK compile-only, so the WinUI types it builds are the host's.
/// </para>
/// </remarks>
public static class PluginSdk
{
    /// <summary>The full SDK version, <c>major.minor.patch</c>; also the version of the <c>SumaFile.PluginSdk</c> package.</summary>
    public const string Version = "1.0.0";

    /// <summary>The SDK major version this host supports; a plugin whose <c>sdkVersion</c> has another major does not load.</summary>
    public const int Major = 1;
}
