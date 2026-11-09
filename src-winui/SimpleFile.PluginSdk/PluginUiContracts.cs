namespace SimpleFile.PluginSdk;

/// <summary>What the host passes to <see cref="ISidebarSectionProvider.CreateSidebarSection"/>.</summary>
/// <param name="SectionId">The section's <c>id</c> in <c>contributions.sidebar</c>.</param>
/// <param name="Provider">The section's <c>provider</c> in the manifest.</param>
public sealed record PluginSidebarSectionRequest(string SectionId, string Provider);

/// <summary>What the host passes to <see cref="ISettingsPageProvider.CreateSettingsPage"/>.</summary>
/// <param name="Provider">The <c>provider</c> of <c>contributions.settingsPage</c>.</param>
public sealed record PluginSettingsPageRequest(string Provider);

/// <summary>How the host presents a window handed to <see cref="IPluginWindows.Attach"/>.</summary>
/// <param name="Title">The window title.</param>
/// <param name="Width">The initial width in device-independent pixels; the host scales it for the display.</param>
/// <param name="Height">The initial height in device-independent pixels; the host scales it for the display.</param>
public sealed record PluginWindowOptions(string Title, int Width, int Height);

/// <summary>Which button closed a dialog shown through <see cref="IPluginDialogs.ShowDialogAsync"/>.</summary>
public enum PluginDialogResult
{
    /// <summary>The close button or Escape, or the host hid the dialog.</summary>
    None = 0,

    /// <summary>The primary button.</summary>
    Primary = 1,

    /// <summary>The secondary button.</summary>
    Secondary = 2,
}
